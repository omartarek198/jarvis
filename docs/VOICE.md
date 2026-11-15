# JARVIS Voice Output

How JARVIS speaks: profiles, formatting, chunking, mastering, caching, fallbacks —
and how to change any of it without touching code.

## Architecture (one choke point)

All spoken audio flows through `TtsService.SynthesizeAsync(text, profile?)`
(`src/Jarvis.Kernel/Services/TtsService.cs`):

```
text ──► profile selection ──► SpeechTextFormatter ──► phrase cache? ──hit──► wav (instant)
              │                                            │miss
              │                                            ▼
              │                                   engine chain (never throws):
              │                                   kokoro (:5593, per-chunk + stitch)
              │                                     └─fail→ piper (per-sentence + stitch)
              │                                         └─fail→ Windows SAPI
              │                                             └─fail→ [] (text-only, logged)
              │                                            │
              └── voice.tts bus event ◄── mastering (toggleable) ◄── PCM
```

- The tray plays the returned WAV (`/api/voice/tts`, `/api/voice/converse`).
- The Kokoro sidecar (`voice/kokoro/server.py`, `127.0.0.1:5593`) receives
  `{text, voice, speed}` per chunk.
- The whole reply is rendered and stitched before a single WAV is returned — there is
  no streaming playback, so long replies wait for every chunk.
- `TtsService.LastEngineUsed` tells you what ACTUALLY spoke (`kokoro`, `piper`,
  `sapi`, `cache`, `none`) — surfaced at `/api/system/health`.
- Every synthesis publishes a `voice.tts` event (`{profile, voice, engine, cached, ms,
  chars}`) on the bus → SignalR, so the console can distinguish cached playback from
  live synthesis. It is skipped from the audit table (telemetry).

## Voice profiles — `voice/profiles.json`

Hand-editable, hot-reloaded on save (mtime check, no kernel restart). A broken file
logs a warning and keeps the last good config; a missing file degrades to a built-in
default. Profiles: `default`, `fast`, `briefing`, `alert`, `boris` (+aliases:
`jarvis`→`default`, `premium`→`briefing`, …), plus nine `council.*` profiles that give
each Council adviser its own voice.

Per profile:

| Key | What it does |
| --- | --- |
| `engine` | `"kokoro"` or `"piper"` — preferred engine; the chain below it remains as fallback |
| `voice` | Kokoro voice id (`bm_fable`, `bm_george`, `am_michael`, …) |
| `speed` | Kokoro speed multiplier |
| `piperVoices` | Piper model filename prefixes, most-preferred first |
| `formatter` | Run `SpeechTextFormatter` (acronyms, ports, durations, traces…) |
| `maxSpokenChars` | Spoken length cap — console always shows full text |
| `chunking` | `minSplitChars` (one-shot below this), `maxChunkChars`, `sentenceGapMs`, `paragraphGapMs`, `leadInMs`, `tailMs` |
| `mastering` | Pure-C# polish chain (below) |
| `cache` | Eligible for the phrase cache |
| `autoEscalate` | Replies > `selection.briefingMinChars` chars switch to the briefing profile |

**Profile selection:** active profile (DB setting `voice.profile`) → length-based
auto-escalation (default 280+ chars → `briefing`) → explicit override via
`SynthesizeAsync(text, "alert")` for callers that want it.

**Voice commands:** `switch voice to <profile>`, `use briefing voice`,
`switch to fast voice`, `what voice profiles do you have`, `regenerate voice cache`.
Legacy `switch voice to jarvis/boris` still works.

### Adding a voice / profile

1. Add a block under `"profiles"` in `voice/profiles.json` (copy `default`, change
   `voice`/`speed`). Save — it hot-reloads.
2. Say `switch voice to <name>`.
3. Kokoro v1.0 ships ~54 voices in `voices-v1.0.bin`; run
   `python -c "from kokoro_onnx import Kokoro; k=Kokoro('voice/kokoro/models/kokoro-v1.0.onnx','voice/kokoro/models/voices-v1.0.bin'); print(k.get_voices())"`
   to list them.
4. New pronunciation problems → add to `"lexicon"` in profiles.json (regex-free,
   word-boundary, wins over built-ins) instead of editing `SpeechTextFormatter`.

## SpeechTextFormatter

`src/Jarvis.Kernel/Services/SpeechTextFormatter.cs` — extends the original
`SpeechText.ForSpeech` sanitizer (URLs→"link", emails, file paths, hex ids, markdown,
length cap — all preserved) with: acronym respelling (`UI`→"U I", `API`→"A P I", …),
Jira ids (`ABC-12`→"A B C twelve"),
`localhost:5588`→"local host, port fifty five eighty eight", durations
(`0h-07m-12s`→"seven minutes, twelve seconds"), `%`→"percent", `snake_case`→spaces,
arrow/boolean symbols, GUID and stack-trace suppression, code-fence removal.
Paragraph breaks survive as `\n` so the chunker can pause longer between paragraphs.
`SpeechTextFormatter.Version` participates in the cache key — bumping it invalidates
all cached phrases.

## Chunking

`SpeechChunker` (in `VoiceProfiles.cs`) splits formatted text into sentence groups of
≤ `maxChunkChars`, with paragraph boundaries carrying longer gaps. Short replies
(≤ `minSplitChars`) are synthesized in one shot — zero added latency for "Done."
Chunks are synthesized in order, silence-trimmed, and stitched with fixed gaps
(deterministic pacing — this generalizes what the Piper path already did to fix its
stochastic mid-reply pauses; Piper still forces one sentence per chunk).

## Mastering

`AudioMastering.cs`, pure C# on 16-bit mono PCM (no NAudio/ffmpeg in the kernel —
NAudio is tray-only, ffmpeg isn't installed):
high-pass 80 Hz → high-shelf −2.5 dB @ 7.8 kHz (de-harsh) → 2.5:1 compression
(−18 dBFS knee, 8/160 ms) → loudness normalize to −20 dBFS RMS, peak-capped −1.5 dBFS.
Deliberately subtle: every reply lands at the same level, slightly warmer. Toggle with
`"mastering": false` per profile. Applied to kokoro and piper output; SAPI is left
alone (unknown format, emergency fallback anyway).

## Cached system phrases

- Location: `voice/cache/<profile>-<hash>/<sha16-of-text>.wav` (gitignored).
- The hash covers engine+voice+speed+mastering+formatter version — editing a profile
  or the formatter automatically invalidates its cache.
- Only texts ≤ `cacheMaxChars` (default 100) on cache-enabled profiles are eligible;
  long/private replies are never cached. A reply that fell back to a different engine
  is never cached either (a one-off SAPI render must not become the permanent "Done.").
- Phrase list: `voice/phrases.json` (16 phrases). Regenerate with the voice command
  `regenerate voice cache` or `TtsService.RegenerateCacheAsync()`.
- Cache also fills organically: any short reply on a cache-enabled profile is stored
  on first synthesis and instant afterwards.

## Fallbacks

Each engine failure logs a warning and falls through; total failure logs an error and
returns empty audio (the console still shows the text). The kernel never crashes over
audio. Kokoro chunk failures abort the whole kokoro attempt (no half-stitched replies)
and fall to Piper.

## Bake-off

A bake-off script (kept out of the public edition along with its ~300 rendered clips)
rendered 10 fixed JARVIS lines × 10 Kokoro voices
(af_heart, af_bella, am_adam, am_michael, am_onyx, am_fenrir, bm_daniel, bm_fable,
bm_lewis, bm_george) × speeds 0.92/0.96/1.00, measuring true synth latency. Outputs
`output/index.html` (listening page + 1–5 scorecard persisted in localStorage),
`results.json/csv`, `summary.json`. It imported kokoro-onnx directly, bypassing the
sidecar. Subjective listening scores were the author's.

### Measured results (this machine, CPU, kokoro-onnx, 30 clips per voice)

| Voice | mean latency | p95 | RTF | pace chars/s (σ) |
| --- | --- | --- | --- | --- |
| af_heart | 1.10 s | 1.49 | 0.42 | 15.8 (1.71) |
| am_fenrir | 1.12 s | 1.54 | 0.45 | 16.9 (1.63) |
| am_adam | 1.17 s | 1.52 | 0.46 | 16.1 (1.69) |
| af_bella | 1.25 s | 1.65 | 0.46 | 15.4 (1.68) |
| **bm_fable** | 1.30 s | 1.68 | **0.39** | 12.5 (1.66) |
| bm_daniel | 1.32 s | 1.69 | 0.48 | 14.6 (1.62) |
| am_onyx | 1.33 s | 1.64 | 0.42 | 12.9 (1.82) |
| bm_george (current) | 1.34 s | 1.74 | 0.42 | 13.1 (1.41) |
| bm_lewis | 1.36 s | 1.82 | 0.46 | 14.0 (1.52) |
| am_michael | 1.38 s | 1.80 | 0.46 | **13.9 (1.35)** |

RTF = synth time / audio duration; synthesis runs 2–3× faster than real time on CPU.
Because the reply is rendered in full before playback, a 1,000-character briefing still
takes a few seconds to start. Latency differences between voices are noise-level
(±0.25 s); the pick is about delivery, not speed.

### Recommendation (objective evidence + reputation; subjective scores are the author's)

- **Daily default: `bm_fable` @ 0.96** — best RTF of the field (0.39), and the slowest
  natural pace (12.5 chars/s): an unhurried, premium register out of the box, where
  bm_george at the old 1.06 was being pushed ~12% faster than a voice that already
  paces quicker — that "rushed AI" feel. bm_fable is also the community's usual pick
  for a British assistant voice.
- **Briefing: `bm_fable` @ 0.90** — same identity, slower, longer pauses, longer cap.
  One voice identity across modes is deliberate; alerts use `bm_george` for contrast.
- Runners-up to audition in index.html: `am_michael` (warm US male, steadiest pacing
  σ 1.35), `bm_lewis` (deeper UK), `bm_daniel` (lighter UK).

## Premium engine investigation

Constraint: a 6 GB consumer GPU without tensor cores (weak FP16), CUDA already shared with
faster-whisper STT; no paid APIs.

| Engine | Install on Windows | This GPU | Quality | Verdict |
| --- | --- | --- | --- | --- |
| **Chatterbox / Chatterbox-Turbo** (Resemble, MIT) | pip + CUDA torch (~30–40 min, multi-GB) | needs ~6 GB VRAM minimum — exactly all of it, and STT/Ollama already use VRAM; CPU mode far slower than real time | Excellent — listeners preferred it over ElevenLabs Turbo 65% of the time | **Briefing-only candidate, not now.** Viable only as an on-demand sidecar that loads, renders the briefing, and unloads. Path documented below. |
| **XTTS v2** (Coqui) | `pip install coqui-tts` (community fork) | 4–6 GB VRAM, borderline | Very good cloning, ages fast | **Rejected** — CPML license (non-commercial), VRAM-tight, abandoned upstream. |
| **F5-TTS** | pip + torch | GPU-hungry, seconds per sentence | Very good | **Rejected** — CC-BY-NC license, latency, fragility. |
| **Piper** | already installed | CPU, instant | Flat prosody | Keep as fallback only. |
| **Kokoro-82M (current)** | pip + 354 MB model files | CPU, ~1.1–1.4 s per short line (bake-off) | Best quality-per-watt of anything local at this size; Apache 2.0 | **Keep as default AND briefing engine**, better voice + mastering + chunking close most of the gap. |

**Chatterbox-later path** (if briefings still feel cheap after the bake-off voice
lands): separate venv `voice/chatterbox/`, FastAPI sidecar on :5594 mirroring the
Kokoro contract, `"engine": "chatterbox"` accepted in the profile schema, loaded
on demand and unloaded after render to give VRAM back to Whisper. Until VRAM grows,
expect ~5–15 s to render a 1,000-char briefing — acceptable for briefings only.
A second option with zero new deps: kokoro-onnx supports `onnxruntime-gpu`
(`pip install onnxruntime-gpu`, set provider env) for ~2–4× faster chunk synthesis.

## Testing

- `dotnet run --project tools/voice-tests` — standalone checks: formatter rules,
  chunk ordering/gaps, mastering invariants (loudness up, peak under ceiling, DC
  removed), and an end-to-end TtsService run covering the fallback chain and cache
  policy. Not part of Jarvis.sln.

## Model files (not in the repository)

Kokoro needs `kokoro-v1.0.onnx` (≈325 MB) and `voices-v1.0.bin` (≈28 MB) in
`voice/kokoro/models/`; both are published with the kokoro-onnx project's
`model-files-v1.0` release. Without them the Kokoro supervisor disables itself and
speech falls back to Piper (`scripts/install-piper.ps1`) or Windows SAPI, which needs
no setup.
