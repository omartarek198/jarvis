using Jarvis.Core;
using Jarvis.Kernel.Modules;
using Jarvis.Kernel.Services;
using Microsoft.EntityFrameworkCore;

// Pin content root + web root to the project dir: by default both follow the launch cwd, so
// appsettings.json and wwwroot/ silently vanished when the kernel was started from the repo
// root (config fell back to code defaults — Graph ClientId etc. never loaded).
var projectDir = Path.Combine(JarvisPaths.Root, "src", "Jarvis.Kernel");
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = Directory.Exists(projectDir) ? projectDir : null,
    WebRootPath = Directory.Exists(Path.Combine(projectDir, "wwwroot")) ? Path.Combine(projectDir, "wwwroot") : null,
});

// SecretsService is needed before Build() (LAN cert password) — register the same instance
using var earlyLoggerFactory = LoggerFactory.Create(b => b.AddConsole());
var secrets = new SecretsService(earlyLoggerFactory.CreateLogger<SecretsService>());

builder.WebHost.ConfigureKestrel(o =>
{
    var localPort = new Uri(builder.Configuration["Jarvis:Urls"] ?? "http://localhost:5588").Port;
    o.ListenLocalhost(localPort);

    // LAN voice endpoint: HTTPS because phone browsers refuse getUserMedia on insecure origins
    if (!string.Equals(builder.Configuration["Jarvis:Lan:Enabled"], "false", StringComparison.OrdinalIgnoreCase))
    {
        var lanPort = int.TryParse(builder.Configuration["Jarvis:Lan:Port"], out var lp) ? lp : 5589;
        try
        {
            var cert = LanCertService.LoadOrCreate(secrets, earlyLoggerFactory.CreateLogger("LanCert"));
            o.ListenAnyIP(lanPort, lo => lo.UseHttps(cert));
        }
        catch (Exception ex)
        {
            earlyLoggerFactory.CreateLogger("LanCert").LogWarning("LAN endpoint disabled: {Msg}", ex.Message);
        }
    }
});

builder.Services.AddDbContext<JarvisDb>(o => o.UseSqlite($"Data Source={JarvisPaths.DbFile}"));
builder.Services.AddSingleton<IEventBus, EventBus>();
builder.Services.AddSingleton<MemoryIndex>();
builder.Services.AddSingleton<PendingActionService>();
builder.Services.AddSingleton<OllamaIntentService>();
builder.Services.AddSingleton<SttClient>();
builder.Services.AddSingleton<TtsService>();
builder.Services.AddSingleton<GitService>();
builder.Services.AddSingleton<AgentService>();
builder.Services.AddSingleton<PlaywrightService>();
builder.Services.AddSingleton<DialogContext>();
builder.Services.AddSingleton<WebSearchService>();
builder.Services.AddSingleton<EmbeddingService>();
builder.Services.AddSingleton<SemanticRouter>();
builder.Services.AddSingleton<GeneralAnswerService>();
builder.Services.AddSingleton<SystemControlService>();
builder.Services.AddSingleton<ScreenshotService>();
builder.Services.AddSingleton<WeatherService>();
builder.Services.AddSingleton<CryptoService>();
builder.Services.AddSingleton<NewsService>();
builder.Services.AddSingleton(secrets);
builder.Services.AddSingleton<EmailService>();
builder.Services.AddSingleton<ImapEmailService>();
builder.Services.AddSingleton<CalendarService>();
builder.Services.AddSingleton<GraphService>();
builder.Services.AddScoped<EntityResolver>();
builder.Services.AddScoped<Prioritizer>();
builder.Services.AddScoped<ProjectStatusService>();
builder.Services.AddScoped<BriefingService>();
builder.Services.AddScoped<WarRoomService>();
builder.Services.AddSingleton<Jarvis.Kernel.Services.Council.CouncilClassifier>();
builder.Services.AddSingleton<Jarvis.Kernel.Services.Council.CouncilStatements>();
builder.Services.AddSingleton<Jarvis.Kernel.Services.Council.CouncilService>();
builder.Services.AddSingleton<AwayModeService>();
builder.Services.AddSingleton<ExternalActions>();

builder.Services.AddSingleton<IJarvisModule, SystemModule>();
builder.Services.AddSingleton<IJarvisModule, AuditModule>();     // early: "what did you just do" / "retry that" are short, greedy phrases
builder.Services.AddSingleton<IJarvisModule, ContextModule>();   // before Commitments: "close it" must win over close-by-text
builder.Services.AddSingleton<IJarvisModule, SleepModule>();     // before Briefing: "good morning/night" log sleep AND chain the briefing
builder.Services.AddSingleton<IJarvisModule, HabitsModule>();    // before Tasks/Commitments: "log gym" / "did i gym" fall through when unmatched
builder.Services.AddSingleton<IJarvisModule, BriefingModule>();
builder.Services.AddSingleton<IJarvisModule, RemindersModule>(); // before Commitments: timed "remind me at/in" wins over capture
builder.Services.AddSingleton<IJarvisModule, FactsModule>();
builder.Services.AddSingleton<IJarvisModule, WeatherModule>();
builder.Services.AddSingleton<IJarvisModule, CryptoNewsModule>();
builder.Services.AddSingleton<IJarvisModule, EmailModule>();
builder.Services.AddSingleton<IJarvisModule, TeamsModule>();     // before Calendar: "what meetings" prefers Graph, falls through to COM
builder.Services.AddSingleton<IJarvisModule, CalendarModule>();
builder.Services.AddSingleton<IJarvisModule, AgentsModule>();
builder.Services.AddSingleton<IJarvisModule, PlaywrightModule>();
builder.Services.AddSingleton<IJarvisModule, WarRoomModule>();
builder.Services.AddSingleton<IJarvisModule, CouncilModule>();   // before Focus: "war council"/"quick council" are specific phrases
builder.Services.AddSingleton<IJarvisModule, AwayModeModule>();  // before Focus: "I'm leaving/back" are specific phrases
builder.Services.AddSingleton<IJarvisModule, FocusModule>();
builder.Services.AddSingleton<IJarvisModule, CommitmentsModule>();
builder.Services.AddSingleton<IJarvisModule, TasksModule>();
builder.Services.AddSingleton<IJarvisModule, GitModule>();
builder.Services.AddSingleton<IJarvisModule, MeetingsModule>();
builder.Services.AddSingleton<IJarvisModule, VoiceModule>();
builder.Services.AddSingleton<IJarvisModule, IdeasModule>();
builder.Services.AddSingleton<IJarvisModule, NotesModule>();
builder.Services.AddSingleton<IJarvisModule, PhoneModule>();
builder.Services.AddSingleton<IJarvisModule, SystemControlModule>(); // before OpenThings: "open spotify"/"pause the music" must not become web searches
builder.Services.AddSingleton<IJarvisModule, OpenThingsModule>(); // after Notes: "search memory for" must win over web search
builder.Services.AddSingleton<IJarvisModule, ProjectsModule>();
builder.Services.AddSingleton<CommandRouter>();

builder.Services.AddHostedService<EventForwarder>();
builder.Services.AddHostedService<AuditSubscriber>();
builder.Services.AddHostedService<SttSupervisor>();
builder.Services.AddHostedService<WakeSupervisor>();
builder.Services.AddHostedService<KokoroSupervisor>();
builder.Services.AddHostedService<BrainWatcher>();
builder.Services.AddHostedService<FocusSampler>();
builder.Services.AddHostedService<SchedulerService>();
builder.Services.AddHostedService<TeamsPoller>();
builder.Services.AddHostedService<EmailPoller>();
builder.Services.AddSingleton<NtfyService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<NtfyService>());
builder.Services.AddSignalR();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<JarvisDb>();
    var demoMode = app.Services.GetRequiredService<ExternalActions>().DemoMode;
    await Seeder.SeedAsync(db, app.Services.GetRequiredService<MemoryIndex>(), demoMode);
    SchemaUpgrader.Apply(app.Services.GetRequiredService<ILogger<Program>>());  // after EnsureCreated: migrate pre-existing DBs
    await app.Services.GetRequiredService<TtsService>().LoadProfileAsync();
    foreach (var p in db.Projects.Where(p => p.Slug == Seeder.PrimaryProjectSlug || p.Slug == "jarvis"))
        await TesterIssuesService.EnsureInboxExistsAsync(p);
    app.Services.GetRequiredService<SecretsService>().ImportSetupFileIfPresent();
    if (!secrets.Has("lan.apikey"))
        secrets.Set("lan.apikey", Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant());
}

// Build the semantic routing index in the background — tier-2 uses the legacy LLM path until ready
_ = Task.Run(() => app.Services.GetRequiredService<SemanticRouter>()
    .InitializeAsync(app.Services.GetRequiredService<CommandRouter>().Definitions));

// Cross-origin guard. Browsers attach an Origin header to cross-site requests and to every
// WebSocket handshake; the kernel's own pages are same-origin and the tray/CLI send none.
// Refusing foreign origins stops other websites from driving /api or listening on /ws.
app.Use(async (http, next) =>
{
    var origin = http.Request.Headers.Origin.FirstOrDefault();
    var guarded = http.Request.Path.StartsWithSegments("/api") || http.Request.Path.StartsWithSegments("/ws");
    if (guarded && !string.IsNullOrEmpty(origin)
        && !(Uri.TryCreate(origin, UriKind.Absolute, out var o)
             && string.Equals(o.Authority, http.Request.Host.Value, StringComparison.OrdinalIgnoreCase)))
    {
        http.Response.StatusCode = 403;
        await http.Response.WriteAsJsonAsync(new { error = "cross-origin request refused" });
        return;
    }
    await next();
});

// LAN guard: non-loopback callers need the key for anything beyond the static PWA shell.
// Header only (never the query string, which leaks into logs/history), constant-time compare.
app.Use(async (http, next) =>
{
    var remote = http.Connection.RemoteIpAddress;
    var isLocal = remote is null || System.Net.IPAddress.IsLoopback(remote);
    if (!isLocal && (http.Request.Path.StartsWithSegments("/api") || http.Request.Path.StartsWithSegments("/ws")
                     || http.Request.Path.StartsWithSegments("/swagger")))
    {
        var key = http.Request.Headers["X-Jarvis-Key"].FirstOrDefault() ?? "";
        var expected = secrets.Get("lan.apikey") ?? "";
        var ok = expected.Length > 0 && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(key), System.Text.Encoding.UTF8.GetBytes(expected));
        if (!ok)
        {
            http.Response.StatusCode = 401;
            await http.Response.WriteAsJsonAsync(new { error = "missing or wrong X-Jarvis-Key" });
            return;
        }
    }
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseSwagger();
app.UseSwaggerUI();

app.MapPost("/api/command", async (CommandRequest req, CommandRouter router, HttpContext http) =>
{
    var r = await router.RouteAsync(req.Text, req.Source ?? "ui", http.RequestServices);
    return Results.Ok(r);
});

app.MapPost("/api/voice/utterance", async (HttpRequest request, SttClient stt, CommandRouter router, HttpContext http, ILogger<Program> log) =>
{
    using var ms = new MemoryStream();
    await request.Body.CopyToAsync(ms);
    if (ms.Length < 1000)
        return Results.Ok(new { transcript = (string?)null, intent = "noise", spoken = "", awaitingConfirmation = false });
    await File.WriteAllBytesAsync(Path.Combine(JarvisPaths.Data, "last-utterance.wav"), ms.ToArray()); // mic diagnostics
    var text = await stt.TranscribeAsync(ms.ToArray());
    log.LogInformation("[voice] utterance {Bytes} bytes -> {Transcript}", ms.Length, text ?? "(null)");
    if (string.IsNullOrWhiteSpace(text))
        return Results.Ok(new { transcript = (string?)null, intent = "stt_offline", spoken = "Speech recognition is offline or heard nothing.", awaitingConfirmation = false });
    var r = await router.RouteAsync(text, "voice", http.RequestServices);
    return Results.Ok(new { transcript = text, intent = r.Intent, spoken = r.Spoken, awaitingConfirmation = r.AwaitingConfirmation });
});

// Transcribe-only (no routing) — the tray uses this in dictation mode
app.MapPost("/api/voice/transcribe", async (HttpRequest request, SttClient stt) =>
{
    using var ms = new MemoryStream();
    await request.Body.CopyToAsync(ms);
    if (ms.Length < 1000) return Results.Ok(new { text = (string?)null });
    return Results.Ok(new { text = await stt.TranscribeAsync(ms.ToArray()) });
});

app.MapPost("/api/voice/tts", async (TtsRequest req, TtsService tts) =>
{
    var wav = await tts.SynthesizeAsync(req.Text, req.Profile);
    return wav.Length == 0 ? Results.NoContent() : Results.Bytes(wav, "audio/wav");
});

// Single round-trip voice loop: wav in -> transcribe -> route -> synthesized reply wav out.
// Transcript/intent ride in URL-encoded headers so the tray needs exactly one request.
app.MapPost("/api/voice/converse", async (HttpRequest request, HttpResponse response,
    SttClient stt, CommandRouter router, TtsService tts, IEventBus bus, JarvisDb db, HttpContext http, ILogger<Program> log) =>
{
    using var ms = new MemoryStream();
    await request.Body.CopyToAsync(ms);
    if (ms.Length < 1000) return Results.NoContent();
    var bytes = ms.ToArray();
    await File.WriteAllBytesAsync(Path.Combine(JarvisPaths.Data, "last-utterance.wav"), bytes); // mic diagnostics
    var peak = 0;
    for (var i = 44; i + 1 < bytes.Length; i += 2)
    {
        var s = Math.Abs((int)BitConverter.ToInt16(bytes, i));
        if (s > peak) peak = s;
    }
    var text = await stt.TranscribeAsync(bytes);
    log.LogInformation("[voice] utterance {Bytes} bytes peak={Peak} -> {Transcript}", ms.Length, peak, text ?? "(null)");

    // Wake-triggered captures get assistant-style manners: "Jarvis" alone is a hail, not noise.
    var wakeTriggered = request.Headers["X-Jarvis-Trigger"].FirstOrDefault() == "wake";
    string[] acks = ["Yes?", "Listening.", "Sir?", "Go ahead."];
    string[] hellos = ["Hello, sir. What can I do for you?", "Hey. What do you need?", "At your service."];
    var normalized = text is null ? "" : CommandRouter.Normalize(text);

    string spoken; string intent; var awaiting = false; var listenAgain = false;
    void Announce() => bus.Publish(new JarvisEvent("router.response",
        new { intent, spoken, tier = "voice", awaitingConfirmation = false }, "voice"));

    if (string.IsNullOrWhiteSpace(text) && wakeTriggered)
    {
        // Heard the wake word, then nothing intelligible — acknowledge and keep listening
        spoken = acks[Random.Shared.Next(acks.Length)];
        intent = "wake_prompt"; listenAgain = true; Announce();
    }
    else if (string.IsNullOrWhiteSpace(text))
    {
        spoken = peak < 200
            ? "Your microphone delivered silence — check the headset connection."
            : "I heard audio but couldn't make out any words. Try again.";
        intent = "stt_empty"; Announce();
    }
    else if (normalized.Length == 0)
    {
        // The capture caught ONLY the wake word ("Jarvis…", "hey Jarvis") — the user paused
        // for acknowledgment. Answer like an assistant and tell the tray to keep listening.
        spoken = acks[Random.Shared.Next(acks.Length)];
        intent = "wake_prompt"; listenAgain = true; Announce();
    }
    else if (System.Text.RegularExpressions.Regex.IsMatch(normalized,
        @"^(hey|hi|hello|yo|sup|hey there|hello there|what's up)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
    {
        // "Jarvis, hey" / "hello jarvis" — a casual greeting deserves one back
        spoken = hellos[Random.Shared.Next(hellos.Length)];
        intent = "wake_greeting"; listenAgain = true; Announce();
    }
    else
    {
        var r = await router.RouteAsync(text!, "voice", http.RequestServices);
        spoken = r.Spoken; intent = r.Intent; awaiting = r.AwaitingConfirmation;
    }

    response.Headers["X-Jarvis-Transcript"] = Uri.EscapeDataString(text ?? "");
    response.Headers["X-Jarvis-Spoken"] = Uri.EscapeDataString(spoken);
    response.Headers["X-Jarvis-Intent"] = intent;
    response.Headers["X-Jarvis-Confirm"] = awaiting ? "1" : "0";
    response.Headers["X-Jarvis-Listen"] = listenAgain ? "1" : "0";
    response.Headers["X-Jarvis-Output"] = (await db.Settings.FindAsync("voice.output"))?.ValueJson.Trim('"') ?? "auto";
    response.Headers["X-Jarvis-Conv"] = (await db.Settings.FindAsync("voice.conversationMode"))?.ValueJson.Trim('"') ?? "off";
    response.Headers["X-Jarvis-Wake"] = (await db.Settings.FindAsync("voice.wakeWord"))?.ValueJson.Trim('"') ?? "on";
    var wav = await tts.SynthesizeAsync(spoken);
    return Results.Bytes(wav, "audio/wav");
});

// Tray reports push-to-talk state so the orb reacts the instant the key goes down
app.MapPost("/api/voice/state", (VoiceStateRequest req, IEventBus bus) =>
{
    bus.Publish(new JarvisEvent("voice.state", new { state = req.State }, "voice"));
    return Results.Ok();
});

// Tray streams 3-channel audio levels (ambient mic / active capture / JARVIS speech) at 10Hz
// for the console's voice scope. Never audited (see AuditSubscriber.Skip) — pure telemetry.
app.MapPost("/api/voice/levels", (VoiceLevelsRequest req, IEventBus bus) =>
{
    bus.Publish(new JarvisEvent("voice.levels", new { a = req.A, c = req.C, s = req.S }, "voice"));
    return Results.Ok();
});

// Tray reports Windows lock/unlock — feeds sleep inference (events table via AuditSubscriber)
app.MapPost("/api/session/state", (VoiceStateRequest req, IEventBus bus) =>
{
    bus.Publish(new JarvisEvent("session.state", new { state = req.State }, "system"));
    return Results.Ok();
});

app.MapGet("/api/projects", async (JarvisDb db) => await db.Projects.AsNoTracking().ToListAsync());

app.MapGet("/api/projects/{slug}", async (string slug, JarvisDb db, ProjectStatusService status) =>
{
    var p = await db.Projects.AsNoTracking().FirstOrDefaultAsync(x => x.Slug == slug);
    if (p is null) return Results.NotFound();
    return Results.Ok(new
    {
        project = p,
        spokenStatus = await status.SynthesizeAsync(p),
        tasks = await db.Tasks.AsNoTracking().Where(t => t.ProjectId == p.Id && t.Status != "done" && t.Status != "dropped").ToListAsync(),
        commitments = await db.Commitments.AsNoTracking().Where(c => c.ProjectId == p.Id && c.Status == "open").ToListAsync(),
        notes = await db.Notes.AsNoTracking().Where(n => n.ProjectId == p.Id).OrderByDescending(n => n.CreatedAt).Take(10).ToListAsync(),
        repos = await db.Repos.AsNoTracking().Where(r => r.ProjectId == p.Id).ToListAsync(),
    });
});

app.MapGet("/api/commitments", async (string? status, JarvisDb db) =>
    await db.Commitments.AsNoTracking()
        .Where(c => status == null || c.Status == status)
        .OrderBy(c => c.DueDate ?? DateTime.MaxValue).ToListAsync());

app.MapGet("/api/tasks", async (string? project, JarvisDb db, EntityResolver resolver) =>
{
    var p = await resolver.ResolveProjectAsync(project);
    return await db.Tasks.AsNoTracking()
        .Where(t => t.Status != "done" && t.Status != "dropped")
        .Where(t => p == null || t.ProjectId == p.Id)
        .ToListAsync();
});

// Command audit trail: one row per routed utterance (did it understand, what ran, did it work)
app.MapGet("/api/commands", async (int? take, JarvisDb db) =>
    await db.CommandLogs.AsNoTracking().OrderByDescending(c => c.CreatedAt)
        .Take(take is > 0 and <= 500 ? take.Value : 50).ToListAsync());

app.MapGet("/api/commands/stats", async (int? days, JarvisDb db) =>
{
    var since = DateTime.UtcNow.AddDays(-(days is > 0 and <= 90 ? days.Value : 7));
    var rows = await db.CommandLogs.AsNoTracking()
        .Where(c => c.CreatedAt >= since)
        .Select(c => new { c.Intent, c.Status, c.Tier }).ToListAsync();
    return new
    {
        total = rows.Count,
        byStatus = rows.GroupBy(r => r.Status).Select(g => new { status = g.Key, count = g.Count() }),
        byTier = rows.GroupBy(r => r.Tier).Select(g => new { tier = g.Key, count = g.Count() }),
        byIntent = rows.GroupBy(r => r.Intent).Select(g => new
        {
            intent = g.Key,
            total = g.Count(),
            ok = g.Count(r => r.Status == "ok"),
            failed = g.Count(r => r.Status == "failed"),
        }).OrderByDescending(x => x.failed).ThenByDescending(x => x.total),
    };
});

app.MapGet("/api/events", async (int? take, JarvisDb db) =>
    await db.Events.AsNoTracking().OrderByDescending(e => e.At).Take(take is > 0 and <= 500 ? take.Value : 50).ToListAsync());

app.MapGet("/api/briefings/latest", async (string? kind, JarvisDb db) =>
    await db.Briefings.AsNoTracking()
        .Where(b => kind == null || b.Kind == kind)
        .OrderByDescending(b => b.At).FirstOrDefaultAsync());

app.MapGet("/api/search", (string q, MemoryIndex idx) =>
    idx.Search(q).Select(h => new { h.Kind, h.RefId, h.Title, h.Snippet }));

app.MapGet("/api/system/health", async (SttClient stt, OllamaIntentService ollama, TtsService tts, JarvisDb db, GraphService graph,
    AgentService agents, ExternalActions external) => new
{
    kernel = "online",
    demoMode = external.DemoMode,
    agentMode = agents.Mode,               // "demo" = scripted adapter, "cli" = real Claude Code / Codex
    externalActions = external.Enabled,
    stt = await stt.IsHealthyAsync(),
    ollama = await ollama.IsHealthyAsync(),
    graph = graph.Status,
    ttsEngine = tts.Engine,
    lastTtsEngine = tts.LastEngineUsed,   // what ACTUALLY spoke last — exposes silent fallbacks
    voiceProfile = tts.CurrentProfile,
    voiceOutput = (await db.Settings.FindAsync("voice.output"))?.ValueJson.Trim('"') ?? "auto",
    projects = await db.Projects.CountAsync(),
    machine = external.DemoMode ? "demo" : Environment.MachineName,   // keep hostnames out of demo recordings
});

// Phone pairing secrets: loopback callers only, and never routed through the command pipeline
// (command results are broadcast over SignalR and written to the audit table).
app.MapGet("/api/phone/pairing", (HttpContext http, SecretsService s, NtfyService ntfy, ExternalActions external) =>
{
    var remote = http.Connection.RemoteIpAddress;
    if (remote is not null && !System.Net.IPAddress.IsLoopback(remote)) return Results.StatusCode(403);
    return Results.Ok(new
    {
        apiKey = s.Get("lan.apikey"),
        ntfyTopic = external.Enabled ? ntfy.Topic : null,
    });
});

app.MapPost("/api/system/stop", (IEventBus bus) =>
{
    bus.Publish(new JarvisEvent("system.stop", new { reason = "api" }, "ui"));
    return Results.Ok(new { stopped = true });
});

app.MapGet("/api/agent-runs", async (int? take, JarvisDb db) =>
    await db.AgentRuns.AsNoTracking().OrderByDescending(r => r.CreatedAt)
        .Take(take is > 0 and <= 100 ? take.Value : 20)
        .Select(r => new { r.Id, r.Agent, r.PromptTemplate, r.Mode, r.Status, r.ResultSummary, r.StartedAt, r.EndedAt, r.ProjectId })
        .ToListAsync());

app.MapGet("/api/agent-runs/{id}", async (string id, JarvisDb db) =>
    await db.AgentRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id) is { } run
        ? Results.Ok(run) : Results.NotFound());

app.MapGet("/api/playwright-runs", async (int? take, JarvisDb db) =>
    await db.PlaywrightRuns.AsNoTracking().OrderByDescending(r => r.CreatedAt)
        .Take(take is > 0 and <= 100 ? take.Value : 10).ToListAsync());

app.MapGet("/api/projects/{slug}/warroom", async (string slug, JarvisDb db, WarRoomService warRoom) =>
{
    var p = await db.Projects.AsNoTracking().FirstOrDefaultAsync(x => x.Slug == slug);
    return p is null ? Results.NotFound() : Results.Ok(await warRoom.BuildAsync(p));
});

// Append typed notes or a pasted Teams transcript to the active meeting (used by end-meeting extraction)
app.MapPost("/api/meetings/notes", async (MeetingNoteRequest req, JarvisDb db) =>
{
    var m = await db.Meetings.FirstOrDefaultAsync(x => x.EndedAt == null);
    if (m is null) return Results.BadRequest(new { error = "no active meeting — say 'start meeting mode' first" });
    m.NotesMd += (m.NotesMd.Length > 0 ? "\n" : "") + req.Text.Trim();
    m.UpdatedAt = DateTime.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok(new { meetingId = m.Id, length = m.NotesMd.Length });
});

app.MapGet("/api/reminders", async (JarvisDb db) =>
    await db.Reminders.AsNoTracking().Where(r => r.Status == "pending").OrderBy(r => r.At).ToListAsync());

app.MapGet("/api/focus/today", async (JarvisDb db) =>
{
    var samples = await db.FocusSamples.AsNoTracking().Where(f => f.At >= DateTime.UtcNow.Date).ToListAsync();
    return new
    {
        byCategory = samples.GroupBy(s => s.Category)
            .Select(g => new { category = g.Key, minutes = g.Sum(s => s.DurationSec) / 60 })
            .OrderByDescending(x => x.minutes),
        topApps = samples.GroupBy(s => s.ProcessName)
            .Select(g => new { app = g.Key, minutes = g.Sum(s => s.DurationSec) / 60 })
            .OrderByDescending(x => x.minutes).Take(8),
    };
});

app.MapGet("/api/council/sessions", async (int? take, JarvisDb db) =>
    await db.CouncilSessions.AsNoTracking().OrderByDescending(s => s.CreatedAt)
        .Take(take is > 0 and <= 100 ? take.Value : 20)
        .Select(s => new { s.Id, s.Mode, s.Category, s.FramedDecision, s.Status, s.FinalRecommendation, s.Confidence, s.CreatedAt, s.EndedAt })
        .ToListAsync());

app.MapGet("/api/council/active", (Jarvis.Kernel.Services.Council.CouncilService council) =>
    council.ActiveSessionId is { } id ? Results.Ok(new { sessionId = id, active = true }) : Results.NoContent());

app.MapGet("/api/council/sessions/{id}", async (string id, JarvisDb db) =>
{
    var s = await db.CouncilSessions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
    if (s is null) return Results.NotFound();
    return Results.Ok(new
    {
        session = s,
        turns = await db.CouncilTurns.AsNoTracking().Where(t => t.SessionId == id).OrderBy(t => t.Seq).ToListAsync(),
        votes = await db.CouncilVotes.AsNoTracking().Where(v => v.SessionId == id).ToListAsync(),
        actions = await db.CouncilActions.AsNoTracking().Where(a => a.SessionId == id).ToListAsync(),
    });
});

app.MapHub<JarvisHub>("/ws");

app.Run();

public record CommandRequest(string Text, string? Source);
public record TtsRequest(string Text, string? Profile = null);
public record MeetingNoteRequest(string Text);
public record VoiceStateRequest(string State);
public record VoiceLevelsRequest(float A, float C, float S);
