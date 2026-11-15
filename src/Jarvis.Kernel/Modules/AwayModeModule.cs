using Jarvis.Core;
using Jarvis.Kernel.Services;

namespace Jarvis.Kernel.Modules;

/// <summary>Voice control for away-from-desk mode — JARVIS supervises the email-driven daemon.</summary>
public class AwayModeModule(AwayModeService away) : IJarvisModule
{
    public string Name => "away";

    public IEnumerable<CommandDefinition> Commands =>
    [
        new()
        {
            Name = "away_mode_start",
            Description = "start away-from-desk mode (email-driven control)",
            Patterns =
            [
                @"^(away( from desk)? mode|i'?m (leaving|going out|heading out|away|afk)|enable away mode|start away mode)$",
            ],
            Examples = ["away from desk mode", "I'm leaving", "enable away mode"],
            Safety = SafetyClass.WriteLocal,
            Handler = ctx =>
            {
                var (_, spoken) = away.Start();
                return Task.FromResult(new CommandResult(spoken));
            },
        },
        new()
        {
            Name = "away_mode_stop",
            Description = "stop away-from-desk mode",
            Patterns =
            [
                @"^(stop away mode|disable away mode|i'?m back|i'?m home|back at my desk|exit away mode)$",
            ],
            Examples = ["stop away mode", "I'm back", "back at my desk"],
            Safety = SafetyClass.WriteLocal,
            Handler = ctx =>
            {
                var (_, spoken) = away.Stop();
                return Task.FromResult(new CommandResult(spoken));
            },
        },
        new()
        {
            Name = "away_mode_status",
            Description = "check whether away mode is running",
            Patterns = [@"^(is )?away mode (on|running|active|status)\??$"],
            Safety = SafetyClass.Read,
            Handler = ctx => Task.FromResult(new CommandResult(
                away.IsRunning ? "Away mode is on — I'm watching your inbox." : "Away mode is off.")),
        },
    ];
}
