/* JARVIS simulated kernel — stands in for the SignalR /ws feed and POST /api/command.
   Same event shape as the real hub: {kind, payload, actor, at}. Swap for @microsoft/signalr later. */

(function () {
  "use strict";

  var listeners = [];
  var running = true;
  var speed = 1;
  var timer = null;
  var stepIndex = 0;
  var pendingConfirm = null;
  var confirmTimer = null;
  var runCounter = 4810;

  function emit(kind, payload, actor) {
    var e = { kind: kind, payload: payload || {}, actor: actor || "kernel", at: new Date() };
    listeners.forEach(function (fn) { try { fn(e); } catch (err) { console.error(err); } });
  }

  var PROJECTS = {
    northwind: {
      name: "Northwind API", readiness: 82,
      spoken: "Northwind API is at eighty-two percent readiness. Two tester issues open, Playwright suite green as of oh-seven-twelve. The auth-refresh task is your highest-leverage move, sir."
    },
    helios: {
      name: "Helios", readiness: 67,
      spoken: "Helios sits at sixty-seven percent. Telemetry exporter types are stale and one commitment to Marta is due Friday."
    },
    "forge-cli": {
      name: "Forge CLI", readiness: 91,
      spoken: "Forge CLI is at ninety-one percent readiness. Release candidate is one green pipeline away, sir."
    }
  };

  function respond(text, delayMs) {
    setTimeout(function () { handleText(text); }, (delayMs || 0) / speed);
  }

  function thinkThen(fn) {
    setTimeout(fn, (1100 + Math.random() * 900) / speed);
  }

  function requestConfirm(utterance, command, spoken) {
    pendingConfirm = { command: command, utterance: utterance };
    emit("router.response", { intent: "ops.destructive", spoken: spoken, tier: "local", awaitingConfirmation: true });
    emit("safety.confirm_request", { command: command, utterance: utterance });
    clearTimeout(confirmTimer);
    confirmTimer = setTimeout(function () {
      if (pendingConfirm) {
        pendingConfirm = null;
        emit("router.response", { intent: "safety.timeout", spoken: "Confirmation window elapsed. Standing down, sir.", tier: "local" });
      }
    }, 14000 / speed);
  }

  function handleText(text) {
    var t = text.trim();
    var lower = t.toLowerCase();
    emit("router.transcript", { text: t, source: "ui" }, "ui");

    thinkThen(function () {
      if (lower === "confirm") {
        if (pendingConfirm) {
          var cmd = pendingConfirm.command;
          pendingConfirm = null; clearTimeout(confirmTimer);
          emit("router.response", { intent: "safety.confirmed", spoken: "Confirmed. Executing: " + cmd + ". Done, sir.", tier: "local" });
        } else {
          emit("router.response", { intent: "chat", spoken: "Nothing is awaiting confirmation, sir.", tier: "local" });
        }
        return;
      }
      if (lower === "cancel") {
        if (pendingConfirm) {
          pendingConfirm = null; clearTimeout(confirmTimer);
          emit("router.response", { intent: "safety.cancelled", spoken: "Cancelled. No action taken.", tier: "local" });
        } else {
          emit("router.response", { intent: "chat", spoken: "Nothing to cancel, sir.", tier: "local" });
        }
        return;
      }
      if (/\b(stop|halt|stand down)\b/.test(lower)) {
        emit("system.stop", {});
        emit("router.response", { intent: "system.stop", spoken: "All activity halted, sir.", tier: "reflex" });
        return;
      }
      if (/\b(wipe|delete|drop|destroy|kill|deploy|rm)\b/.test(lower)) {
        requestConfirm(t, inferCommand(lower), "That action is destructive. Confirm to proceed, sir.");
        return;
      }
      if (/briefing/.test(lower)) {
        emit("router.response", { intent: "briefing.request", spoken: "Compiling your briefing now, sir.", tier: "local" });
        setTimeout(function () {
          emit("briefing.ready", { kind: "morning", spoken: "Your morning briefing is ready. Three priorities, one overdue commitment." });
        }, 2600 / speed);
        return;
      }
      if (/\bmeeting\b/.test(lower)) {
        emit("meeting.started", { title: "Ad-hoc meeting" });
        emit("router.response", { intent: "meeting.start", spoken: "Meeting mode engaged. I am taking notes, sir.", tier: "local" });
        return;
      }
      if (/close .*commitment/.test(lower)) {
        emit("router.response", { intent: "commitment.close", spoken: "Commitment closed and logged to the ledger, sir.", tier: "local" });
        return;
      }
      if (/status|readiness|how('s| is)/.test(lower)) {
        var slug = Object.keys(PROJECTS).find(function (k) { return lower.indexOf(k) >= 0; }) || "northwind";
        emit("router.response", { intent: "project.status", spoken: PROJECTS[slug].spoken, tier: "local", data: { project: slug, readiness: PROJECTS[slug].readiness } });
        return;
      }
      if (/agent|fix|refactor|run /.test(lower)) {
        emit("router.response", { intent: "agent.dispatch", spoken: "Dispatching an agent for that now, sir.", tier: "local" });
        setTimeout(demoAgent, 1200 / speed);
        return;
      }
      emit("router.response", { intent: "chat", spoken: "Noted and logged, sir. Nothing actionable came back from the local model — try a project command.", tier: "local" });
    });
  }

  function inferCommand(lower) {
    if (lower.indexOf("staging") >= 0) return "db.wipe --env staging --project northwind";
    if (lower.indexOf("deploy") >= 0) return "deploy.release --target prod";
    if (lower.indexOf("branch") >= 0) return "git.branch.delete --remote";
    return "ops.exec --guarded";
  }

  function demoAgent() {
    var id = "run-" + (++runCounter);
    var tasks = [
      ["Fix flaky auth-refresh spec", "northwind", "Auth-refresh spec stabilised, sir. Three consecutive green runs.", "1 file changed · +22 −9"],
      ["Regenerate telemetry exporter types", "helios", "Telemetry types regenerated for Helios. Zero drift remaining.", "2 files changed · +118 −64"],
      ["Tighten release pipeline cache", "forge-cli", "Pipeline cache tightened. Build time down forty seconds.", "1 file changed · +14 −3"]
    ];
    var pick = tasks[Math.floor(Math.random() * tasks.length)];
    emit("agent.queued", { runId: id, task: pick[0], project: pick[1] });
    setTimeout(function () { emit("agent.progress", { runId: id, text: "Reproducing on chromium · attempt 1/3" }); }, 2400 / speed);
    setTimeout(function () { emit("agent.progress", { runId: id, text: "Patch applied · re-running suite" }); }, 5200 / speed);
    setTimeout(function () { emit("agent.done", { runId: id, status: "succeeded", spoken: pick[2], summary: pick[3] }); }, 8400 / speed);
  }

  function demoConfirm() {
    emit("_sim.listening", {});
    setTimeout(function () {
      handleText("wipe the staging database for northwind");
    }, 1300 / speed);
  }

  /* ---------- ambient scenario (loops) ---------- */
  var steps = [
    [3200, function () { emit("_sim.listening", {}); }],
    [1600, function () { handleText("Jarvis, status on Northwind API."); }],
    [9000, function () {
      var id = "run-" + (++runCounter);
      emit("agent.queued", { runId: id, task: "Fix flaky auth-refresh spec", project: "northwind" });
      window.__simRunA = id;
    }],
    [2500, function () { emit("agent.progress", { runId: window.__simRunA, text: "Reproduced failure on chromium · retry 1/3" }); }],
    [2600, function () {
      var id = "run-" + (++runCounter);
      emit("agent.queued", { runId: id, task: "Regenerate telemetry exporter types", project: "helios" });
      window.__simRunB = id;
    }],
    [2400, function () { emit("agent.progress", { runId: window.__simRunA, text: "Patched token clock-skew handling" }); }],
    [2600, function () { emit("agent.done", { runId: window.__simRunB, status: "succeeded", spoken: "Telemetry types regenerated for Helios. Zero drift remaining.", summary: "2 files changed · +118 −64" }); }],
    [2400, function () { emit("agent.progress", { runId: window.__simRunA, text: "Re-running spec suite · 34 tests" }); }],
    [3000, function () { emit("agent.done", { runId: window.__simRunA, status: "succeeded", spoken: "Auth-refresh spec stabilised, sir. Three consecutive green runs.", summary: "1 file changed · +22 −9" }); }],
    [4200, function () { emit("playwright.started", { project: "northwind", suite: "smoke" }); }],
    [4400, function () { emit("playwright.done", { passed: 34, failed: 0, spoken: "Playwright smoke suite: thirty-four passed, none failed." }); }],
    [5200, function () { emit("briefing.ready", { kind: "morning", spoken: "Your morning briefing is ready. Three priorities, one overdue commitment." }); }],
    [7000, function () { demoConfirm(); }],
    [16000, function () { emit("focus.ended", { spoken: "Focus block complete: ninety-two minutes of deep work on Northwind API." }); }],
    [6000, function () { emit("fleet.heartbeat", { machine: "AURORA-7", status: "online", cpu: "12%" }); }],
    [9000, function () { }]
  ];

  function loop() {
    if (!running) return;
    var entry = steps[stepIndex];
    timer = setTimeout(function () {
      if (pendingConfirm) { timer = setTimeout(loop, 1000); return; }
      try { entry[1](); } catch (e) { console.error(e); }
      stepIndex = (stepIndex + 1) % steps.length;
      loop();
    }, entry[0] / speed);
  }

  window.JarvisSim = {
    on: function (fn) { listeners.push(fn); },
    sendCommand: function (text) { handleText(text); },
    demoAgent: demoAgent,
    demoConfirm: demoConfirm,
    setSpeed: function (v) { speed = Math.max(0.1, v); },
    setRunning: function (v) {
      v = !!v;
      if (v === running) return;
      running = v;
      clearTimeout(timer);
      if (running) loop();
    },
    start: function () {
      setTimeout(function () {
        emit("router.response", { intent: "greeting", spoken: "Online. Good morning, sir — all systems nominal.", tier: "reflex" });
      }, 900 / speed);
      loop();
    },
    health: function () {
      return { kernel: "ok", stt: "ok", ollama: "ok", ttsEngine: "ok", voiceProfile: "Jarvis-Neural-v2", projects: 3, machine: "DESKTOP-STARK" };
    }
  };
})();
