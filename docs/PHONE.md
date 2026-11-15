# Phone access

Three independent channels. **All of them are off in the public edition's default
configuration** (`Jarvis:Lan:Enabled=false`, `Jarvis:ExternalActions:Enabled=false`).
Enable only the ones you need, in a local settings file.

Pairing values (LAN API key, ntfy topic) are served by a loopback-only endpoint and are
never part of a command response, because command responses are broadcast over SignalR
and written to the audit table:

```
curl http://localhost:5588/api/phone/pairing      # from the PC itself; 403 from anywhere else
```

## 1. Push notifications (ntfy)

Requires `Jarvis:ExternalActions:Enabled=true`. Reminders, agent results and similar
one-line announcements are POSTed to `https://ntfy.sh/<topic>`.

1. Install the **ntfy** app.
2. Read `ntfyTopic` from `/api/phone/pairing`. Treat it as a secret: anyone who knows the
   topic can read and send notifications on it.
3. Subscribe to it in the app. Test with "remind me in 1 minute to test the phone".
4. Optional quiet hours: setting `ntfy.quiet` = `"22-08"` (local hours).

The public ntfy.sh server is used as-is; for anything sensitive, point the service at a
self-hosted ntfy instance with access tokens (not implemented here).

## 2. Voice from the phone (LAN PWA) — same Wi-Fi only

Requires `Jarvis:Lan:Enabled=true`, and the PC's LAN address added to `AllowedHosts`.

1. The kernel serves HTTPS on port **5589** with a self-signed certificate it generates
   on first start (`data/lan-cert.pfx`, password in the DPAPI store). Phone browsers need
   trusted HTTPS for microphone access:
   - **Android Chrome:** open `https://<pc-ip>:5589/mobile.html` and proceed past the
     certificate warning.
   - **iOS / proper route:** use [mkcert](https://github.com/FiloSottile/mkcert) to issue a
     certificate for the PC's address, export it to `data/lan-cert.pfx`, store its password
     as the secret `lan.certpass`, and install mkcert's root CA on the phone.
2. Paste the `apiKey` from `/api/phone/pairing` when the page asks (kept in the phone
   browser's localStorage).
3. Hold the circle, speak, release. Replies come back as voice and text; Mutate
   commands ask for confirmation.

Security: every `/api`, `/ws` and `/swagger` request from another device must carry the
`X-Jarvis-Key` header (constant-time comparison; the query string is not accepted).
Browser requests from foreign origins are refused. The listener is LAN-only — no port
forwarding.

## 3. Codex relay

`AGENTS.md` teaches a Codex agent running in this repository to relay
"ask jarvis …" messages to the local kernel through the CLI
(`Jarvis.Cli --json --source relay "…"`) and to pass JARVIS's reply back verbatim. With
the Codex mobile app connected to the PC, that gives phone access from anywhere without
opening a port. Guarded actions round-trip: the relay reports that confirmation is
needed and forwards your "confirm" within the 2-minute window.

Requirements: kernel running; Codex's sandbox for this workspace must allow running
local commands and localhost network access.

## Troubleshooting

| Symptom | Fix |
|---|---|
| PWA says "kernel unreachable" | PC asleep? Kernel running with LAN enabled? Firewall allows inbound TCP 5589? |
| Mic blocked in the PWA | The certificate isn't trusted — use the mkcert route |
| 401 unauthorized | Re-paste the key from `/api/phone/pairing` |
| No ntfy pushes | External actions disabled, or topic mismatch |
| Codex relay says offline | Start the kernel (`scripts\start-jarvis.ps1`) |
