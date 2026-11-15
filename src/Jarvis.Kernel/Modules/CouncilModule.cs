using Jarvis.Core;
using Jarvis.Kernel.Services.Council;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.Kernel.Modules;

/// <summary>
/// The Council: convene multiple decision lenses, moderate a structured deliberation, vote
/// with weights, and end with a recommendation, an action, and a saved decision record.
/// Mid-session turns are captured by the CommandRouter's council hook, not by these patterns.
/// </summary>
public class CouncilModule(CouncilService council) : IJarvisModule
{
    public string Name => "council";

    public IEnumerable<CommandDefinition> Commands =>
    [
        new()
        {
            Name = "council_convene",
            Description = "convene the council for a structured decision",
            Patterns =
            [
                @"^(convene|call|summon|assemble)( the)? council( with (?<agents>[\w ,]+?))?(( about| on| regarding)?:? (?<topic>.+))?$",
                @"^(start )?council protocol(:? (?<topic>.+))?$",
            ],
            SlotHints = ["topic"],
            Examples =
            [
                "convene the council",
                "call the council about the new feature",
                "summon the council should I take this freelance project",
            ],
            Safety = SafetyClass.WriteLocal,
            Handler = ctx => council.StartAsync(ctx, CouncilMode.Full, ctx.Slot("topic")),
        },
        new()
        {
            Name = "council_quick",
            Description = "quick council: a 3-lens, fast decision",
            Patterns = [@"^quick council:? (?<topic>.+)$"],
            SlotHints = ["topic"],
            Examples = ["quick council gym or ship the release", "quick council should I sleep now"],
            Safety = SafetyClass.WriteLocal,
            Handler = ctx => council.StartAsync(ctx, CouncilMode.Quick, ctx.Slot("topic")),
        },
        new()
        {
            Name = "council_full",
            Description = "full council: 5-7 lenses with cross-examination",
            Patterns = [@"^full council:? (?<topic>.+)$"],
            SlotHints = ["topic"],
            Examples = ["full council is AI content a real opportunity or FOMO"],
            Safety = SafetyClass.WriteLocal,
            Handler = ctx => council.StartAsync(ctx, CouncilMode.Full, ctx.Slot("topic")),
        },
        new()
        {
            Name = "council_war",
            Description = "war council: product / business strategy with project context",
            Patterns = [@"^war council:? (?<topic>.+)$"],
            SlotHints = ["topic"],
            Examples = ["war council should we build consent forms now or later"],
            Safety = SafetyClass.WriteLocal,
            Handler = ctx => council.StartAsync(ctx, CouncilMode.War, ctx.Slot("topic")),
        },
        new()
        {
            Name = "council_emergency",
            Description = "emergency council: short, assertive, veto-enabled",
            Patterns = [@"^emergency council:? (?<topic>.+)$"],
            SlotHints = ["topic"],
            Examples = ["emergency council sleep now or keep working"],
            Safety = SafetyClass.WriteLocal,
            Handler = ctx => council.StartAsync(ctx, CouncilMode.Emergency, ctx.Slot("topic")),
        },
        new()
        {
            Name = "council_recall",
            Description = "recall a past council decision by topic",
            Patterns =
            [
                @"^why did (we|the council) (defer|reject|decide( on| against)?|choose) (?<topic>.+)$",
                @"^what did the council (decide|say) about (?<topic>.+)$",
                @"^did (we|the council) already discuss (?<topic>.+)$",
            ],
            SlotHints = ["topic"],
            Examples = ["why did we defer consent forms", "what did the council decide about the gym"],
            Safety = SafetyClass.Read,
            Handler = async ctx =>
            {
                var topic = ctx.Slot("topic") ?? "";
                var db = ctx.S<JarvisDb>();
                var words = topic.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Where(w => w.Length > 3).Take(4).ToArray();
                if (words.Length == 0) return new CommandResult("") { Unhandled = true };
                var candidates = await db.CouncilSessions.AsNoTracking()
                    .Where(s => s.Status == "decided")
                    .OrderByDescending(s => s.CreatedAt).Take(100).ToListAsync();
                var hit = candidates.FirstOrDefault(s => words.Any(w =>
                    s.FramedDecision.Contains(w, StringComparison.OrdinalIgnoreCase)
                    || s.FinalRecommendation.Contains(w, StringComparison.OrdinalIgnoreCase)));
                if (hit is null)
                    return new CommandResult($"No council decision on record about {topic}.");
                var votes = await db.CouncilVotes.AsNoTracking()
                    .Where(v => v.SessionId == hit.Id).ToListAsync();
                var dissent = string.IsNullOrWhiteSpace(hit.DissentSummary) ? "" :
                    $" Dissent at the time: {hit.DissentSummary}.";
                return new CommandResult(
                    $"On {hit.CreatedAt.ToLocalTime():MMMM d}, the council ({hit.Mode}) debated \"{hit.FramedDecision}\" " +
                    $"and decided: {hit.FinalRecommendation}{dissent}",
                    new { hit.Id, hit.Mode, hit.Category, votes = votes.Select(v => new { v.AgentKey, v.Position, v.Confidence }) });
            },
        },
        new()
        {
            Name = "council_recent",
            Description = "list recent council decisions",
            Patterns =
            [
                @"^(show|list)( my)? recent council decisions$",
                @"^what (did|has) the council decide[d]?( recently)?$",
            ],
            Safety = SafetyClass.Read,
            Handler = async ctx =>
            {
                var db = ctx.S<JarvisDb>();
                var recent = await db.CouncilSessions.AsNoTracking()
                    .Where(s => s.Status == "decided")
                    .OrderByDescending(s => s.CreatedAt).Take(5).ToListAsync();
                if (recent.Count == 0)
                    return new CommandResult("No council decisions on record yet.");
                var spoken = "Recent council decisions: " + string.Join(" ",
                    recent.Select((s, i) => $"{i + 1}. {s.FramedDecision} — {s.FinalRecommendation}"));
                return new CommandResult(spoken, recent.Select(s => new
                { s.Id, s.Mode, s.Category, s.FramedDecision, s.FinalRecommendation, s.CreatedAt }));
            },
        },
    ];
}
