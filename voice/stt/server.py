"""JARVIS STT sidecar — faster-whisper over local HTTP.

Started and supervised by Jarvis.Kernel. Binds to localhost only.
Env: JARVIS_WHISPER_MODEL (default base.en), JARVIS_STT_PORT (default 5591).
"""
import os
import site
import tempfile


def _add_nvidia_dll_dirs():
    """Make pip-installed cuBLAS/cuDNN (nvidia-cublas-cu12, nvidia-cudnn-cu12) visible to ctranslate2.
    ctranslate2 resolves CUDA DLLs via the default search path, so PATH (not add_dll_directory) is required."""
    dll_dirs = []
    for sp in site.getsitepackages() + [site.getusersitepackages()]:
        nv = os.path.join(sp, "nvidia")
        if not os.path.isdir(nv):
            continue
        for root, _dirs, files in os.walk(nv):
            if root.endswith("bin") and any(f.endswith(".dll") for f in files):
                dll_dirs.append(root)
    if dll_dirs:
        os.environ["PATH"] = ";".join(dll_dirs) + ";" + os.environ.get("PATH", "")


_add_nvidia_dll_dirs()

import numpy as np
import uvicorn
from fastapi import FastAPI, Request
from faster_whisper import WhisperModel

# base.en: ~3x faster than small.en — right tradeoff for short push-to-talk commands on a
# GPU without tensor cores. Set JARVIS_WHISPER_MODEL=small.en for dictation accuracy.
MODEL_SIZE = os.environ.get("JARVIS_WHISPER_MODEL", "base.en")
PORT = int(os.environ.get("JARVIS_STT_PORT", "5591"))

# Entity dictionary biases recognition toward the assistant's vocabulary (edit for your own projects)
INITIAL_PROMPT = (
    "JARVIS, Northwind, Claude, Codex, Playwright, Jira, Teams, "
    "repo, commit, branch, briefing, commitment, tester issues."
)


def load_model():
    # int8_float16 is the fast path on pre-tensor-core GPUs; fall back gracefully
    for device, compute in (("cuda", "int8_float16"), ("cuda", "float16"), ("cpu", "int8")):
        try:
            m = WhisperModel(MODEL_SIZE, device=device, compute_type=compute)
            # CUDA failures surface lazily — probe with a second of silence now
            segs, _ = m.transcribe(np.zeros(16000, dtype=np.float32), language="en")
            list(segs)
            print(f"[stt] {MODEL_SIZE} loaded on {device} ({compute})", flush=True)
            return m, device
        except Exception as e:
            print(f"[stt] {device}/{compute} unavailable ({type(e).__name__}: {e})", flush=True)
    raise RuntimeError("no usable whisper backend")


model, device = load_model()
app = FastAPI()


@app.get("/health")
def health():
    return {"ok": True, "model": MODEL_SIZE, "device": device}


@app.post("/transcribe")
async def transcribe(request: Request):
    data = await request.body()
    fd, path = tempfile.mkstemp(suffix=".wav")
    try:
        with os.fdopen(fd, "wb") as f:
            f.write(data)
        segments, _ = model.transcribe(
            path, language="en",
            beam_size=1,  # greedy: fastest, accurate enough for short commands (entity prompt does the heavy lifting)
            condition_on_previous_text=False,
            initial_prompt=INITIAL_PROMPT,
        )
        text = " ".join(s.text.strip() for s in segments).strip()
        return {"text": text}
    finally:
        try:
            os.unlink(path)
        except OSError:
            pass


if __name__ == "__main__":
    uvicorn.run(app, host="127.0.0.1", port=PORT, log_level="warning")
