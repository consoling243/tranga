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
/// Connector for https://comix.to/ – now uses the JSON search endpoint
/// (which returns a hash_id + slug that must be combined to build the real URL).
/// </summary>
public class Comix : MangaConnector
{
    public Comix()
        : base(
            "Comix",
            new[] { "en" },
            new[] { "comix.to" },
            "https://comix.to/static/favicon.ico")   // replace with a nicer icon if you have one
    {
        this.downloadClient = new HttpDownloadClient();
    }

    #region SEARCH -------------------------------------------------------------

    /// <summary>
    /// Calls the JSON API and builds
    /// the proper title URL from the returned hash_id + slug.
    /// </summary>
    public override (Manga, MangaConnectorId<Manga>)[] SearchManga(string mangaSearchName)
    {
        Log.InfoFormat("Searching on comix.to (JSON API): {0}", mangaSearchName);

        // 1️⃣ Normalise the query – keep only alphanumerics and spaces.
        string sanitizedTitle = string.Join(' ',
            Regex.Matches(mangaSearchName, @"[A-Za-z0-9]+")
                 .Select(m => m.Value)
                 .Where(v => v.Length > 0))
            .ToLowerInvariant();

        // 2️⃣ Build the JSON‑API URL.
        // The API returns a structure like the example you posted.
        string apiUrl = $"https://comix.to/api/v2/manga?order[relevance]=desc&keyword={HttpUtility.UrlEncode(sanitizedTitle)}&genres_mode=and&limit=28&page=1";

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
        if (string.IsNullOrWhiteSpace(json))
        {
            Log.Warn("Empty JSON returned from search API");
            return [];
        }

        // --------------------------------------------------------------
        // Minimal deserialization – we only need hash_id, slug and title.
        // Using System.Text.Json because it is already referenced.
        // --------------------------------------------------------------

        using var doc = JsonDocument.Parse(json);
        JsonElement root   = doc.RootElement;
        JsonElement result = root.GetProperty("result");
        JsonElement items  = result.GetProperty("items");

        var seenIds = new HashSet<string>();
        var mangas  = new List<(Manga, MangaConnectorId<Manga>)>();

        foreach (JsonElement item in items.EnumerateArray())
        {
            // Required fields – if any are missing we skip the entry.
            if (!item.TryGetProperty("hash_id", out JsonElement hashEl) ||
                !item.TryGetProperty("slug",     out JsonElement slugEl))
                continue;

            string hash = hashEl.GetString() ?? "";
            string slug = slugEl.GetString() ?? "";

            if (string.IsNullOrWhiteSpace(hash) || string.IsNullOrWhiteSpace(slug))
                continue;

            // The ID that the rest of the connector expects:
            //   "hash-slug"  (e.g. "5zrxl-kanojo-no-carrera")
            string combinedId = $"{hash}-{slug}";
            if (!seenIds.Add(combinedId))
                continue;   // duplicate entry in API response

            string titleUrl = $"https://comix.to/title/{combinedId}";

            Log.DebugFormat("Parsing manga from JSON result – URL: {0}", titleUrl);
            var parsed = GetMangaFromUrl(titleUrl);          // reuse existing HTML parser
            if (parsed.HasValue)
            {
                mangas.Add(parsed.Value);
                Log.DebugFormat("Added '{0}'", parsed.Value.Item1.Name);
            }
            else
            {
                Log.WarnFormat("Failed to parse manga page at {0}", titleUrl);
            }
        }

        // De‑duplicate by the unique manga key (generated from its name).
        return mangas.DistinctBy(r => r.Item1.Key).ToArray();
    }

    #endregion

    #region MANGA INFO ---------------------------------------------------------

    public override (Manga, MangaConnectorId<Manga>)? GetMangaFromUrl(string url)
    {
        Log.InfoFormat("Fetching manga info from: {0}", url);

        // URL pattern we expect:
        //   https://comix.to/title/{hash}-{slug}
        var match = Regex.Match(url,
            @"https?://(?:www\.)?comix\.to/title/(?<id>[^/]+)/?",
            RegexOptions.IgnoreCase);

        if (!match.Success)
        {
            Log.Error("URL does not match comix.to title pattern");
            return null;
        }

        string id = match.Groups["id"].Value;               // hash‑slug (e.g. 5zrxl-kanojo-no-carrera)
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

        // The shared parser expects the *slug* part as its ID argument.
        // Since we store the whole "hash‑slug" string, just pass it through.
        return ParseMangaFromHtml(doc, id, canonicalUrl);
    }

    public override (Manga, MangaConnectorId<Manga>)? GetMangaFromId(string mangaIdOnSite)
    {
        // The ID on comix.to is exactly the "hash‑slug" string we stored.
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
        // -----------------------------------------------------------------
        // The body of this method is **exactly the same** as in the previous
        // version – only the parameter name changed (slug now includes hash).
        // -----------------------------------------------------------------

        var titleNode = doc.DocumentNode.SelectSingleNode("//title");
        string rawTitle = titleNode?.InnerText ?? mangaSlugOnSite;
        var titleMatch = Regex.Match(rawTitle, @"^(.*?)\s*\|\s*comix\.to", RegexOptions.IgnoreCase);
        string cleanTitle = titleMatch.Success ? titleMatch.Groups[1].Value.Trim() : rawTitle;
        cleanTitle = HtmlEntity.DeEntitize(cleanTitle);

        var coverNode = doc.DocumentNode.SelectSingleNode("//img[contains(@class,'cover')]");
        string coverUrl = coverNode?.GetAttributeValue("src", "") ?? "";
        if (!string.IsNullOrEmpty(coverUrl) && !coverUrl.StartsWith("http"))
            coverUrl = $"https://comix.to{coverUrl}";

        var descNode = doc.DocumentNode.SelectSingleNode("//div[contains(@class,'description')]");
        string description = HtmlEntity.DeEntitize(descNode?.InnerText ?? "").Trim();

        var tagNodes = doc.DocumentNode.SelectNodes("//a[contains(@class,'tag')]");
        List<MangaTag> tags = tagNodes?
            .Select(n => new MangaTag(HtmlEntity.DeEntitize(n.InnerText.Trim())))
            .ToList() ?? new List<MangaTag>();

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

        var authorNodes = doc.DocumentNode.SelectNodes("//a[contains(@class,'author')]");
        List<Author> authors = authorNodes?
            .Select(n => new Author(HtmlEntity.DeEntitize(n.InnerText.Trim())))
            .ToList() ?? new List<Author>();

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

    private const int Limit = 100;
    public override (Chapter, MangaConnectorId<Chapter>)[] GetChapters(
        MangaConnectorId<Manga> manga,
        string? language = null)
    {
        Log.InfoFormat("Fetching chapter list for slug: {0}", manga.IdOnConnectorSite);

        // The stored ID is the combined "hash‑slug" (e.g. 5zrxl-kanojo-no-carrera).
        string slug = manga.IdOnConnectorSite;

        // string chaptersUrl = $"https://comix.to/api/v2/manga/{hash}/chapters?limit={Limit}page=1&order[number]=asc";

        // HttpResponseMessage response = downloadClient
        //     .MakeRequest(chaptersUrl, RequestType.Default)
        //     .GetAwaiter()
        //     .GetResult();

        // if (!response.IsSuccessStatusCode)
        // {
        //     Log.Error($"Failed to load chapter list – status {(int)response.StatusCode}");
        //     return [];
        // }

        int page = 1;
        int last_page = 1; 
        // response.result.pagination.last_page;
        
        while(page <= last_page)
        {
            string chaptersUrl = $"https://comix.to/api/v2/manga/{hash}/chapters?limit={Limit}page={page}&order[number]=asc";
            
            HttpResponseMessage response = downloadClient
            .MakeRequest(chaptersUrl, RequestType.Default)
            .GetAwaiter()
            .GetResult();

            if (!response.IsSuccessStatusCode)
            {
                Log.Error($"Failed to load chapter list – status {(int)response.StatusCode}");
                return [];
            }

            page += 1;
            if(last_page != response.result.pagination.last_page) {
                last_page = response.result.pagination.last_page;
            }

            string html = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            var doc    = new HtmlDocument();
            doc.LoadHtml(html);

            var chapterNodes = doc.DocumentNode.SelectNodes("//a[contains(@href, '/title/') and contains(@href, '-chapter-')]");
            if (chapterNodes == null || chapterNodes.Count == 0)
                return [];

            var chapters = new List<(Chapter, MangaConnectorId<Chapter>)>();

            foreach (var node in chapterNodes)
            {
                string href = node.GetAttributeValue("href", "").Trim();
                if (string.IsNullOrEmpty(href))
                    continue;

                string fullUrl = href.StartsWith("http")
                    ? href
                    : $"https://comix.to{(href.StartsWith("/") ? "" : "/")}{href}";

                // Visible text may contain volume info, chapter number etc.
                string nodeText = node.InnerText.Trim();

                // ---- VOLUME (optional) -----------------------------------------
                int? volumeNumber = null;
                var volMatch = Regex.Match(nodeText,
                    @"(?:vol\.?|volume|season)\s*([0-9]+)",
                    RegexOptions.IgnoreCase);
                if (volMatch.Success && int.TryParse(volMatch.Groups[1].Value, out int v))
                    volumeNumber = v;

                // ---- CHAPTER NUMBER ---------------------------------------------
                string chapterNumber;
                var chMatch = Regex.Match(nodeText,
                    @"(?:ch\.?|chapter)\s*([0-9]+(?:\.[0-9]+)?)",
                    RegexOptions.IgnoreCase);
                if (chMatch.Success)
                    chapterNumber = chMatch.Groups[1].Value;
                else
                {
                    // Fallback – last numeric token in the string.
                    var numbers = Regex.Matches(nodeText, @"[0-9]+(?:\.[0-9]+)?")
                                    .Cast<Match>()
                                    .Select(m => m.Value)
                                    .ToArray();

                    if (numbers.Length == 0)
                    {
                        Log.Warn($"Unable to determine chapter number from '{nodeText}'. Skipping.");
                        continue;
                    }

                    chapterNumber = numbers.Last();
                }

                // ---- BUILD CHAPTER OBJECT ---------------------------------------
                var chapter = new Chapter(manga.Obj, chapterNumber, volumeNumber, null);

                // The ID we store is the numeric part before "-chapter-".
                // Example: 2407319-chapter-1 => "2407319"
                string idOnSite = new Uri(fullUrl).Segments.Last()
                                    .Split(new[] { '-' }, StringSplitOptions.RemoveEmptyEntries)[0];

                var mcId = new MangaConnectorId<Chapter>(chapter, this, idOnSite, fullUrl);
                chapter.MangaConnectorIds.Add(mcId);

                chapters.Add((chapter, mcId));
            }
            Log.InfoFormat("Found {0} chapters for '{1}' page '{2}'", chapters.Count, manga.Obj.Name, page);
        }
        return chapters.OrderBy(c => c.Item1, new Chapter.ChapterComparer()).ToArray();

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
        var doc    = new HtmlDocument();
        doc.LoadHtml(html);

        // Images look like: <img alt="Page 1" src="/media/manga/xxxxx.jpg">
        var imgNodes = doc.DocumentNode.SelectNodes("//img[starts-with(@alt, 'Page')]");
        if (imgNodes == null || imgNodes.Count == 0)
        {
            Log.Warn("No page images found on chapter page.");
            return [];
        }

        var urls = imgNodes
            .Select(img =>
            {
                string src = img.GetAttributeValue("src", "")
                             ?? img.GetAttributeValue("data-src", "");

                if (!string.IsNullOrEmpty(src) && !src.StartsWith("http"))
                    src = $"https://comix.to{src}";
                return src;
            })
            .Where(u => !string.IsNullOrEmpty(u))
            .ToArray();

        Log.InfoFormat("Found {0} image URLs for chapter {1}", urls.Length, chapterId.Obj);
        return urls;
    }

    #endregion
}