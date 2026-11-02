/* Elan's Outpost - the Atlas model viewer (3.6, Atlas 3D models steps 3 + 4). Runs inside the app's sandboxed WebView2 (a WebView2CompositionControl in the
   NPC page's "Model" card), served from https://models.outpost.local/_page/viewer.html. It knows nothing but what the app sends it (window.chrome.webview
   messages: show / load / pause / clear) and the files under its own host: <key>/m/<display>.m2b2 (CreatureModel's "M2B2", Services/Models3d/CreatureModelBuild.cs) and
   <key>/t/<fdid>.mtx (ObjTexture's "MTX1"). three.js r149, the same file as the world map's.
   Axes are already three.js's (up +y, the creature faces +z). Skinning: one SkinnedMesh, its Skeleton's bones are NOT in the scene graph; every frame the
   page works out each bone's matrix in model space itself, the WoW M2 way - translate(pivot) * T * R * S * translate(-pivot), times the parent's - from the
   Stand sequence's tracks (linear / slerp; hermite and bezier keys are read as linear, noted) and writes it to the bone's matrixWorld. The bind pose is the
   identity (bone inverses identity, bindMode "detached" with an identity bind matrix), so the shader's bone matrix is exactly that matrix.
   Not done yet (noted): billboard bones (bill != 0) turn like the rest, texture animations, colour / transparency tracks after the first key, particles. */
(function () {
  'use strict';
  var wv = window.chrome && window.chrome.webview;
  function send(o) { if (wv) { try { wv.postMessage(o); } catch (e) { /* the app is gone */ } } }
  var errs = 0;
  function reportErr(msg) { msg = String(msg || 'unknown error').slice(0, 200); if (++errs <= 5) send({ t: 'error', msg: msg }); }
  window.addEventListener('error', function (e) { reportErr((e.message || e) + (e.lineno ? ' (line ' + e.lineno + ')' : '')); });
  window.addEventListener('unhandledrejection', function (e) { reportErr('unhandled: ' + (e.reason && e.reason.message || e.reason)); });
  function clamp(v, a, b) { return v < a ? a : v > b ? b : v; }

  // the state, read by the app's self-test through window.mv (EvalForTest)
  var ST = { state: 'idle', why: '', gl: false, s3tc: false, model: '', nv: 0, ni: 0, bones: 0, animated: 0, billboards: 0, batches: 0, texWanted: 0, texLoaded: 0,
    texFailed: 0, texDecoded: 0, animMs: 0, duration: 0, frames: 0, paused: false, hidden: false, notes: [], loads: 0 };
  var BG = 0x16191C;            // Brush.Surface1 (DESIGN.md): the card the viewer sits in

  var canvas = document.getElementById('gl');
  var renderer = null, scene = null, camera = null, pivot = null, mesh = null, skel = null, M = null, gen = 0;
  try {
    renderer = new THREE.WebGLRenderer({ canvas: canvas, antialias: true, alpha: false, powerPreference: 'low-power' });
    renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
    renderer.setClearColor(BG, 1);
    ST.gl = true;
    ST.s3tc = !!renderer.getContext().getExtension('WEBGL_compressed_texture_s3tc');
  } catch (e) { renderer = null; ST.gl = false; }

  scene = new THREE.Scene();
  camera = new THREE.PerspectiveCamera(30, 1, 0.05, 2000);
  scene.add(camera);
  // a soft key and fill (fixed to the camera, so the turning creature is always lit from the front-left) over a quiet sky / ground fill
  scene.add(new THREE.HemisphereLight(0xdde4ec, 0x2a2f35, 0.55));
  var key = new THREE.DirectionalLight(0xfff0dc, 0.85); key.position.set(-1.2, 1.6, 1.4); camera.add(key);
  var fill = new THREE.DirectionalLight(0x9fb4d0, 0.3); fill.position.set(1.5, 0.3, 0.6); camera.add(fill);
  var rim = new THREE.DirectionalLight(0xc8d6e8, 0.25); rim.position.set(0.4, 1.0, -1.6); camera.add(rim);
  pivot = new THREE.Group(); scene.add(pivot);

  // ---------------------------------------------------------------------------------------------------------------- the view: framed, turned, zoomed
  var view = { yaw: 0, pitch: 0.26, zoom: 1, dist: 5, target: new THREE.Vector3(), spin: 0.32, dragging: false, lastX: 0, lastY: 0, idleAt: 0 };
  var YAW0 = 0.61;             // 35 degrees: a 3/4 view from the front (the creature faces +z)
  function placeCamera() {
    var d = view.dist * view.zoom, p = view.pitch, y = YAW0;
    camera.position.set(Math.sin(y) * Math.cos(p) * d, Math.sin(p) * d, Math.cos(y) * Math.cos(p) * d).add(view.target);
    camera.lookAt(view.target);
    camera.near = Math.max(0.01, d / 200); camera.far = d * 20 + 50; camera.updateProjectionMatrix();
    pivot.rotation.y = view.yaw;
  }
  function resize() {
    if (!renderer) return;
    var w = Math.max(1, window.innerWidth), h = Math.max(1, window.innerHeight);
    renderer.setSize(w, h, false);
    camera.aspect = w / h; camera.updateProjectionMatrix();
    // the fit works on the narrower side: a tall creature in a wide card still fits
    if (M) frame();
  }
  function frame() {
    var mn = M.meta.min, mx = M.meta.max;
    var c = new THREE.Vector3((mn[0] + mx[0]) / 2, (mn[1] + mx[1]) / 2, (mn[2] + mx[2]) / 2);
    var r = 0.5 * Math.sqrt(Math.pow(mx[0] - mn[0], 2) + Math.pow(mx[1] - mn[1], 2) + Math.pow(mx[2] - mn[2], 2));
    r = Math.max(r, 0.05);
    var vf = camera.fov * Math.PI / 360, hf = Math.atan(Math.tan(vf) * camera.aspect);
    view.dist = r / Math.sin(Math.min(vf, hf)) * 0.86;      // the bounding sphere is generous: a little closer still leaves room to turn
    if (mesh) mesh.position.set(-c.x, -c.y, -c.z);
    view.target.set(0, 0, 0);
    placeCamera();
  }
  window.addEventListener('resize', resize);

  canvas.addEventListener('pointerdown', function (e) {
    if (e.button !== 0) return;
    view.dragging = true; view.lastX = e.clientX; view.lastY = e.clientY; canvas.classList.add('drag');
    try { canvas.setPointerCapture(e.pointerId); } catch (x) { }
    e.preventDefault();
  });
  canvas.addEventListener('pointermove', function (e) {
    if (!view.dragging) return;
    view.yaw += (e.clientX - view.lastX) * 0.012;
    view.pitch = clamp(view.pitch + (e.clientY - view.lastY) * 0.006, -0.35, 1.2);
    view.lastX = e.clientX; view.lastY = e.clientY; placeCamera();
  });
  function endDrag(e) { if (!view.dragging) return; view.dragging = false; view.idleAt = performance.now(); canvas.classList.remove('drag'); try { canvas.releasePointerCapture(e.pointerId); } catch (x) { } }
  canvas.addEventListener('pointerup', endDrag);
  canvas.addEventListener('pointercancel', endDrag);
  canvas.addEventListener('wheel', function (e) {
    e.preventDefault();
    view.zoom = clamp(view.zoom * Math.exp(clamp(e.deltaY, -300, 300) * 0.0012), 0.45, 2.2);
    placeCamera();
  }, { passive: false });
  canvas.addEventListener('dblclick', function () { view.yaw = 0; view.pitch = 0.26; view.zoom = 1; placeCamera(); });
  canvas.addEventListener('contextmenu', function (e) { e.preventDefault(); });

  // ---------------------------------------------------------------------------------------------------------------- textures (MTX1), lifted from view3d.js oTexParse
  // DXT blocks go to the GPU as they are (s3tc); without s3tc the top level is decoded here into RGBA (the fallback), an RGBA file is used as it is
  function rgb565(c, p, o) { p[o] = (((c >> 11) & 31) * 527 + 23) >> 6; p[o + 1] = (((c >> 5) & 63) * 259 + 33) >> 6; p[o + 2] = ((c & 31) * 527 + 23) >> 6; }
  function dxtDecode(fmt, src, w, h) {
    var out = new Uint8Array(w * h * 4), bw = Math.max(1, (w + 3) >> 2), bh = Math.max(1, (h + 3) >> 2), bs = fmt <= 2 ? 8 : 16, p = 0;
    var pal = new Uint8Array(16), al = new Uint8Array(16), av = new Uint8Array(8);
    for (var by = 0; by < bh; by++) for (var bx = 0; bx < bw; bx++, p += bs) {
      if (p + bs > src.length) return out;
      var cp = bs === 16 ? p + 8 : p, i, k;
      if (fmt === 3) for (i = 0; i < 16; i++) { var byt = src[p + (i >> 1)]; al[i] = ((i & 1) ? byt >> 4 : byt & 15) * 17; }
      else if (fmt === 5) {
        var a0 = src[p], a1 = src[p + 1]; av[0] = a0; av[1] = a1;
        if (a0 > a1) for (k = 1; k <= 6; k++) av[k + 1] = (((7 - k) * a0 + k * a1) / 7) | 0;
        else { for (k = 1; k <= 4; k++) av[k + 1] = (((5 - k) * a0 + k * a1) / 5) | 0; av[6] = 0; av[7] = 255; }
        var lo = src[p + 2] | (src[p + 3] << 8) | (src[p + 4] << 16), hi = src[p + 5] | (src[p + 6] << 8) | (src[p + 7] << 16);
        for (i = 0; i < 16; i++) al[i] = av[i < 8 ? (lo >> (3 * i)) & 7 : (hi >> (3 * (i - 8))) & 7];
      } else for (i = 0; i < 16; i++) al[i] = 255;
      var c0 = src[cp] | (src[cp + 1] << 8), c1 = src[cp + 2] | (src[cp + 3] << 8);
      rgb565(c0, pal, 0); rgb565(c1, pal, 4); pal[3] = pal[7] = pal[11] = pal[15] = 255;
      var four = c0 > c1 || fmt >= 3;
      for (k = 0; k < 3; k++) {
        if (four) { pal[8 + k] = (2 * pal[k] + pal[4 + k]) / 3 | 0; pal[12 + k] = (pal[k] + 2 * pal[4 + k]) / 3 | 0; }
        else { pal[8 + k] = (pal[k] + pal[4 + k]) >> 1; pal[12 + k] = 0; }
      }
      if (!four && fmt === 2) pal[15] = 0;
      var bits = (src[cp + 4] | (src[cp + 5] << 8) | (src[cp + 6] << 16) | (src[cp + 7] << 24)) >>> 0;
      for (i = 0; i < 16; i++) {
        var x = bx * 4 + (i & 3), y = by * 4 + (i >> 2);
        if (x >= w || y >= h) continue;
        var ci = (bits >>> (2 * i)) & 3, o = (y * w + x) * 4;
        out[o] = pal[ci * 4]; out[o + 1] = pal[ci * 4 + 1]; out[o + 2] = pal[ci * 4 + 2];
        out[o + 3] = fmt <= 2 ? pal[ci * 4 + 3] : al[i];
      }
    }
    return out;
  }
  function texParse(buf) {
    var dv = new DataView(buf);
    if (buf.byteLength < 12 || dv.getUint32(0, true) !== 0x3158544D) return null;          // "MTX1"
    var w = dv.getUint16(4, true), h = dv.getUint16(6, true), fmt = dv.getUint8(8), n = dv.getUint8(9), p = 12, lv = [];
    if (w < 1 || h < 1 || w > 4096 || h > 4096 || n < 1 || n > 16) return null;
    for (var k = 0; k < n; k++) {
      if (p + 4 > buf.byteLength) return null;
      var len = dv.getInt32(p, true); p += 4;
      if (len <= 0 || p + len > buf.byteLength) return null;
      lv.push({ data: new Uint8Array(buf, p, len), width: Math.max(1, w >> k), height: Math.max(1, h >> k) }); p += (len + 3) & ~3;
    }
    var t;
    if (fmt === 0) {
      if (lv[0].data.length !== w * h * 4) return null;
      t = new THREE.DataTexture(lv[0].data, w, h, THREE.RGBAFormat); t.generateMipmaps = true;
    } else {
      var f = fmt === 1 ? THREE.RGB_S3TC_DXT1_Format : fmt === 2 ? THREE.RGBA_S3TC_DXT1_Format : fmt === 3 ? THREE.RGBA_S3TC_DXT3_Format : fmt === 5 ? THREE.RGBA_S3TC_DXT5_Format : null;
      if (f === null) return null;
      if (ST.s3tc) { t = new THREE.CompressedTexture(lv, w, h, f); t.generateMipmaps = false; }
      else { t = new THREE.DataTexture(dxtDecode(fmt, lv[0].data, w, h), w, h, THREE.RGBAFormat); t.generateMipmaps = true; ST.texDecoded++; }
    }
    t.minFilter = THREE.LinearMipmapLinearFilter; t.magFilter = THREE.LinearFilter; t.flipY = false; t.needsUpdate = true;
    t.anisotropy = renderer ? Math.min(8, renderer.capabilities.getMaxAnisotropy()) : 1;
    return t;
  }

  // ---------------------------------------------------------------------------------------------------------------- the model file (M2B2)
  function parseModel(buf) {
    var dv = new DataView(buf);
    if (buf.byteLength < 12 || dv.getUint32(0, true) !== 0x3242324D) return null;          // "M2B2"
    var jl = dv.getInt32(4, true);
    if (jl <= 0 || 8 + jl > buf.byteLength) return null;
    var meta = JSON.parse(new TextDecoder().decode(new Uint8Array(buf, 8, jl)));
    var ds = 8 + ((jl + 3) & ~3), at = meta.at, nv = meta.nv | 0, ni = meta.ni | 0;
    if (meta.v !== 1 || !at || ds + (at.end | 0) > buf.byteLength) return null;
    if (nv <= 0 || nv > 2000000 || ni < 3 || ni % 3 !== 0 || !Array.isArray(meta.batches) || !Array.isArray(meta.subs) || !Array.isArray(meta.bones)) return null;
    var m = { meta: meta, buf: buf, ds: ds, nv: nv, ni: ni };
    m.pos = new Float32Array(buf, ds + at.pos, nv * 3);
    m.nor = new Int8Array(buf, ds + at.nor, nv * 4);
    m.uv = new Float32Array(buf, ds + at.uv, nv * 2);
    m.bw = new Uint8Array(buf, ds + at.bw, nv * 4);
    m.bi = new Uint8Array(buf, ds + at.bi, nv * 4);
    m.idx = meta.idx16 ? new Uint16Array(buf, ds + at.idx, ni) : new Uint32Array(buf, ds + at.idx, ni);
    function tr(a, w) {
      if (!a || a.length < 4) return null;
      var off = a[0] | 0, n = a[1] | 0;
      if (n <= 0 || off < 0 || ds + off + n * 4 * (1 + w) > buf.byteLength) return null;
      return { t: new Uint32Array(buf, ds + off, n), v: new Float32Array(buf, ds + off + n * 4, n * w), n: n, interp: a[2] | 0, gs: a[3] | 0, w: w };
    }
    m.bones = meta.bones.map(function (b) { return { p: b.p | 0, pivot: b.pivot || [0, 0, 0], bill: b.bill | 0, t: tr(b.t, 3), r: tr(b.r, 4), s: tr(b.s, 3) }; });
    m.loops = Array.isArray(meta.globalLoops) ? meta.globalLoops : [];
    m.duration = meta.anim && meta.anim.duration > 0 ? meta.anim.duration : 0;
    return m;
  }

  // ---------------------------------------------------------------------------------------------------------------- the bones, every frame
  var tv = new THREE.Vector3(), sv = new THREE.Vector3(), qa = new THREE.Quaternion(), qb = new THREE.Quaternion(), lm = new THREE.Matrix4();
  var hermiteSeen = false;
  function trackTime(tk, ms) {
    if (tk.gs >= 0) { var L = M.loops[tk.gs] || 0; return L > 0 ? ms % L : 0; }       // a global sequence: its own loop length
    return M.duration > 0 ? ms % M.duration : 0;
  }
  // the key before time t (binary search) and how far towards the next one
  function seg(tk, t) {
    var a = 0, b = tk.n - 1;
    if (t <= tk.t[0]) return [0, 0, 0];
    if (t >= tk.t[b]) return [b, b, 0];
    while (b - a > 1) { var c = (a + b) >> 1; if (tk.t[c] <= t) a = c; else b = c; }
    var span = tk.t[b] - tk.t[a];
    if (tk.interp === 0) return [a, a, 0];
    if (tk.interp > 1) hermiteSeen = true;
    return [a, b, span > 0 ? (t - tk.t[a]) / span : 0];
  }
  function vec(tk, ms, def, out) {
    if (!tk) return out.copy(def);
    var s = seg(tk, trackTime(tk, ms)), v = tk.v, i = s[0] * 3, j = s[1] * 3, f = s[2];
    return out.set(v[i] + (v[j] - v[i]) * f, v[i + 1] + (v[j + 1] - v[i + 1]) * f, v[i + 2] + (v[j + 2] - v[i + 2]) * f);
  }
  function quat(tk, ms, out) {
    if (!tk) return out.set(0, 0, 0, 1);
    var s = seg(tk, trackTime(tk, ms)), v = tk.v, i = s[0] * 4, j = s[1] * 4;
    qa.set(v[i], v[i + 1], v[i + 2], v[i + 3]);
    if (s[2] === 0 || i === j) return out.copy(qa).normalize();
    qb.set(v[j], v[j + 1], v[j + 2], v[j + 3]);
    return out.slerpQuaternions(qa, qb, s[2]).normalize();
  }
  var ZERO = new THREE.Vector3(0, 0, 0), ONE = new THREE.Vector3(1, 1, 1), boneOrder = [], world = [];
  function orderBones() {
    var n = M.bones.length, state = new Uint8Array(n); boneOrder = [];
    function visit(i, depth) {
      if (state[i] === 2) return; if (state[i] === 1 || depth > 256) { M.bones[i].p = -1; return; }     // a loop in the parents: the bone stands alone
      state[i] = 1;
      var p = M.bones[i].p;
      if (p >= 0 && p < n && p !== i) visit(p, depth + 1); else M.bones[i].p = -1;
      state[i] = 2; boneOrder.push(i);
    }
    for (var i = 0; i < n; i++) visit(i, 0);
    world = M.bones.map(function () { return new THREE.Matrix4(); });
  }
  // WoW M2: local = translate(pivot) * T * R * S * translate(-pivot); model = parent's model * local
  function poseBones(ms) {
    for (var k = 0; k < boneOrder.length; k++) {
      var i = boneOrder[k], b = M.bones[i], pv = b.pivot, w = world[i];
      vec(b.t, ms, ZERO, tv); quat(b.r, ms, qa); vec(b.s, ms, ONE, sv);
      w.compose(tv, qa, sv);
      lm.makeTranslation(-pv[0], -pv[1], -pv[2]); w.multiply(lm);
      var e = w.elements; e[12] += pv[0]; e[13] += pv[1]; e[14] += pv[2];
      if (b.p >= 0) w.premultiply(world[b.p]);
    }
    if (skel) for (var j = 0; j < world.length; j++) skel.bones[j].matrixWorld.copy(world[j]);
  }

  // ---------------------------------------------------------------------------------------------------------------- the mesh and its materials
  var texCache = {};
  function texture(fdid, wrap, done) {
    var keyT = fdid + '|' + wrap, e = texCache[keyT];
    if (e) { if (e.state === 'loading') e.waiters.push(done); else done(e.tex); return; }
    e = texCache[keyT] = { state: 'loading', tex: null, waiters: [done] };
    ST.texWanted++;
    var mine = gen;
    fetch('/' + M.key + '/t/' + fdid + '.mtx').then(function (r) { return r.ok ? r.arrayBuffer() : null; }).then(function (buf) {
      if (mine !== gen) return;
      var t = buf ? texParse(buf) : null;
      if (!t) { e.state = 'none'; ST.texFailed++; }
      else {
        t.wrapS = (wrap & 1) ? THREE.RepeatWrapping : THREE.ClampToEdgeWrapping; t.wrapT = (wrap & 2) ? THREE.RepeatWrapping : THREE.ClampToEdgeWrapping;
        e.tex = t; e.state = 'ready'; ST.texLoaded++;
      }
    }).catch(function () { e.state = 'none'; ST.texFailed++; }).then(function () { if (mine !== gen) return; var w = e.waiters; e.waiters = []; w.forEach(function (f) { f(e.tex); }); });
  }
  var STAND_IN = 0x8a7a60;
  // M2 blend modes: 0 opaque, 1 alpha key, 2 alpha blend, 3 add (no alpha), 4 add, 5 modulate, 6 modulate 2x, 7 blend add
  function material(b) {
    var blend = b.blend | 0, glow = blend >= 3;
    var o = { color: b.tex > 0 ? 0xffffff : STAND_IN, side: b.two ? THREE.DoubleSide : THREE.FrontSide };
    var m = (b.unlit || glow) ? new THREE.MeshBasicMaterial(o) : new THREE.MeshLambertMaterial(o);
    var a = typeof b.alpha === 'number' ? clamp(b.alpha, 0, 1) : 1;
    if (blend === 1) m.alphaTest = 0.502;
    if (blend === 2 || ((blend === 0 || blend === 1) && a < 0.999)) { m.transparent = true; m.depthWrite = false; }
    if (glow) {
      m.transparent = true; m.depthWrite = false; m.blending = THREE.CustomBlending; m.blendEquation = THREE.AddEquation;
      if (blend === 3) { m.blendSrc = THREE.OneFactor; m.blendDst = THREE.OneFactor; }
      else if (blend === 4) { m.blendSrc = THREE.SrcAlphaFactor; m.blendDst = THREE.OneFactor; }
      else if (blend === 5) { m.blendSrc = THREE.DstColorFactor; m.blendDst = THREE.ZeroFactor; }
      else if (blend === 6) { m.blendSrc = THREE.DstColorFactor; m.blendDst = THREE.SrcColorFactor; }
      else { m.blendSrc = THREE.OneFactor; m.blendDst = THREE.OneMinusSrcAlphaFactor; }
    }
    m.opacity = a;
    if (b.noZWrite) m.depthWrite = false;
    if (b.noZTest) m.depthTest = false;
    return m;
  }

  function build(m) {
    var g = new THREE.BufferGeometry();
    g.setAttribute('position', new THREE.BufferAttribute(m.pos, 3));
    g.setAttribute('normal', new THREE.BufferAttribute(m.nor, 4, true));
    g.setAttribute('uv', new THREE.BufferAttribute(m.uv, 2));
    g.setAttribute('skinIndex', new THREE.BufferAttribute(m.bi, 4));
    g.setAttribute('skinWeight', new THREE.BufferAttribute(m.bw, 4, true));
    g.setIndex(new THREE.BufferAttribute(m.idx, 1));
    // one group per batch, in the game's order (priority plane, then as listed): three draws the opaque ones first, the blended ones after
    var order = m.meta.batches.map(function (b, i) { return { b: b, i: i }; }).sort(function (x, y) { return ((x.b.prio | 0) - (y.b.prio | 0)) || (x.i - y.i); });
    var mats = [];
    order.forEach(function (o) {
      var s = m.meta.subs[o.b.sub | 0];
      if (!s || s.n <= 0) return;
      var mat = material(o.b); mats.push(mat);
      g.addGroup(s.i0 | 0, s.n | 0, mats.length - 1);
      if (o.b.tex > 0) texture(o.b.tex, o.b.wrap | 0, function (t) { if (t) { mat.map = t; mat.color.setHex(0xffffff); } else mat.color.setHex(STAND_IN); mat.needsUpdate = true; });
    });
    ST.batches = mats.length;
    var bones = m.bones.map(function () { var b = new THREE.Bone(); b.matrixAutoUpdate = false; b.matrixWorldNeedsUpdate = false; return b; });
    if (bones.length === 0) { bones.push(new THREE.Bone()); m.bones.push({ p: -1, pivot: [0, 0, 0], bill: 0, t: null, r: null, s: null }); }
    skel = new THREE.Skeleton(bones, bones.map(function () { return new THREE.Matrix4(); }));
    var sm = new THREE.SkinnedMesh(g, mats);
    sm.bindMode = 'detached';
    sm.bind(skel, new THREE.Matrix4());
    sm.frustumCulled = false;            // the bind pose's bounds are not the animated ones
    orderBones();
    ST.bones = m.bones.length;
    ST.animated = m.bones.filter(function (b) { return b.t || b.r || b.s; }).length;
    ST.billboards = m.bones.filter(function (b) { return b.bill; }).length;
    if (ST.billboards) ST.notes.push(ST.billboards + ' billboard bones turn with the rest (not faced to the camera yet)');
    return sm;
  }

  function clear() {
    gen++;
    if (mesh) {
      pivot.remove(mesh);
      try { mesh.geometry.dispose(); [].concat(mesh.material).forEach(function (x) { x.dispose(); }); } catch (e) { }
      try { if (skel) skel.dispose(); } catch (e) { }
    }
    Object.keys(texCache).forEach(function (k) { var t = texCache[k].tex; if (t) try { t.dispose(); } catch (e) { } });
    texCache = {}; mesh = null; skel = null; M = null;
    ST.nv = ST.ni = ST.bones = ST.animated = ST.billboards = ST.batches = ST.texWanted = ST.texLoaded = ST.texFailed = ST.texDecoded = 0; ST.animMs = 0; ST.duration = 0; ST.notes = [];
  }

  // every state message names its model, so the app can tell a late answer about the look before from one about this look
  function fail(why) { ST.state = 'error'; ST.why = why; send({ t: 'state', state: 'error', why: why, model: ST.model }); }

  var MODEL_RX = /^([A-Za-z0-9._-]{1,80})\/m\/(\d{1,9})\.m2b2$/;
  var seq = 0;                 // the model asked for last: an older fetch that comes back later is dropped
  // show: a first model, the view set to its start (3/4 from the front, no zoom). keep (3.6 step 5, the "load" message: another look of the same
  // creature): the view stays as it is (turned, tilted, zoomed, spinning on) and the look before stays drawn until the new file is in
  function show(path, keep) {
    var mm = MODEL_RX.exec(path || '');
    if (!mm) return;
    var mine = ++seq;
    if (!keep || !M) clear();
    if (!keep) { view.yaw = 0; view.pitch = 0.26; view.zoom = 1; }
    ST.model = path;
    if (!renderer) { clear(); fail('no 3D graphics here (WebGL could not start)'); return; }
    ST.state = 'loading'; ST.why = '';
    if (keep) ST.loads++;
    fetch('/' + path).then(function (r) { return r.ok ? r.arrayBuffer() : null; }).then(function (buf) {
      if (mine !== seq) return;
      if (!buf) { clear(); fail('the model file is missing'); return; }
      var m = parseModel(buf);
      if (!m) { clear(); fail('the model file could not be read'); return; }
      if (M) clear();
      m.key = mm[1]; M = m;
      ST.nv = m.nv; ST.ni = m.ni; ST.duration = m.duration;
      mesh = build(m);
      pivot.add(mesh);
      poseBones(0);
      resize();
      ST.state = 'shown';
      send({ t: 'state', state: 'shown', model: path, bones: ST.bones, batches: ST.batches, textures: (m.meta.textures || []).length });
      kick();
    }).catch(function (e) { if (mine === seq) { clear(); fail('the model could not be shown: ' + (e && e.message || e)); } });
  }

  // ---------------------------------------------------------------------------------------------------------------- the loop: runs only while shown and not paused
  var last = 0, raf = 0, clock = 0;
  function tick(now) {
    raf = 0;
    if (ST.paused || ST.hidden || !M) return;
    var dt = last ? clamp(now - last, 0, 100) : 16; last = now;
    advance(dt);
    if (!view.dragging && now - view.idleAt > 1200) { view.yaw += view.spin * dt / 1000; placeCamera(); }
    render();
    raf = requestAnimationFrame(tick);
  }
  function advance(dt) { ST.animMs += dt; clock += dt; poseBones(clock); }
  function render() { if (!renderer) return; renderer.render(scene, camera); ST.frames++; }
  function kick() { if (!raf && !ST.paused && !ST.hidden && M) { last = 0; raf = requestAnimationFrame(tick); } }
  function stop() { if (raf) { cancelAnimationFrame(raf); raf = 0; } }
  document.addEventListener('visibilitychange', function () { ST.hidden = document.hidden; if (ST.hidden) stop(); else kick(); });

  // ---------------------------------------------------------------------------------------------------------------- the app's messages (validated)
  if (wv) wv.addEventListener('message', function (e) {
    var d = e.data;
    if (!d || typeof d !== 'object' || typeof d.t !== 'string') return;
    if (d.t === 'show' && typeof d.model === 'string') show(d.model, false);
    else if (d.t === 'load' && typeof d.model === 'string') show(d.model, true);
    else if (d.t === 'pause') { ST.paused = d.on === true; if (ST.paused) stop(); else kick(); }
    else if (d.t === 'clear') { seq++; stop(); clear(); ST.state = 'idle'; render(); }
  });

  // ---------------------------------------------------------------------------------------------------------------- the self-test's hook
  var snap = null;
  window.mv = {
    st: ST,
    // moves the clock on by ms (as frames would) and draws one frame; works while paused or hidden
    step: function (ms) { if (!M) return false; advance(ms | 0); render(); return true; },
    // the pose at an exact clock time (ms since the start: the Stand loop and the global sequences), every bone's model-space matrix (column-major, 16 each)
    pose: function (ms) { if (!M) return null; clock = +ms; ST.animMs = +ms; poseBones(clock); var a = []; world.forEach(function (w) { for (var k = 0; k < 16; k++) a.push(w.elements[k]); }); return a; },
    snap: function () { snap = world.map(function (w) { return w.elements.slice(); }); return snap.length; },
    // how many bones' matrices differ from the snapshot
    changed: function () {
      if (!snap) return -1; var n = 0;
      for (var i = 0; i < world.length && i < snap.length; i++) { var e = world[i].elements, s = snap[i]; for (var k = 0; k < 16; k++) if (Math.abs(e[k] - s[k]) > 1e-5) { n++; break; } }
      return n;
    },
    // the shader's bone matrices are the page's own (identity inverses, identity bind): the first bone's checked
    boneTexOk: function () { if (!skel) return false; skel.update(); var a = skel.boneMatrices, e = world[0].elements; for (var k = 0; k < 16; k++) if (Math.abs(a[k] - e[k]) > 1e-5) return false; return true; },
    // draws a frame and reads it back: how many pixels are not the card's colour
    probe: function () {
      if (!renderer || !M) return { w: 0, h: 0, lit: 0 };
      render();
      var gl = renderer.getContext(), w = gl.drawingBufferWidth, h = gl.drawingBufferHeight, px = new Uint8Array(w * h * 4);
      gl.readPixels(0, 0, w, h, gl.RGBA, gl.UNSIGNED_BYTE, px);
      var lit = 0, br = (BG >> 16) & 255, bg = (BG >> 8) & 255, bb = BG & 255;
      for (var i = 0; i < px.length; i += 4) if (Math.abs(px[i] - br) + Math.abs(px[i + 1] - bg) + Math.abs(px[i + 2] - bb) > 24) lit++;
      return { w: w, h: h, lit: lit, frac: lit / Math.max(1, w * h) };
    },
    hermite: function () { return hermiteSeen; },
    // 3.6 step 5: the view (kept by a "load"), and a zoom set as the wheel would
    viewOf: function () { return { yaw: view.yaw, pitch: view.pitch, zoom: view.zoom }; },
    zoom: function (z) { view.zoom = clamp(+z, 0.45, 2.2); placeCamera(); return view.zoom; }
  };

  resize();
  render();
  send({ t: 'hello', gl: ST.gl, s3tc: ST.s3tc });
})();
