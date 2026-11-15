# JARVIS — notes for coding agents

Voice-controlled orchestration kernel. Routing, data, speech and local models run on the
machine; coding work is delegated to the Claude Code and Codex CLIs (subscription-based,
no per-token API keys). See `README.md` and `docs/ARCHITECTURE.md` for the full picture.

## Layout

- `src/Jarvis.Core` — entities (EF Core SQLite), event bus, command contracts
- `src/Jarvis.Kernel` — ASP.NET Core host on `localhost:5588`: command router, modules,
  REST + SignalR (`/ws`), audit, sidecar supervision, web console (`wwwroot/`)
- `src/Jarvis.Tray` — WinForms tray: push-to-talk hook, mic capture, wake word, playback
- `src/Jarvis.Cli` — `jarvis "<utterance>"` / REPL test client
- `src/Jarvis.DemoAgent` — demo adapter standing in for the agent CLIs in demo mode
- `voice/` — Python sidecars: faster-whisper STT (:5591), openWakeWord (:5592), Kokoro TTS (:5593)
- `data/` — runtime state (SQLite, secret store, brains); never committed

## Commands

```
dotnet build Jarvis.sln                       # stop the kernel/tray first (file locks)
dotnet test tests/Jarvis.Tests
dotnet run --project src/Jarvis.Kernel        # kernel (demo mode by default)
src\Jarvis.Cli\bin\Debug\net9.0\Jarvis.Cli.exe "what should I focus on"
```

## Conventions that matter

- **Module registration order in `Program.cs` is load-bearing**: specific modules before
  greedy ones (`ProjectsModule` matches "what is <X>"). New command = a `CommandDefinition`
  in a module: anchored regex with named groups → slots; the handler resolves scoped
  services via `ctx.S<T>()` and returns the sentence JARVIS speaks. Return
  `Unhandled = true` to fall through to the semantic/LLM tiers. Give collision-prone
  commands `Examples` for the semantic router.
- **Safety**: `SafetyClass.Mutate` commands are parked by the router and need "confirm"
  within 2 minutes. Never bypass this for repo-mutating or outbound actions. Side effects
  that leave the machine also check `ExternalActions.Enabled`.
- **Every bus event** is audited to the `events` table (except `AuditSubscriber.Skip`) and
  forwarded to SignalR — never put secrets in event payloads or command results.
- Agent dispatch (`AgentService`) runs `claude -p --output-format stream-json` /
  `codex exec` as child processes with the prompt on stdin; prompts end with the RESULT
  contract block (appended automatically) so summaries and follow-ups parse.
- Schema changes go through `SchemaUpgrader` (keyed, idempotent SQL), never by deleting
  the database.
