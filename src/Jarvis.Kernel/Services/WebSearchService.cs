using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jarvis.Kernel.Services;

public record WebHit(string Title, string Snippet, string Url);
public record ImageHit(string Title, string Thumbnail, string Url);

/// <summary>
/// Free web search via DuckDuckGo's unofficial endpoints (no key, no cost). The HTML scrape
/// can break when DDG changes markup — all selectors live here, and every failure degrades
/// to an empty list, never an exception.
/// </summary>
public class WebSearchService(ILogger<WebSearchService> log)
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        // Full browser UA: Bing serves the image-grid markup (with m="{json}" tiles) only to browsers
        c.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
        return c;
    }

    public async Task<List<WebHit>> SearchAsync(string query, int take = 4)
    {
        try
        {
            var html = await Http.GetStringAsync($"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(query)}");
            var titles = Regex.Matches(html, """<a[^>]*class="result__a"[^>]*href="(?<href>[^"]+)"[^>]*>(?<title>.+?)</a>""",
                RegexOptions.Singleline);
            var snippets = Regex.Matches(html, """<a[^>]*class="result__snippet"[^>]*>(?<snip>.+?)</a>""",
                RegexOptions.Singleline);
            var hits = new List<WebHit>();
            for (var i = 0; i < titles.Count && hits.Count < take; i++)
            {
                var title = StripHtml(titles[i].Groups["title"].Value);
                var url = DecodeDdgHref(titles[i].Groups["href"].Value);
                var snip = i < snippets.Count ? StripHtml(snippets[i].Groups["snip"].Value) : "";
                if (title.Length > 0) hits.Add(new(title, snip, url));
            }
            return hits;
        }
        catch (Exception ex)
        {
            log.LogWarning("web search failed: {Msg}", ex.Message);
            return [];
        }
    }

    public async Task<List<ImageHit>> ImageSearchAsync(string query, int take = 6)
    {
        try
        {
            // Openverse (openverse.org): official free image API, no key needed.
            // (DDG i.js 403s bots and Bing's grid is JS-rendered — both scrapes proved dead ends.)
            var json = await Http.GetStringAsync(
                $"https://api.openverse.org/v1/images/?q={Uri.EscapeDataString(query)}&page_size={take}");
            using var doc = JsonDocument.Parse(json);
            var hits = new List<ImageHit>();
            if (doc.RootElement.TryGetProperty("results", out var results))
                foreach (var r in results.EnumerateArray())
                {
                    if (hits.Count >= take) break;
                    var url = r.TryGetProperty("url", out var u) ? u.GetString() : null;
                    var thumb = r.TryGetProperty("thumbnail", out var t) && t.ValueKind == JsonValueKind.String
                        ? t.GetString() : url;
                    var title = r.TryGetProperty("title", out var ti) && ti.ValueKind == JsonValueKind.String
                        ? ti.GetString() : "";
                    var landing = r.TryGetProperty("foreign_landing_url", out var fl) && fl.ValueKind == JsonValueKind.String
                        ? fl.GetString() : url;
                    if (!string.IsNullOrEmpty(thumb))
                        hits.Add(new(title ?? "", thumb, landing ?? thumb!));
                }
            return hits;
        }
        catch (Exception ex)
        {
            log.LogWarning("image search failed: {Msg}", ex.Message);
            return [];
        }
    }

    private static string StripHtml(string s) =>
        System.Net.WebUtility.HtmlDecode(Regex.Replace(s, "<.*?>", "")).Trim();

    /// <summary>DDG result hrefs are redirect links: //duckduckgo.com/l/?uddg=&lt;encoded&gt;&amp;...</summary>
    private static string DecodeDdgHref(string href)
    {
        var m = Regex.Match(href, @"uddg=(?<u>[^&]+)");
        return m.Success ? Uri.UnescapeDataString(m.Groups["u"].Value) : href;
    }
}
