"""JARVIS wake-word sidecar — openWakeWord over a local WebSocket.

Started and supervised by Jarvis.Kernel (same pattern as the STT sidecar). Binds to
localhost only. The tray streams raw 16 kHz mono int16 PCM frames; this emits a JSON
line {"detected": true, "score": 0.87} whenever the model crosses the threshold.

Model: pretrained "hey_jarvis" — triggers acceptably on a bare "Jarvis". If the miss
rate with your accent is high, train a custom single-word model with openWakeWord's
synthetic-data Colab and drop the .onnx next to this file (env JARVIS_WAKE_MODEL).

Env: JARVIS_WAKE_PORT (default 5592), JARVIS_WAKE_THRESHOLD (default 0.45),
     JARVIS_WAKE_MODEL (path to a custom .onnx, optional).
"""
import asyncio
import json
import os


def say(msg):
    """print that survives a broken stdout pipe (orphaned sidecar after parent death) —
    a failed log line must never crash the websocket handler."""
    try:
        print(msg, flush=True)
    except OSError:
        pass

import numpy as np
import uvicorn
from fastapi import FastAPI, WebSocket, WebSocketDisconnect

PORT = int(os.environ.get("JARVIS_WAKE_PORT", "5592"))
THRESHOLD = float(os.environ.get("JARVIS_WAKE_THRESHOLD", "0.45"))
CUSTOM_MODEL = os.environ.get("JARVIS_WAKE_MODEL", "")
CHUNK = 1280  # 80 ms @ 16 kHz — openWakeWord's expected frame size


def load_model():
    from openwakeword.model import Model
    from openwakeword.utils import download_models

    if CUSTOM_MODEL and os.path.exists(CUSTOM_MODEL):
        say(f"[wake] custom model: {CUSTOM_MODEL}")
        return Model(wakeword_models=[CUSTOM_MODEL], inference_framework="onnx")
    download_models(["hey_jarvis"])  # no-op when already cached
    say("[wake] pretrained hey_jarvis loaded")
    return Model(wakeword_models=["hey_jarvis"], inference_framework="onnx")


model = load_model()
app = FastAPI()


@app.get("/health")
def health():
    return {"ok": True, "threshold": THRESHOLD}


@app.websocket("/ws")
async def ws(socket: WebSocket):
    await socket.accept()
    model.reset()
    pending = b""
    last_fire = 0.0
    chunks = 0
    win_peak = 0
    win_score = 0.0
    say("[wake] client connected")
    try:
        while True:
            data = await socket.receive_bytes()
            pending += data
            while len(pending) >= CHUNK * 2:  # int16 = 2 bytes/sample
                frame = np.frombuffer(pending[: CHUNK * 2], dtype=np.int16)
                pending = pending[CHUNK * 2:]
                scores = model.predict(frame)
                score = max(scores.values()) if scores else 0.0
                # telemetry: one line per ~6s — proves audio is flowing and shows near-misses
                chunks += 1
                win_peak = max(win_peak, int(np.abs(frame).max()))
                win_score = max(win_score, float(score))
                if chunks % 75 == 0:
                    say(f"[wake] peak={win_peak} maxscore={win_score:.2f}")
                    win_peak, win_score = 0, 0.0
                now = asyncio.get_event_loop().time()
                if score >= THRESHOLD and now - last_fire > 2.0:
                    last_fire = now
                    model.reset()  # don't re-fire on the tail of the same utterance
                    say(f"[wake] DETECTED score={score:.2f}")
                    await socket.send_text(json.dumps({"detected": True, "score": round(float(score), 3)}))
    except WebSocketDisconnect:
        say("[wake] client disconnected")


if __name__ == "__main__":
    uvicorn.run(app, host="127.0.0.1", port=PORT, log_level="warning")
