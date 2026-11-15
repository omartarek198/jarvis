/* JARVIS Command Console — LIVE kernel feed.
   Replaces the design prototype's sim.js with the real SignalR hub (/ws) + REST.
   Exposes the same surface app.js consumes (on/start/sendCommand/setSpeed/setRunning),
   plus health polling pushed into window.JarvisApp.setHealth.
   Dev simulation remains available at ?sim=1 (sim.js stays untouched). */

(function () {
  "use strict";

  var simMode = new URLSearchParams(location.search).get("sim") === "1";
  if (simMode) return; // leave window.JarvisSim (from sim.js) in charge

  var handlers = [];
  var connection = null;
  var connected = false;

  function emit(e) {
    handlers.forEach(function (h) {
      try { h(e); } catch (err) { console.error("handler error", err); }
    });
  }

  function normalize(raw) {
    return {
      kind: raw.kind || "unknown",
      payload: raw.payload || {},
      actor: raw.actor || "system",
      at: raw.at ? new Date(raw.at) : new Date(),
    };
  }

  /* ---------- health ---------- */
  function mapHealth(h) {
    // lastTtsEngine = what actually spoke last; sapi means the good voice silently fell back
    var good = function (e) { return e === "kokoro" || e === "piper"; };
    var tts = h.lastTtsEngine === "sapi" ? "warming" : good(h.ttsEngine) || good(h.lastTtsEngine) ? "ok" : "warming";
    return {
      kernel: h.kernel === "online" ? "ok" : "down",
      stt: h.stt ? "ok" : "down",
      ollama: h.ollama ? "ok" : "down",
      ttsEngine: tts,
      machine: (h.machine || "") + " · tts:" + (h.lastTtsEngine || h.ttsEngine || "?") + " · voice:" + (h.voiceProfile || "?") + " · out:" + (h.voiceOutput || "auto"),
    };
  }

  function pollHealth() {
    fetch("/api/system/health")
      .then(function (r) { return r.json(); })
      .then(function (h) {
        if (window.JarvisApp && window.JarvisApp.setHealth) window.JarvisApp.setHealth(mapHealth(h));
      })
      .catch(function () {
        if (window.JarvisApp && window.JarvisApp.setHealth)
          window.JarvisApp.setHealth({ kernel: "down", stt: "down", ollama: "down", ttsEngine: "down", machine: "kernel unreachable" });
      });
  }

  /* ---------- feed surface (same shape as JarvisSim) ---------- */
  var live = {
    on: function (h) { handlers.push(h); },

    start: function () {
      pollHealth();
      setInterval(pollHealth, 10000);

      connection = new signalR.HubConnectionBuilder()
        .withUrl("/ws")
        .withAutomaticReconnect()
        .build();

      connection.on("event", function (raw) { emit(normalize(raw)); });
      connection.onreconnected(function () { pollHealth(); });

      connection.start()
        .then(function () { connected = true; })
        .catch(function (err) { console.error("hub connect failed", err); });
    },

    sendCommand: function (text) {
      fetch("/api/command", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ text: text, source: "ui" }),
      })
        .then(function (r) { return r.json(); })
        .then(function (resp) {
          // The hub also broadcasts router.transcript/router.response — prefer those.
          // Only synthesize events if the hub is down (degraded but usable).
          if (connected && connection && connection.state === signalR.HubConnectionState.Connected) return;
          emit({ kind: "router.transcript", payload: { text: text, source: "ui" }, actor: "ui", at: new Date() });
          emit({
            kind: "router.response",
            payload: { intent: resp.intent, spoken: resp.spoken, tier: resp.tier, awaitingConfirmation: !!resp.awaitingConfirmation },
            actor: "ui", at: new Date(),
          });
        })
        .catch(function (err) {
          emit({ kind: "router.response", payload: { intent: "error", spoken: "Kernel unreachable: " + err.message }, actor: "ui", at: new Date() });
        });
    },

    // Tweaks-panel hooks from the prototype — no-ops against the live kernel
    setSpeed: function () {},
    setRunning: function () {},
  };

  window.JarvisSim = live;
})();
