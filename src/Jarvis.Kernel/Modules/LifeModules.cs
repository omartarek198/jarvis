using System.Diagnostics;
using Jarvis.Core;
using Jarvis.Kernel.Services;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.Kernel.Modules;

public class RemindersModule : IJarvisModule
{
    public string Name => "reminders";

    public IEnumerable<CommandDefinition> Commands =>
    [
        new()
        {
            Name = "set_timed_reminder",
            Description = "set a reminder that fires at a clock time or after a duration",
            Patterns =
            [
                @"^remind me (at|@) (?<time>[\w:. ]+?) (to|that|about) (?<text>.+)$",
                @"^remind me (to|that|about) (?<text>.+?) at (?<time>[\w:. ]+)$",
                @"^remind me in (?<dur>[\w. ]+?) (to|that|about) (?<text>.+)$",
                @"^remind me (to|that|about) (?<text>.+?) in (?<dur>[\w. ]+)$",
            ],
            SlotHints = ["time", "dur", "text"],
            Safety = SafetyClass.WriteLocal,
            Handler = async ctx =>
            {
                DateTime? at = DateParser.TryParseTimeSpec(ctx.Slot("time"));
                if (at is null && DateParser.TryParseDuration(ctx.Slot("dur")) is { } dur) at = DateTime.Now.Add(dur);
                if (at is null) return new CommandResult("") { Unhandled = true }; // plain 'remind me to X' = commitment
                var db = ctx.S<JarvisDb>();
                var r = new Reminder { Text = ctx.Slot("text") ?? "reminder", At = at.Value };
                db.Reminders.Add(r);
                await db.SaveChangesAsync();
                ctx.S<DialogContext>().RecordItems([new DialogContext.ListItem("reminder", r.Id, r.Text)]);
                return new CommandResult($"Reminder set for {SpeakAt(at.Value)}: {r.Text}.", new { reminder = r });
            }
        },
        new()
        {
            Name = "set_alarm",
            Description = "set an alarm at a clock time, optionally recurring daily/weekdays",
            Patterns = [@"^(set )?(an )?alarm (for|at) (?<time>[\w:. ]+?)( (?<rec>daily|every ?day|weekdays|every weekday))?$",
                        @"^wake me( up)? at (?<time>[\w:. ]+)$"],
            SlotHints = ["time", "rec"],
            Safety = SafetyClass.WriteLocal,
            Handler = async ctx =>
            {
                var at = DateParser.TryParseTimeSpec(ctx.Slot("time"));
                if (at is null) return new CommandResult($"I couldn't parse the time '{ctx.Slot("time")}'. Try: alarm at 7 am.");
                var rec = ctx.Slot("rec")?.Replace(" ", "") switch
                {
                    "daily" or "everyday" => "daily",
                    "weekdays" or "everyweekday" => "weekdays",
                    _ => "",
                };
                var db = ctx.S<JarvisDb>();
                var r = new Reminder { Text = "Alarm", At = at.Value, Kind = "alarm", Recurrence = rec };
                db.Reminders.Add(r);
                await db.SaveChangesAsync();
                return new CommandResult($"Alarm set for {SpeakAt(at.Value)}{(rec == "" ? "" : $", repeating {rec}")}.");
            }
        },
        new()
        {
            Name = "set_timer",
            Description = "start a countdown timer",
            Patterns = [@"^(set |start )?a? ?timer( for)? (?<dur>[\w. ]+)$"],
            SlotHints = ["dur"],
            Safety = SafetyClass.WriteLocal,
            Handler = async ctx =>
            {
                var dur = DateParser.TryParseDuration(ctx.Slot("dur"));
                if (dur is null) return new CommandResult($"I couldn't parse '{ctx.Slot("dur")}' as a duration. Try: timer for 10 minutes.");
                var db = ctx.S<JarvisDb>();
                var r = new Reminder { Text = $"{DateParser.TryParseDuration(ctx.Slot("dur"))!.Value.TotalMinutes:F0} minute timer", At = DateTime.Now.Add(dur.Value), Kind = "timer" };
                db.Reminders.Add(r);
                await db.SaveChangesAsync();
                return new CommandResult($"Timer running — I'll call out in {SpeechText.Normalize(SpeakDuration(dur.Value))}.");
            }
        },
        new()
        {
            Name = "list_reminders",
            Description = "list upcoming reminders, alarms and timers",
            Patterns = [@"^(list|what are|show)( my)? (reminders|alarms|timers)( and (alarms|timers|reminders))*\??$"],
            Handler = async ctx =>
            {
                var db = ctx.S<JarvisDb>();
                var pending = await db.Reminders.AsNoTracking()
                    .Where(r => r.Status == "pending").OrderBy(r => r.At).Take(8).ToListAsync();
                ctx.S<DialogContext>().RecordItems(pending.Select(r => new DialogContext.ListItem("reminder", r.Id, r.Text)));
                if (pending.Count == 0) return new CommandResult("Nothing scheduled. Say: remind me at 7 pm to, and then the thing.");
                var lines = pending.Select(r => $"{r.Kind} {SpeakAt(r.At)}: {r.Text}");
                return new CommandResult($"{pending.Count} scheduled: " + string.Join(". ", lines) + ".", new { pending });
            }
        },
        new()
        {
            Name = "cancel_reminder",
            Description = "cancel the next or all reminders/alarms/timers",
            Patterns = [@"^cancel (the )?(?<which>next|all|last)? ?(reminders?|alarms?|timers?)$"],
            SlotHints = ["which"],
            Safety = SafetyClass.WriteLocal,
            Handler = async ctx =>
            {
                var db = ctx.S<JarvisDb>();
                var pending = await db.Reminders.Where(r => r.Status == "pending").OrderBy(r => r.At).ToListAsync();
                if (pending.Count == 0) return new CommandResult("Nothing to cancel.");
                if (ctx.Slot("which") == "all")
                {
                    pending.ForEach(r => { r.Status = "cancelled"; r.UpdatedAt = DateTime.UtcNow; });
                    await db.SaveChangesAsync();
                    return new CommandResult($"Cancelled all {pending.Count}.");
                }
                var target = ctx.Slot("which") == "last" ? pending.OrderByDescending(r => r.CreatedAt).First() : pending[0];
                target.Status = "cancelled"; target.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
                return new CommandResult($"Cancelled: {target.Text} at {SpeakAt(target.At)}.");
            }
        },
        new()
        {
            Name = "snooze",
            Description = "snooze the reminder that just fired (default 10 minutes)",
            Patterns = [@"^snooze( (for )?(?<dur>[\w ]+))?$"],
            SlotHints = ["dur"],
            Safety = SafetyClass.WriteLocal,
            Handler = async ctx =>
            {
                var db = ctx.S<JarvisDb>();
                var fired = await db.Reminders.Where(r => r.FiredAt != null)
                    .OrderByDescending(r => r.FiredAt).FirstOrDefaultAsync();
                if (fired is null) return new CommandResult("Nothing recently fired to snooze.");
                var dur = DateParser.TryParseDuration(ctx.Slot("dur")) ?? TimeSpan.FromMinutes(10);
                fired.Status = "pending"; fired.At = DateTime.Now.Add(dur); fired.FiredAt = null;
                fired.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
                return new CommandResult($"Snoozed {SpeechText.Normalize(SpeakDuration(dur))} — {fired.Text}.");
            }
        },
    ];

    private static string SpeakAt(DateTime at) =>
        at.Date == DateTime.Today ? at.ToString("h:mm tt")
        : at.Date == DateTime.Today.AddDays(1) ? "tomorrow " + at.ToString("h:mm tt")
        : at.ToString("dddd h:mm tt");

    private static string SpeakDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} hours {t.Minutes} minutes".Replace(" 0 minutes", "")
        : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} minutes"
        : $"{t.Seconds} seconds";
}

public class FactsModule : IJarvisModule
{
    public string Name => "facts";

    public IEnumerable<CommandDefinition> Commands =>
    [
        new()
        {
            Name = "remember_fact",
            Description = "store a fact in long-term memory",
            Patterns = [@"^remember (that )?(?<text>.+)$"],
            SlotHints = ["text"],
            Safety = SafetyClass.WriteLocal,
            Handler = async ctx =>
            {
                var text = ctx.Slot("text") ?? "";
                if (text.Length < 4) return new CommandResult("") { Unhandled = true };
                var db = ctx.S<JarvisDb>();
                var f = new Fact { Text = text };
                db.Facts.Add(f);
                await db.SaveChangesAsync();
                ctx.S<MemoryIndex>().Add("fact", f.Id, text.Length > 60 ? text[..60] : text, text);
                ctx.S<DialogContext>().RecordItems([new DialogContext.ListItem("fact", f.Id, f.Text)]);
                return new CommandResult("Remembered.", new { fact = f });
            }
        },
        new()
        {
            Name = "recall_facts",
            Description = "recall stored facts about a topic",
            Examples = ["what do you know about me", "what do you remember about my preferences",
                        "what have I told you about myself"],
            Patterns = [@"^what do you (remember|know) about (?<q>.+)$", @"^recall (?<q>.+)$"],
            SlotHints = ["q"],
            Handler = async ctx =>
            {
                var q = ctx.Slot("q") ?? "";
                // "about me / about us / everything" = dump the whole fact store, not an FTS miss
                if (System.Text.RegularExpressions.Regex.IsMatch(q, @"^(me|myself|us|everything|all|anything)$",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    var everything = await ctx.S<JarvisDb>().Facts.AsNoTracking()
                        .OrderByDescending(f => f.CreatedAt).Take(12).ToListAsync();
                    return everything.Count == 0
                        ? new CommandResult("Nothing in the fact store yet. Say: remember that, and then the thing.")
                        : new CommandResult($"I know {SpeechText.Number(everything.Count)} things about you: " +
                            string.Join(". ", everything.Select(f => f.Text)) + ".", new { facts = everything });
                }
                var hits = ctx.S<MemoryIndex>().Search(q).Where(h => h.Kind == "fact").Take(4).ToList();
                if (hits.Count == 0)
                {
                    var all = ctx.S<MemoryIndex>().Search(q);
                    if (all.Count == 0) return new CommandResult("") { Unhandled = true };  // general tier knows the facts
                    return new CommandResult($"No facts, but related memories: " + string.Join(". ", all.Take(2).Select(h => $"a {h.Kind}: {h.Title}")));
                }
                var db = ctx.S<JarvisDb>();
                var texts = new List<string>();
                foreach (var h in hits)
                    texts.Add((await db.Facts.AsNoTracking().FirstOrDefaultAsync(f => f.Id == h.RefId))?.Text ?? h.Title);
                return new CommandResult(string.Join(". ", texts) + ".", new { facts = texts });
            }
        },
        new()
        {
            Name = "forget_fact",
            Description = "delete the fact just stored or mentioned",
            Patterns = [@"^forget (that|it|the last fact)$"],
            Safety = SafetyClass.WriteLocal,
            Handler = async ctx =>
            {
                var dialog = ctx.S<DialogContext>();
                var item = dialog.LastItems.FirstOrDefault(i => i.Kind == "fact");
                if (item is null) return new CommandResult("I'm not sure which fact — say 'recall' it first.");
                var db = ctx.S<JarvisDb>();
                var f = await db.Facts.FirstOrDefaultAsync(x => x.Id == item.Id);
                if (f is null) return new CommandResult("Already gone.");
                db.Facts.Remove(f);
                await db.SaveChangesAsync();
                return new CommandResult($"Forgotten: {item.Title}.");
            }
        },
    ];
}

public class WeatherModule(WeatherService weather) : IJarvisModule
{
    public string Name => "weather";

    public IEnumerable<CommandDefinition> Commands =>
    [
        new()
        {
            Name = "weather",
            Description = "current weather or forecast for a place (defaults to home)",
            Examples = ["what\'s the weather like", "tell me about the weather", "is it going to rain today", "how hot is it outside", "do I need a jacket"],
            Patterns =
            [
                @"^(what('?s| is) the )?weather( like)?( (?<when>today|tomorrow))?( (in|at|for) (?<place>.+?))?( (?<when2>today|tomorrow))?\??$",
                @"^will it rain( (?<when>today|tomorrow))?( in (?<place>.+))?\??$",
                @"^(what('?s| is)|how('?s| is)) it (like )?outside\??$",
            ],
            SlotHints = ["place", "when"],
            Handler = async ctx =>
            {
                var when = ctx.Slot("when") ?? ctx.Slot("when2") ?? "today";
                var spoken = await weather.SpokenForecastAsync(ctx.Slot("place"), when);
                return new CommandResult(spoken ?? "The weather service didn't answer — try again in a minute.");
            }
        },
    ];
}

public class CryptoNewsModule(CryptoService crypto, NewsService news) : IJarvisModule
{
    public string Name => "cryptonews";

    public IEnumerable<CommandDefinition> Commands =>
    [
        new()
        {
            Name = "crypto_price",
            Description = "live price of a cryptocurrency",
            Examples = ["what\'s bitcoin at", "how much is ethereum right now", "check crypto prices"],
            Patterns = [@"^(what('?s| is) the )?price of (?<coin>[\w -]+?)\??$", @"^how much is (?<coin>[\w -]+?)( worth)?\??$"],
            SlotHints = ["coin"],
            Handler = async ctx =>
            {
                var coin = ctx.Slot("coin") ?? "";
                var prices = await crypto.PricesAsync([coin]);
                if (prices is null || prices.Count == 0) return new CommandResult("") { Unhandled = true }; // not a coin → tier-3
                var (id, usd) = prices.First();
                return new CommandResult($"{Cap(id)} is at {CryptoService.SpeakPrice(usd)}.", new { id, usd });
            }
        },
        new()
        {
            Name = "crypto_watchlist",
            Description = "prices for the crypto watchlist",
            Patterns = [@"^crypto( prices| watchlist| update)?\??$", @"^how('?s| is) (the )?crypto( market)?( doing)?\??$"],
            Handler = async ctx =>
            {
                var list = await crypto.WatchlistAsync();
                var prices = await crypto.PricesAsync(list);
                if (prices is null) return new CommandResult("CoinGecko didn't answer — try again shortly.");
                return new CommandResult(string.Join(". ", prices.Select(p => $"{Cap(p.Key)}: {CryptoService.SpeakPrice(p.Value)}")) + ".",
                    new { prices });
            }
        },
        new()
        {
            Name = "news_headlines",
            Description = "read the top news headlines",
            Patterns = [@"^(news|headlines|news headlines|what('?s| is) (in the news|happening in the world))\??$"],
            Handler = async ctx =>
            {
                var heads = await news.HeadlinesAsync();
                if (heads.Count == 0) return new CommandResult("No headlines came back from the feeds.");
                return new CommandResult("Headlines: " + string.Join(". ", heads.Select(h => h.Title)) + ".",
                    new { headlines = heads });
            }
        },
        new()
        {
            Name = "news_topic",
            Description = "latest news on a topic — spoken headlines + links card; 'show me' also opens the browser",
            Examples = ["pull up some crypto news", "show me the latest news about bitcoin",
                        "any news on openai", "what's the news about the stock market", "get me tech news"],
            Patterns = [@"^(?<show>show me |open |pull up )?(the )?(latest |today'?s )?news (on|about|for) (?<topic>.+)$",
                        @"^what('s| is) (in the news|the (latest )?news) (on|about) (?<topic>.+)$"],
            SlotHints = ["topic", "show"],
            Handler = async ctx =>
            {
                var topic = ctx.Slot("topic")!;
                ctx.Audit.Tool = "http:google-news-rss";
                var items = await news.TopicAsync(topic, 5);
                if (items.Count == 0) return new CommandResult($"No fresh news found on {topic}.");

                var browserUrl = $"https://news.google.com/search?q={Uri.EscapeDataString(topic)}";
                if (ctx.Slot("show") is "show me " or "open " && ctx.Source != "test")
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(browserUrl) { UseShellExecute = true }); } catch { }

                var spokenTop = items.Take(3).Select(i =>
                    i.Source.Length > 0 ? $"{i.Title}, from {i.Source}" : i.Title);
                return new CommandResult(
                    $"Latest on {topic}: " + string.Join(". ", spokenTop) + ".",
                    new
                    {
                        url = browserUrl,
                        card = new { type = "links", items = items.Select(i => new { title = i.Title, snippet = i.Source, url = i.Link }) },
                    });
            }
        },
    ];

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpper(s[0]) + s[1..].Replace("-", " ");
}

public class OpenThingsModule : IJarvisModule
{
    public string Name => "openthings";

    private static readonly Dictionary<string, string> Sites = new(StringComparer.OrdinalIgnoreCase)
    {
        ["youtube"] = "https://www.youtube.com", ["github"] = "https://github.com",
        ["gmail"] = "https://mail.google.com", ["google"] = "https://www.google.com",
        ["reddit"] = "https://www.reddit.com", ["stack overflow"] = "https://stackoverflow.com",
        ["stackoverflow"] = "https://stackoverflow.com", ["jira"] = "https://www.atlassian.com/software/jira",
        ["chatgpt"] = "https://chatgpt.com", ["claude"] = "https://claude.ai",
    };

    public IEnumerable<CommandDefinition> Commands =>
    [
        new()
        {
            Name = "show_pictures",
            Description = "show pictures of something (cards in the console + browser image search)",
            Patterns = [@"^(show me |pull up |find )?(pictures|images|photos) of (?<q>.+)$"],
            SlotHints = ["q"],
            Handler = async ctx =>
            {
                var q = ctx.Slot("q") ?? "";
                var result = await OpenUrl(ctx, $"https://www.bing.com/images/search?q={Uri.EscapeDataString(q)}",
                    $"Pulling up pictures of {q}.");
                var images = await ctx.S<WebSearchService>().ImageSearchAsync(q, take: 6);
                ctx.Audit.Tool = "http:duckduckgo-images";
                return images.Count == 0 ? result : new CommandResult(result.Spoken, new
                {
                    url = $"https://www.bing.com/images/search?q={Uri.EscapeDataString(q)}",
                    card = new { type = "images", items = images.Select(i => new { title = i.Title, thumb = i.Thumbnail, url = i.Url }) },
                });
            }
        },
        new()
        {
            Name = "play_video",
            Description = "search a video on YouTube",
            Patterns = [@"^play (?<q>.+?)( on youtube)?$", @"^(open|find) (?<q>.+) on youtube$"],
            SlotHints = ["q"],
            Handler = ctx => OpenUrl(ctx, $"https://www.youtube.com/results?search_query={Uri.EscapeDataString(ctx.Slot("q") ?? "")}",
                $"Searching YouTube for {ctx.Slot("q")}.")
        },
        new()
        {
            Name = "open_site",
            Description = "open a known site or domain in the browser",
            Patterns = [@"^open (?<site>youtube|github|gmail|google|reddit|stack ?overflow|jira|chatgpt|claude)$",
                        @"^open (?<url>[\w-]+(\.[\w-]+)+(/\S*)?)$"],
            SlotHints = ["site", "url"],
            Handler = ctx =>
            {
                var url = ctx.Slot("site") is { } site && Sites.TryGetValue(site, out var known) ? known
                        : ctx.Slot("url") is { } u ? (u.StartsWith("http") ? u : "https://" + u)
                        : null;
                // Slots filled by the semantic/LLM tiers are not re-checked against the regex,
                // so refuse anything that is not a plain http(s) URL before handing it to the shell.
                if (url is not null && (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)
                        || (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp)))
                    url = null;
                return url is null
                    ? Task.FromResult(new CommandResult("") { Unhandled = true })
                    : OpenUrl(ctx, url, $"Opening {ctx.Slot("site") ?? ctx.Slot("url")}.");
            }
        },
        new()
        {
            Name = "web_search",
            Description = "search the web (result cards in the console + browser)",
            Patterns = [@"^(search|google)( the web)?( for)? (?<q>.+)$"],
            SlotHints = ["q"],
            Handler = async ctx =>
            {
                var q = ctx.Slot("q") ?? "";
                var result = await OpenUrl(ctx, $"https://duckduckgo.com/?q={Uri.EscapeDataString(q)}",
                    $"Searching for {q}.");
                var hits = await ctx.S<WebSearchService>().SearchAsync(q, take: 4);
                ctx.Audit.Tool = "http:duckduckgo";
                if (hits.Count == 0) return result;
                var top = hits[0];
                return new CommandResult($"Searching for {q}. Top result: {top.Title}.", new
                {
                    url = $"https://duckduckgo.com/?q={Uri.EscapeDataString(q)}",
                    card = new { type = "links", items = hits.Select(h => new { title = h.Title, snippet = h.Snippet, url = h.Url }) },
                });
            }
        },
    ];

    private static Task<CommandResult> OpenUrl(CommandContext ctx, string url, string spoken)
    {
        if (ctx.Source != "test") // verification runs shouldn't pop browser windows overnight
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { return Task.FromResult(new CommandResult($"Couldn't open the browser: {ex.Message}")); }
        }
        return Task.FromResult(new CommandResult(spoken, new { url }));
    }
}
