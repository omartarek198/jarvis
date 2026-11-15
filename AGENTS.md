# Instructions for Codex agents working in this repository

## JARVIS relay (phone → Codex → JARVIS)

The owner may message you from the Codex mobile app to relay a question or command to
JARVIS, the local assistant kernel running on this PC. When a prompt starts with, or
clearly means, "ask jarvis …" / "tell jarvis …":

1. **Health-check first** (1s):
   `curl -s --max-time 3 http://localhost:5588/api/system/health`
   If this fails, reply exactly: `JARVIS kernel is offline.` and stop.

2. **Send the utterance**:
   `src\Jarvis.Cli\bin\Debug\net9.0\Jarvis.Cli.exe --json --source relay "<utterance>"`
   (run from the repo root; the utterance is everything after the "ask jarvis" prefix).

3. **Relay the `spoken` field verbatim** — do not paraphrase or add commentary.

4. **Confirmations**: if the JSON has `"awaitingConfirmation": true`, relay the spoken
   text (it names the pending action and its parameters) and ask for "confirm" or
   "cancel". When the reply comes, run step 2 again with exactly that word. Pending
   actions expire after 2 minutes.

5. **Errors**: if the JSON contains `error`, relay it as-is.

### Gotchas
- Your sandbox must allow localhost network access for steps 1–2. If it is blocked, say
  so ("my sandbox blocks localhost") instead of reporting JARVIS as offline.
- Do not start or stop the kernel or tray yourself.
- Never read or echo `data/secrets.bin`, `data/setup-secrets.json`, `data/backups/`, or
  the `/api/phone/pairing` endpoint.

## Working on the codebase

Conventions live in `CLAUDE.md` (module registration order matters, Mutate commands need
confirmation, schema changes go through `SchemaUpgrader`). Build with
`dotnet build Jarvis.sln` — stop the kernel and tray first or files are locked.
