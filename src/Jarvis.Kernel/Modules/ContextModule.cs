using Jarvis.Core;
using Jarvis.Kernel.Services;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.Kernel.Modules;

/// <summary>Follow-up commands powered by DialogContext: "close it", "the second one", "and for atlas".</summary>
public class ContextModule(DialogContext dialog) : IJarvisModule
{
    public string Name => "context";

    private static readonly Dictionary<string, int> Ordinals = new(StringComparer.OrdinalIgnoreCase)
    {
        ["first"] = 1, ["second"] = 2, ["third"] = 3, ["fourth"] = 4, ["fifth"] = 5,
        ["sixth"] = 6, ["seventh"] = 7, ["last"] = -1,
    };

    public IEnumerable<CommandDefinition> Commands =>
    [
        new()
        {
            Name = "close_referenced",
            Description = "close the item just talked about ('close it', 'mark that done', 'close the second one')",
            Patterns =
            [
                @"^(close|complete|finish|mark) (it|that|that one)( (as )?done)?$",
                @"^(close|complete|finish) the (?<ord>first|second|third|fourth|fifth|sixth|seventh|last)( one)?$",
            ],
            SlotHints = ["ord"],
            Safety = SafetyClass.WriteLocal,
            Handler = async ctx =>
            {
                DialogContext.ListItem? item;
                if (ctx.Slot("ord") is { } ord && Ordinals.TryGetValue(ord, out var n))
                    item = n == -1 ? dialog.LastItems.LastOrDefault() : dialog.ItemByOrdinal(n);
                else
                    item = dialog.SingleRecentItem();
                if (item is null)
                    return new CommandResult("I'm not sure what 'it' refers to — list something first.");

                var db = ctx.S<JarvisDb>();
                switch (item.Kind)
                {
                    case "commitment":
                        var c = await db.Commitments.FirstOrDefaultAsync(x => x.Id == item.Id);
                        if (c is null || c.Status != "open") return new CommandResult($"{item.Title} is already closed.");
                        c.Status = "done"; c.ClosedAt = DateTime.UtcNow; c.UpdatedAt = DateTime.UtcNow;
                        await db.SaveChangesAsync();
                        return new CommandResult($"Closed: {c.Text}.");
                    case "task":
                        var t = await db.Tasks.FirstOrDefaultAsync(x => x.Id == item.Id);
                        if (t is null || t.Status is "done" or "dropped") return new CommandResult($"{item.Title} is already done.");
                        t.Status = "done"; t.CompletedAt = DateTime.UtcNow; t.UpdatedAt = DateTime.UtcNow;
                        await db.SaveChangesAsync();
                        return new CommandResult($"Task done: {t.Title}.");
                    default:
                        return new CommandResult($"I can't close a {item.Kind} yet.");
                }
            }
        },
        new()
        {
            Name = "followup_project",
            Description = "repeat the last command for a different project ('and for atlas?', 'what about jarvis?')",
            Patterns = [@"^(and|what about|how about|same)( for| on| in)? (?<project>[\w .'-]+?)\??$"],
            SlotHints = ["project"],
            Handler = async ctx =>
            {
                var last = dialog.LastExchange;
                if (last is null || last.Intent is "general_answer" or "unknown" or "followup_project" or "close_referenced")
                    return new CommandResult("") { Unhandled = true };
                var project = await ctx.S<EntityResolver>().ResolveProjectAsync(ctx.Slot("project"));
                if (project is null) return new CommandResult("") { Unhandled = true };

                var slots = new Dictionary<string, string>(last.Slots, StringComparer.OrdinalIgnoreCase)
                {
                    ["project"] = project.Slug,
                    ["repo"] = project.Slug,
                };
                var router = ctx.S<CommandRouter>();
                var result = await router.ExecuteByNameAsync(last.Intent, slots, ctx.Source, ctx.Services);
                return result is null || result.Unhandled
                    ? new CommandResult("") { Unhandled = true }
                    : result;
            }
        },
    ];
}
