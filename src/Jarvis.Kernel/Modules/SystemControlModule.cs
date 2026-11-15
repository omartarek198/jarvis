using Jarvis.Core;
using Jarvis.Kernel.Services;

namespace Jarvis.Kernel.Modules;

/// <summary>
/// Hands on the PC: volume, media keys, lock, open apps. Registered BEFORE OpenThingsModule
/// so "open spotify" hits the app dictionary, and "pause the music" never becomes a YouTube search.
/// </summary>
public class SystemControlModule(SystemControlService sys, ExternalActions external) : IJarvisModule
{
    public string Name => "systemcontrol";

    private static readonly Dictionary<string, int> NumberWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ten"] = 10, ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fifty"] = 50,
        ["sixty"] = 60, ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90,
        ["one hundred"] = 100, ["a hundred"] = 100, ["hundred"] = 100, ["zero"] = 0,
    };

    // Public edition: OS-level actions (volume, media keys, lock, app launch) only run when
    // Jarvis:ExternalActions:Enabled is true. The command catalog stays visible either way.
    public IEnumerable<CommandDefinition> Commands => Raw.Select(d => new CommandDefinition
    {
        Name = d.Name, Description = d.Description, Patterns = d.Patterns, Safety = d.Safety,
        SlotHints = d.SlotHints, Examples = d.Examples,
        Handler = ctx => external.Enabled ? d.Handler(ctx) : Task.FromResult(ExternalActions.Blocked("System control")),
    });

    private IEnumerable<CommandDefinition> Raw =>
    [
        new()
        {
            Name = "volume_updown",
            Description = "turn the system volume up or down",
            Patterns = [@"^(turn |put )?(the )?volume (?<dir>up|down)( a (bit|notch|little))?$",
                        @"^(?<dir>louder|quieter)( please)?$"],
            SlotHints = ["dir"],
            Handler = ctx =>
            {
                var up = ctx.Slot("dir") is "up" or "louder";
                if (up) sys.VolumeUp(); else sys.VolumeDown();
                return Task.FromResult(new CommandResult($"Volume {(up ? "up" : "down")}."));
            }
        },
        new()
        {
            Name = "volume_set",
            Description = "set the system volume to a percentage",
            Patterns = [@"^set( the)? volume to (?<pct>[\w ]+?)( percent)?$", @"^volume (?<pct>\d{1,3})( percent)?$"],
            SlotHints = ["pct"],
            Handler = ctx =>
            {
                var raw = ctx.Slot("pct") ?? "";
                if (!int.TryParse(raw, out var pct) && !NumberWords.TryGetValue(raw, out pct))
                    return Task.FromResult(new CommandResult($"I couldn't parse '{raw}' as a volume. Try: set volume to 50."));
                sys.SetVolumePercent(pct);
                return Task.FromResult(new CommandResult($"Volume set to {pct} percent."));
            }
        },
        new()
        {
            Name = "volume_mute",
            Description = "mute or unmute the system audio",
            Patterns = [@"^(mute|unmute)( the)?( sound| volume| audio| pc)?$"],
            Handler = ctx =>
            {
                sys.MuteToggle();
                return Task.FromResult(new CommandResult(ctx.Utterance.StartsWith("un", StringComparison.OrdinalIgnoreCase)
                    ? "Unmuted." : "Toggled mute."));
            }
        },
        new()
        {
            Name = "media_control",
            Description = "play, pause or skip the current media (any player)",
            Patterns = [@"^(?<op>pause|resume|stop)( the)?( music| song| media| video| playback)?$",
                        @"^play( the)? (music|song|media|video)$",
                        @"^(?<op>next|previous|skip)( track| song)?( please)?$"],
            SlotHints = ["op"],
            Handler = ctx =>
            {
                var op = ctx.Slot("op") ?? "play";
                switch (op)
                {
                    case "next" or "skip": sys.NextTrack(); return Task.FromResult(new CommandResult("Next track."));
                    case "previous": sys.PreviousTrack(); return Task.FromResult(new CommandResult("Previous track."));
                    default: sys.PlayPause(); return Task.FromResult(new CommandResult(op == "pause" ? "Paused." : "Playing."));
                }
            }
        },
        new()
        {
            Name = "lock_pc",
            Description = "lock the workstation",
            Patterns = [@"^lock (the |my )?(pc|computer|screen|machine|workstation)$"],
            Safety = SafetyClass.Mutate,
            Handler = _ => Task.FromResult(new CommandResult(
                sys.Lock() ? "Locking. See you soon." : "Windows refused the lock request."))
        },
        new()
        {
            Name = "open_app",
            Description = "launch a desktop app by name (notepad, vs code, teams, spotify, terminal…)",
            Patterns = [@"^(open|launch|start) (?<app>notepad|calc(ulator)?|file explorer|explorer|(windows )?terminal|vs ?code|visual studio code|(microsoft )?teams|outlook|spotify|task manager|settings)$"],
            SlotHints = ["app"],
            Handler = ctx =>
            {
                var app = ctx.Slot("app") ?? "";
                return Task.FromResult(sys.TryOpenApp(app, out var err)
                    ? new CommandResult($"Opening {app}.")
                    : new CommandResult(err));
            }
        },
    ];
}
