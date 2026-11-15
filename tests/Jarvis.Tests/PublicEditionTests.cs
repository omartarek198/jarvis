using System.Collections.Concurrent;
using System.Text.Json;
using Jarvis.Core;
using Jarvis.Kernel.Modules;
using Jarvis.Kernel.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jarvis.Tests;

/// <summary>
/// End-to-end check of the agent pipeline in demo mode: the real AgentService launches the
/// jarvis-demo-agent adapter as a child process, streams its output, writes the transcript,
/// parses the RESULT contract and files follow-up tasks — the same code path a real
/// Claude Code / Codex run takes.
/// </summary>
public class DemoAgentPipelineTests
{
    private static string SampleRepo => Path.Combine(JarvisPaths.Root, "samples", "northwind-api");

    [Theory]
    [InlineData("claude")]   // stream-json parsing path
    [InlineData("codex")]    // plain-text parsing path
    public async Task DemoRun_GoesThroughSupervisor_ParsesResult_AndFilesFollowUps(string agent)
    {
        Environment.SetEnvironmentVariable("JARVIS_DEMO_AGENT_FAST", "1");   // inherited by the child
        var dbPath = Path.Combine(Path.GetTempPath(), $"jarvis-test-{Guid.NewGuid():N}.db");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<JarvisDb>(o => o.UseSqlite($"Data Source={dbPath}"));
        services.AddSingleton<IEventBus, EventBus>();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Jarvis:Agents:Mode"] = "demo" }).Build());
        services.AddSingleton<AgentService>();
        await using var sp = services.BuildServiceProvider();
        using (var scope = sp.CreateScope())
            await scope.ServiceProvider.GetRequiredService<JarvisDb>().Database.EnsureCreatedAsync();

        var bus = sp.GetRequiredService<IEventBus>();
        var done = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new ConcurrentQueue<string>();
        using var sub = bus.Subscribe(e =>
        {
            var payload = JsonSerializer.SerializeToElement(e.Payload);
            if (e.Kind == "agent.progress") progress.Enqueue(payload.GetProperty("text").GetString() ?? "");
            if (e.Kind == "agent.done") done.TrySetResult(payload);
            return Task.CompletedTask;
        });

        var agents = sp.GetRequiredService<AgentService>();
        Assert.Equal("demo", agents.Mode);
        var repo = new Repo { Name = "northwind-api", Path = SampleRepo };
        var (run, spoken) = await agents.DispatchAsync(agent, "Task from the user: list the open TODOs",
            "test-run", project: null, repo: repo, edit: false);
        Assert.Contains("demo adapter", spoken);

        var finished = await done.Task.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal("done", finished.GetProperty("status").GetString());

        using var scope2 = sp.CreateScope();
        var db = scope2.ServiceProvider.GetRequiredService<JarvisDb>();
        var row = await db.AgentRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id);
        Assert.Equal(0, row.ExitCode);
        Assert.StartsWith("Demo agent (scripted, no model) inspected", row.ResultSummary);
        Assert.False(string.IsNullOrEmpty(row.ResultJson));
        Assert.True(File.Exists(row.TranscriptPath));

        // The sample repo carries three TODO/FIXME markers → three follow-up tasks (Source=agent)
        var followUps = await db.Tasks.AsNoTracking().Where(t => t.Source == "agent").ToListAsync();
        Assert.Equal(3, followUps.Count);
        Assert.Contains(followUps, t => t.Title.Contains("app/invoices.py"));

        if (agent == "claude")
        {
            Assert.StartsWith("demo-", row.SessionId);                 // captured from the system/init line
            Assert.Contains(progress, p => p == "[tool] Glob");       // tool_use blocks surface as progress
        }

        try { File.Delete(row.TranscriptPath!); } catch { /* best effort */ }
    }
}

public class PublicEditionSafetyTests
{
    private static IConfiguration Config(params (string Key, string Value)[] pairs) => new ConfigurationBuilder()
        .AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => (string?)p.Value)).Build();

    [Fact]
    public void ExternalActions_DefaultToOff_WhenKeyIsMissing()
    {
        var external = new ExternalActions(Config());
        Assert.False(external.Enabled);
        Assert.False(external.DemoMode);
    }

    [Fact]
    public void ExternalActions_CanBeEnabledExplicitly()
        => Assert.True(new ExternalActions(Config(("Jarvis:ExternalActions:Enabled", "true"))).Enabled);

    [Fact]
    public async Task SystemControl_IsBlocked_WhenExternalActionsAreOff()
    {
        var module = new SystemControlModule(new SystemControlService(NullLogger<SystemControlService>.Instance),
            new ExternalActions(Config()));
        foreach (var def in module.Commands)
        {
            var result = await def.Handler(new CommandContext { Utterance = "test", Source = "test", Services = null! });
            Assert.Contains("switched off", result.Spoken);
        }
    }

    [Fact]
    public async Task PhoneSetup_NeverReturnsSecrets_InTheCommandResult()
    {
        // Command results are broadcast over SignalR and written to the audit table, so the
        // LAN API key and ntfy topic must not appear in them.
        var module = new PhoneModule(new ExternalActions(Config()));
        foreach (var def in module.Commands)
        {
            var result = await def.Handler(new CommandContext { Utterance = "test", Source = "test", Services = null! });
            var json = JsonSerializer.Serialize(result.Data);
            Assert.DoesNotContain("apiKey", json);
            Assert.DoesNotContain("ntfyTopic", json);
            Assert.DoesNotContain("ntfy.sh/", json);
        }
    }

    [Fact]
    public void ConfirmationPrompt_NamesTheParameters()
    {
        var details = CommandRouter.DescribeSlots(new Dictionary<string, string>
            { ["task"] = "rename the invoice export", ["project"] = "northwind", ["empty"] = " " });
        Assert.Equal(" (task: rename the invoice export; project: northwind)", details);
        Assert.Equal("", CommandRouter.DescribeSlots(new Dictionary<string, string>()));
    }
}
