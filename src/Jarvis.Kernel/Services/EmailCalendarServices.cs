using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using Jarvis.Core;

namespace Jarvis.Kernel.Services;

/// <summary>
/// Outbound email over the user's own SMTP account (Gmail app password in the DPAPI store).
/// Every send sits behind the Mutate→confirm gate — JARVIS never emails silently.
/// </summary>
public class EmailService(SecretsService secrets, IEventBus bus, ExternalActions external, IConfiguration config, ILogger<EmailService> log)
{
    /// <summary>Where "email me ..." commands deliver. Set Jarvis:Email:DefaultRecipient locally.</summary>
    public string DefaultRecipient => config["Jarvis:Email:DefaultRecipient"] is { Length: > 0 } r ? r : "the configured owner address";
    private string Host => secrets.Get("smtp.host") ?? "smtp.gmail.com";
    private int Port => int.TryParse(secrets.Get("smtp.port"), out var p) ? p : 587;

    public bool Configured => external.Enabled && secrets.Has("smtp.user") && secrets.Has("smtp.password");

    /// <summary>Connect + authenticate + disconnect. No mail is sent.</summary>
    public async Task<(bool Ok, string Detail)> VerifyAsync()
    {
        if (!Configured) return (false, "smtp.user / smtp.password not in the secret store");
        try
        {
            using var client = new SmtpClient();
            await client.ConnectAsync(Host, Port, SecureSocketOptions.StartTls);
            await client.AuthenticateAsync(secrets.Get("smtp.user") ?? "", secrets.Get("smtp.password") ?? "");
            await client.DisconnectAsync(true);
            return (true, $"authenticated against {Host}:{Port}");
        }
        catch (Exception ex)
        {
            log.LogWarning("[email] verify failed: {Type}", ex.GetType().Name);
            return (false, ex.Message);
        }
    }

    public async Task<(bool Ok, string Detail)> SendAsync(string subject, string bodyMarkdown, string? to = null)
    {
        if (!external.Enabled) return (false, "outbound email is disabled (Jarvis:ExternalActions:Enabled=false)");
        if (!Configured) return (false, "email isn't configured — add smtp credentials to the secret store");
        if (to is null && string.IsNullOrEmpty(config["Jarvis:Email:DefaultRecipient"]))
            return (false, "no recipient — set Jarvis:Email:DefaultRecipient");
        try
        {
            var msg = new MimeMessage();
            msg.From.Add(new MailboxAddress("JARVIS", secrets.Get("smtp.user") ?? ""));
            msg.To.Add(MailboxAddress.Parse(to ?? DefaultRecipient));
            msg.Subject = subject;
            msg.Body = new BodyBuilder { TextBody = bodyMarkdown }.ToMessageBody();

            using var client = new SmtpClient();
            await client.ConnectAsync(Host, Port, SecureSocketOptions.StartTls);
            await client.AuthenticateAsync(secrets.Get("smtp.user") ?? "", secrets.Get("smtp.password") ?? "");
            await client.SendAsync(msg);
            await client.DisconnectAsync(true);
            bus.Publish(new JarvisEvent("email.sent", new { subject, to = to ?? DefaultRecipient }, "system"));
            return (true, $"sent to {to ?? DefaultRecipient}");
        }
        catch (Exception ex)
        {
            log.LogWarning("[email] send failed: {Type}: {Msg}", ex.GetType().Name, ex.Message);
            return (false, ex.Message);
        }
    }
}

/// <summary>
/// Inbound email over IMAP (same credentials as SMTP — Gmail needs an app password, which
/// is still missing as of P2; everything here degrades to a clear spoken explanation).
/// Reading is free-tier-safe; archive/flag are Mutate-gated at the module level.
/// </summary>
public class ImapEmailService(SecretsService secrets, ExternalActions external, ILogger<ImapEmailService> log)
{
    public record MailSummary(uint Uid, string From, string FromAddress, string Subject, DateTimeOffset Date);

    private string Host => secrets.Get("imap.host") ?? "imap.gmail.com";
    private int Port => int.TryParse(secrets.Get("imap.port"), out var p) ? p : 993;
    public bool Configured => external.Enabled && secrets.Has("smtp.user") && secrets.Has("smtp.password");

    private async Task<ImapClient> ConnectAsync()
    {
        var client = new ImapClient();
        await client.ConnectAsync(Host, Port, SecureSocketOptions.SslOnConnect);
        await client.AuthenticateAsync(secrets.Get("smtp.user") ?? "", secrets.Get("smtp.password") ?? "");
        return client;
    }

    public async Task<(List<MailSummary>? Mails, string? Error)> UnreadAsync(int take = 5)
    {
        if (!Configured) return (null, "email credentials aren't in the secret store yet");
        try
        {
            using var client = await ConnectAsync();
            var inbox = client.Inbox;
            await inbox.OpenAsync(FolderAccess.ReadOnly);
            var uids = await inbox.SearchAsync(SearchQuery.NotSeen);
            var newest = uids.Reverse().Take(take).ToList();
            var result = new List<MailSummary>();
            foreach (var uid in newest)
            {
                var msg = await inbox.GetMessageAsync(uid);
                var from = msg.From.Mailboxes.FirstOrDefault();
                result.Add(new(uid.Id, from?.Name is { Length: > 0 } n ? n : from?.Address ?? "unknown",
                    from?.Address ?? "", msg.Subject ?? "(no subject)", msg.Date));
            }
            await client.DisconnectAsync(true);
            return (result, null);
        }
        catch (Exception ex)
        {
            log.LogWarning("[imap] unread failed: {Type}: {Msg}", ex.GetType().Name, ex.Message);
            var hint = ex.Message.Contains("InvalidSecondFactor") || ex.Message.Contains("Username and Password not accepted")
                ? " Gmail needs an app password — create one at myaccount.google.com/apppasswords."
                : "";
            return (null, ex.Message + hint);
        }
    }

    public async Task<string?> BodyAsync(uint uid, int cap = 600)
    {
        if (!Configured) return null;
        try
        {
            using var client = await ConnectAsync();
            await client.Inbox.OpenAsync(FolderAccess.ReadOnly);
            var msg = await client.Inbox.GetMessageAsync(new UniqueId(uid));
            await client.DisconnectAsync(true);
            var text = msg.TextBody ?? HtmlToText(msg.HtmlBody ?? "");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
            return text.Length <= cap ? text : text[..cap] + "… message truncated.";
        }
        catch (Exception ex) { log.LogWarning("[imap] body failed: {Msg}", ex.Message); return null; }
    }

    /// <summary>Gmail: moving out of INBOX archives; otherwise use an Archive folder if present.</summary>
    public async Task<bool> ArchiveAsync(uint uid)
    {
        if (!Configured) return false;
        try
        {
            using var client = await ConnectAsync();
            var inbox = client.Inbox;
            await inbox.OpenAsync(FolderAccess.ReadWrite);
            var id = new UniqueId(uid);
            IMailFolder? target = null;
            try { target = await client.GetFolderAsync("[Gmail]/All Mail"); } catch { }
            if (target is null) try { target = await client.GetFolderAsync("Archive"); } catch { }
            if (target is not null) await inbox.MoveToAsync(id, target);
            else await inbox.AddFlagsAsync(id, MessageFlags.Seen, true);   // best effort: mark read
            await client.DisconnectAsync(true);
            return true;
        }
        catch (Exception ex) { log.LogWarning("[imap] archive failed: {Msg}", ex.Message); return false; }
    }

    public async Task<bool> FlagAsync(uint uid)
    {
        if (!Configured) return false;
        try
        {
            using var client = await ConnectAsync();
            await client.Inbox.OpenAsync(FolderAccess.ReadWrite);
            await client.Inbox.AddFlagsAsync(new UniqueId(uid), MessageFlags.Flagged, true);
            await client.DisconnectAsync(true);
            return true;
        }
        catch (Exception ex) { log.LogWarning("[imap] flag failed: {Msg}", ex.Message); return false; }
    }

    private static string HtmlToText(string html) =>
        System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(
            System.Text.RegularExpressions.Regex.Replace(html, "<(style|script)[^>]*>.*?</\\1>", " ",
                System.Text.RegularExpressions.RegexOptions.Singleline),
            "<.*?>", " "));
}

/// <summary>
/// Proactive inbox awareness: every 2 minutes, announce new unread mail from VIP senders
/// (Person.IsVip name match or address listed in Setting email.vips). Sender + subject only —
/// bodies are never spoken proactively.
/// </summary>
public class EmailPoller(ImapEmailService imap, IEventBus bus, IServiceScopeFactory scopes,
    ILogger<EmailPoller> log) : BackgroundService
{
    private readonly HashSet<uint> _announced = [];

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(45), ct);
        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(); }
            catch (Exception ex) { log.LogWarning("[email-poller] tick failed: {Msg}", ex.Message); }
            await Task.Delay(TimeSpan.FromMinutes(2), ct);
        }
    }

    private async Task TickAsync()
    {
        if (!imap.Configured) return;
        var (mails, _) = await imap.UnreadAsync(take: 10);
        if (mails is null || mails.Count == 0) return;

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JarvisDb>();
        var vipNames = await db.People.AsNoTracking().Where(p => p.IsVip).Select(p => p.Name).ToListAsync();
        var vipAddresses = ((await db.Settings.FindAsync("email.vips"))?.ValueJson.Trim('"') ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var m in mails)
        {
            if (!_announced.Add(m.Uid)) continue;
            var isVip = vipNames.Any(v => m.From.Contains(v, StringComparison.OrdinalIgnoreCase))
                     || vipAddresses.Any(a => m.FromAddress.Equals(a, StringComparison.OrdinalIgnoreCase));
            if (!isVip) continue;
            bus.Publish(new JarvisEvent("email.vip", new
            {
                uid = m.Uid, from = m.From,
                spoken = $"VIP email from {m.From}: {m.Subject}.",
            }, "scheduler"));
        }
        if (_announced.Count > 1000) _announced.Clear();
    }
}

/// <summary>
/// Read-only calendar via classic Outlook desktop COM (late-bound — no interop assembly).
/// Absent/new Outlook → returns null and every caller degrades silently.
/// </summary>
public class CalendarService(ExternalActions external, ILogger<CalendarService> log)
{
    public record CalEvent(string Subject, DateTime Start, DateTime End, string Location);

    // COM activation can launch Outlook on the desktop, so it counts as an external action.
    public bool Available => external.Enabled && Type.GetTypeFromProgID("Outlook.Application") is not null;

    public List<CalEvent>? EventsFor(DateTime dayLocal)
    {
        if (!OperatingSystem.IsWindows() || !Available) return null;
        try
        {
            var t = Type.GetTypeFromProgID("Outlook.Application")!;
            dynamic app = Activator.CreateInstance(t)!;
            dynamic ns = app.GetNamespace("MAPI");
            dynamic calendar = ns.GetDefaultFolder(9 /* olFolderCalendar */);
            dynamic items = calendar.Items;
            items.IncludeRecurrences = true;
            items.Sort("[Start]");
            var from = dayLocal.Date;
            var to = from.AddDays(1);
            dynamic restricted = items.Restrict(
                $"[Start] >= '{from:MM/dd/yyyy hh:mm tt}' AND [Start] < '{to:MM/dd/yyyy hh:mm tt}'");
            var events = new List<CalEvent>();
            foreach (dynamic item in restricted)
            {
                try
                {
                    events.Add(new CalEvent((string)item.Subject, (DateTime)item.Start, (DateTime)item.End,
                        (string)(item.Location ?? "")));
                }
                catch { }
                if (events.Count >= 15) break;
            }
            return events;
        }
        catch (Exception ex)
        {
            log.LogWarning("[calendar] outlook read failed: {Msg}", ex.Message);
            return null;
        }
    }
}
