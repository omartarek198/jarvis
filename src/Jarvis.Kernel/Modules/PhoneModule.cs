using Jarvis.Core;
using Jarvis.Kernel.Services;

namespace Jarvis.Kernel.Modules;

/// <summary>Phone pairing info: the LAN API key, the mobile PWA address, the ntfy topic.
/// Secrets are shown in the console response, not spoken aloud.</summary>
public class PhoneModule(ExternalActions external) : IJarvisModule
{
    public string Name => "phone";

    public IEnumerable<CommandDefinition> Commands =>
    [
        new()
        {
            Name = "phone_setup_info",
            Description = "show the phone pairing info (mobile URL, API key, ntfy topic)",
            Patterns = [@"^(what('s| is) my )?(phone|lan|mobile) (key|setup|pairing|access)( info)?\??$",
                        @"^set ?up my phone$"],
            Handler = ctx =>
            {
                var ip = LanCertService.LanAddresses().FirstOrDefault()?.ToString() ?? "<pc-ip>";
                var port = 5589;
                // The API key and ntfy topic are NOT returned here: command results are broadcast
                // over SignalR and persisted in the audit table. They are served only by the
                // loopback-only GET /api/phone/pairing endpoint.
                return Task.FromResult(new CommandResult(
                    "The mobile page address is in the console. Fetch the API key from this PC at " +
                    "slash api slash phone slash pairing. Full steps are in docs slash PHONE dot md.",
                    new
                    {
                        mobileUrl = $"https://{ip}:{port}/mobile.html",
                        pairing = "GET http://localhost:5588/api/phone/pairing (loopback only)",
                        pushNotifications = external.Enabled ? "enabled" : "disabled (Jarvis:ExternalActions:Enabled=false)",
                        runbook = "docs/PHONE.md",
                    }));
            }
        },
        new()
        {
            Name = "ntfy_topic_info",
            Description = "show the ntfy notification topic for the phone",
            Patterns = [@"^(what('s| is) my )?(notification|ntfy) topic\??$"],
            Handler = _ => Task.FromResult(new CommandResult(
                "Fetch your notification topic from this PC at slash api slash phone slash pairing, then subscribe to it in the ntfy app.",
                new { pairing = "GET http://localhost:5588/api/phone/pairing (loopback only)" }))
        },
    ];
}
