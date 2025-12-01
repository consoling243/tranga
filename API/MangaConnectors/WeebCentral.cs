using System.Net;
using System.Text.RegularExpressions;
using System.Web;
using API.MangaDownloadClients;
using API.Schema.MangaContext;
using HtmlAgilityPack;
using static System.Text.RegularExpressions.Regex;

namespace API.MangaConnectors;

/// <summary>
/// Connector for https://weebcentral.com
/// </summary>
public class WeebCentral : MangaConnector
{

    // -----------------------------------------------------------------
    public WeebCentral() : base(
        nameof(WeebCentral),
        ["en"],
        ["weebcentral.com", "www.weebcentral.com"],
        "/favicon.png")
    {
        this.downloadClient = new HttpDownloadClient();
    }

    // -----------------------------------------------------------------
    // SEARCH
    // -----------------------------------------------------------------
    public override (Manga, MangaConnectorId<Manga>)[] SearchManga(string mangaSearchName)
    {
        foreach (string uri in BaseUris)
            if (SearchMangaWithDomain(mangaSearchName, uri) is { } result)
                return result;

        return [];
    }

    private (Manga, MangaConnectorId<Manga>)[]? SearchMangaWithDomain(string mangaSearchName, string domain)
    {
        Log.DebugFormat("Using domain {0}", domain);
        Uri baseUri = new($"https://{domain}/");

        List<(Manga, MangaConnectorId<Manga>)> ret = [];

        for (int page = 1; ; page++)
        {
            Uri searchUri = new(
                baseUri,
                $"search?word={HttpUtility.UrlEncode(mangaSearchName)}&lang={Tranga.Settings.DownloadLanguage}&page={page}");

            if (downloadClient.MakeRequest(searchUri.ToString(), RequestType.Default).Result
                is { StatusCode: >= HttpStatusCode.OK and < HttpStatusCode.Ambiguous } result)
            {
                HtmlDocument document = result.CreateDocument();

                // No results → break out of the paging loop.
                if (document.DocumentNode.SelectSingleNode("//button[contains(text(),\"No Data\")]") != null)
                    break;

                if (document.GetNodesWith("q4_9") is not { Count: > 0 } resultNodes)
                    return [];

                IEnumerable<string> urls = resultNodes
                    .Select(node => node.SelectSingleNode("//a[contains(@href,'title')]")
                                         ?.Attributes["href"]?.Value)
                    .Where(u => u != null)!
                    .Distinct();

                ret.AddRange(urls.Select(link =>
                    ((Manga, MangaConnectorId<Manga>))
                        GetMangaFromUrl(new Uri(baseUri, link).ToString())!));
            }
            else
                return null; // request failed → abort search for this domain
        }

        // Remove duplicates (same manga can appear on several pages)
        return ret.DistinctBy(r => r.Item1.Key).ToArray();
    }

    // -----------------------------------------------------------------
    // GET MANGA BY ID / URL
    // -----------------------------------------------------------------
    public override (Manga, MangaConnectorId<Manga>)? GetMangaFromId(string mangaIdOnSite)
    {
        foreach (string uri in BaseUris)
            if (GetMangaFromIdWithDomain(mangaIdOnSite, uri) is { } result)
                return result;

        return null;
    }

    private (Manga, MangaConnectorId<Manga>)? GetMangaFromIdWithDomain(string mangaIdOnSite, string domain)
    {
        Log.DebugFormat("Using domain {0}", domain);
        Uri baseUri = new($"https://{domain}/");
        return GetMangaFromUrl(new Uri(baseUri, $"title/{mangaIdOnSite}").ToString());
    }

    // Regex that extracts the “clean” url (without extra slug) and captures
    //   1 – whole base part,
    //   2 – domain part,
    //   3 – manga id,
    //   4 – optional chapter id.
    private readonly Regex _urlRex = new(@"((.*)\/title\/(\d*))(?:[^\/]*\/(\d*))?.*");

    public override (Manga, MangaConnectorId<Manga>)? GetMangaFromUrl(string url)
    {
        // -----------------------------------------------------------------
        // 1️⃣ Normalise the URL – we only need the “/title/<id>” part.
        // -----------------------------------------------------------------
        if (_urlRex.Match(url) is not { Success: true } matchedUrl ||
            matchedUrl.Groups[1] is not { Success: true } cleanedUrl)
            return null;

        // -----------------------------------------------------------------
        // 2️⃣ Download the manga page
        // -----------------------------------------------------------------
        if (downloadClient.MakeRequest(cleanedUrl.Value, RequestType.Default).Result
            is { StatusCode: >= HttpStatusCode.OK and < HttpStatusCode.Ambiguous } result)
        {
            HtmlDocument document = result.CreateDocument();

            // ------------------- BASIC INFO -------------------
            if (document.GetNodeWith("q1_1")?.GetAttributeValue("title", string.Empty) is not
                { Length: > 0 } name)
            {
                Log.Debug("Name not found.");
                return null;
            }

            name = HttpUtility.HtmlDecode(name);
            string description = HttpUtility.HtmlDecode(document.GetNodeWith("0a_9")?.InnerText ?? string.Empty);

            if (document.GetNodeWith("q1_1")?.GetAttributeValue("src", string.Empty) is not
                { Length: > 0 } coverRelative)
            {
                Log.Debug("Cover not found.");
                return null;
            }

            // Build absolute cover URL
            if (matchedUrl.Groups[2] is not { Success: true } baseUrl)
                return null;

            string coverUrl = $"{baseUrl.Value}{coverRelative}";

            // ------------------- RELEASE STATUS -------------------
            MangaReleaseStatus releaseStatus = document.GetNodeWith("Yn_5")?.InnerText.ToLower() switch
            {
                "pending"   => MangaReleaseStatus.Unreleased,
                "ongoing"   => MangaReleaseStatus.Continuing,
                "completed" => MangaReleaseStatus.Completed,
                "hiatus"    => MangaReleaseStatus.OnHiatus,
                "cancelled" => MangaReleaseStatus.Cancelled,
                _           => MangaReleaseStatus.Unreleased
            };

            // ------------------- AUTHORS -------------------
            ICollection<Author> authors = document.GetNodeWith("tz_4")?
                .ChildNodes.Where(n => n.Name == "a")
                .Select(n => HttpUtility.HtmlDecode(n.InnerText))
                .Select(t => new Author(t))
                .ToList() ?? [];

            // ------------------- TAGS -------------------
            ICollection<MangaTag> mangaTags = document.GetNodesWith("kd_0")?
                .SelectMany(n =>
                {
                    string text = HttpUtility.HtmlDecode(n.InnerText);
                    return text.Split('•').Select(t => t.Trim());
                })
                .Select(t => new MangaTag(t))
                .ToList() ?? [];

            // ------------------- ALT‑TITLES -------------------
            ICollection<AltTitle> altTitles = document.GetNodeWith("tz_2")?
                .ChildNodes.Where(n => n.InnerText.Trim().Length > 1)
                ?.SelectMany(n =>
                {
                    string text = HttpUtility.HtmlDecode(n.InnerText);
                    return text.Split('•').Select(t => t.Trim());
                })
                .Select(t => new AltTitle(string.Empty, t))
                .ToList() ?? [];

            // ------------------- LINKS -------------------
            ICollection<Link> links = [];

            // ------------------- MANGA OBJECT -------------------
            if (matchedUrl.Groups[3] is not { Success: true } idMatch)
                return null;

            Manga manga = new(name, description, coverUrl, releaseStatus,
                              authors, mangaTags, links, altTitles);

            MangaConnectorId<Manga> mcId =
                new(manga, this, idMatch.Value, cleanedUrl.Value);

            manga.MangaConnectorIds.Add(mcId);
            return (manga, mcId);
        }

        // request failed
        return null;
    }

    // -----------------------------------------------------------------
    // GET CHAPTER LIST
    // -----------------------------------------------------------------
    public override (Chapter, MangaConnectorId<Chapter>)[] GetChapters(MangaConnectorId<Manga> mangaId,
                                                                      string? language = null)
    {
        foreach (string uri in BaseUris)
            if (GetChaptersFromDomain(mangaId, uri) is { } result)
                return result;

        return [];
    }

    private (Chapter, MangaConnectorId<Chapter>)[]? GetChaptersFromDomain(MangaConnectorId<Manga> mangaId,
                                                                          string domain)
    {
        Log.DebugFormat("Using domain {0}", domain);
        Uri baseUri = new($"https://{domain}/");
        Uri requestUri = new(baseUri, $"title/{mangaId.IdOnConnectorSite}");

        List<(Chapter, MangaConnectorId<Chapter>)> ret = [];

        if (downloadClient.MakeRequest(requestUri.ToString(), RequestType.Default).Result
            is { StatusCode: >= HttpStatusCode.OK and < HttpStatusCode.Ambiguous } result)
        {
            HtmlDocument document = result.CreateDocument();

            if (document.GetNodesWith("8t_8") is not { } chapterNodes)
            {
                Log.Debug("No chapters found.");
                return null;
            }

            foreach (HtmlNode chapterNode in chapterNodes)
            {
                if (ParseChapter(mangaId.Obj, chapterNode, baseUri) is { } ch)
                    ret.Add(ch);
            }
        }
        else
            return null;

        return ret.ToArray();
    }

    // -----------------------------------------------------------------
    // PARSE SINGLE CHAPTER NODE
    // -----------------------------------------------------------------
    private static readonly Regex VolChTitleRex =
        new(@"(?:.*(?:Vol\.?(?:ume)?)\s*([0-9]+))?.*(?:Ch\.?(?:apter)?)\s*((?:\d+\.)*[0-9]+)\s*(?::|-\s+(.*))?");

    private (Chapter, MangaConnectorId<Chapter>)? ParseChapter(Manga manga,
                                                               HtmlNode chapterNode,
                                                               Uri baseUri)
    {
        // ----- link & title -----
        HtmlNode linkNode = chapterNode.SelectSingleNode("./div[1]/a");
        string linkText = HttpUtility.HtmlDecode(linkNode.InnerText);
        Match linkMatch = VolChTitleRex.Match(linkText);

        HtmlNode? titleNode = chapterNode.SelectSingleNode("./div[1]/span");

        // ----- chapter / volume numbers -----
        string chapterNumber;
        int? volumeNumber = null;

        if (!linkMatch.Success || !linkMatch.Groups[2].Success)
        {
            Log.DebugFormat("Not in standard Volume/Chapter format: {0}", linkText);
            if (Match(linkText, @"[^\d]*((?:\d+\.)*\d+)[^\d]*") is not { Success: true } fallbackMatch)
                return null;

            chapterNumber = fallbackMatch.Groups[1].Value;
        }
        else
        {
            chapterNumber = linkMatch.Groups[2].Value;
            volumeNumber = linkMatch.Groups[1].Success ? int.Parse(linkMatch.Groups[1].Value) : null;
        }

        // ----- optional title -----
        string? title = titleNode is not null
            ? HttpUtility.HtmlDecode(titleNode.InnerText)[2..]   // remove leading “- ” that WeebCentral adds
            : (linkMatch.Groups[3].Success ? linkMatch.Groups[3].Value : null);

        // ----- build IDs -----
        string url = new Uri(baseUri, linkNode.GetAttributeValue("href", "")).ToString();

        if (_urlRex.Match(url) is not { Success: true } matchedUrl)
            return null;

        if (matchedUrl.Groups[3] is not { Success: true } mangaMatch ||
            matchedUrl.Groups[4] is not { Success: true } chapterMatch)
            return null;

        string id = $"{mangaMatch.Value}/{chapterMatch.Value}";

        Chapter chapter = new(manga, chapterNumber, volumeNumber, title);
        MangaConnectorId<Chapter> chId = new(
            chapter,
            this,
            id,
            // we keep the same “pretty” ID format that MangaPark uses – it works for WeebCentral as well.
            string.Join('/', matchedUrl.Groups[2].Value, nameof(title), matchedUrl.Groups[3].Value, matchedUrl.Groups[4].Value));

        chapter.MangaConnectorIds.Add(chId);
        return (chapter, chId);
    }

    // -----------------------------------------------------------------
    // GET IMAGE URLs FOR ONE CHAPTER
    // -----------------------------------------------------------------
    internal override string[] GetChapterImageUrls(MangaConnectorId<Chapter> chapterId)
    {
        foreach (string uri in BaseUris)
            if (GetChapterImageUrlsFromDomain(chapterId, uri) is { } result)
                return result;

        return [];
    }

    private string[]? GetChapterImageUrlsFromDomain(MangaConnectorId<Chapter> chapterId,
                                                    string domain)
    {
        Log.DebugFormat("Using domain {0}", domain);
        Uri baseUri = new($"https://{domain}/");
        // The page that contains the JSON with image URLs is the same “title/<chapter‑id>”.
        Uri requestUri = new(baseUri, $"title/{chapterId.IdOnConnectorSite}");

        if (downloadClient.MakeRequest(requestUri.ToString(), RequestType.Default).Result
            is { StatusCode: >= HttpStatusCode.OK and < HttpStatusCode.Ambiguous } result)
        {
            HtmlDocument document = result.CreateDocument();

            // WeebCentral embeds a `<script type="qwik/json">` block that contains an array of image URLs.
            if (document.DocumentNode
                .SelectSingleNode("//script[@type='qwik/json']")?.InnerText is not { } imageJson)
            {
                Log.Debug("No images found.");
                return null;
            }

            // Extract all quoted URLs – the same regex works for both sites.
            MatchCollection matchCollection = Matches(imageJson,
                                                       @"""https?:\/\/[\da-zA-Z\.]+\/[^,]*\.[a-z]+""");

            return matchCollection.Select(m => m.Value.Trim('"')).ToArray();
        }

        return null;
    }
}

// ---------------------------------------------------------------------
// Helper extensions
// ---------------------------------------------------------------------
internal static class WeebCentralHelper
{
    internal static HtmlDocument CreateDocument(this HttpResponseMessage result)
    {
        HtmlDocument document = new();
        using StreamReader sr = new(result.Content.ReadAsStream());
        // Both sites use the same “q:key” → “qkey” hack.
        string htmlStr = sr.ReadToEnd().Replace("q:key", "qkey");
        document.LoadHtml(htmlStr);
        return document;
    }

    //internal static HtmlNode? GetNodeWith(this HtmlDocument document, string search) =>
    //    document.DocumentNode.SelectSingleNode("/html").GetNodeWith(search);

    internal static HtmlNode? GetNodeWith(this HtmlNode node, string search) =>
        node.SelectNodes($"{node.XPath}//*[@qkey='{search}']")?.FirstOrDefault();

    //internal static HtmlNodeCollection? GetNodesWith(this HtmlDocument document, string search) =>
    //    document.DocumentNode.SelectSingleNode("/html ").GetNodesWith(search);

    // ReSharper disable once ReturnTypeCanBeNotNullable HAP nullable
    internal static HtmlNodeCollection? GetNodesWith(this HtmlNode node, string search) =>
        node.SelectNodes($"{node.XPath}//*[@qkey='{search}']");
}
