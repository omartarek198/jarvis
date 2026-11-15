/* JARVIS mobile PWA — hold-to-talk over the LAN.
   Records via MediaRecorder (webm/opus on Android, mp4/aac on iOS — the faster-whisper
   sidecar decodes both via PyAV), POSTs to /api/voice/converse with the X-Jarvis-Key,
   plays the returned wav. The key is asked for once and kept in localStorage. */
(function () {
  "use strict";

  var statusEl = document.getElementById("status");
  var dialog = document.getElementById("dialog");
  var talk = document.getElementById("talk");
  var keybar = document.getElementById("keybar");
  var recorder = null, chunks = [], stream = null, busy = false;

  function key() { return localStorage.getItem("jarvis-key") || ""; }
  function setStatus(t) { statusEl.textContent = t; }

  function say(cls, text) {
    var d = document.createElement("div");
    d.className = cls;
    d.textContent = text;
    dialog.appendChild(d);
    dialog.scrollTop = dialog.scrollHeight;
  }

  function needKey() {
    keybar.style.display = "flex";
    talk.disabled = true;
    setStatus("paste your phone key (say: phone setup, on the PC)");
  }

  document.getElementById("key-save").addEventListener("click", function () {
    var v = document.getElementById("key-input").value.trim();
    if (!v) return;
    localStorage.setItem("jarvis-key", v);
    keybar.style.display = "none";
    boot();
  });

  function boot() {
    fetch("/api/system/health", { headers: { "X-Jarvis-Key": key() } })
      .then(function (r) {
        if (r.status === 401) { needKey(); return null; }
        return r.json();
      })
      .then(function (h) {
        if (!h) return;
        setStatus("online · " + (h.machine || ""));
        talk.disabled = false;
      })
      .catch(function () { setStatus("kernel unreachable — is the PC awake?"); });
  }

  function pickMime() {
    var prefs = ["audio/webm;codecs=opus", "audio/webm", "audio/mp4"];
    for (var i = 0; i < prefs.length; i++)
      if (window.MediaRecorder && MediaRecorder.isTypeSupported(prefs[i])) return prefs[i];
    return "";
  }

  function startRec(ev) {
    ev.preventDefault();
    if (busy || talk.disabled || recorder) return;
    navigator.mediaDevices.getUserMedia({ audio: { channelCount: 1, sampleRate: 16000 } })
      .then(function (s) {
        stream = s;
        chunks = [];
        var mime = pickMime();
        recorder = new MediaRecorder(s, mime ? { mimeType: mime } : undefined);
        recorder.ondataavailable = function (e) { if (e.data.size > 0) chunks.push(e.data); };
        recorder.onstop = sendUtterance;
        recorder.start();
        talk.classList.add("rec");
        setStatus("listening…");
      })
      .catch(function (e) { setStatus("mic blocked: " + e.name + " (trust the cert + allow mic)"); });
  }

  function stopRec(ev) {
    if (ev) ev.preventDefault();
    if (!recorder) return;
    talk.classList.remove("rec");
    setStatus("thinking…");
    recorder.stop();
    stream.getTracks().forEach(function (t) { t.stop(); });
  }

  function sendUtterance() {
    var blob = new Blob(chunks, { type: recorder.mimeType || "audio/webm" });
    recorder = null;
    if (blob.size < 2000) { setStatus("too short — hold while you speak"); return; }
    busy = true;
    fetch("/api/voice/converse", {
      method: "POST",
      headers: { "X-Jarvis-Key": key(), "Content-Type": "application/octet-stream" },
      body: blob
    })
      .then(function (r) {
        if (r.status === 401) { needKey(); throw new Error("unauthorized"); }
        var transcript = decodeURIComponent(r.headers.get("X-Jarvis-Transcript") || "");
        var spoken = decodeURIComponent(r.headers.get("X-Jarvis-Spoken") || "");
        var confirm = r.headers.get("X-Jarvis-Confirm") === "1";
        if (transcript) say("me", "“" + transcript + "”");
        if (spoken) say("jarvis", spoken + (confirm ? " (hold and say: confirm)" : ""));
        return r.blob();
      })
      .then(function (wav) {
        if (wav && wav.size > 100) {
          var url = URL.createObjectURL(wav);
          var audio = new Audio(url);
          audio.onended = function () { URL.revokeObjectURL(url); };
          audio.play().catch(function () { /* iOS needs the tap gesture — already had one */ });
        }
        setStatus("online");
      })
      .catch(function (e) { if (e.message !== "unauthorized") setStatus("failed: " + e.message); })
      .finally(function () { busy = false; });
  }

  talk.addEventListener("pointerdown", startRec);
  talk.addEventListener("pointerup", stopRec);
  talk.addEventListener("pointercancel", stopRec);
  talk.addEventListener("contextmenu", function (e) { e.preventDefault(); });

  // Narrow scope: the SW must never control the desktop console at /
  if ("serviceWorker" in navigator)
    navigator.serviceWorker.register("sw.js", { scope: "/mobile" }).catch(function () {});
  boot();
})();
