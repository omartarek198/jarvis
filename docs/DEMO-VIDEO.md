# Demo video

**[Watch (1:50, 1080p)](https://github.com/omartarek198/jarvis/releases/download/v1.0.0/jarvis-demo.mp4)**
· [720p, 8 MB](https://github.com/omartarek198/jarvis/releases/download/v1.0.0/jarvis-demo-small.mp4)

## What is real

Everything on screen is footage of this repository running locally, recorded from the
browser. Nothing was mocked up or re-created.

| Segment | What happened |
|---|---|
| Ask | "what should I focus on right now" routed by the grammar tier and answered from the demo database |
| Guard | "ask claude to fix the tax rounding in northwind" parked as a Mutate command; the confirm bar shows its parameters |
| Delegate | After "confirm", the kernel launched the **real Claude Code CLI** (2.1.x, `acceptEdits`) on `samples/northwind-api`. The activity stream shows its actual tool calls; the run took about 40 s and is shown at 7× |
| Verify | `git diff` of the sample repository after the run — the agent rounded tax per line item and added two tests |
| Deliberate | A quick Council on `qwen2.5:7b` via Ollama: a clarifying question, three adviser statements, a vote. Waiting time is shown at 9× and 3× |
| Explain | "why did you do that", answered from the command audit log |

Settings for the run: `Jarvis:Demo:Enabled=true` (synthetic data), `Jarvis:Agents:Mode=cli`
(real agents), external actions off.

## What was edited

- **Commands were typed**, not spoken. The voice you hear is JARVIS's own Kokoro TTS
  (`/api/voice/tts`) speaking its actual replies; long replies were trimmed at sentence
  boundaries. The tray app normally plays these; the browser recording has no audio, so
  the speech was laid over the footage at the moment each reply appeared.
- Waiting time was sped up, and every sped-up segment carries an on-screen `N×` badge.
- Title cards, captions, zooms, the architecture animation and the synthesized music bed
  were added in a [Remotion](https://www.remotion.dev) edit.

## How it was produced

1. A throwaway copy of the repository ran with real agents and the Kokoro model files.
2. A Playwright script drove the web console scene by scene, recording each scene to its
   own clip, logging when each reply arrived and fetching JARVIS's TTS for it.
3. Because headless recording drifts under load, cut points were set from the frames
   themselves (when the confirm bar turns red, when `agent.done` lands), not from logs.
4. The clips were cut in a Remotion composition and rendered to H.264.

## Recording your own

Run the kernel with `Jarvis__Agents__Mode=cli` in a throwaway clone (edit runs change
`samples/northwind-api`), make sure Ollama has `qwen2.5:3b`, `qwen2.5:7b` and
`nomic-embed-text`, and keep notifications, other windows and account details off screen.
Health tooltips show `demo` instead of the machine name in demo mode.
