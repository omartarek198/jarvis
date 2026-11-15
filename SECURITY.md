# Security model

JARVIS is a single-user assistant that runs on the owner's Windows machine and can start
coding agents, send email and control the desktop. This file states what it defends
against, what it does not, and the defaults of this public edition.

## Trust boundaries

- **Loopback is trusted.** Any process on the machine that can reach
  `http://localhost:5588` can issue commands, including "confirm". There is no per-user
  authentication on loopback; the kernel is meant to be the only privileged service of a
  single-user desktop.
- **Browsers are not.** Requests carrying a foreign `Origin` (other websites, sandboxed
  iframes) are refused on `/api` and `/ws`, which blocks cross-site command submission and
  cross-site WebSocket listening. `AllowedHosts` is limited to `localhost`, `127.0.0.1` and
  `[::1]`, which blocks DNS-rebinding requests.
- **LAN clients need a key.** The optional HTTPS listener (off by default) requires an
  `X-Jarvis-Key` header on `/api`, `/ws` and `/swagger`, compared in constant time. The
  key is never accepted in the query string, never returned by a command, and is served
  only to loopback callers at `GET /api/phone/pairing`.

## The confirmation gate is a safety feature, not an authorization boundary

Mutate commands (agent edit runs, Jira writes, email, mailbox changes, screen capture,
PC lock) are parked until "confirm" arrives within two minutes, and the prompt names the
concrete parameters. This protects against misheard speech and accidental commands. It
does not stop a caller that already has API access, because that caller can also send
"confirm". There is one pending slot, shared by all local sources.

## Agents

- Read-only runs use Claude Code's plan mode or an explicit tool allowlist; Codex runs
  with `--sandbox read-only`.
- Edit runs use `acceptEdits` with a tool allowlist that includes build and test commands.
  An agent that can edit test files and then run them can execute arbitrary code in the
  target repository: treat an edit run as code execution, which is why it requires
  confirmation.
- Prompts are written to the agent's stdin, never to its command line.
- Agent transcripts can contain anything the agent read. They are stored under `data/`,
  which is git-ignored.

## Public-edition defaults

| Setting | Default | Effect |
|---|---|---|
| `Jarvis:Demo:Enabled` | `true` | Synthetic seed data (fictional projects, people, sample repo); hostname hidden from `/api/system/health` |
| `Jarvis:Agents:Mode` | `demo` | Agent runs launch the scripted `jarvis-demo-agent` adapter (no model, no file edits) |
| `Jarvis:ExternalActions:Enabled` | `false` (also when the key is absent) | No email send or mailbox access, no ntfy push, no Microsoft Graph sign-in, no Outlook COM, no away-mode daemon, no volume/media/lock/app launch |
| `Jarvis:Lan:Enabled` | `false` | No network listener beyond loopback |
| `Jarvis:Focus:SampleForegroundWindow` | `false` | Window titles are not recorded |
| `Jarvis:Backups:CommitBrains` | `false` | No automatic git commits |

Read-only internet lookups still work when asked for (weather via Open-Meteo, prices via
CoinGecko, news RSS, DuckDuckGo results for web search and for the general-answer tier).

## Known limitations

- No authentication between local processes (see above).
- `scripts/install-piper.ps1` downloads pinned releases without hash verification.
- Running `git` or `npx playwright test` in a registered repository honours that
  repository's own configuration. Register only repositories you trust.
- The away-mode email daemon that `AwayModeService` can supervise is a separate private
  tool and is not part of this repository. Its email channel authenticates by sender
  address only, which is why it stays behind the external-actions switch.

## Reporting

Please open a GitHub issue without exploit details, or contact the author through GitHub,
for anything security-relevant.
