/* JARVIS Command Console — UI wiring. Consumes window.JarvisSim (stand-in for SignalR /ws)
   and drives window.OrbEngine. Swap JarvisSim for the real hub connection later. */

(function () {
  "use strict";

  var engine = null;
  var forcedState = null;
  var orbState = "idle";
  var speakTimer = null;
  var thinkTimer = null;
  var stopTimer = null;
  var typeTimer = null;
  var armed = false;

  var $ = function (s) { return document.querySelector(s); };
  var input, transcriptEl, responseEl, stateText, stateDot, streamList, streamPanel;

  var STATE_COLORS = {
    idle: "var(--accent)", listening: "#cfeffb", thinking: "#ffd35d",
    speaking: "#bdf0ff", alert: "#ff5d6c", stopped: "#5e6a78"
  };

  /* ---------------- orb state ---------------- */
  function setOrbState(name) {
    orbState = name;
    if (engine && !forcedState) engine.setState(name);
    var shown = forcedState || name;
    stateText.textContent = shown;
    stateDot.style.background = STATE_COLORS[shown] || "var(--accent)";
    stateDot.style.boxShadow = "0 0 8px " + (STATE_COLORS[shown] || "var(--accent)");
  }

  /* ---------------- dialogue ---------------- */
  function typeTranscript(text) {
    clearInterval(typeTimer);
    transcriptEl.innerHTML = "";
    var span = document.createElement("span");
    var caret = document.createElement("span");
    caret.className = "caret";
    transcriptEl.appendChild(span);
    transcriptEl.appendChild(caret);
    var i = 0;
    typeTimer = setInterval(function () {
      i++;
      span.textContent = "“" + text.slice(0, i) + (i >= text.length ? "”" : "");
      if (i >= text.length) { clearInterval(typeTimer); caret.remove(); }
    }, 26);
  }

  function materializeResponse(text) {
    responseEl.innerHTML = "";
    if (!text) return;
    var words = text.split(" ");
    var ANIMATE = 60; // long replies (briefings) used to spawn hundreds of staggered timers \u2014 laggy
    words.forEach(function (w, i) {
      var s;
      if (/^https?:\/\//.test(w)) {           // URLs become links, not word-art
        s = document.createElement("a");
        s.href = w.replace(/[.,;)\]]+$/, "");
        s.target = "_blank"; s.rel = "noreferrer";
        s.textContent = w.length > 60 ? w.slice(0, 57) + "\u2026" : w;
      } else {
        s = document.createElement("span");
        s.textContent = w;
      }
      s.className = "w";
      responseEl.appendChild(s);
      responseEl.appendChild(document.createTextNode(" "));
      if (i < ANIMATE) setTimeout(function () { s.classList.add("on"); }, 60 + i * 42);
      else s.classList.add("on");
    });
    var copy = document.createElement("button");
    copy.className = "copy-btn";
    copy.textContent = "\u29c9";
    copy.title = "Copy response";
    copy.addEventListener("click", function () {
      navigator.clipboard.writeText(text).then(function () {
        copy.textContent = "\u2713";
        setTimeout(function () { copy.textContent = "\u29c9"; }, 1200);
      });
    });
    responseEl.appendChild(copy);
  }

  /* ---------------- voice scope: 3-channel telemetry oscilloscope ----------------
     MIC  = ambient (the always-listening wake mic)   — deep teal
     EAR  = active capture (PTT / wake window open)   — arctic white-teal
     VOICE= JARVIS speaking (TTS envelope)            — electric cyan          */
  var scope = (function () {
    var canvas, g, W = 0, H = 0, t = 0;
    var N = 140;                                   // scrolling history samples
    var RIBBON = 9;                                // curves per channel — the woven mesh look
    var chans = [
      { buf: new Float32Array(N), cur: 0, tgt: 0, color: "31,143,150",  hot: "94,242,232",  gain: 0.5,  seed: 0.0 },
      { buf: new Float32Array(N), cur: 0, tgt: 0, color: "188,255,244", hot: "234,255,255", gain: 1.0,  seed: 2.1 },
      { buf: new Float32Array(N), cur: 0, tgt: 0, color: "54,214,255",  hot: "160,240,255", gain: 1.0,  seed: 4.2 },
    ];
    var head = 0;
    // drifting node particles, brightened by total signal (the dots in the reference art)
    var nodes = [];
    for (var ni = 0; ni < 22; ni++)
      nodes.push({ x: Math.random(), y: Math.random(), r: 0.8 + Math.random() * 2.4,
                   vx: 0.0002 + Math.random() * 0.0006, ph: Math.random() * 6.28, ring: Math.random() < 0.3 });

    function fit() {
      var pr = Math.min(window.devicePixelRatio || 1, 1.5);
      W = canvas.clientWidth * pr; H = canvas.clientHeight * pr;
      canvas.width = W; canvas.height = H;
    }

    function sample(ch, i) {           // smoothed history lookup, oldest→newest across x
      return ch.buf[(head + 1 + i) % N];
    }

    function drawRibbon(ch) {
      var mid = H * 0.52;
      var amp = H * 0.46 * ch.gain;
      var energy = ch.cur;
      for (var k = 0; k < RIBBON; k++) {
        var kf = k / (RIBBON - 1);                       // 0..1 across the ribbon
        var spread = (kf - 0.5) * 2;                     // -1..1
        var lineAmp = amp * (0.6 + 0.55 * Math.cos(spread * 1.4));
        g.beginPath();
        var step = Math.max(2, Math.floor(N / 70));
        for (var i = 0; i <= N - step; i += step) {
          var v = sample(ch, i);
          // weave: each strand phase-shifts along x so strands cross like threads
          var weave = Math.sin(i * 0.10 + t * 1.4 + ch.seed + spread * 2.6)
                    * (0.22 + 0.85 * v);
          var y = mid - v * lineAmp - spread * (3 + 26 * v) - weave * lineAmp * 0.35;
          var x = (i / (N - 1)) * W;
          if (i === 0) g.moveTo(x, y);
          else {
            var pv = sample(ch, i - step);
            var px = ((i - step) / (N - 1)) * W;
            g.quadraticCurveTo(px, mid - pv * lineAmp - spread * (3 + 26 * pv) - weave * lineAmp * 0.35,
              (px + x) / 2, (mid - pv * lineAmp + y) / 2 * 0.5 + y * 0.5);
          }
        }
        var a = (0.09 + 0.30 * energy) * (1 - Math.abs(spread) * 0.4);
        g.strokeStyle = "rgba(" + (energy > 0.45 ? ch.hot : ch.color) + "," + a.toFixed(3) + ")";
        g.lineWidth = 1;
        g.stroke();
      }
      // crest line: one bright stroke riding the top of the family
      g.beginPath();
      for (var j = 0; j < N; j += 2) {
        var vv = sample(ch, j);
        var xx = (j / (N - 1)) * W;
        var yy = mid - vv * amp;
        if (j === 0) g.moveTo(xx, yy); else g.lineTo(xx, yy);
      }
      g.strokeStyle = "rgba(" + ch.hot + "," + (0.25 + 0.6 * energy).toFixed(3) + ")";
      g.lineWidth = 1.4;
      g.shadowColor = "rgb(" + ch.hot + ")";
      g.shadowBlur = 4 + energy * 22;
      g.stroke();
      g.shadowBlur = 0;
    }

    function draw() {
      requestAnimationFrame(draw);
      if (!W) fit();
      t += 0.016;
      g.clearRect(0, 0, W, H);
      g.globalCompositeOperation = "lighter";      // additive — overlapping families glow

      var total = 0;
      for (var c = 0; c < 3; c++) {
        var ch = chans[c];
        ch.cur += (ch.tgt - ch.cur) * 0.3;
        // idle shimmer so the sea is never dead flat
        ch.buf[head] = ch.cur + Math.sin(t * 2.2 + ch.seed) * 0.012 + (Math.random() - 0.5) * 0.01;
        total += ch.cur;
        drawRibbon(ch);
      }

      // node particles drifting through the field
      for (var n = 0; n < nodes.length; n++) {
        var nd = nodes[n];
        nd.x += nd.vx; if (nd.x > 1.02) nd.x = -0.02;
        var glow = 0.18 + 0.5 * total + 0.2 * Math.sin(t * 1.7 + nd.ph);
        var px2 = nd.x * W, py2 = (0.18 + 0.64 * nd.y) * H + Math.sin(t * 0.9 + nd.ph) * 4;
        if (nd.ring) {
          g.beginPath(); g.arc(px2, py2, nd.r * 2.6, 0, 6.2832);
          g.strokeStyle = "rgba(120,210,230," + (glow * 0.5).toFixed(3) + ")";
          g.lineWidth = 0.8; g.stroke();
        } else {
          g.beginPath(); g.arc(px2, py2, nd.r, 0, 6.2832);
          g.fillStyle = "rgba(200,245,255," + Math.min(0.85, glow).toFixed(3) + ")";
          g.fill();
        }
      }
      g.globalCompositeOperation = "source-over";
      head = (head + 1) % N;
    }

    return {
      init: function () {
        canvas = document.getElementById("scope-canvas");
        if (!canvas) return;
        g = canvas.getContext("2d");
        fit();
        window.addEventListener("resize", fit);
        draw();
      },
      feed: function (a, c, s) {
        chans[0].tgt = a || 0;
        chans[1].tgt = c || 0;
        chans[2].tgt = s || 0;
      },
    };
  })();

  /* rich response cards: images grid / link list (payload.data.card from handlers) */
  function renderCard(card) {
    if (!card || !card.type || !card.items) return;
    var wrap = document.createElement("div");
    wrap.className = "response-card";
    if (card.type === "images") {
      wrap.style.cssText = "margin-top:14px;display:grid;grid-template-columns:repeat(3,1fr);gap:8px;max-width:520px;";
      card.items.forEach(function (it) {
        var a = document.createElement("a");
        a.href = it.url || it.thumb; a.target = "_blank"; a.rel = "noreferrer";
        a.style.cssText = "display:block;aspect-ratio:4/3;overflow:hidden;border-radius:6px;border:1px solid rgba(255,255,255,.12);";
        var img = document.createElement("img");
        img.src = it.thumb; img.alt = it.title || ""; img.title = it.title || "";
        img.referrerPolicy = "no-referrer"; img.loading = "lazy";
        img.style.cssText = "width:100%;height:100%;object-fit:cover;";
        img.onerror = function () { a.remove(); };
        a.appendChild(img); wrap.appendChild(a);
      });
    } else if (card.type === "links") {
      wrap.style.cssText = "margin-top:14px;max-width:560px;text-align:left;";
      card.items.forEach(function (it) {
        var row = document.createElement("a");
        row.href = it.url; row.target = "_blank"; row.rel = "noreferrer";
        row.style.cssText = "display:block;padding:8px 10px;margin-bottom:6px;border:1px solid rgba(255,255,255,.10);border-radius:6px;text-decoration:none;color:inherit;";
        var t = document.createElement("div");
        t.textContent = it.title || it.url;
        t.style.cssText = "font-size:13px;color:var(--accent);";
        var s = document.createElement("div");
        s.textContent = it.snippet || "";
        s.style.cssText = "font-size:11px;opacity:.6;margin-top:2px;";
        row.appendChild(t); if (it.snippet) row.appendChild(s);
        wrap.appendChild(row);
      });
    } else if (card.type === "kv") {
      wrap.style.cssText = "margin-top:14px;max-width:420px;text-align:left;";
      card.items.forEach(function (it) {
        var row = document.createElement("div");
        row.style.cssText = "display:flex;justify-content:space-between;padding:5px 10px;border-bottom:1px solid rgba(255,255,255,.08);font-size:13px;";
        var k = document.createElement("span"); k.textContent = it.key; k.style.opacity = ".6";
        var v = document.createElement("span"); v.textContent = it.value;
        row.appendChild(k); row.appendChild(v); wrap.appendChild(row);
      });
    }
    if (wrap.children.length > 0) responseEl.appendChild(wrap);
  }

  /* command audit trail line: tier \u00b7 intent \u00b7 status \u00b7 duration */
  var STATUS_ICON = { ok: "\u2713", failed: "\u2717", unhandled: "?", awaiting_confirm: "\u23f3", cancelled: "\u2298", expired: "\u2298" };
  function appendAuditLine(p) {
    if (!p || !p.intent) return;
    var line = document.createElement("div");
    line.className = "audit-line";
    line.style.cssText = "margin-top:10px;font-size:11px;letter-spacing:.08em;opacity:.55;font-family:inherit;text-transform:uppercase;";
    var bits = [p.tier || "", p.intent || ""];
    if (p.status) bits.push((STATUS_ICON[p.status] || "") + " " + p.status.replace("_", " "));
    if (p.durationMs) bits.push(p.durationMs + "ms");
    line.textContent = bits.filter(Boolean).join(" \u00b7 ");
    line.title = "command audit \u2014 see /api/commands" + (p.logId ? " \u00b7 log " + p.logId : "");
    responseEl.appendChild(line);
  }

  /* ---------------- arm / confirm theater ---------------- */
  function arm(p) {
    armed = true;
    document.body.classList.add("armed");
    var msg = $("#confirm-msg");
    msg.innerHTML = "";
    var what = document.createElement("div");
    what.className = "confirm-what";
    what.textContent = (p && p.description) || (p && p.command) || "guarded action pending";
    msg.appendChild(what);
    if (p && p.utterance) {
      var heard = document.createElement("div");
      heard.className = "confirm-heard";
      heard.textContent = "“" + p.utterance + "”";
      msg.appendChild(heard);
    }
    // The parameters the action will run with (e.g. task / project / recipient)
    if (p && p.slots && typeof p.slots === "object") {
      var parts = Object.keys(p.slots)
        .filter(function (k) { return String(p.slots[k] || "").trim(); })
        .map(function (k) { return k + ": " + p.slots[k]; });
      if (parts.length) {
        var params = document.createElement("div");
        params.className = "confirm-heard";
        params.textContent = parts.join("  ·  ");
        msg.appendChild(params);
      }
    }
    if (p && p.command) {
      var chip = document.createElement("div");
      chip.className = "confirm-chip";
      chip.textContent = p.command.toUpperCase().replace(/_/g, " ");
      msg.appendChild(chip);
    }
    $("#confirm-bar").hidden = false;
    setOrbState("alert");
  }

  function disarm() {
    if (!armed) return;
    armed = false;
    document.body.classList.remove("armed");
    $("#confirm-bar").hidden = true;
  }

  /* ---------------- activity stream ---------------- */
  var KIND_CLASS = [
    ["router.", "k-router"], ["agent.", "k-agent"], ["playwright.", "k-playwright"],
    ["safety.", "k-safety"], ["briefing.", "k-briefing"], ["focus.", "k-focus"],
    ["system.", "k-system"], ["meeting.", "k-meeting"]
  ];
  var SPOKEN_KINDS = ["agent.done", "briefing.ready", "focus.ended", "playwright.done", "reminder.due"];

  function kindClass(kind) {
    for (var i = 0; i < KIND_CLASS.length; i++) {
      if (kind.indexOf(KIND_CLASS[i][0]) === 0) return KIND_CLASS[i][1];
    }
    return "k-other";
  }

  function summarize(e) {
    var p = e.payload || {};
    switch (e.kind) {
      case "router.transcript": return "“" + p.text + "” · " + (p.source || "ui");
      case "router.response": return p.spoken + (p.awaitingConfirmation ? " · AWAITING CONFIRMATION" : "") + " · " + (p.intent || "");
      case "safety.confirm_request": return p.command + " ← “" + p.utterance + "”";
      case "agent.queued": return (p.task || p.template || "") + " · " + (p.project || "") + " · " + (p.runId || "");
      case "agent.progress": return p.text + " · " + p.runId;
      case "agent.done": return (p.status || "") + " · " + (p.summary || "") + " — " + (p.spoken || "");
      case "playwright.started": return (p.project || "") + " · " + (p.suite || "") + " suite";
      case "playwright.done": return p.passed + " passed · " + p.failed + " failed";
      case "briefing.ready": return (p.kind || "") + " briefing ready";
      case "focus.ended": return p.spoken || "focus block ended";
      case "reminder.due": return p.spoken || (p.kind + " fired");
      case "voice.state": return p.state;
      case "system.stop": return "all activity halted";
      default:
        try { return JSON.stringify(p).slice(0, 90); } catch (err) { return ""; }
    }
  }

  function pad(n) { return String(n).padStart(2, "0"); }
  function fmtTime(d) { return pad(d.getHours()) + ":" + pad(d.getMinutes()) + ":" + pad(d.getSeconds()); }

  function addStreamRow(e) {
    var row = document.createElement("div");
    row.className = "ev" + (SPOKEN_KINDS.indexOf(e.kind) >= 0 ? " spoken" : "");
    var t = document.createElement("span"); t.className = "t"; t.textContent = fmtTime(e.at);
    var k = document.createElement("span"); k.className = "kind " + kindClass(e.kind); k.textContent = e.kind;
    var b = document.createElement("span"); b.className = "body"; b.textContent = summarize(e);
    row.appendChild(t); row.appendChild(k); row.appendChild(b);
    streamList.prepend(row);
    while (streamList.children.length > 80) streamList.lastChild.remove();
  }

  /* ---------------- toasts (mirror what the tray speaks) ---------------- */
  function toast(kind, text) {
    var el = document.createElement("div");
    el.className = "toast";
    el.innerHTML =
      '<div class="head"><span class="eq"><i></i><i></i><i></i></span>' +
      '<span class="label">SPOKEN · ' + kind + "</span></div>" +
      '<div class="body"></div>';
    el.querySelector(".body").textContent = text;
    $("#toasts").appendChild(el);
    setTimeout(function () { el.classList.add("bye"); }, 6200);
    setTimeout(function () { el.remove(); }, 6800);
  }

  /* ---------------- event handling ---------------- */
  function handleEvent(e) {
    if (e.kind === "_sim.listening") { setOrbState("listening"); return; }
    if (e.kind === "voice.levels") {
      var lv = e.payload || {};
      scope.feed(lv.a, lv.c, lv.s);
      return;   // pure telemetry — never hits the activity stream
    }
    if (e.kind === "voice.state") {
      /* live push-to-talk: react the instant the key goes down (tray posts listening/thinking/idle) */
      var vs = (e.payload && e.payload.state) || "";
      if (vs === "listening") setOrbState("listening");
      else if (vs === "thinking") {
        setOrbState("thinking");
        clearTimeout(thinkTimer);
        thinkTimer = setTimeout(function () { if (orbState === "thinking") setOrbState("idle"); }, 14000);
      }
      else if (vs === "idle" && (orbState === "listening" || orbState === "thinking")) setOrbState("idle");
      return;
    }
    addStreamRow(e);

    switch (e.kind) {
      case "router.transcript":
        typeTranscript(e.payload.text);
        responseEl.innerHTML = "";
        setOrbState("thinking");
        clearTimeout(thinkTimer);
        thinkTimer = setTimeout(function () { if (orbState === "thinking") setOrbState("idle"); }, 14000);
        break;

      case "router.response":
        clearTimeout(thinkTimer);
        materializeResponse(e.payload.spoken || "");
        if (e.payload.data && e.payload.data.card) renderCard(e.payload.data.card);
        appendAuditLine(e.payload);
        if (e.payload.awaitingConfirmation) {
          /* arming handled by safety.confirm_request; set alert anyway */
          setOrbState("alert");
        } else {
          disarm();
          setOrbState("speaking");
          clearTimeout(speakTimer);
          var dur = Math.min(7500, 1600 + (e.payload.spoken || "").length * 42);
          speakTimer = setTimeout(function () { if (orbState === "speaking") setOrbState("idle"); }, dur);
        }
        break;

      case "safety.confirm_request":
        arm(e.payload);
        break;

      case "system.stop":
        disarm();
        setOrbState("stopped");
        clearTimeout(stopTimer);
        stopTimer = setTimeout(function () { if (orbState === "stopped") setOrbState("idle"); }, 6500);
        break;

      case "agent.queued":
        if (engine) engine.addRun(e.payload.runId);
        break;

      case "agent.done":
        if (engine) engine.endRun(e.payload.runId);
        if (e.payload.spoken) toast(e.kind, e.payload.spoken);
        break;

      case "briefing.ready":
      case "focus.ended":
      case "playwright.done":
        if (e.payload.spoken) toast(e.kind, e.payload.spoken);
        break;
    }
  }

  /* ---------------- health arc + badges ---------------- */
  function polar(cx, cy, r, deg) {
    var rad = (deg - 90) * Math.PI / 180; /* 0 = 12 o'clock, clockwise */
    return [cx + r * Math.cos(rad), cy + r * Math.sin(rad)];
  }
  function arcPath(cx, cy, r, a0, a1) {
    var s = polar(cx, cy, r, a0), e = polar(cx, cy, r, a1);
    var large = (a1 - a0) > 180 ? 1 : 0;
    return "M " + s[0].toFixed(2) + " " + s[1].toFixed(2) + " A " + r + " " + r + " 0 " + large + " 1 " + e[0].toFixed(2) + " " + e[1].toFixed(2);
  }

  var ARC_SEGS = [
    { key: "stt", label: "STT", a0: 118, a1: 152 },
    { key: "ollama", label: "LLM", a0: 163, a1: 197 },
    { key: "ttsEngine", label: "TTS", a0: 208, a1: 242 }
  ];

  function statusColor(v) {
    if (v === "ok") return "color-mix(in oklab, var(--accent) 55%, transparent)";
    if (v === "warming" || v === "degraded") return "var(--amber)";
    return "var(--red)";
  }

  function renderArc(health) {
    var svg = $("#health-arc");
    svg.innerHTML = "";
    var NS = "http://www.w3.org/2000/svg";
    ARC_SEGS.forEach(function (seg) {
      var p = document.createElementNS(NS, "path");
      p.setAttribute("d", arcPath(100, 100, 92, seg.a0, seg.a1));
      p.setAttribute("stroke", statusColor(health[seg.key]));
      svg.appendChild(p);
      var mid = (seg.a0 + seg.a1) / 2;
      var pos = polar(100, 100, 84, mid);
      var t = document.createElementNS(NS, "text");
      t.setAttribute("x", pos[0]); t.setAttribute("y", pos[1] + 2);
      t.textContent = seg.label;
      svg.appendChild(t);
    });
  }

  var BADGES = [["kernel", "KERNEL"], ["stt", "STT"], ["ollama", "OLLAMA"], ["ttsEngine", "TTS"]];

  function renderBadges(health) {
    var wrap = $("#health-badges");
    wrap.innerHTML = "";
    BADGES.forEach(function (b) {
      var v = health[b[0]];
      var el = document.createElement("div");
      el.className = "badge" + (v === "ok" ? "" : (v === "warming" ? " warn" : " down"));
      el.innerHTML = '<span class="dot"></span><span class="lbl"></span>';
      el.querySelector(".lbl").textContent = b[1];
      el.title = b[1] + ": " + v + " · " + health.machine;
      wrap.appendChild(el);
    });
  }

  var health = { kernel: "warming", stt: "warming", ollama: "warming", ttsEngine: "warming", machine: "connecting…" };
  function setHealth(h) { health = h; renderArc(health); renderBadges(health); }

  /* ---------------- tweaks bridge ---------------- */
  function applyTweaks(t) {
    if (!t) return;
    document.documentElement.style.setProperty("--accent", t.accent || "#3ee0ff");
    if (engine) {
      if (t.orbStyle && t.orbStyle !== engine.variant) engine.setVariant(t.orbStyle);
      engine.setAccent(t.accent || "#3ee0ff");
    }
    var f = (t.forceState && t.forceState !== "auto") ? t.forceState : null;
    if (f !== forcedState) {
      forcedState = f;
      if (engine) engine.setState(forcedState || orbState);
      var shown = forcedState || orbState;
      stateText.textContent = shown;
      stateDot.style.background = STATE_COLORS[shown] || "var(--accent)";
    }
    window.JarvisSim.setSpeed(t.simSpeed || 1);
    window.JarvisSim.setRunning(t.simOn !== false);
  }

  /* ---------------- init ---------------- */
  function init() {
    /* evict any stale root-scoped service worker (registered by an earlier mobile.html visit)
       — it intercepted every request on this origin and made the console laggy */
    if ("serviceWorker" in navigator) {
      navigator.serviceWorker.getRegistrations().then(function (regs) {
        regs.forEach(function (r) {
          if (new URL(r.scope).pathname === "/") r.unregister();
        });
      }).catch(function () {});
    }

    input = $("#command-input");
    transcriptEl = $("#transcript");
    responseEl = $("#response");
    stateText = $("#state-text");
    stateDot = $("#state-dot");
    streamList = $("#stream-list");
    streamPanel = $("#stream-panel");

    var reduced = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
    if (!reduced && window.OrbEngine) {
      try {
        engine = new OrbEngine($("#orb-canvas"));
      } catch (err) {
        console.warn("WebGL unavailable, using CSS fallback", err);
      }
    }
    if (!engine) {
      $("#orb-canvas").remove();
      $("#orb-fallback").hidden = false;
    }

    setOrbState("idle");
    renderArc(health);
    renderBadges(health);
    scope.init();

    /* clock */
    var clockEl = $("#clock");
    setInterval(function () { clockEl.textContent = fmtTime(new Date()); }, 1000);
    clockEl.textContent = fmtTime(new Date());

    /* command bar */
    $("#command-bar").addEventListener("submit", function (ev) {
      ev.preventDefault();
      var v = input.value.trim();
      if (!v) return;
      input.value = "";
      window.JarvisSim.sendCommand(v);
    });
    input.addEventListener("input", function () {
      if (input.value.length > 0 && (orbState === "idle" || orbState === "speaking")) setOrbState("listening");
      else if (input.value.length === 0 && orbState === "listening") setOrbState("idle");
    });
    input.focus();

    /* one keystroke away */
    document.addEventListener("keydown", function (ev) {
      if (ev.key === "Escape") { streamPanel.classList.remove("open"); return; }
      if (ev.ctrlKey || ev.metaKey || ev.altKey) return;
      if (ev.key.length !== 1) return;
      var a = document.activeElement;
      if (a === input) return;
      if (a && (a.tagName === "INPUT" || a.tagName === "TEXTAREA" || a.tagName === "SELECT" || a.isContentEditable)) return;
      input.focus();
    });

    /* confirm buttons */
    $("#btn-confirm").addEventListener("click", function () { window.JarvisSim.sendCommand("confirm"); });
    $("#btn-cancel").addEventListener("click", function () { window.JarvisSim.sendCommand("cancel"); });

    /* stream panel */
    $("#stream-toggle").addEventListener("click", function () { streamPanel.classList.toggle("open"); });
    $("#stream-close").addEventListener("click", function () { streamPanel.classList.remove("open"); });

    /* tweaks */
    window.addEventListener("jarvis-tweaks", function (ev) { applyTweaks(ev.detail); });

    /* feed */
    window.JarvisSim.on(handleEvent);
    window.JarvisSim.start();
  }

  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", init);
  else init();

  window.JarvisApp = { applyTweaks: applyTweaks, setHealth: setHealth };
})();
