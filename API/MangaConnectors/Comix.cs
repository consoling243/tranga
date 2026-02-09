using System.Text.RegularExpressions;
using System.Web;
using HtmlAgilityPack;
using API.MangaDownloadClients;
using API.Schema.MangaContext;
using log4net;
using System.Collections.Generic;
using System.Linq;               // For OrderBy, DistinctBy …
using System.Text.Json;
using System.Text;
using System.Threading.Tasks;

namespace API.MangaConnectors;

/// <summary>
/// Connector for https://comix.to/ – uses the public JSON search endpoint
/// and the paged chapter‑list API (v2).
/// </summary>
public class Comix : MangaConnector
{
    public Comix()
        : base(
            "Comix",
            new[] { "en" },
            new[] { "comix.to" },
            "https://comix.to/static/favicon.ico")
    {
        this.downloadClient = new HttpDownloadClient();
    }

    #region SEARCH -------------------------------------------------------------

    public override (Manga, MangaConnectorId<Manga>)[] SearchManga(string mangaSearchName)
    {
        Log.InfoFormat("Searching on comix.to (JSON API): {0}", mangaSearchName);

        string sanitizedTitle = string.Join(' ',
            Regex.Matches(mangaSearchName, @"[A-Za-z0-9]+")
                 .Select(m => m.Value)
                 .Where(v => v.Length > 0))
            .ToLowerInvariant();

        string apiUrl = $"https://comix.to/api/v2/manga?order[relevance]=desc&keyword={HttpUtility.UrlEncode(sanitizedTitle)}&genres_mode=and&limit=5&page=1";

        HttpResponseMessage response = downloadClient
            .MakeRequest(apiUrl, RequestType.Default)
            .GetAwaiter()
            .GetResult();

        if (!response.IsSuccessStatusCode)
        {
            Log.Error($"Search request failed – status {(int)response.StatusCode}");
            return [];
        }

        string json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        using var doc = JsonDocument.Parse(json);
        JsonElement root   = doc.RootElement;
        JsonElement result = root.GetProperty("result");
        JsonElement items  = result.GetProperty("items");

        var seenIds = new HashSet<string>();
        var mangas  = new List<(Manga, MangaConnectorId<Manga>)>();

        foreach (JsonElement item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("hash_id", out JsonElement hashEl) ||
                !item.TryGetProperty("slug",    out JsonElement slugEl))
                continue;

            string hash = hashEl.GetString() ?? "";
            string slug = slugEl.GetString() ?? "";

            if (string.IsNullOrWhiteSpace(hash) || string.IsNullOrWhiteSpace(slug))
                continue;

            string combinedId = $"{hash}-{slug}";
            if (!seenIds.Add(combinedId))
                continue;   // duplicate in API response

            string titleUrl = $"https://comix.to/title/{combinedId}";

            Log.DebugFormat("Parsing manga from JSON result – URL: {0}", titleUrl);
            var parsed = GetMangaFromUrl(titleUrl);          // reuse existing HTML parser
            if (parsed.HasValue)
                mangas.Add(parsed.Value);
            else
                Log.WarnFormat("Failed to parse manga page at {0}", titleUrl);
        }

        return mangas.DistinctBy(r => r.Item1.Key).ToArray();
    }

    #endregion

    #region MANGA INFO ---------------------------------------------------------

    public override (Manga, MangaConnectorId<Manga>)? GetMangaFromUrl(string url)
    {
        Log.InfoFormat("Fetching manga info from: {0}", url);

        var match = Regex.Match(url,
            @"https?://(?:www\.)?comix\.to/title/(?<id>[^/]+)/?",
            RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            Log.Error("URL does not match comix.to title pattern");
            return null;
        }

        string id = match.Groups["id"].Value;               // hash‑slug
        string canonicalUrl = $"https://comix.to/title/{id}/";

        HttpResponseMessage response = downloadClient
            .MakeRequest(canonicalUrl, RequestType.MangaInfo)
            .GetAwaiter()
            .GetResult();

        if (!response.IsSuccessStatusCode)
        {
            Log.Error($"Failed to retrieve manga page – status {(int)response.StatusCode}");
            return null;
        }

        string html = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        var doc    = new HtmlDocument();
        doc.LoadHtml(html);

        return ParseMangaFromHtml(doc, id, canonicalUrl);
    }

    public override (Manga, MangaConnectorId<Manga>)? GetMangaFromId(string mangaIdOnSite)
    {
        string url = $"https://comix.to/title/{mangaIdOnSite}/";

        HttpResponseMessage response = downloadClient
            .MakeRequest(url, RequestType.MangaInfo)
            .GetAwaiter()
            .GetResult();

        if (!response.IsSuccessStatusCode)
            return null;

        string html = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        var doc    = new HtmlDocument();
        doc.LoadHtml(html);

        return ParseMangaFromHtml(doc, mangaIdOnSite, url);
    }

    private (Manga, MangaConnectorId<Manga>) ParseMangaFromHtml(
        HtmlDocument doc,
        string mangaSlugOnSite,
        string url)
    {
        // ----- TITLE --------------------------------------------------------
        var titleNode = doc.DocumentNode.SelectSingleNode("//title");
        string rawTitle = titleNode?.InnerText ?? mangaSlugOnSite;
        var titleMatch = Regex.Match(rawTitle, @"^(.*?)\s*\|\s*comix\.to", RegexOptions.IgnoreCase);
        string cleanTitle = titleMatch.Success ? titleMatch.Groups[1].Value.Trim() : rawTitle;
        cleanTitle = HtmlEntity.DeEntitize(cleanTitle);

        // ----- COVER ---------------------------------------------------------
    string coverUrl = null;

    var preloadLinkNode = doc.DocumentNode.SelectSingleNode(
        "//link[@rel='preload' and @as='image' and @href]");

    if (preloadLinkNode != null)
    {
        // The attribute already contains a full URL in the current site layout.
        coverUrl = preloadLinkNode.GetAttributeValue("href", "").Trim();

        // Defensive: some older pages might have a relative URL – make it absolute.
        if (!string.IsNullOrEmpty(coverUrl) && !coverUrl.StartsWith("http"))
            coverUrl = $"https://comix.to{coverUrl}";
    }

    // 2️⃣ Fallback to the old <img class="cover"> selector (kept for safety).
    if (string.IsNullOrWhiteSpace(coverUrl))
    {
        var coverNode = doc.DocumentNode.SelectSingleNode("//img[contains(@class,'cover')]");
        coverUrl = coverNode?.GetAttributeValue("src", "") ?? "";
        if (!string.IsNullOrEmpty(coverUrl) && !coverUrl.StartsWith("http"))
            coverUrl = $"https://static.comix.to{coverUrl}";
    }

        // ----- DESCRIPTION ---------------------------------------------------
        var descNode = doc.DocumentNode.SelectSingleNode("//div[contains(@class,'description')]");
        string description = HtmlEntity.DeEntitize(descNode?.InnerText ?? "").Trim();

        // ----- TAGS -----------------------------------------------------------
        var tagNodes = doc.DocumentNode.SelectNodes("//a[contains(@class,'tag')]");
        List<MangaTag> tags = tagNodes?
            .Select(n => new MangaTag(HtmlEntity.DeEntitize(n.InnerText.Trim())))
            .ToList() ?? new List<MangaTag>();

        // ----- STATUS ---------------------------------------------------------
        var statusNode = doc.DocumentNode.SelectSingleNode("//span[contains(@class,'status')]");
        string rawStatus = HtmlEntity.DeEntitize(statusNode?.InnerText ?? "").ToLowerInvariant().Trim();
        MangaReleaseStatus releaseStatus = rawStatus switch
        {
            "ongoing"   => MangaReleaseStatus.Continuing,
            "hiatus"    => MangaReleaseStatus.OnHiatus,
            "completed" => MangaReleaseStatus.Completed,
            "canceled" or "cancelled" => MangaReleaseStatus.Cancelled,
            _           => MangaReleaseStatus.Unreleased
        };

        // ----- AUTHORS --------------------------------------------------------
        var authorNodes = doc.DocumentNode.SelectNodes("//a[contains(@class,'author')]");
        List<Author> authors = authorNodes?
            .Select(n => new Author(HtmlEntity.DeEntitize(n.InnerText.Trim())))
            .ToList() ?? new List<Author>();

        // ----- YEAR -----------------------------------------------------------
        var yearNode = doc.DocumentNode.SelectSingleNode("//span[contains(@class,'year')]");
        uint? year = null;
        if (uint.TryParse(yearNode?.InnerText.Trim(), out uint parsedYear))
            year = parsedYear;

        List<AltTitle> altTitles = new();
        List<Link> links       = new();

        var manga = new Manga(
            cleanTitle,
            description,
            coverUrl,
            releaseStatus,
            authors,
            tags,
            links,
            altTitles,
            null,          // language – keep null for deterministic key
            0f,            // rating (unknown)
            year,
            null);         // extra data

        var mcId = new MangaConnectorId<Manga>(manga, this, mangaSlugOnSite, url);
        manga.MangaConnectorIds.Add(mcId);

        return (manga, mcId);
    }

    #endregion

    #region CHAPTER LIST -------------------------------------------------------

    /// <summary>
    /// Retrieves the full list of chapters using the paged v2 API.
    /// The stored manga ID is "{hash}-{slug}" – we extract the hash part for the request.
    /// </summary>
    public override (Chapter, MangaConnectorId<Chapter>)[] GetChapters(
        MangaConnectorId<Manga> manga,
        string? language = null)
    {
        Log.InfoFormat("Fetching chapter list via API for: {0}", manga.IdOnConnectorSite);

        // -----------------------------------------------------------------
        // 1️⃣ Extract hash_id (the part before the first dash) and keep the
        //    whole slug because we need it later to build the canonical URL.
        // -----------------------------------------------------------------
        string fullSlug = manga.IdOnConnectorSite;               // e.g. "5zrxl-kanojo-no-carrera"
        int dashIdx = fullSlug.IndexOf('-');
        if (dashIdx <= 0)
        {
            Log.Error($"Cannot extract hash_id from stored ID '{fullSlug}'");
            return [];
        }

        string hashId   = fullSlug.Substring(0, dashIdx);       // "5zrxl"
        string slugPart = fullSlug;                             // keep the whole thing for URLs

        var allChapters = new List<(Chapter, MangaConnectorId<Chapter>)>();

        int page = 1;
        int lastPage = 1;   // will be overwritten after first request

        while (page <= lastPage)
        {
            string apiUrl = $"https://comix.to/api/v2/manga/{hashId}/chapters?limit=100&page={page}&order[number]=asc";

            HttpResponseMessage response = downloadClient
                .MakeRequest(apiUrl, RequestType.Default)
                .GetAwaiter()
                .GetResult();

            if (!response.IsSuccessStatusCode)
            {
                Log.Error($"Failed to retrieve chapter page {page} – status {(int)response.StatusCode}");
                break;
            }

            string json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(json);
            JsonElement root   = doc.RootElement;
            JsonElement result = root.GetProperty("result");
            JsonElement items  = result.GetProperty("items");

            // -----------------------------------------------------------------
            // 2️⃣ Parse every chapter returned on this page.
            // -----------------------------------------------------------------
            foreach (JsonElement ch in items.EnumerateArray())
            {
                Log.Info($"Retrieving chapters: {ch}");
                // Required fields – if any are missing we skip that entry.
                if (!ch.TryGetProperty("chapter_id", out JsonElement idEl) ||
                    !ch.TryGetProperty("number",     out JsonElement numEl))
                    continue;

                string chapterIdOnSite = idEl.GetInt32().ToString();          // e.g. 7853102
                string numberStr        = numEl.GetInt32().ToString();      // may be int or float in JSON

                // Volume is optional.
                int? volumeNumber = null;
                // if (ch.TryGetProperty("volume", out JsonElement volEl) &&
                //     volEl.ValueKind != JsonValueKind.Null &&
                //     int.TryParse(volEl.GetString(), out int v))
                //     volumeNumber = v;

                // Optional human‑readable title of the chapter.
                string? chTitle = null;
                // if (ch.TryGetProperty("name", out JsonElement nameEl) &&
                //     nameEl.ValueKind != JsonValueKind.Null)
                //     chTitle = HtmlEntity.DeEntitize(nameEl.GetString()?.Trim() ?? "");

                Log.Info($"Retrieving volumeNumber: {volumeNumber}");
                Log.Info($"Retrieving chTitle: {chTitle}");

                Log.Info($"Retrieving chapterObj: {manga.Obj}, numberStr: {numberStr}, volumeNumber: {volumeNumber}, chTitle: {chTitle}");
                // Build Chapter object.
                var chapter = new Chapter(manga.Obj, numberStr, volumeNumber, chTitle);
                Log.Info($"Retrieving chapter: {chapter}");

                // Canonical URL – the same pattern that you would see when clicking “Read”.
                string canonicalUrl =
                    $"https://comix.to/title/{slugPart}/{chapterIdOnSite}-chapter-{numberStr}";

                var mcId = new MangaConnectorId<Chapter>(chapter, this,
                                                        chapterIdOnSite,
                                                        canonicalUrl);
                chapter.MangaConnectorIds.Add(mcId);
                allChapters.Add((chapter, mcId));
                Log.Info($"Retrieving mcId: {mcId}");
                Log.Info($"Retrieving canonicalUrl: {canonicalUrl}");
            }

            // -----------------------------------------------------------------
            // 3️⃣ Pagination – read the pagination block to know how many pages
            //    remain. The API returns "last_page".
            // -----------------------------------------------------------------
            JsonElement pagination = result.GetProperty("pagination");
            if (page == 1)   // only need to read it once, but doing it each loop is cheap
                lastPage = pagination.GetProperty("last_page").GetInt32();

            page++;
        }

        Log.InfoFormat("Found {0} chapters for '{1}' (hash {2})", allChapters.Count,
                       manga.Obj.Name, hashId);

        // Sort using the built‑in ChapterComparer (numeric + volume aware)
        return allChapters
               .OrderBy(c => c.Item1, new Chapter.ChapterComparer())
               .ToArray();
    }

    #endregion

    #region CHAPTER IMAGES -----------------------------------------------------

    internal override string[] GetChapterImageUrls(MangaConnectorId<Chapter> chapterId)
    {
        Log.InfoFormat("Fetching image URLs for chapter: {0}", chapterId.Obj);

        if (chapterId.WebsiteUrl == null)
        {
            Log.Error("Chapter URL is null – cannot continue.");
            return [];
        }

        // comix.to checks the referrer header.  We pass the manga page as referrer.
        string? referrer = null;
        if (chapterId.Obj.ParentManga.MangaConnectorIds?.Any() == true)
        {
            referrer = chapterId.Obj.ParentManga.MangaConnectorIds
                .FirstOrDefault(id => id.MangaConnectorName == this.Name)?
                .WebsiteUrl;
        }

        return GetChapterImageUrlsAsync(chapterId, referrer).GetAwaiter().GetResult();
    }

    private async Task<string[]> GetChapterImageUrlsAsync(
        MangaConnectorId<Chapter> chapterId,
        string? referrer)
    {
        Log.Info($"Logging website url: {chapterId.websiteUrl}");
        await using var chromium = new ChromiumDownloadClient();

        HttpResponseMessage response = await chromium.MakeRequest(
            chapterId.WebsiteUrl!,
            RequestType.Default,
            referrer);

        if (!response.IsSuccessStatusCode)
        {
            Log.Error($"Failed to load chapter page – status {(int)response.StatusCode}");
            return [];
        }

        string html = await response.Content.ReadAsStringAsync();
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // Images look like: <img alt="Page 1" src="/media/manga/xxxxx.jpg">
        var imgNodes = doc.DocumentNode.SelectNodes("//img[starts-with(@alt, '')]");
        Log.Info($"Image Nodes: {imgNodes.ToString}")
        if (imgNodes == null || imgNodes.Count == 0)
        {
            Log.Warn("No page images found on chapter page.");
            return [];
        }

        var imageUrls = imgNodes
            .Select(img =>
            {
                string src = img.GetAttributeValue("src", "")
                             ?? img.GetAttributeValue("data-src", "");

                if (!string.IsNullOrEmpty(src))
                    src = $"{src}";
                    Log.Info($"Retrieving src: {src}");
                return src;
            })
            .Where(u => !string.IsNullOrEmpty(u))
            .ToArray();

        Log.InfoFormat("Found {0} image URLs for chapter {1}", imageUrls.Length, chapterId.Obj);
        return imageUrls;
    }

    #endregion
}