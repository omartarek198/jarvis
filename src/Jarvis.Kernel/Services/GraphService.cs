using System.Text.Json;
using Jarvis.Core;

namespace Jarvis.Kernel.Services;

/// <summary>
/// Microsoft Graph via OAuth device-code flow — hand-rolled (two token endpoints), no MSAL
/// dependency. Free Azure app registration; ClientId/TenantId come from appsettings
/// Jarvis:Graph. Refresh token lives in the DPAPI secret store. Until the user registers the
/// app this whole service reports "not configured" and every caller degrades silently.
/// </summary>
public class GraphService(IConfiguration config, SecretsService secrets, IEventBus bus, ExternalActions external, ILogger<GraphService> log)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private const string Scopes = "offline_access User.Read Chat.Read Presence.Read Presence.ReadWrite Calendars.Read Mail.Read";

    private readonly Lock _lock = new();
    private string? _accessToken;
    private DateTime _accessExpires = DateTime.MinValue;
    private string? _lastError;

    private string? ClientId => config["Jarvis:Graph:ClientId"];
    private string Tenant => config["Jarvis:Graph:TenantId"] ?? "organizations";
    private string TokenUrl => $"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/token";

    public bool Configured => external.Enabled && !string.IsNullOrEmpty(ClientId);
    public bool LoggedIn => Configured && secrets.Has("graph.refresh");

    /// <summary>not_configured | needs_login | ok | degraded</summary>
    public string Status =>
        !Configured ? "not_configured"
        : !LoggedIn ? "needs_login"
        : _lastError is null ? "ok" : "degraded";

    /// <summary>Begin device-code login. Returns the spoken instruction (code + url) or an error.</summary>
    public async Task<string> StartDeviceLoginAsync()
    {
        if (!Configured)
            return "Microsoft Graph isn't configured yet. Register a free Azure app and put its client id " +
                   "in appsettings under Jarvis Graph ClientId — the steps are in docs/PUBLIC-EDITION.md.";
        try
        {
            var resp = await Http.PostAsync(
                $"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/devicecode",
                new FormUrlEncodedContent(new Dictionary<string, string>
                    { ["client_id"] = ClientId!, ["scope"] = Scopes }));
            var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
            if (!resp.IsSuccessStatusCode)
                return $"Microsoft rejected the login request: {json.GetProperty("error").GetString()}";

            var deviceCode = json.GetProperty("device_code").GetString()!;
            var userCode = json.GetProperty("user_code").GetString()!;
            var url = json.GetProperty("verification_uri").GetString()!;
            var interval = json.TryGetProperty("interval", out var iv) ? iv.GetInt32() : 5;
            var expires = json.TryGetProperty("expires_in", out var ex) ? ex.GetInt32() : 900;

            _ = Task.Run(() => PollForTokenAsync(deviceCode, interval, expires));
            bus.Publish(new JarvisEvent("graph.code", new { url, code = userCode,
                spoken = $"Go to {url.Replace("https://", "")} and enter code {string.Join(" ", userCode.ToCharArray())}." }, "system"));
            bus.Publish(new JarvisEvent("clipboard.set", new { text = userCode, label = "login code" }, "system"));
            return $"To connect your Microsoft account: go to {url.Replace("https://", "")} and enter the code " +
                   $"{string.Join(", ", userCode.ToCharArray())}. I'll announce when you're in.";
        }
        catch (Exception e)
        {
            log.LogWarning("[graph] device code start failed: {Msg}", e.Message);
            return $"Couldn't reach Microsoft login: {e.Message}";
        }
    }

    private async Task PollForTokenAsync(string deviceCode, int intervalSec, int expiresSec)
    {
        var deadline = DateTime.UtcNow.AddSeconds(expiresSec);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(intervalSec));
            try
            {
                var resp = await Http.PostAsync(TokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                    ["device_code"] = deviceCode,
                    ["client_id"] = ClientId!,
                }));
                var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
                if (resp.IsSuccessStatusCode)
                {
                    StoreTokens(json);
                    var who = await GetAsync("/me");
                    var name = who?.RootElement.TryGetProperty("displayName", out var dn) == true ? dn.GetString() : "you";
                    bus.Publish(new JarvisEvent("graph.connected",
                        new { spoken = $"Microsoft account connected — hello, {name}. Teams and calendar are live." }, "system"));
                    return;
                }
                var err = json.TryGetProperty("error", out var e) ? e.GetString() : "unknown";
                if (err is "authorization_pending" or "slow_down") continue;
                log.LogWarning("[graph] device login failed: {Err}", err);
                return;
            }
            catch (Exception ex) { log.LogWarning("[graph] token poll error: {Msg}", ex.Message); }
        }
    }

    private void StoreTokens(JsonElement json)
    {
        lock (_lock)
        {
            _accessToken = json.GetProperty("access_token").GetString();
            _accessExpires = DateTime.UtcNow.AddSeconds(json.GetProperty("expires_in").GetInt32() - 120);
            if (json.TryGetProperty("refresh_token", out var rt) && rt.GetString() is { } r)
                secrets.Set("graph.refresh", r);
            _lastError = null;
        }
    }

    private async Task<string?> GetTokenAsync()
    {
        lock (_lock)
            if (_accessToken is not null && DateTime.UtcNow < _accessExpires) return _accessToken;
        if (!LoggedIn) return null;
        try
        {
            var resp = await Http.PostAsync(TokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = secrets.Get("graph.refresh")!,
                ["client_id"] = ClientId!,
                ["scope"] = Scopes,
            }));
            var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
            if (!resp.IsSuccessStatusCode)
            {
                _lastError = json.TryGetProperty("error", out var e) ? e.GetString() : "refresh failed";
                log.LogWarning("[graph] token refresh failed: {Err} (CA policy may have revoked it — say 'connect microsoft' to re-login)", _lastError);
                return null;
            }
            StoreTokens(json);
            return _accessToken;
        }
        catch (Exception ex) { _lastError = ex.Message; return null; }
    }

    /// <summary>GET graph.microsoft.com/v1.0{path}. Null on any failure — callers degrade.</summary>
    public async Task<JsonDocument?> GetAsync(string path)
    {
        var token = await GetTokenAsync();
        if (token is null) return null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://graph.microsoft.com/v1.0{path}");
            req.Headers.Authorization = new("Bearer", token);
            var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                log.LogWarning("[graph] GET {Path} -> {Status}", path, (int)resp.StatusCode);
                return null;
            }
            return JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        }
        catch (Exception ex) { log.LogWarning("[graph] GET {Path} failed: {Msg}", path, ex.Message); return null; }
    }

    public record ChatUnread(string ChatId, string Who, string Topic, string Preview, DateTime At);
    public record GraphEvent(string Subject, DateTime StartLocal, DateTime EndLocal, string Location, bool IsOnline);

    /// <summary>Chats whose last message is newer than the user's read marker.</summary>
    public async Task<List<ChatUnread>?> UnreadChatsAsync(int top = 20)
    {
        var doc = await GetAsync($"/me/chats?$expand=lastMessagePreview&$top={top}&$orderby=lastMessagePreview/createdDateTime desc");
        if (doc is null) return null;
        var unread = new List<ChatUnread>();
        if (!doc.RootElement.TryGetProperty("value", out var chats)) return unread;
        foreach (var c in chats.EnumerateArray())
        {
            if (!c.TryGetProperty("lastMessagePreview", out var prev) || prev.ValueKind != JsonValueKind.Object) continue;
            var lastMsgAt = prev.TryGetProperty("createdDateTime", out var ca) && ca.ValueKind == JsonValueKind.String
                ? ca.GetDateTime() : DateTime.MinValue;
            var readAt = c.TryGetProperty("viewpoint", out var vp) && vp.TryGetProperty("lastMessageReadDateTime", out var ra)
                && ra.ValueKind == JsonValueKind.String ? ra.GetDateTime() : DateTime.MinValue;
            if (lastMsgAt <= readAt) continue;

            var topic = c.TryGetProperty("topic", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "";
            var who = prev.TryGetProperty("from", out var fr) && fr.TryGetProperty("user", out var u)
                && u.TryGetProperty("displayName", out var dn) && dn.ValueKind == JsonValueKind.String ? dn.GetString()! : "someone";
            var body = prev.TryGetProperty("body", out var b) && b.TryGetProperty("content", out var bc)
                && bc.ValueKind == JsonValueKind.String ? bc.GetString()! : "";
            body = System.Text.RegularExpressions.Regex.Replace(body, "<.*?>", " ").Trim();
            unread.Add(new(c.GetProperty("id").GetString()!, who, topic, Truncate(body, 140), lastMsgAt));
        }
        return unread;
    }

    public async Task<List<GraphEvent>?> CalendarViewAsync(DateTime dayLocal)
    {
        var fromUtc = dayLocal.Date.ToUniversalTime();
        var toUtc = dayLocal.Date.AddDays(1).ToUniversalTime();
        var doc = await GetAsync($"/me/calendarView?startDateTime={fromUtc:O}&endDateTime={toUtc:O}&$orderby=start/dateTime&$top=15");
        if (doc is null) return null;
        var events = new List<GraphEvent>();
        if (!doc.RootElement.TryGetProperty("value", out var value)) return events;
        foreach (var e in value.EnumerateArray())
        {
            var subject = e.TryGetProperty("subject", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()! : "(no subject)";
            var start = ParseGraphTime(e, "start");
            var end = ParseGraphTime(e, "end");
            var loc = e.TryGetProperty("location", out var l) && l.TryGetProperty("displayName", out var ld)
                && ld.ValueKind == JsonValueKind.String ? ld.GetString()! : "";
            var online = e.TryGetProperty("isOnlineMeeting", out var om) && om.ValueKind == JsonValueKind.True;
            events.Add(new(subject, start, end, loc, online));
        }
        return events;
    }

    private static DateTime ParseGraphTime(JsonElement e, string prop)
    {
        if (!e.TryGetProperty(prop, out var p) || !p.TryGetProperty("dateTime", out var dt)) return DateTime.MinValue;
        var raw = DateTime.Parse(dt.GetString()!);   // Graph returns UTC when no Prefer header is sent
        return DateTime.SpecifyKind(raw, DateTimeKind.Utc).ToLocalTime();
    }

    private static string Truncate(string s, int len) => s.Length <= len ? s : s[..len] + "…";
}
