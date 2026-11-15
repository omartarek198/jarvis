using Jarvis.Core;
using Jarvis.Kernel.Services;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.Kernel.Modules;

internal static class M
{
    public static T S<T>(this CommandContext c) where T : notnull => c.Services.GetRequiredService<T>();
}

public class SystemModule(PendingActionService pending, IEventBus bus) : IJarvisModule
{
    public string Name => "system";

    public IEnumerable<CommandDefinition> Commands =>
    [
        new()
        {
            Name = "help",
            Description = "list what JARVIS can do",
            Patterns = [@"^(help|what can you do|list commands)$"],
            Examples = ["what can you do", "what integrations do you have", "what are your capabilities",
                        "what features do you support", "what services are you connected to"],
            Handler = ctx =>
            {
                var router = ctx.S<CommandRouter>();
                var names = router.Definitions.Select(d => d.Name).ToArray();
                return Task.FromResult(new CommandResult(
                    $"I run {names.Length} commands across your day. Comms: Teams, work and personal email, " +
                    "and Jira. Coding: Claude and Codex agents on your repos, screen vision, Playwright QA. " +
                    "Life: weather, news on any topic, crypto, reminders and alarms, habits, sleep, meetings " +
                    "with live transcription. Plus music and system control, notes, facts I remember, and " +
                    "briefings. Just ask for a thing — the full catalog is in the console.",
                    new { commands = router.Definitions.Select(d => new { d.Name, d.Description }) }));
            }
        },
        new()
        {
            Name = "system_status",
            Description = "report kernel, voice and model health",
            Patterns = [@"^(system status|status report|are you (ok|okay|online|there)|health check)$"],
            Handler = async ctx =>
            {
                var db = ctx.S<JarvisDb>();
                var stt = await ctx.S<SttClient>().IsHealthyAsync();
                var ollama = await ctx.S<OllamaIntentService>().IsHealthyAsync();
                var tts = ctx.S<TtsService>();
                var counts = new
                {
                    projects = await db.Projects.CountAsync(),
                    openCommitments = await db.Commitments.CountAsync(c => c.Status == "open"),
                    openTasks = await db.Tasks.CountAsync(t => t.Status == "open" || t.Status == "doing"),
                };
                var spoken = $"All systems nominal. Speech recognition {(stt ? "online" : "offline")}, " +
                             $"local model {(ollama ? "online" : "offline")}, voice engine {tts.Engine}. " +
                             $"{counts.projects} projects, {counts.openCommitments} open commitments, {counts.openTasks} open tasks.";
                return new CommandResult(spoken, new { stt, ollama, ttsEngine = tts.Engine, counts });
            }
        },
        new()
        {
            Name = "what_time",
            Description = "say the current time",
            Patterns = [@"^(what time is it|what'?s the time|time check|current time|tell me the time)\??$"],
            Examples = ["what time is it", "what's the time right now", "tell me the current time"],
            Handler = _ => Task.FromResult(new CommandResult($"It's {DateTime.Now:h:mm tt}."))
        },
        new()
        {
            Name = "what_date",
            Description = "say today's date and day of week",
            Patterns = [@"^(what('s| is) (the date|today'?s date)|what (day|date) is (it|today)( today)?|today'?s date)\??$"],
            Examples = ["what's the date today", "what day is it", "what's today's date"],
            Handler = _ => Task.FromResult(new CommandResult($"Today is {DateTime.Now:dddd, MMMM d, yyyy}."))
        },
        new()
        {
            Name = "confirm",
            Description = "confirm the pending action",
            Patterns = [@"^(confirm|confirmed|yes,? do it|go ahead|proceed|make it so)$"],
            Handler = ctx => pending.ConfirmAsync(ctx.Services)
        },
        new()
        {
            Name = "cancel",
            Description = "cancel the pending action",
            Patterns = [@"^(cancel|never ?mind|abort)( that)?$"],
            Handler = _ => Task.FromResult(pending.Cancel())
        },
        new()
        {
            Name = "emergency_stop",
            Description = "halt running child processes and speech",
            Patterns = [@"^(stop( everything)?|emergency stop|halt|shut up)$"],
            Handler = _ =>
            {
                bus.Publish(new JarvisEvent("system.stop", new { reason = "user command" }, "voice"));
                return Task.FromResult(new CommandResult("Stopping all activity."));
            }
        },
    ];
}

public class VoiceModule : IJarvisModule
{
    public string Name => "voice";

    public IEnumerable<CommandDefinition> Commands =>
    [
        new()
        {
            Name = "switch_voice",
            Description = "switch the speaking voice profile (default, fast, briefing, alert, boris — see voice/profiles.json)",
            Patterns =
            [
                @"^(switch|change)( the)? voice to (?<profile>\w+)$",
                @"^(switch to|use) (the )?(?<profile>\w+) voice$",
            ],
            SlotHints = ["profile"],
            Safety = SafetyClass.WriteLocal,
            Handler = async ctx =>
            {
                var tts = ctx.S<TtsService>();
                var requested = ctx.Slot("profile")?.ToLowerInvariant() ?? "default";
                var resolved = tts.ResolveProfileName(requested);
                if (resolved is null)
                    return new CommandResult(
                        $"I don't have a {requested} profile. Available: {string.Join(", ", tts.ProfileNames)}.");
                await tts.SetProfileAsync(resolved);
                var p = tts.GetProfile(resolved);
                var note = tts.Engine == "sapi"
                    ? " Its engine isn't installed yet, so the Windows voice fills in for now." : "";
                return new CommandResult(resolved == "boris"
                    ? $"Voice profile switched to Boris.{note}"
                    : $"Voice profile set to {resolved} — {p.Engine}, voice {p.Voice}, speed {p.Speed:0.##}.{note}");
            }
        },
        new()
        {
            Name = "list_voices",
            Description = "list the available voice profiles",
            Patterns = [@"^(list|what) voice profiles( do (i|you) have)?$", @"^what voices do (i|you) have$"],
            Handler = ctx =>
            {
                var tts = ctx.S<TtsService>();
                var lines = tts.ProfileNames.Select(n =>
                {
                    var p = tts.GetProfile(n);
                    var active = n == tts.CurrentProfile ? " — active" : "";
                    return $"{n} ({p.Engine}, {p.Voice}, {p.Speed:0.##}x){active}";
                });
                return Task.FromResult(new CommandResult(
                    $"Voice profiles: {string.Join("; ", lines)}. Say: use briefing voice, or: switch voice to fast."));
            }
        },
        new()
        {
            Name = "rebuild_voice_cache",
            Description = "wipe and re-render the cached system phrases for every cache-enabled voice profile",
            Patterns = [@"^(regenerate|rebuild|refresh) (the )?(voice|phrase) cache$"],
            Safety = SafetyClass.WriteLocal,
            Handler = async ctx =>
            {
                var (cached, attempted) = await ctx.S<TtsService>().RegenerateCacheAsync();
                return new CommandResult(cached == attempted
                    ? $"Voice cache rebuilt — {SpeechText.Number(cached)} phrases rendered."
                    : $"Voice cache rebuilt — {SpeechText.Number(cached)} of {SpeechText.Number(attempted)} phrases rendered; the rest fell back to another engine and were skipped.");
            }
        },
        new()
        {
            Name = "say",
            Description = "speak the given text out loud",
            Patterns = [@"^(say|repeat after me)[,:]? (?<text>.+)$"],
            SlotHints = ["text"],
            Handler = ctx => Task.FromResult(new CommandResult(ctx.Slot("text") ?? ""))
        },
        new()
        {
            Name = "take_dictation",
            Description = "type everything you say into the active window until you say stop dictation",
            Patterns = [@"^(take|start) dictation$", @"^type this[,:]? (?<text>.+)$"],
            SlotHints = ["text"],
            Safety = SafetyClass.WriteLocal,
            Handler = ctx =>
            {
                var bus = ctx.S<IEventBus>();
                if (ctx.Slot("text") is { } oneShot)
                {
                    bus.Publish(new JarvisEvent("dictation.type", new { text = oneShot }, "voice"));
                    return Task.FromResult(new CommandResult("Typed."));
                }
                bus.Publish(new JarvisEvent("dictation.state", new { on = true }, "voice"));
                return Task.FromResult(new CommandResult(
                    "Dictation on — everything you say gets typed into the active window. Say: stop dictation, to end."));
            }
        },
        new()
        {
            Name = "stop_dictation",
            Description = "end dictation mode",
            Patterns = [@"^(stop|end) dictation$"],
            Safety = SafetyClass.WriteLocal,
            Handler = ctx =>
            {
                ctx.S<IEventBus>().Publish(new JarvisEvent("dictation.state", new { on = false }, "voice"));
                return Task.FromResult(new CommandResult("Dictation off."));
            }
        },
        new()
        {
            Name = "wake_word_toggle",
            Description = "turn the 'Jarvis' wake word on or off (push-to-talk always works)",
            Patterns = [@"^(turn )?(the )?wake word (?<on>on|off)$", @"^(go to sleep|stop listening)$"],
            SlotHints = ["on"],
            Safety = SafetyClass.WriteLocal,
            Handler = async ctx =>
            {
                var on = ctx.Slot("on") == "on";   // "go to sleep"/"stop listening" → off
                var db = ctx.S<JarvisDb>();
                var s = await db.Settings.FindAsync("voice.wakeWord");
                if (s is null) db.Settings.Add(new Setting { Key = "voice.wakeWord", ValueJson = on ? "\"on\"" : "\"off\"" });
                else s.ValueJson = on ? "\"on\"" : "\"off\"";
                await db.SaveChangesAsync();
                ctx.S<IEventBus>().Publish(new JarvisEvent("wake.state", new { on }, "voice"));
                return new CommandResult(on
                    ? "Wake word on — just say Jarvis."
                    : "Wake word off. Push-to-talk still works — hold Right Control.");
            }
        },
        new()
        {
            Name = "conversation_mode_toggle",
            Description = "keep the mic open after replies for follow-ups, no wake word needed",
            Patterns = [@"^(turn )?conversation mode (?<on>on|off)$"],
            SlotHints = ["on"],
            Safety = SafetyClass.WriteLocal,
            Handler = async ctx =>
            {
                var on = ctx.Slot("on") == "on";
                var db = ctx.S<JarvisDb>();
                var s = await db.Settings.FindAsync("voice.conversationMode");
                if (s is null) db.Settings.Add(new Setting { Key = "voice.conversationMode", ValueJson = on ? "\"on\"" : "\"off\"" });
                else s.ValueJson = on ? "\"on\"" : "\"off\"";
                await db.SaveChangesAsync();
                return new CommandResult(on
                    ? "Conversation mode on — after I reply, just keep talking."
                    : "Conversation mode off.");
            }
        },
        new()
        {
            Name = "voice_output",
            Description = "route spoken replies to speakers, headset, or auto (system default)",
            Patterns = [@"^(set |switch |change )?(voice )?output to (the )?(?<dev>speakers|headset|auto|default)$"],
            SlotHints = ["dev"],
            Safety = SafetyClass.WriteLocal,
            Handler = async ctx =>
            {
                var dev = ctx.Slot("dev")?.ToLowerInvariant() ?? "auto";
                if (dev == "default") dev = "auto";
                var db = ctx.S<JarvisDb>();
                var s = await db.Settings.FindAsync("voice.output");
                if (s is null) db.Settings.Add(new Setting { Key = "voice.output", ValueJson = $"\"{dev}\"" });
                else s.ValueJson = $"\"{dev}\"";
                await db.SaveChangesAsync();
                return new CommandResult(dev == "speakers"
                    ? "Replies will play through the speakers — full quality even while the headset mic is engaged."
                    : $"Voice output set to {dev}.");
            }
        },
    ];
}

public class BriefingModule : IJarvisModule
{
    public string Name => "briefing";

    public IEnumerable<CommandDefinition> Commands =>
    [
        new()
        {
            Name = "morning_briefing",
            Description = "deliver the morning briefing",
            Patterns = [@"^(morning briefing|good morning|prepare my morning briefing|brief me)$"],
            Handler = async ctx =>
            {
                var (md, spoken) = await ctx.S<BriefingService>().BuildMorningAsync();
                var db = ctx.S<JarvisDb>();
                db.Briefings.Add(new Briefing { Kind = "morning", ContentMd = md, SpokenSummary = spoken });
                await db.SaveChangesAsync();
                return new CommandResult(spoken, new { markdown = md });
            }
        },
        new()
        {
            Name = "shutdown_briefing",
            Description = "deliver the end-of-day shutdown briefing",
            Patterns = [@"^(shutdown briefing|end of day( briefing)?|evening briefing|good night)$"],
            Handler = async ctx =>
            {
                var (md, spoken) = await ctx.S<BriefingService>().BuildShutdownAsync();
                var db = ctx.S<JarvisDb>();
                db.Briefings.Add(new Briefing { Kind = "shutdown", ContentMd = md, SpokenSummary = spoken });
                await db.SaveChangesAsync();
                return new CommandResult(spoken, new { markdown = md });
            }
        },
        new()
        {
            Name = "weekly_review",
            Description = "deliver the weekly review (tasks, focus, habits, sleep, agents)",
            Patterns = [@"^(weekly review|review my week|how was my week)\??$"],
            Handler = async ctx =>
            {
                var (md, spoken) = await ctx.S<BriefingService>().BuildWeeklyAsync();
                var db = ctx.S<JarvisDb>();
                db.Briefings.Add(new Briefing { Kind = "weekly", ContentMd = md, SpokenSummary = spoken });
                await db.SaveChangesAsync();
                return new CommandResult(spoken, new { markdown = md });
            }
        },
        new()
        {
            Name = "what_matters",
            Description = "say what deserves attention right now",
            Patterns = [@"^what should i (care about|focus on)( right now)?$"],
            Handler = async ctx =>
            {
                var db = ctx.S<JarvisDb>();
                var overdue = await db.Commitments.AsNoTracking()
                    .Where(c => c.Status == "open" && c.DueDate != null && c.DueDate < DateTime.Now)
                    .OrderBy(c => c.DueDate).Take(3).ToListAsync();
                var ranked = await ctx.S<Prioritizer>().RankAsync(take: 1);
                var parts = new List<string>();
                if (overdue.Count > 0)
                    parts.Add($"{overdue.Count} overdue commitment{(overdue.Count == 1 ? "" : "s")}, starting with: {overdue[0].Text}");
                if (ranked.Count > 0)
                    parts.Add($"highest ROI task is {ranked[0].Task.Title} on {ranked[0].Project}");
                return new CommandResult(parts.Count == 0
                    ? "Nothing is on fire. Ledger clear, no urgent tasks. Pick something strategic."
                    : "Right now: " + string.Join("; and ", parts) + ".");
            }
        },
    ];
}
