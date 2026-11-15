/* JARVIS orb — "the abyssal machine-mind".
   A bioluminescent neural organism: a spherical lattice of neurons joined by synapses,
   with signal packets visibly travelling the network. Each kernel state re-tunes the
   whole organism — tempo, firing density, signal direction, and color temperature:

     idle      deep-sea teal, slow breathing, lone synapses firing in the dark
     listening arctic glow, signals flow INWARD, the organism goes still and attentive
     thinking  IGNITES — molten amber over violet, cascading pulse storms, fast rotation
     speaking  electric cyan-white, waves radiate OUTWARD in speech-like cadence
     alert     blood-red double heartbeat
     stopped   ash grey, almost frozen

   Engineering: every layer is additive-blend self-glow (sprite math in shaders) — no
   post-processing, no ReadPixels (the old engine stalled the iGPU with it). Draw calls:
   nodes(1) + synapses(1) + flares(1) + dust(1) + comets(1) + core sprites(3) + halo(1).
   API-compatible with the old engine: setState/setAccent/setVariant/addRun/endRun/resize. */

(function () {
  "use strict";

  /* ------------------------------ state recipes ------------------------------ */
  /* a: bright color, b: deep color, halo: atmosphere; speed: packet velocity,
     density: fraction of synapses firing, dir: +1 outward / -1 inward,
     agit: lattice breathing amplitude, rot: rotation rate, flare: flares/sec,
     core: core intensity, env: amplitude envelope ("calm"|"cadence"|"heartbeat") */
  var STATES = {
    idle:      { a: 0x5ef2e8, b: 0x0a3c46, halo: 0x0e6e78, speed: 0.12, density: 0.32,
                 dir: 1,  agit: 0.045, rot: 0.045, flare: 0.6,  core: 0.55, halOp: 0.38, env: "calm" },
    listening: { a: 0xbcfff4, b: 0x0e5c5e, halo: 0x23b0a6, speed: 0.38, density: 0.45,
                 dir: -1, agit: 0.018, rot: 0.018, flare: 2.0,  core: 0.85, halOp: 0.42, env: "calm" },
    thinking:  { a: 0xffc46b, b: 0x6e2a86, halo: 0xff9a2e, speed: 1.05, density: 0.88,
                 dir: 1,  agit: 0.105, rot: 0.17,  flare: 14.0, core: 1.0,  halOp: 0.58, env: "calm" },
    speaking:  { a: 0xeaffff, b: 0x0f7fae, halo: 0x36d6ff, speed: 0.55, density: 0.62,
                 dir: 1,  agit: 0.06,  rot: 0.07,  flare: 5.0,  core: 0.9,  halOp: 0.5,  env: "cadence" },
    alert:     { a: 0xff5d6c, b: 0x58101c, halo: 0xff2440, speed: 0.45, density: 0.5,
                 dir: 1,  agit: 0.05,  rot: 0.05,  flare: 3.0,  core: 0.95, halOp: 0.55, env: "heartbeat" },
    stopped:   { a: 0x8a98a5, b: 0x1a2026, halo: 0x223040, speed: 0.02, density: 0.05,
                 dir: 1,  agit: 0.0,   rot: 0.006, rotY: 0, flare: 0.0, core: 0.25, halOp: 0.18, env: "calm" },
  };

  var NODE_COUNT = 460;
  var DUST_COUNT = 540;
  var FLARE_POOL = 48;
  var COMET_POOL = 8;
  var R_LATTICE = 1.72;

  /* ------------------------------ helpers ------------------------------ */

  function fibSphere(i, n) {
    var golden = Math.PI * (3 - Math.sqrt(5));
    var y = 1 - (i / (n - 1)) * 2;
    var r = Math.sqrt(1 - y * y);
    var th = golden * i;
    return [Math.cos(th) * r, y, Math.sin(th) * r];
  }

  function glowTexture(size, inner, mid) {
    var c = document.createElement("canvas");
    c.width = c.height = size;
    var g = c.getContext("2d");
    var grd = g.createRadialGradient(size / 2, size / 2, 0, size / 2, size / 2, size / 2);
    grd.addColorStop(0, "rgba(255,255,255," + inner + ")");
    grd.addColorStop(0.25, "rgba(255,255,255," + mid + ")");
    grd.addColorStop(1, "rgba(255,255,255,0)");
    g.fillStyle = grd;
    g.fillRect(0, 0, size, size);
    var tex = new THREE.CanvasTexture(c);
    tex.needsUpdate = true;
    return tex;
  }

  /* ------------------------------ engine ------------------------------ */

  function OrbEngine(canvas) {
    this.canvas = canvas;
    this.variant = "neural";
    this.accent = new THREE.Color(0x3ee0ff);

    this.renderer = new THREE.WebGLRenderer({
      canvas: canvas, antialias: false, alpha: true, powerPreference: "low-power",
    });
    this.renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 1.5));

    this.scene = new THREE.Scene();
    this.camera = new THREE.PerspectiveCamera(30, 1, 0.1, 80);
    this.camera.position.z = 8;

    this.group = new THREE.Group();          // everything that rotates
    this.scene.add(this.group);

    this._t = 0;
    this._last = performance.now();
    this._stateName = "idle";
    this._cur = this._materialize(STATES.idle);   // live (lerped) params
    this._tgt = this._materialize(STATES.idle);
    this._runs = [];                               // active agent comets

    this._buildLattice();
    this._buildSynapses();
    this._buildCore();
    this._buildDust();
    this._buildFlares();
    this._buildComets();
    this._buildHalo();

    this.resize();
    var self = this;
    window.addEventListener("resize", function () { self.resize(); });
    if (window.ResizeObserver) {
      this._ro = new ResizeObserver(function () { self.resize(); });
      this._ro.observe(canvas);
    }
    this._loop();
  }

  OrbEngine.prototype._materialize = function (s) {
    return {
      a: new THREE.Color(s.a), b: new THREE.Color(s.b), halo: new THREE.Color(s.halo),
      speed: s.speed, density: s.density, dir: s.dir, agit: s.agit, rot: s.rot,
      flare: s.flare, core: s.core, halOp: s.halOp, env: s.env,
    };
  };

  /* ---------- layer: neurons ---------- */
  OrbEngine.prototype._buildLattice = function () {
    var pos = new Float32Array(NODE_COUNT * 3);
    var phase = new Float32Array(NODE_COUNT);
    var shell = new Float32Array(NODE_COUNT);
    this._nodePos = [];
    for (var i = 0; i < NODE_COUNT; i++) {
      var p = fibSphere(i, NODE_COUNT);
      var jitter = 0.92 + Math.random() * 0.18;            // organic two-ish shell depth
      var x = p[0] * R_LATTICE * jitter, y = p[1] * R_LATTICE * jitter, z = p[2] * R_LATTICE * jitter;
      pos[i * 3] = x; pos[i * 3 + 1] = y; pos[i * 3 + 2] = z;
      phase[i] = Math.random();
      shell[i] = jitter > 1.0 ? 1 : 0;
      this._nodePos.push(new THREE.Vector3(x, y, z));
    }
    var geo = new THREE.BufferGeometry();
    geo.setAttribute("position", new THREE.BufferAttribute(pos, 3));
    geo.setAttribute("aPhase", new THREE.BufferAttribute(phase, 1));
    geo.setAttribute("aShell", new THREE.BufferAttribute(shell, 1));

    this._nodeMat = new THREE.ShaderMaterial({
      transparent: true, depthWrite: false, blending: THREE.AdditiveBlending,
      uniforms: {
        uTime: { value: 0 }, uAgit: { value: 0.04 }, uEnv: { value: 1 },
        uColorA: { value: new THREE.Color() }, uColorB: { value: new THREE.Color() },
        uPx: { value: this.renderer.getPixelRatio() }, uScale: { value: 1 },
      },
      vertexShader: [
        "attribute float aPhase; attribute float aShell;",
        "uniform float uTime, uAgit, uPx, uEnv, uScale;",
        "varying float vTw;",
        "void main(){",
        "  vec3 dirN = normalize(position);",
        "  float br = sin(uTime*0.8 + aPhase*6.2831);",
        "  vec3 p = position + dirN * br * uAgit;",
        "  vec4 mv = modelViewMatrix * vec4(p, 1.0);",
        "  float tw = 0.5 + 0.5*sin(uTime*(1.3 + aShell*0.9) + aPhase*12.566);",
        "  vec3 nView = normalize(normalMatrix * dirN);",
        "  float rim = 1.0 - abs(nView.z);",
        "  vTw = tw * uEnv * (0.55 + 0.75 * rim);",
        "  gl_PointSize = (3.4 + 4.8*tw*uEnv) * uScale * uPx * (20.0 / -mv.z);",
        "  gl_Position = projectionMatrix * mv;",
        "}",
      ].join("\n"),
      fragmentShader: [
        "uniform vec3 uColorA, uColorB;",
        "varying float vTw;",
        "void main(){",
        "  float d = length(gl_PointCoord - 0.5);",
        "  float a = pow(smoothstep(0.5, 0.0, d), 1.7);",
        "  vec3 col = mix(uColorB, uColorA, vTw);",
        "  gl_FragColor = vec4(col, a * (0.3 + 0.7*vTw));",
        "}",
      ].join("\n"),
    });
    this.group.add(new THREE.Points(geo, this._nodeMat));
  };

  /* ---------- layer: synapses with travelling packets ---------- */
  OrbEngine.prototype._buildSynapses = function () {
    // connect each neuron to its 2 nearest forward neighbours -> organic web
    var segs = [];
    for (var i = 0; i < NODE_COUNT; i++) {
      var best = [-1, -1], bd = [1e9, 1e9];
      for (var j = 0; j < NODE_COUNT; j++) {
        if (i === j) continue;
        var d = this._nodePos[i].distanceToSquared(this._nodePos[j]);
        if (d < bd[0]) { bd[1] = bd[0]; best[1] = best[0]; bd[0] = d; best[0] = j; }
        else if (d < bd[1]) { bd[1] = d; best[1] = j; }
      }
      if (best[0] > i) segs.push([i, best[0]]);
      else if (best[0] >= 0 && best[0] < i) segs.push([best[0], i]);
      if (best[1] > i) segs.push([i, best[1]]);
    }
    // subdivide each segment so the packet has vertices to light up
    var STEPS = 6;
    var count = segs.length * STEPS * 2;
    var pos = new Float32Array(count * 3);
    var aT = new Float32Array(count);
    var aSeed = new Float32Array(count);
    var aRad = new Float32Array(count);
    var k = 0;
    for (var s = 0; s < segs.length; s++) {
      var A = this._nodePos[segs[s][0]], B = this._nodePos[segs[s][1]];
      var seed = Math.random();
      // order ends so "outward" means away from the planet core consistently per edge
      var inner = A.length() <= B.length() ? A : B;
      var outer = A.length() <= B.length() ? B : A;
      for (var st = 0; st < STEPS; st++) {
        var t0 = st / STEPS, t1 = (st + 1) / STEPS;
        var p0 = inner.clone().lerp(outer, t0), p1 = inner.clone().lerp(outer, t1);
        // slight outward bow so the web reads as organic, not wireframe
        var bow = Math.sin(Math.PI * (t0 + t1) * 0.5) * 0.05;
        p0.multiplyScalar(1 + bow * Math.sin(Math.PI * t0));
        p1.multiplyScalar(1 + bow * Math.sin(Math.PI * t1));
        pos[k * 3] = p0.x; pos[k * 3 + 1] = p0.y; pos[k * 3 + 2] = p0.z;
        aT[k] = t0; aSeed[k] = seed; aRad[k] = inner.length(); k++;
        pos[k * 3] = p1.x; pos[k * 3 + 1] = p1.y; pos[k * 3 + 2] = p1.z;
        aT[k] = t1; aSeed[k] = seed; aRad[k] = inner.length(); k++;
      }
    }
    var geo = new THREE.BufferGeometry();
    geo.setAttribute("position", new THREE.BufferAttribute(pos, 3));
    geo.setAttribute("aT", new THREE.BufferAttribute(aT, 1));
    geo.setAttribute("aSeed", new THREE.BufferAttribute(aSeed, 1));

    this._synMat = new THREE.ShaderMaterial({
      transparent: true, depthWrite: false, blending: THREE.AdditiveBlending,
      uniforms: {
        uTime: { value: 0 }, uSpeed: { value: 0.1 }, uDensity: { value: 0.2 },
        uDir: { value: 1 }, uEnv: { value: 1 },
        uColorA: { value: new THREE.Color() }, uColorB: { value: new THREE.Color() },
      },
      vertexShader: [
        "attribute float aT; attribute float aSeed;",
        "uniform float uTime, uSpeed, uDensity, uDir, uEnv;",
        "varying float vGlow;",
        "float hash(float n){ return fract(sin(n)*43758.5453); }",
        "void main(){",
        "  float cycle = uTime * uSpeed * (0.7 + 0.6*aSeed) + aSeed * 7.31;",
        "  float travel = fract(cycle);",
        "  float p = (uDir > 0.0) ? aT : 1.0 - aT;",
        "  float packet = smoothstep(0.16, 0.0, abs(p - travel));",
        "  float fire = step(1.0 - uDensity, hash(aSeed * 91.7 + floor(cycle)));",
        "  vGlow = (0.11 + 0.17*uDensity + packet * fire * 1.15) * uEnv;",
        "  gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0);",
        "}",
      ].join("\n"),
      fragmentShader: [
        "uniform vec3 uColorA, uColorB;",
        "varying float vGlow;",
        "void main(){",
        "  vec3 col = mix(uColorB, uColorA, clamp(vGlow, 0.0, 1.0));",
        "  gl_FragColor = vec4(col, clamp(vGlow, 0.0, 0.85));",
        "}",
      ].join("\n"),
    });
    this.group.add(new THREE.LineSegments(geo, this._synMat));
  };

  /* ---------- layer: core (nested glow sprites + dense kernel points) ---------- */
  OrbEngine.prototype._buildCore = function () {
    this._coreTexSoft = glowTexture(128, 0.9, 0.32);
    this._coreTexHot = glowTexture(128, 1.0, 0.5);
    this._coreSprites = [];
    var scales = [1.45, 0.95, 0.55];
    for (var i = 0; i < 3; i++) {
      var m = new THREE.SpriteMaterial({
        map: i === 2 ? this._coreTexHot : this._coreTexSoft,
        transparent: true, depthWrite: false, blending: THREE.AdditiveBlending,
        color: 0xffffff, opacity: 0.5,
      });
      var sp = new THREE.Sprite(m);
      sp.scale.setScalar(scales[i]);
      this.scene.add(sp);            // core doesn't rotate — it burns in place
      this._coreSprites.push(sp);
    }
    // kernel: a tight cluster of bright points inside
    var N = 70, pos = new Float32Array(N * 3), ph = new Float32Array(N), sh = new Float32Array(N);
    for (var j = 0; j < N; j++) {
      var v = new THREE.Vector3().randomDirection ? new THREE.Vector3().randomDirection() :
        new THREE.Vector3(Math.random() * 2 - 1, Math.random() * 2 - 1, Math.random() * 2 - 1).normalize();
      v.multiplyScalar(0.18 + Math.pow(Math.random(), 1.6) * 0.34);
      pos[j * 3] = v.x; pos[j * 3 + 1] = v.y; pos[j * 3 + 2] = v.z;
      ph[j] = Math.random(); sh[j] = 1;
    }
    var geo = new THREE.BufferGeometry();
    geo.setAttribute("position", new THREE.BufferAttribute(pos, 3));
    geo.setAttribute("aPhase", new THREE.BufferAttribute(ph, 1));
    geo.setAttribute("aShell", new THREE.BufferAttribute(sh, 1));
    this._kernel = new THREE.Points(geo, this._nodeMat);   // shares the neuron shader
    this.group.add(this._kernel);
  };

  /* ---------- layer: ambient dust shell ---------- */
  OrbEngine.prototype._buildDust = function () {
    var pos = new Float32Array(DUST_COUNT * 3), ph = new Float32Array(DUST_COUNT), sh = new Float32Array(DUST_COUNT);
    for (var i = 0; i < DUST_COUNT; i++) {
      var p = fibSphere((i * 7919) % DUST_COUNT, DUST_COUNT);
      var r = 2.2 + Math.pow(Math.random(), 0.6) * 1.5;
      pos[i * 3] = p[0] * r + (Math.random() - 0.5) * 0.7;
      pos[i * 3 + 1] = p[1] * r + (Math.random() - 0.5) * 0.7;
      pos[i * 3 + 2] = p[2] * r + (Math.random() - 0.5) * 0.7;
      ph[i] = Math.random(); sh[i] = 0;
    }
    var geo = new THREE.BufferGeometry();
    geo.setAttribute("position", new THREE.BufferAttribute(pos, 3));
    geo.setAttribute("aPhase", new THREE.BufferAttribute(ph, 1));
    geo.setAttribute("aShell", new THREE.BufferAttribute(sh, 1));
    this._dustMat = this._nodeMat.clone();
    this._dustMat.uniforms = THREE.UniformsUtils.clone(this._nodeMat.uniforms);
    this._dust = new THREE.Points(geo, this._dustMat);
    this._dust.rotation.z = 0.4;
    this.scene.add(this._dust);     // counter-rotated separately for parallax
  };

  /* ---------- layer: flares (neurons firing hard) ---------- */
  OrbEngine.prototype._buildFlares = function () {
    var pos = new Float32Array(FLARE_POOL * 3);
    var size = new Float32Array(FLARE_POOL);
    var alpha = new Float32Array(FLARE_POOL);
    var geo = new THREE.BufferGeometry();
    geo.setAttribute("position", new THREE.BufferAttribute(pos, 3));
    geo.setAttribute("aSize", new THREE.BufferAttribute(size, 1));
    geo.setAttribute("aAlpha", new THREE.BufferAttribute(alpha, 1));
    this._flareMat = new THREE.ShaderMaterial({
      transparent: true, depthWrite: false, blending: THREE.AdditiveBlending,
      uniforms: { uColor: { value: new THREE.Color() }, uPx: { value: this.renderer.getPixelRatio() } },
      vertexShader: [
        "attribute float aSize; attribute float aAlpha;",
        "uniform float uPx; varying float vA;",
        "void main(){",
        "  vA = aAlpha;",
        "  vec4 mv = modelViewMatrix * vec4(position, 1.0);",
        "  gl_PointSize = aSize * uPx * (20.0 / -mv.z);",
        "  gl_Position = projectionMatrix * mv;",
        "}",
      ].join("\n"),
      fragmentShader: [
        "uniform vec3 uColor; varying float vA;",
        "void main(){",
        "  float d = length(gl_PointCoord - 0.5);",
        "  float a = pow(smoothstep(0.5, 0.0, d), 2.2);",
        "  gl_FragColor = vec4(uColor, a * vA);",
        "}",
      ].join("\n"),
    });
    this._flares = new THREE.Points(geo, this._flareMat);
    this.group.add(this._flares);
    this._flareLife = new Float32Array(FLARE_POOL);   // 0 = dead
    this._flareAcc = 0;
  };

  OrbEngine.prototype._spawnFlare = function () {
    for (var i = 0; i < FLARE_POOL; i++) {
      if (this._flareLife[i] <= 0) {
        var node = this._nodePos[(Math.random() * this._nodePos.length) | 0];
        var attr = this._flares.geometry.attributes;
        attr.position.array[i * 3] = node.x;
        attr.position.array[i * 3 + 1] = node.y;
        attr.position.array[i * 3 + 2] = node.z;
        attr.position.needsUpdate = true;
        this._flareLife[i] = 1;
        return;
      }
    }
  };

  /* ---------- layer: agent-run comets ---------- */
  OrbEngine.prototype._buildComets = function () {
    var pos = new Float32Array(COMET_POOL * 3);
    var size = new Float32Array(COMET_POOL);
    var alpha = new Float32Array(COMET_POOL);
    var geo = new THREE.BufferGeometry();
    geo.setAttribute("position", new THREE.BufferAttribute(pos, 3));
    geo.setAttribute("aSize", new THREE.BufferAttribute(size, 1));
    geo.setAttribute("aAlpha", new THREE.BufferAttribute(alpha, 1));
    this._cometMat = this._flareMat.clone();
    this._cometMat.uniforms = THREE.UniformsUtils.clone(this._flareMat.uniforms);
    this._cometMat.uniforms.uColor.value = new THREE.Color(0xaef7ff);
    this._comets = new THREE.Points(geo, this._cometMat);
    this.scene.add(this._comets);
  };

  /* ---------- layer: halo ---------- */
  OrbEngine.prototype._buildHalo = function () {
    this._haloMat = new THREE.SpriteMaterial({
      map: glowTexture(256, 0.5, 0.16),
      transparent: true, depthWrite: false, blending: THREE.AdditiveBlending, opacity: 0.35,
    });
    var halo = new THREE.Sprite(this._haloMat);
    halo.scale.setScalar(7.5);
    halo.position.z = -1.5;
    this.scene.add(halo);
  };

  /* ------------------------------ public API ------------------------------ */

  OrbEngine.prototype.setState = function (name) {
    var s = STATES[name] || STATES.idle;
    this._stateName = name;
    this._tgt = this._materialize(s);
  };

  OrbEngine.prototype.setAccent = function (hex) {
    try { this.accent = new THREE.Color(hex); } catch (e) { }
  };

  OrbEngine.prototype.setVariant = function (name) { this.variant = name || "neural"; };

  OrbEngine.prototype.addRun = function (id) {
    if (this._runs.length >= COMET_POOL) return;
    var tilt = Math.random() * Math.PI;
    var n = new THREE.Vector3(Math.sin(tilt), Math.cos(tilt), Math.sin(tilt * 2.7)).normalize();
    var u = new THREE.Vector3(0, 1, 0).cross(n);
    if (u.lengthSq() < 0.01) u = new THREE.Vector3(1, 0, 0);
    u.normalize();
    var v = n.clone().cross(u).normalize();
    this._runs.push({ id: id, angle: Math.random() * 6.28, speed: 0.7 + Math.random() * 0.5, r: 2.35, u: u, v: v, born: this._t });
  };

  OrbEngine.prototype.endRun = function (id) {
    this._runs = this._runs.filter(function (r) { return r.id !== id; });
  };

  OrbEngine.prototype.resize = function () {
    var w = window.innerWidth, h = window.innerHeight;
    if (!w || !h) return;
    this.renderer.setSize(w, h, false);
    var aspect = w / h;
    this.camera.aspect = aspect;
    this.camera.position.z = 8 / Math.min(1, aspect * 1.15);
    if (aspect < 1) {
      /* portrait station: zoom out so the lattice fits the narrow width with margin,
         then lift the orb into the top third — conversation owns the rest */
      this.camera.position.z *= 1.18;
      var halfH = this.camera.position.z * Math.tan((this.camera.fov / 2) * Math.PI / 180);
      this.scene.position.y = halfH * 0.40;
    } else {
      this.scene.position.y = 0;
    }
    this.camera.updateProjectionMatrix();
  };

  /* ------------------------------ frame loop ------------------------------ */

  function lerp(a, b, k) { return a + (b - a) * k; }

  OrbEngine.prototype._envelope = function () {
    var t = this._t;
    if (this._cur.env === "heartbeat") {
      var ph = (t * 1.15) % 1;                                 // lub-dub
      var th = Math.exp(-Math.pow((ph - 0.06) * 14, 2)) + 0.7 * Math.exp(-Math.pow((ph - 0.28) * 14, 2));
      return 0.55 + th * 0.9;
    }
    if (this._cur.env === "cadence")                            // speech-ish rhythm
      return 0.8 + 0.25 * Math.abs(Math.sin(t * 6.3)) * (0.6 + 0.4 * Math.sin(t * 2.1));
    return 1.0;
  };

  OrbEngine.prototype._loop = function () {
    var self = this;
    requestAnimationFrame(function () { self._loop(); });

    var now = performance.now();
    var dt = Math.min(0.05, (now - this._last) / 1000);
    this._last = now;
    this._t += dt;

    // ease live params toward the target state
    var k = Math.min(1, dt * 3.2);
    var c = this._cur, g = this._tgt;
    c.a.lerp(g.a, k); c.b.lerp(g.b, k); c.halo.lerp(g.halo, k);
    c.speed = lerp(c.speed, g.speed, k);
    c.density = lerp(c.density, g.density, k);
    c.dir = lerp(c.dir, g.dir, k);
    c.agit = lerp(c.agit, g.agit, k);
    c.rot = lerp(c.rot, g.rot, k);
    c.flare = lerp(c.flare, g.flare, k);
    c.core = lerp(c.core, g.core, k);
    c.halOp = lerp(c.halOp, g.halOp, k);
    c.env = g.env;

    var env = this._envelope();

    // rotation: slow tumble on two axes; thinking spins faster
    this.group.rotation.y += c.rot * dt;
    this.group.rotation.x = Math.sin(this._t * 0.11) * 0.16;
    this._dust.rotation.y -= c.rot * 0.35 * dt;

    // uniforms
    var nu = this._nodeMat.uniforms;
    nu.uTime.value = this._t; nu.uAgit.value = c.agit; nu.uEnv.value = env;
    nu.uColorA.value.copy(c.a); nu.uColorB.value.copy(c.b);

    var du = this._dustMat.uniforms;
    du.uTime.value = this._t * 0.5; du.uAgit.value = 0; du.uEnv.value = 0.33; du.uScale.value = 0.42;
    du.uColorA.value.copy(c.a).multiplyScalar(0.55); du.uColorB.value.copy(c.b);

    var su = this._synMat.uniforms;
    su.uTime.value = this._t; su.uSpeed.value = c.speed; su.uDensity.value = c.density;
    su.uDir.value = c.dir >= 0 ? 1 : -1; su.uEnv.value = env;
    su.uColorA.value.copy(c.a); su.uColorB.value.copy(c.b);

    // core breathing
    var breathe = 1 + 0.06 * Math.sin(this._t * 1.4);
    for (var i = 0; i < this._coreSprites.length; i++) {
      var sp = this._coreSprites[i];
      sp.material.color.copy(i === 2 ? c.a : c.halo);
      sp.material.opacity = (0.28 + 0.45 * c.core) * env * (i === 0 ? 0.7 : 1);
      sp.scale.setScalar([1.5, 0.98, 0.58][i] * breathe * (0.85 + 0.3 * c.core));
      sp.position.copy(this.scene.position).sub(this.scene.position); // stays at group origin
    }
    // halo
    this._haloMat.opacity = c.halOp * (0.85 + 0.15 * env);
    this._haloMat.color.copy(c.halo);

    // flares: spawn by state rate; thinking bursts in clusters
    this._flareAcc += dt * c.flare;
    while (this._flareAcc >= 1) {
      this._flareAcc -= 1;
      this._spawnFlare();
      if (c.flare > 8) { this._spawnFlare(); this._spawnFlare(); }
    }
    var fa = this._flares.geometry.attributes;
    this._flareMat.uniforms.uColor.value.copy(c.a);
    for (var f = 0; f < FLARE_POOL; f++) {
      if (this._flareLife[f] > 0) {
        this._flareLife[f] -= dt * 1.6;
        var life = Math.max(0, this._flareLife[f]);
        fa.aAlpha.array[f] = Math.sin(Math.PI * life) * 0.9;
        fa.aSize.array[f] = 9 + (1 - life) * 13;
      } else fa.aAlpha.array[f] = 0;
    }
    fa.aAlpha.needsUpdate = true; fa.aSize.needsUpdate = true;

    // comets: orbiting agent runs
    var ca = this._comets.geometry.attributes;
    for (var q = 0; q < COMET_POOL; q++) {
      if (q < this._runs.length) {
        var run = this._runs[q];
        run.angle += dt * run.speed;
        var px = run.u.x * Math.cos(run.angle) + run.v.x * Math.sin(run.angle);
        var py = run.u.y * Math.cos(run.angle) + run.v.y * Math.sin(run.angle);
        var pz = run.u.z * Math.cos(run.angle) + run.v.z * Math.sin(run.angle);
        ca.position.array[q * 3] = px * run.r;
        ca.position.array[q * 3 + 1] = py * run.r;
        ca.position.array[q * 3 + 2] = pz * run.r;
        ca.aSize.array[q] = 7 + 2 * Math.sin(this._t * 5 + q);
        ca.aAlpha.array[q] = 0.85;
      } else ca.aAlpha.array[q] = 0;
    }
    ca.position.needsUpdate = true; ca.aSize.needsUpdate = true; ca.aAlpha.needsUpdate = true;

    this.renderer.render(this.scene, this.camera);
  };

  window.OrbEngine = OrbEngine;
})();
