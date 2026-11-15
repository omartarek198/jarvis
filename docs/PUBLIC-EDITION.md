# Public edition

This repository is a sanitized snapshot of a private, daily-use project. The source is the
real implementation. Personal data, credentials and private integrations were left out,
and a few defaults were changed so a fresh clone is safe to run.

## Real implementation vs demo adapter

| Part | Status in this repository |
|---|---|
| Kernel, router, 148 commands, safety gate, audit, event bus, SignalR, persistence | Real, unchanged in design |
| Agent orchestration (`AgentService`): process supervision, stream parsing, transcripts, RESULT contract, follow-up tasks, session resume | Real. Runs the real CLIs with `Jarvis:Agents:Mode=cli` |
| `src/Jarvis.DemoAgent` | **Demo adapter.** Stands in for `claude`/`codex` in demo mode: same arguments, same output formats, a deterministic read-only scan of the target directory, no model |
| Semantic routing, slot extraction, general answers, Council statements | Real; need Ollama locally. Without it, routing stays on the grammar tier and the Council uses its offline templates |
| Voice: tray, STT/wake/Kokoro sidecars, TTS chain, mastering, phrase cache | Real; need Python packages and model downloads (see README) |
| Web console, Council page, war room, mobile PWA | Real |
| `wwwroot/sim.js` (`?sim=1`) | **UI simulation** with canned events; it never talks to the kernel |
| Seed data (projects, tasks, commitments, tester issues, `samples/northwind-api`) | **Synthetic.** Every name is fictional |

## Excluded

| Excluded | Why |
|---|---|
| `data/` (SQLite database and backups, DPAPI secret store, LAN certificate, project "brains", agent transcripts, voice recordings, screenshots) | Personal and work data, credentials |
| Original git history | Contains personal data and large model binaries; this repository starts fresh |
| Kokoro model files (354 MB), Piper binaries and voices, rendered voice samples | Third-party binaries, size; downloaded locally instead |
| Voice bake-off renders and tool | Generated media and machine-specific paths; results are summarised in `docs/VOICE.md` |
| Planning documents and design hand-offs | Personal roadmap content; superseded by `docs/ARCHITECTURE.md` |
| Away-mode email daemon (a separate Python tool) | Private integration with its own credential and threat model; only the kernel-side supervisor is included, behind the external-actions switch |
| Account identifiers (Azure app/tenant ids, email addresses, Jira site, notification topics) | Private configuration |

## Changes made for publication

- **Neutral identity.** The owner's name, products, clients and locations were replaced
  with neutral wording ("the user", fictional sample projects, a neutral default city).
  One Council lens was generalised from a domain-specific persona to "Customer Voice".
- **Demo mode.** `Jarvis:Demo:Enabled` seeds synthetic data; `Jarvis:Agents:Mode=demo`
  routes agent runs to the demo adapter.
- **External-actions switch.** `ExternalActions` gates email, mailbox changes, ntfy,
  Microsoft Graph, Outlook COM, the away-mode daemon and OS control. Off unless explicitly
  enabled.
- **Hardening.**
  - Agent prompts go to stdin instead of argv.
  - Cross-origin requests are refused and `AllowedHosts` is limited to loopback names.
  - The LAN key is header-only and compared in constant time; Swagger sits behind the LAN guard.
  - Pairing secrets are no longer part of command results (which are broadcast and
    audited); they come from a loopback-only endpoint.
  - Screen capture requires confirmation, and `open_site` accepts only http(s) URLs.
  - Untrusted text is escaped in the war-room page.
  - Foreground-window sampling and the automatic git snapshot are opt-in.
- **Clarity.** Confirmation prompts and the console's confirm bar show the concrete
  parameters. The console's TTS badge recognises Kokoro.
- **Tests.** `PublicEditionTests.cs` covers the demo agent pipeline end to end and the
  new safety behaviour (98 tests in total).

## Running against real tools

1. Install the Claude Code CLI and/or the Codex CLI and sign in.
2. Copy `src/Jarvis.Kernel/appsettings.Live.example.json` to `appsettings.Live.json`
   (git-ignored), adjust it, and start the kernel with `ASPNETCORE_ENVIRONMENT=Live`.
   For a single setting, environment variables work too, e.g.
   `Jarvis__Agents__Mode=cli`.
3. Credentials never go into settings files. Create `data/setup-secrets.json` with a flat
   string map, for example
   `{"smtp.user": "you@example.com", "smtp.password": "<app password>"}`; the kernel
   imports it into the DPAPI store on startup and deletes the file.
4. Register your own repositories by voice or text: `register repo C:\path\to\repo for <project>`.

A sensible middle ground for screen recordings is demo data with real agents:
`Jarvis__Agents__Mode=cli` and nothing else. External actions stay off. Run it in a
throwaway clone, because edit-mode runs will change `samples/northwind-api`.
