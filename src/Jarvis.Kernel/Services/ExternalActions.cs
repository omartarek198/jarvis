using Jarvis.Core;

namespace Jarvis.Kernel.Services;

/// <summary>
/// Public-edition guard for side effects that leave the machine or act on the operating system:
/// outbound email and mailbox changes, push notifications, Microsoft Graph sign-in, Outlook COM,
/// the away-mode email daemon, and media/volume/lock/app-launch control.
///
/// <c>Jarvis:ExternalActions:Enabled</c> defaults to <c>false</c> when the key is absent, so a
/// fresh clone can never send anything until the owner opts in. Read-only lookups (weather,
/// news, web search) and local agent runs are not covered by this switch.
/// </summary>
public class ExternalActions(IConfiguration config)
{
    public bool Enabled => config.GetValue("Jarvis:ExternalActions:Enabled", false);

    /// <summary>Demo mode: synthetic seed data, no foreground-window sampling.</summary>
    public bool DemoMode => config.GetValue("Jarvis:Demo:Enabled", false);

    public static CommandResult Blocked(string what) => new(
        $"{what} is switched off in this build. External actions are disabled by default; " +
        "set Jarvis:ExternalActions:Enabled to true in a local settings file to allow them.",
        new { blocked = true, reason = "Jarvis:ExternalActions:Enabled=false" });
}
