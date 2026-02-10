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

#region CHAPTER IMAGES -----------------------------------------------------

internal override string[] GetChapterImageUrls(MangaConnectorId<Chapter> chapterId)
{
    Log.InfoFormat("Fetching image URLs for chapter: {0}", chapterId.Obj);

    if (chapterId.WebsiteUrl == null)
    {
        Log.Error("Chapter URL is null – cannot continue.");
        return [];
    }

    // Keep the referrer logic you already had – some sites check it.
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
    // -------------------------------------------------------------
    // 1️⃣ Request the canonical chapter URL – it returns a JSON payload
    //    that contains a "chapter" object with an "images" array.
    // -------------------------------------------------------------
    await using var chromium = new ChromiumDownloadClient();

    HttpResponseMessage response = await chromium.MakeRequest(
        chapterId.WebsiteUrl!,
        RequestType.Default,
        referrer);

    if (!response.IsSuccessStatusCode)
    {
        Log.Error($"Failed to load chapter JSON – status {(int)response.StatusCode}");
        return [];
    }

    string body = await response.Content.ReadAsStringAsync();

    // -------------------------------------------------------------
    // 2️⃣ Try to parse the whole response as JSON. If that fails,
    //    fall back to extracting the substring that starts with
    //    "\"chapter\":{" and ends at the matching closing brace.
    // -------------------------------------------------------------
    JsonDocument doc;
    try
    {
        doc = JsonDocument.Parse(body);
    }
    catch (JsonException)
    {
        // The server sometimes wraps the JSON in a tiny HTML wrapper.
        // Find the first occurrence of "\"chapter\":{" and parse from there.
        int startIdx = body.IndexOf("\"chapter\":{", StringComparison.Ordinal);
        if (startIdx < 0)
        {
            Log.Warn("Could not locate \"chapter\" object in response.");
            return [];
        }

        // Extract a balanced JSON object for the chapter block.
        int braceDepth = 0;
        int endIdx = startIdx;
        for (int i = startIdx; i < body.Length; i++)
        {
            char c = body[i];
            if (c == '{') braceDepth++;
            else if (c == '}')
            {
                braceDepth--;
                if (braceDepth == 0)
                {
                    endIdx = i;
                    break;
                }
            }
        }

        string chapterJson = $"{{{body.Substring(startIdx, endIdx - startIdx + 1)}}}";
        doc = JsonDocument.Parse(chapterJson);
    }

    using (doc) // ensure disposal
    {
        JsonElement root = doc.RootElement;

        // The API may return:
        //   { "status":200, "result":{ "chapter":{ ... } } }
        // or directly: { "chapter":{ ... } }
        JsonElement chapterNode;
        if (root.TryGetProperty("result", out JsonElement resultNode) &&
            resultNode.TryGetProperty("chapter", out chapterNode))
        {
            // ok – we have it
        }
        else if (root.TryGetProperty("chapter", out chapterNode))
        {
            // ok – top‑level chapter object
        }
        else
        {
            Log.Warn("JSON does not contain a 'chapter' object.");
            return [];
        }

        // ---------------------------------------------------------
        // 3️⃣ Extract the images array.
        //    Each element looks like:
        //      { "width":1560, "height":1200,
        //        "url":"https://…/01.webp" }
        // ---------------------------------------------------------
        if (!chapterNode.TryGetProperty("images", out JsonElement imagesArray))
        {
            Log.Warn("'chapter' object does not contain an 'images' array.");
            return [];
        }

        var urls = new List<string>();
        foreach (JsonElement img in imagesArray.EnumerateArray())
        {
            if (img.TryGetProperty("url", out JsonElement urlEl))
            {
                string url = urlEl.GetString() ?? "";
                // The API already returns absolute URLs, but guard just in case.
                if (!string.IsNullOrWhiteSpace(url) && !url.StartsWith("http"))
                    url = $"https://comix.to{url}";
                urls.Add(url);
            }
        }

        Log.InfoFormat(
            "Found {0} image URLs for chapter {1}",
            urls.Count,
            chapterId.Obj);

        return urls.ToArray();
    }
}
#endregion


