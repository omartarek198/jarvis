"""JARVIS neural TTS sidecar — Kokoro (82M) over local HTTP.

Replaces Piper's flat delivery with actual prosody. Started and supervised by
Jarvis.Kernel (same contract as STT/wake). Binds to localhost only. CPU inference:
~0.3-1s per sentence — fine for spoken replies.

Env: JARVIS_TTS_PORT (default 5593), JARVIS_TTS_VOICE (default bm_george — British male;
     try bm_fable, bm_lewis, bm_daniel, am_michael), JARVIS_TTS_SPEED (default 1.06).
Models: voice/kokoro/models/kokoro-v1.0.onnx + voices-v1.0.bin (downloaded once, ~340MB).
"""
import io
import os


def say(msg):
    try:
        print(msg, flush=True)
    except OSError:
        pass  # orphaned: parent's pipe is gone — never crash a handler over a log line


import numpy as np
import soundfile as sf
import uvicorn
from fastapi import FastAPI
from pydantic import BaseModel
from kokoro_onnx import Kokoro

PORT = int(os.environ.get("JARVIS_TTS_PORT", "5593"))
VOICE = os.environ.get("JARVIS_TTS_VOICE", "bm_george")
SPEED = float(os.environ.get("JARVIS_TTS_SPEED", "1.06"))
HERE = os.path.dirname(os.path.abspath(__file__))

kokoro = Kokoro(os.path.join(HERE, "models", "kokoro-v1.0.onnx"),
                os.path.join(HERE, "models", "voices-v1.0.bin"))
say(f"[tts] kokoro loaded, voice={VOICE} speed={SPEED}")

app = FastAPI()


class SynthRequest(BaseModel):
    text: str
    voice: str | None = None
    speed: float | None = None


@app.get("/health")
def health():
    return {"ok": True, "engine": "kokoro", "voice": VOICE}


@app.post("/synthesize")
def synthesize(req: SynthRequest):
    from fastapi.responses import Response
    samples, sample_rate = kokoro.create(
        req.text.strip(),
        voice=req.voice or VOICE,
        speed=req.speed or SPEED,
        lang="en-gb" if (req.voice or VOICE).startswith("b") else "en-us",
    )
    buf = io.BytesIO()
    sf.write(buf, np.asarray(samples), sample_rate, format="WAV", subtype="PCM_16")
    return Response(content=buf.getvalue(), media_type="audio/wav")


if __name__ == "__main__":
    uvicorn.run(app, host="127.0.0.1", port=PORT, log_level="warning")
