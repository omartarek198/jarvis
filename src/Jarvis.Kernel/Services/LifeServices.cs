using System.Text.Json;
using System.Xml.Linq;
using Jarvis.Core;

namespace Jarvis.Kernel.Services;

internal static class LifeHttp
{
    /// <summary>Some free APIs (CoinGecko notably) reject requests without a User-Agent.</summary>
    public static HttpClient Create(int timeoutSeconds = 10)
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("JARVIS-local/1.0");
        return c;
    }
}

/// <summary>Open-Meteo: free, keyless weather + geocoding.</summary>
public class WeatherService(IServiceScopeFactory scopes, ILogger<WeatherService> log)
{
    private static readonly HttpClient Http = LifeHttp.Create();
    private readonly Dictionary<string, (double Lat, double Lon, string Name)> _geoCache = new(StringComparer.OrdinalIgnoreCase);
    private (string Key, string Spoken, DateTime At)? _lastForecast; // 15-min cache

    private static readonly Dictionary<int, string> WmoCodes = new()
    {
        [0] = "clear skies", [1] = "mostly clear", [2] = "partly cloudy", [3] = "overcast",
        [45] = "fog", [48] = "freezing fog", [51] = "light drizzle", [53] = "drizzle", [55] = "heavy drizzle",
        [61] = "light rain", [63] = "rain", [65] = "heavy rain", [71] = "light snow", [73] = "snow", [75] = "heavy snow",
        [80] = "rain showers", [81] = "rain showers", [82] = "violent rain showers",
        [95] = "thunderstorms", [96] = "thunderstorms with hail", [99] = "severe thunderstorms",
    };

    public async Task<(double Lat, double Lon, string Name)?> GeocodeAsync(string place)
    {
        if (_geoCache.TryGetValue(place, out var hit)) return hit;
        try
        {
            var url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(place)}&count=1&language=en";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0) return null;
            var r = results[0];
            var geo = (r.GetProperty("latitude").GetDouble(), r.GetProperty("longitude").GetDouble(),
                       r.GetProperty("name").GetString() ?? place);
            _geoCache[place] = geo;
            return geo;
        }
        catch (Exception ex) { log.LogWarning("geocode failed: {Msg}", ex.Message); return null; }
    }

    public async Task<(double Lat, double Lon, string Name)> HomeAsync()
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JarvisDb>();
        var s = await db.Settings.FindAsync("weather.home");
        if (s is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(s.ValueJson);
                return (doc.RootElement.GetProperty("lat").GetDouble(),
                        doc.RootElement.GetProperty("lon").GetDouble(),
                        doc.RootElement.GetProperty("name").GetString() ?? "home");
            }
            catch { }
        }
        return (51.507, -0.128, "London");   // neutral fallback; set weather.home to your own city
    }

    /// <summary>Spoken forecast. when: today | tomorrow | week.</summary>
    public async Task<string?> SpokenForecastAsync(string? place, string when)
    {
        (double Lat, double Lon, string Name) geo;
        if (string.IsNullOrWhiteSpace(place)) geo = await HomeAsync();
        else
        {
            var g = await GeocodeAsync(place);
            if (g is null) return $"I couldn't find a place called {place}.";
            geo = g.Value;
        }

        var cacheKey = $"{geo.Name}|{when}";
        if (_lastForecast is { } c && c.Key == cacheKey && DateTime.UtcNow - c.At < TimeSpan.FromMinutes(15))
            return c.Spoken;

        try
        {
            var url = $"https://api.open-meteo.com/v1/forecast?latitude={geo.Lat:F3}&longitude={geo.Lon:F3}" +
                      "&current=temperature_2m,apparent_temperature,weather_code,wind_speed_10m" +
                      "&daily=temperature_2m_max,temperature_2m_min,precipitation_probability_max,weather_code" +
                      "&timezone=auto&forecast_days=3";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            var root = doc.RootElement;
            var cur = root.GetProperty("current");
            var daily = root.GetProperty("daily");
            string Code(JsonElement e) => WmoCodes.TryGetValue(e.GetInt32(), out var t) ? t : "mixed conditions";

            string spoken;
            if (when == "tomorrow")
            {
                spoken = $"Tomorrow in {geo.Name}: {Code(daily.GetProperty("weather_code")[1])}, " +
                         $"{daily.GetProperty("temperature_2m_min")[1].GetDouble():F0} to {daily.GetProperty("temperature_2m_max")[1].GetDouble():F0} degrees, " +
                         $"{daily.GetProperty("precipitation_probability_max")[1].GetInt32()} percent chance of rain.";
            }
            else
            {
                spoken = $"{geo.Name} right now: {cur.GetProperty("temperature_2m").GetDouble():F0} degrees, {Code(cur.GetProperty("weather_code"))}, " +
                         $"feels like {cur.GetProperty("apparent_temperature").GetDouble():F0}. " +
                         $"Today {daily.GetProperty("temperature_2m_min")[0].GetDouble():F0} to {daily.GetProperty("temperature_2m_max")[0].GetDouble():F0} degrees, " +
                         $"{daily.GetProperty("precipitation_probability_max")[0].GetInt32()} percent chance of rain.";
            }
            _lastForecast = (cacheKey, spoken, DateTime.UtcNow);
            return spoken;
        }
        catch (Exception ex)
        {
            log.LogWarning("forecast failed: {Msg}", ex.Message);
            return null;
        }
    }
}

/// <summary>CoinGecko simple price — free, keyless, read-only. No trading, ever.</summary>
public class CryptoService(IServiceScopeFactory scopes, ILogger<CryptoService> log)
{
    private static readonly HttpClient Http = LifeHttp.Create();
    private static readonly Dictionary<string, string> Ids = new(StringComparer.OrdinalIgnoreCase)
    {
        ["btc"] = "bitcoin", ["bitcoin"] = "bitcoin", ["eth"] = "ethereum", ["ethereum"] = "ethereum",
        ["sol"] = "solana", ["solana"] = "solana", ["bnb"] = "binancecoin", ["xrp"] = "ripple",
        ["doge"] = "dogecoin", ["dogecoin"] = "dogecoin", ["ada"] = "cardano", ["cardano"] = "cardano",
        ["ton"] = "the-open-network", ["dot"] = "polkadot", ["link"] = "chainlink",
    };

    public async Task<Dictionary<string, double>?> PricesAsync(IEnumerable<string> coins)
    {
        var ids = coins.Select(c => Ids.GetValueOrDefault(c.Trim(), c.Trim().ToLowerInvariant())).Distinct().ToArray();
        if (ids.Length == 0) return null;
        try
        {
            var url = $"https://api.coingecko.com/api/v3/simple/price?ids={string.Join(',', ids)}&vs_currencies=usd";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Value.TryGetProperty("usd", out var usd)) result[p.Name] = usd.GetDouble();
            return result.Count > 0 ? result : null;
        }
        catch (Exception ex) { log.LogWarning("crypto prices failed: {Msg}", ex.Message); return null; }
    }

    public async Task<string[]> WatchlistAsync()
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JarvisDb>();
        var s = await db.Settings.FindAsync("crypto.watchlist");
        try { return JsonSerializer.Deserialize<string[]>(s?.ValueJson ?? "[]") ?? []; }
        catch { return []; }
    }

    public static string SpeakPrice(double usd) =>
        usd >= 1000 ? $"{usd:N0} dollars" : usd >= 1 ? $"{usd:F2} dollars" : $"{usd:F4} dollars";
}

/// <summary>RSS headlines from configured feeds — free, no accounts.</summary>
public class NewsService(IServiceScopeFactory scopes, ILogger<NewsService> log)
{
    private static readonly HttpClient Http = LifeHttp.Create(8);

    public async Task<List<(string Title, string Source)>> HeadlinesAsync(int max = 5)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JarvisDb>();
        var s = await db.Settings.FindAsync("news.feeds");
        string[] feeds;
        try { feeds = JsonSerializer.Deserialize<string[]>(s?.ValueJson ?? "[]") ?? []; }
        catch { feeds = []; }

        var headlines = new List<(string, string)>();
        foreach (var feed in feeds)
        {
            try
            {
                var xml = XDocument.Parse(await Http.GetStringAsync(feed));
                var channelTitle = xml.Descendants("channel").Elements("title").FirstOrDefault()?.Value ?? "news";
                foreach (var item in xml.Descendants("item").Take(3))
                {
                    var title = item.Element("title")?.Value?.Trim();
                    if (!string.IsNullOrWhiteSpace(title)) headlines.Add((title, channelTitle));
                }
            }
            catch (Exception ex) { log.LogWarning("rss {Feed} failed: {Msg}", feed, ex.Message); }
            if (headlines.Count >= max) break;
        }
        return [.. headlines.Take(max)];
    }

    /// <summary>Topic news via Google News RSS search — free, keyless, fresh.</summary>
    public async Task<List<(string Title, string Source, string Link)>> TopicAsync(string topic, int max = 5)
    {
        try
        {
            var url = $"https://news.google.com/rss/search?q={Uri.EscapeDataString(topic)}&hl=en-US&gl=US&ceid=US:en";
            var xml = XDocument.Parse(await Http.GetStringAsync(url));
            var items = new List<(string, string, string)>();
            foreach (var item in xml.Descendants("item").Take(max))
            {
                var title = item.Element("title")?.Value?.Trim() ?? "";
                var link = item.Element("link")?.Value?.Trim() ?? "";
                var source = item.Element("source")?.Value?.Trim() ?? "";
                // Google News titles end with " - Source"; strip when we have the source separately
                if (source.Length > 0 && title.EndsWith(" - " + source)) title = title[..^(source.Length + 3)];
                if (title.Length > 0) items.Add((title, source, link));
            }
            return items;
        }
        catch (Exception ex)
        {
            log.LogWarning("topic news failed: {Msg}", ex.Message);
            return [];
        }
    }
}
