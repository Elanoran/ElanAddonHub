/* Elan's Outpost - World map, 3D terrain page. Runs inside the app's sandboxed WebView2, served from https://worldmap.outpost.local/_page/.
   It knows nothing but what the app sends it (window.chrome.webview messages), the terrain height files of the continent
   (<build>/<map>/terrain/{col}_{row}.wmt: 65 x 65 16-bit heights + 16 x 16 area ids per ADT tile) and the minimap picture tiles (z3..z7 jpg).
   World: three.js units are yards; X = east, Y = up, Z = south, the origin the corner of the 64 x 64 tile grid (so X = col * 533.33, Z = row * 533.33).
   The plane the app talks in is the 2D map's (lat = -row * 4, lng = col * 4). Everything the app sends is in that plane; the formulas are repeated here. */
(function () {
  'use strict';
  var TY = 1600 / 3, UNITS = 4, MAXZ = 9;
  var wv = window.chrome && window.chrome.webview;
  function send(o) { if (wv) { try { wv.postMessage(o); } catch (e) { /* the app is gone */ } } }
  // a script error is told to the app (logged, and in the world map's "Copy details"); the first few only
  var errs = [];
  function reportErr(msg) { msg = String(msg || 'unknown error').slice(0, 200); errs.push(msg); if (errs.length <= 5) send({ t: 'error', msg: msg }); }
  window.addEventListener('error', function (e) { reportErr((e.message || e) + (e.lineno ? ' (line ' + e.lineno + ')' : '')); });
  window.addEventListener('unhandledrejection', function (e) { reportErr('unhandled: ' + (e.reason && e.reason.message || e.reason)); });
  // the app bumps "rev" when tiles that could not be read before were read again: the file addresses change, so the browser cache is not used
  function rv() { return cfg && cfg.rev ? '?r=' + cfg.rev : ''; }
  function $(id) { return document.getElementById(id); }
  function el(tag, cls, html) { var e = document.createElement(tag); if (cls) e.className = cls; if (html != null) e.innerHTML = html; return e; }
  function esc(s) { return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]; }); }
  function ll2xz(lat, lng) { return [lng / UNITS * TY, -lat / UNITS * TY]; }
  function xz2ll(x, z) { return [-z / TY * UNITS, x / TY * UNITS]; }
  function xz2w(x, z) { return [(32 - z / TY) * TY, (32 - x / TY) * TY]; }            // world x (north), y (west)
  function clamp(v, a, b) { return v < a ? a : v > b ? b : v; }
  // the 2D map's zoom <-> a viewing distance (a 800 px tall 2D view at zoom z shows 800 * 133.33 / 2^z yards)
  function zToDist(z) { return clamp(106666 / Math.pow(2, z), 60, 40000); }
  function distToZ(d) { return clamp(Math.log(106666 / d) / Math.LN2, 0, MAXZ); }

  var DETAIL = {
    low:    { maxDist: 9000,  tris: 350000,  texQ: 0.7, lodQ: 0.6, maxLvl: 6, cache: 70,  canvas: 4,  dpr: 1,   aniso: 2 },
    medium: { maxDist: 16000, tris: 800000,  texQ: 1.0, lodQ: 1.0, maxLvl: 7, cache: 130, canvas: 10, dpr: 1.5, aniso: 4 },
    high:   { maxDist: 26000, tris: 1800000, texQ: 1.5, lodQ: 1.5, maxLvl: 7, cache: 230, canvas: 20, dpr: 2,   aniso: 8 }
  };
  var prefs = { detail: 'medium', exag: 1.5 };
  function D() { return DETAIL[prefs.detail] || DETAIL.medium; }

  var renderer = null, scene = null, camera = null, canvas = null, fog = null, glInfo = '', software = false;
  var cfg = null, terrain = null, tileList = [], tileMap = {}, heightsLoaded = 0, heightsFailed = 0, heightGen = 0;
  var cam = { tx: 0, ty: 0, tz: 0, az: 0, tilt: 55, dist: 6000 };
  var dirty = true, ready = false, inited = false, texErrors = 0, texLoaded = 0, frameN = 0;
  var landBounds = null, pinObj = null;
  var defs = {}, on = {}, layers = {}, requested = {}, counts = {}, panelRows = {};
  var forceNoGl = false;

  // ---------------------------------------------------------------- toast, tip, menu
  var toastTimer = 0;
  function toast(text, ok) { var t = $('toast'); t.textContent = text; t.className = 'show' + (ok === false ? ' bad' : ''); clearTimeout(toastTimer); toastTimer = setTimeout(function () { t.className = ''; }, 3200); }
  function showTip(x, y, name, line, extra) {
    var t = $('tip'); t.innerHTML = '<b>' + esc(name) + '</b>' + (line ? '<span>' + esc(line) + '</span>' : '') + (extra ? '<i>' + esc(extra) + '</i>' : ''); t.hidden = false;
    var w = t.offsetWidth, h = t.offsetHeight, px = x + 14, py = y + 16;
    if (px + w > window.innerWidth - 4) px = x - w - 10; if (py + h > window.innerHeight - 4) py = y - h - 10;
    t.style.left = Math.max(4, px) + 'px'; t.style.top = Math.max(4, py) + 'px';
  }
  function hideTip() { $('tip').hidden = true; }
  function hideMenu() { $('ctx').hidden = true; }
  function menu(x, y, header, items) {
    var m = $('ctx'); m.innerHTML = '';
    if (header) m.appendChild(el('div', 'hd', esc(header)));
    items.forEach(function (it) { var b = el('button', '', esc(it.text)); b.type = 'button'; b.setAttribute('role', 'menuitem'); b.addEventListener('click', function () { hideMenu(); it.run(); }); m.appendChild(b); });
    m.hidden = false;
    var w = m.offsetWidth, h = m.offsetHeight;
    m.style.left = Math.max(4, Math.min(x, window.innerWidth - w - 4)) + 'px'; m.style.top = Math.max(4, Math.min(y, window.innerHeight - h - 4)) + 'px';
    var f = m.querySelector('button'); if (f) f.focus();
  }
  function openLink(link) { if (link) send({ t: 'open', id: link }); }

  // ---------------------------------------------------------------- the height grids
  // file: "WMT1" | int col | int row | float minZ | float maxZ | ushort H[65*65] | int area[16*16]
  function parseTile(buf) {
    if (!buf || buf.byteLength !== 9494) return null;
    var dv = new DataView(buf);
    if (dv.getUint8(0) !== 87 || dv.getUint8(1) !== 77 || dv.getUint8(2) !== 84 || dv.getUint8(3) !== 49) return null;
    var t = { c: dv.getInt32(4, true), r: dv.getInt32(8, true), min: dv.getFloat32(12, true), max: dv.getFloat32(16, true) };
    if (!(t.max >= t.min)) return null;
    t.H = new Uint16Array(buf.slice(20, 20 + 65 * 65 * 2));
    t.A = new Int32Array(buf.slice(20 + 65 * 65 * 2, 20 + 65 * 65 * 2 + 1024));
    return t;
  }
  function tileAt(c, r) { return tileMap[c * 64 + r]; }
  // the ground height in yards at three.js x, z (0 where there is no terrain: the sea)
  function groundY(x, z) {
    var col = x / TY, row = z / TY, c = Math.floor(col), r = Math.floor(row);
    var t = tileMap[c * 64 + r]; if (!t || !t.H) return 0;
    var u = (col - c) * 64, v = (row - r) * 64, j = Math.min(63, Math.floor(u)), i = Math.min(63, Math.floor(v)), fu = u - j, fv = v - i;
    var H = t.H, k = i * 65 + j, s = (t.max - t.min) / 65535;
    var h00 = H[k], h10 = H[k + 1], h01 = H[k + 65], h11 = H[k + 66];
    return t.min + s * ((h00 * (1 - fu) + h10 * fu) * (1 - fv) + (h01 * (1 - fu) + h11 * fu) * fv);
  }
  function groundYE(x, z) { return groundY(x, z) * prefs.exag; }
  // the height the camera stays above: the ground, or the water surface over it (the camera does not dive into the sea)
  function surfaceYE(x, z) { var g = groundY(x, z), w = waterAtXZ(x, z); return (w !== null && w > g ? w : g) * prefs.exag; }

  function loadHeights() {
    var order = tileList.slice().sort(function (a, b) { return Math.hypot((a.c + .5) * TY - cam.tx, (a.r + .5) * TY - cam.tz) - Math.hypot((b.c + .5) * TY - cam.tx, (b.r + .5) * TY - cam.tz); });
    var idx = 0, active = 0, base = terrain.base;
    function pump() {
      while (active < 14 && idx < order.length) {
        var t = order[idx++]; active++;
        (function (t) {
          fetch(base + '/' + t.c + '_' + t.r + '.wmt' + rv()).then(function (r) { return r.ok ? r.arrayBuffer() : null; }).then(function (buf) {
            var p = parseTile(buf);
            if (p && p.c === t.c && p.r === t.r) { t.H = p.H; t.A = p.A; t.min = p.min; t.max = p.max; heightsLoaded++; heightGen++; dirty = true; } else heightsFailed++;
          }).catch(function () { heightsFailed++; }).then(function () { active--; pump(); });
        })(t);
      }
    }
    pump();
  }

  // ---------------------------------------------------------------- tile meshes
  var meshes = {};         // key -> {t, mesh, stride, lvl, mat, tex (applied key), want, seen}
  var tex = {};            // cache key -> {tex, state, last, cbs}
  var inFlight = 0, canvasTexCount = 0;
  var seaTex = null;

  function lvlOf(d) {
    var px = 572000 / Math.max(d, 60) * (Math.max(window.innerHeight || 0, 400) / 1000);
    var need = px * D().texQ;
    var l = 3 + Math.ceil(Math.log(Math.max(need, 1) / 32) / Math.LN2);
    return clamp(l, 3, D().maxLvl);
  }
  function strideOf(d) {
    var q = D().lodQ;
    return d < 1100 * q ? 1 : d < 2800 * q ? 2 : d < 6500 * q ? 4 : 8;
  }

  function makeTex(img, canvasLike) {
    var t = new THREE.Texture(img);
    t.encoding = THREE.sRGBEncoding; t.minFilter = THREE.LinearMipmapLinearFilter; t.magFilter = THREE.LinearFilter; t.anisotropy = D().aniso; t.generateMipmaps = true; t.needsUpdate = true;
    return t;
  }
  function imgUrl(lvl, x, y) { return cfg.base + '/z' + lvl + '/' + x + '_' + y + '.jpg' + rv(); }
  function loadImg(url, cb) {
    var im = new Image(), done = false; inFlight++;
    function fin(r) { if (done) return; done = true; inFlight--; cb(r); }
    im.onload = function () { texLoaded++; fin(im); };
    im.onerror = function () { fin(null); };           // no file: that part is open sea
    im.src = url;
  }
  // a texture for the picture tile (lvl 3..6) at tile x, y, or for lvl 7 the 512 px composite of an ADT tile's four native tiles; cb(texture) when ready
  function getTex(lvl, x, y, cb) {
    var key = lvl + '/' + x + '_' + y, e = tex[key];
    if (e) { e.last = frameN; if (e.state === 'ready') { cb(e.tex); return; } e.cbs.push(cb); return; }
    e = tex[key] = { tex: null, state: 'loading', last: frameN, cbs: [cb], lvl: lvl };
    function finish(t) { e.tex = t; e.state = 'ready'; var l = e.cbs; e.cbs = []; l.forEach(function (f) { f(t); }); dirty = true; }
    if (lvl < 7) {
      loadImg(imgUrl(lvl, x, y), function (im) { if (!im) { texErrors++; finish(seaTex); e.shared = true; } else finish(makeTex(im)); });
    } else {
      var cv = document.createElement('canvas'); cv.width = cv.height = 512; var g = cv.getContext('2d');
      g.fillStyle = (cfg.sea || '#081110'); g.fillRect(0, 0, 512, 512);
      var left = 4, any = false;
      for (var dy = 0; dy < 2; dy++) for (var dx = 0; dx < 2; dx++) (function (dx, dy) {
        loadImg(imgUrl(7, x * 2 + dx, y * 2 + dy), function (im) {
          if (im) { g.drawImage(im, dx * 256, dy * 256); any = true; } else texErrors++;
          if (--left === 0) { canvasTexCount++; e.canvas = true; finish(makeTex(cv)); }
        });
      })(dx, dy);
    }
  }
  function releaseOld() {
    var keys = Object.keys(tex), budget = D().cache;
    if (keys.length <= budget) return;
    var used = {}; Object.keys(meshes).forEach(function (k) { var m = meshes[k]; if (m.texKey) used[m.texKey] = 1; if (m.wantKey) used[m.wantKey] = 1; });
    var cand = keys.filter(function (k) { return !used[k] && tex[k].state === 'ready'; }).sort(function (a, b) { return tex[a].last - tex[b].last; });
    var n = keys.length - budget;
    for (var i = 0; i < cand.length && n > 0; i++, n--) {
      var e = tex[cand[i]]; if (e.tex && !e.shared) { try { e.tex.dispose(); } catch (x) { } } if (e.canvas) canvasTexCount--; delete tex[cand[i]];
    }
  }

  // the picture of a mesh is cut from a tile of level `lvl`: 6 and 7 cover one ADT tile (uv 0..1), 5 and below a block of 2^(6-lvl) tiles square
  function fillUv(g, t, stride, lvl) {
    var n = 64 / stride + 1, sc = lvl >= 6 ? 1 : Math.pow(2, 6 - lvl), cm = lvl >= 6 ? 0 : (t.c % sc), rm = lvl >= 6 ? 0 : (t.r % sc), uv = g.attributes.uv.array, o = 0;
    for (var i = 0; i < n; i++) for (var j = 0; j < n; j++, o++) { uv[o * 2] = (cm + j * stride / 64) / sc; uv[o * 2 + 1] = 1 - (rm + i * stride / 64) / sc; }
    var edge = g.userData.edge, base = n * n;
    for (var e = 0; e < edge.length; e++) { uv[(base + e) * 2] = uv[edge[e] * 2]; uv[(base + e) * 2 + 1] = uv[edge[e] * 2 + 1]; }
    g.attributes.uv.needsUpdate = true;
  }

  function buildGeometry(t, stride, lvl) {
    var n = 64 / stride + 1, skirt = 20 + stride * 14, ex = prefs.exag;
    var nv = n * n + 4 * n, pos = new Float32Array(nv * 3), nor = new Float32Array(nv * 3), uv = new Float32Array(nv * 2);
    var s = (t.max - t.min) / 65535, H = t.H, step = stride * TY / 64;
    function h(i, j) { return (t.min + H[Math.min(64, i * stride) * 65 + Math.min(64, j * stride)] * s) * ex; }
    var o = 0;
    for (var i = 0; i < n; i++) for (var j = 0; j < n; j++, o++) {
      pos[o * 3] = (t.c + j * stride / 64) * TY; pos[o * 3 + 1] = h(i, j); pos[o * 3 + 2] = (t.r + i * stride / 64) * TY;
      var hl = h(i, Math.max(0, j - 1)), hr = h(i, Math.min(n - 1, j + 1)), hu = h(Math.max(0, i - 1), j), hd = h(Math.min(n - 1, i + 1), j);
      var dxs = (Math.min(n - 1, j + 1) - Math.max(0, j - 1)) * step, dzs = (Math.min(n - 1, i + 1) - Math.max(0, i - 1)) * step;
      var nx = (hl - hr) / dxs, nz = (hu - hd) / dzs, inv = 1 / Math.sqrt(nx * nx + 1 + nz * nz);
      nor[o * 3] = nx * inv; nor[o * 3 + 1] = inv; nor[o * 3 + 2] = nz * inv;
    }
    // skirts: a strip below each edge hides the cracks between neighbours of different detail
    var edge = [];
    for (var j2 = 0; j2 < n; j2++) edge.push(j2);                                  // north (i = 0)
    for (var i2 = 1; i2 < n; i2++) edge.push(i2 * n + n - 1);                      // east
    for (var j3 = n - 2; j3 >= 0; j3--) edge.push((n - 1) * n + j3);               // south
    for (var i3 = n - 2; i3 >= 1; i3--) edge.push(i3 * n);                         // west
    var ring = edge.length, vi = n * n, skirtStart = vi;
    for (var e = 0; e < ring; e++, vi++) {
      var src = edge[e];
      pos[vi * 3] = pos[src * 3]; pos[vi * 3 + 1] = pos[src * 3 + 1] - skirt; pos[vi * 3 + 2] = pos[src * 3 + 2];
      nor[vi * 3] = 0; nor[vi * 3 + 1] = 1; nor[vi * 3 + 2] = 0;
    }
    var tris = (n - 1) * (n - 1) * 2 + ring * 2;
    var index = new Uint16Array(tris * 3), k = 0;
    for (var a = 0; a < n - 1; a++) for (var b = 0; b < n - 1; b++) {
      var v0 = a * n + b, v1 = v0 + 1, v2 = v0 + n, v3 = v2 + 1;
      index[k++] = v0; index[k++] = v2; index[k++] = v1; index[k++] = v1; index[k++] = v2; index[k++] = v3;
    }
    for (var e2 = 0; e2 < ring; e2++) {
      var a0 = edge[e2], a1 = edge[(e2 + 1) % ring], b0 = skirtStart + e2, b1 = skirtStart + (e2 + 1) % ring;
      index[k++] = a0; index[k++] = b0; index[k++] = a1; index[k++] = a1; index[k++] = b0; index[k++] = b1;
    }
    var g = new THREE.BufferGeometry();
    g.setAttribute('position', new THREE.BufferAttribute(pos, 3)); g.setAttribute('normal', new THREE.BufferAttribute(nor, 3)); g.setAttribute('uv', new THREE.BufferAttribute(uv, 2));
    g.setIndex(new THREE.BufferAttribute(index, 1));
    g.userData.tris = tris; g.userData.edge = edge;
    fillUv(g, t, stride, lvl);
    return g;
  }

  // ---------------------------------------------------------------- liquids (MH2O): one small mesh per tile and kind, drawn after the terrain
  // file <col>_<row>.wmw: "WMW1" | int col | int row | int layers | layers x { u8 chunk, u8 kind, u8 flags (1 flat), u8, u16 type, u16, u64 cell mask, f32 height | f32 height[9 x 9] }
  // chunk = row * 16 + column in the tile, mask bit cy * 8 + cx (cy north -> south, cx west -> east), kind 1 water, 2 ocean, 3 magma, 4 slime
  var waterMats = [null, null, null, null, null], waterBase = [null, { c: 0x3a93c4, o: 0.55 }, { c: 0x103f6e, o: 0.68 }, { c: 0xff5a12, o: 0.9 }, { c: 0x58c43a, o: 0.74 }];
  var waterActive = 0, waterLoaded = 0, waterFailed = 0, waterMeshCount = 0, outerSea = null;
  function parseWater(buf, c, r) {
    if (!buf || buf.byteLength < 16) return null;
    var dv = new DataView(buf), len = buf.byteLength;
    if (dv.getUint8(0) !== 87 || dv.getUint8(1) !== 77 || dv.getUint8(2) !== 87 || dv.getUint8(3) !== 49) return null;
    if (dv.getInt32(4, true) !== c || dv.getInt32(8, true) !== r) return null;
    var n = dv.getInt32(12, true), o = 16, L = [];
    if (n < 0 || n > 4096) return null;
    for (var i = 0; i < n; i++) {
      if (o + 16 > len) return null;
      var l = { chunk: dv.getUint8(o), kind: dv.getUint8(o + 1), flat: (dv.getUint8(o + 2) & 1) !== 0, type: dv.getUint16(o + 4, true), lo: dv.getUint32(o + 8, true), hi: dv.getUint32(o + 12, true), h: 0, H: null };
      o += 16;
      if (l.kind < 1 || l.kind > 4) return null;
      if (l.flat) { if (o + 4 > len) return null; l.h = dv.getFloat32(o, true); o += 4; }
      else { if (o + 324 > len) return null; l.H = new Float32Array(81); for (var k = 0; k < 81; k++) l.H[k] = dv.getFloat32(o + k * 4, true); o += 324; }
      L.push(l);
    }
    return o === len ? L : null;
  }
  function wbit(l, cx, cy) { var b = cy * 8 + cx; return b < 32 ? (l.lo >>> b) & 1 : (l.hi >>> (b - 32)) & 1; }
  function loadWater(t) {
    t.ws = 1; waterActive++;
    fetch(terrain.base + '/' + t.c + '_' + t.r + '.wmw' + rv()).then(function (r) { return r.ok ? r.arrayBuffer() : null; }).then(function (buf) {
      var L = parseWater(buf, t.c, t.r);
      if (L) { t.W = L; t.ws = 2; waterLoaded++; } else { t.W = []; t.ws = 3; waterFailed++; }
      t.wgen = (t.wgen || 0) + 1; dirty = true;
    }).catch(function () { t.W = []; t.ws = 3; waterFailed++; }).then(function () { waterActive--; });
  }
  // the highest liquid surface (yards, unexaggerated) over three.js x, z; null = none
  function waterAtXZ(x, z) {
    var col = x / TY, row = z / TY, c = Math.floor(col), r = Math.floor(row), t = tileMap[c * 64 + r];
    if (!t || !t.W) return null;
    var fx = (col - c) * 16, fy = (row - r) * 16, ix = Math.min(15, Math.floor(fx)), iy = Math.min(15, Math.floor(fy));
    var cx = Math.min(7, Math.floor((fx - ix) * 8)), cy = Math.min(7, Math.floor((fy - iy) * 8)), best = null, chunk = iy * 16 + ix;
    for (var i = 0; i < t.W.length; i++) {
      var l = t.W[i]; if (l.chunk !== chunk || !wbit(l, cx, cy)) continue;
      var h = l.flat ? l.h : (l.H[cy * 9 + cx] + l.H[cy * 9 + cx + 1] + l.H[(cy + 1) * 9 + cx] + l.H[(cy + 1) * 9 + cx + 1]) / 4;
      if (best === null || h > best) best = h;
    }
    return best;
  }
  function addQuad(a, x0, z0, x1, z1, ytl, ytr, ybl, ybr) {
    a.push(x0, ytl, z0, x0, ybl, z1, x1, ytr, z0, x1, ytr, z0, x0, ybl, z1, x1, ybr, z1);
  }
  function blockHas(l, bx, by, b) { for (var yy = 0; yy < b; yy++) for (var xx = 0; xx < b; xx++) if (wbit(l, bx + xx, by + yy)) return true; return false; }
  // b = cells per quad side (1 near, 2, 4 far): a chunk that is flat and fully flooded is one quad at any distance
  function waterArrays(t, b, ex) {
    var out = {}, cs = TY / 128, chs = TY / 16;
    for (var i = 0; i < t.W.length; i++) {
      var l = t.W[i], ix = l.chunk & 15, iy = l.chunk >> 4, x0 = (t.c + ix / 16) * TY, z0 = (t.r + iy / 16) * TY, a = out[l.kind] || (out[l.kind] = []), y = l.h * ex;
      if (l.flat && l.lo === 0xFFFFFFFF && l.hi === 0xFFFFFFFF) { addQuad(a, x0, z0, x0 + chs, z0 + chs, y, y, y, y); continue; }
      for (var by = 0; by < 8; by += b) {
        if (l.flat) {
          var bx = 0;
          while (bx < 8) {
            if (!blockHas(l, bx, by, b)) { bx += b; continue; }
            var s = bx; while (bx < 8 && blockHas(l, bx, by, b)) bx += b;
            addQuad(a, x0 + s * cs, z0 + by * cs, x0 + bx * cs, z0 + (by + b) * cs, y, y, y, y);
          }
        } else {
          var H = l.H;
          for (var bx2 = 0; bx2 < 8; bx2 += b) if (blockHas(l, bx2, by, b))
            addQuad(a, x0 + bx2 * cs, z0 + by * cs, x0 + (bx2 + b) * cs, z0 + (by + b) * cs, H[by * 9 + bx2] * ex, H[by * 9 + bx2 + b] * ex, H[(by + b) * 9 + bx2] * ex, H[(by + b) * 9 + bx2 + b] * ex);
        }
      }
    }
    return out;
  }
  function clearWater(m) {
    if (!m.wm) return;
    m.wm.forEach(function (w) { scene.remove(w); w.geometry.dispose(); waterMeshCount--; });
    m.wm = null;
  }
  function rebuildWater(m, t, b, ex) {
    clearWater(m);
    var arrs = waterArrays(t, b, ex), list = [];
    Object.keys(arrs).forEach(function (k) {
      var g = new THREE.BufferGeometry();
      g.setAttribute('position', new THREE.BufferAttribute(new Float32Array(arrs[k]), 3));
      var w = new THREE.Mesh(g, waterMats[+k]); w.renderOrder = 2; w.userData.kind = +k; scene.add(w); list.push(w); waterMeshCount++;
    });
    m.wm = list; m.wcls = b; m.wex = ex; m.wgen = t.wgen || 0;
  }
  // open sea where the world table has no tile (the tile grid outside the continent): one flat plane at sea level made of runs of missing tiles
  function buildOuterSea() {
    if (outerSea) { scene.remove(outerSea); outerSea.geometry.dispose(); outerSea = null; }
    var a = [], big = 130000, N = 64 * TY;
    addQuad(a, -big, -big, big, 0, 0, 0, 0, 0); addQuad(a, -big, N, big, big, 0, 0, 0, 0);
    addQuad(a, -big, 0, 0, N, 0, 0, 0, 0); addQuad(a, N, 0, big, N, 0, 0, 0, 0);
    for (var r = 0; r < 64; r++) {
      var c = 0;
      while (c < 64) {
        if (tileMap[c * 64 + r] || holeMap[c * 64 + r]) { c++; continue; }
        var s = c; while (c < 64 && !tileMap[c * 64 + r] && !holeMap[c * 64 + r]) c++;
        addQuad(a, s * TY, r * TY, c * TY, (r + 1) * TY, 0, 0, 0, 0);
      }
    }
    var g = new THREE.BufferGeometry(); g.setAttribute('position', new THREE.BufferAttribute(new Float32Array(a), 3));
    outerSea = new THREE.Mesh(g, waterMats[2]); outerSea.renderOrder = 2; outerSea.frustumCulled = false; scene.add(outerSea);
    buildHoles();
  }
  // tiles whose terrain could not be read yet and that have no low-detail heights either (the app's "holes"): a flat hatched placeholder at sea level in
  // the theme's raised-surface colours (Surface2 / Surface3), so the gap reads as "not loaded yet" and never as black land or open sea
  var holeMap = {}, holeMesh = null, holeMat = null;
  function buildHoles() {
    if (holeMesh) { scene.remove(holeMesh); holeMesh.geometry.dispose(); holeMesh = null; }
    var keys = Object.keys(holeMap); if (!keys.length) return;
    if (!holeMat) {
      var cv = document.createElement('canvas'); cv.width = cv.height = 64; var g2 = cv.getContext('2d');
      g2.fillStyle = '#1E2226'; g2.fillRect(0, 0, 64, 64); g2.strokeStyle = '#262B30'; g2.lineWidth = 8;
      for (var k = -64; k <= 128; k += 32) { g2.beginPath(); g2.moveTo(k, 0); g2.lineTo(k + 64, 64); g2.stroke(); }
      var ht = new THREE.CanvasTexture(cv); ht.wrapS = ht.wrapT = THREE.RepeatWrapping; ht.encoding = THREE.sRGBEncoding;
      holeMat = new THREE.MeshBasicMaterial({ map: ht, fog: true });
    }
    var pos = [], uv = [], rep = 8;
    keys.forEach(function (k) {
      var c = Math.floor(+k / 64), r = +k % 64, x0 = c * TY, z0 = r * TY, x1 = x0 + TY, z1 = z0 + TY;
      pos.push(x0, 1, z0, x0, 1, z1, x1, 1, z0, x1, 1, z0, x0, 1, z1, x1, 1, z1); uv.push(0, 0, 0, rep, rep, 0, rep, 0, 0, rep, rep, rep);
    });
    var g = new THREE.BufferGeometry(); g.setAttribute('position', new THREE.BufferAttribute(new Float32Array(pos), 3)); g.setAttribute('uv', new THREE.BufferAttribute(new Float32Array(uv), 2));
    holeMesh = new THREE.Mesh(g, holeMat); holeMesh.renderOrder = 1; holeMesh.frustumCulled = false; scene.add(holeMesh);
  }
  function shimmer(now) {
    var p = now / 1000;
    if (!waterMats[1]) return;
    waterMats[1].opacity = waterBase[1].o + 0.04 * Math.sin(p * 1.7);
    waterMats[2].opacity = waterBase[2].o + 0.03 * Math.sin(p * 1.1 + 1);
    waterMats[3].opacity = waterBase[3].o + 0.06 * Math.sin(p * 2.3); waterMats[3].color.setRGB(1, 0.30 + 0.09 * Math.sin(p * 2.3), 0.07);
    waterMats[4].opacity = waterBase[4].o + 0.05 * Math.sin(p * 1.9 + 2);
  }

  var pendingBuilds = false, seaColor = 0x081110, selStats = { meshes: 0, tris: 0, desired: 0 }, built = 0;
  function updateTiles(now) {
    if (!renderer || !terrain) return;
    var m4 = new THREE.Matrix4().multiplyMatrices(camera.projectionMatrix, camera.matrixWorldInverse), fr = new THREE.Frustum().setFromProjectionMatrix(m4);
    var eye = camera.position, ex = prefs.exag, cap = D();
    var maxDist = Math.max(cap.maxDist, cam.dist * 2.2);
    var cand = [], sph = new THREE.Sphere(new THREE.Vector3(), 1);
    for (var i = 0; i < tileList.length; i++) {
      var t = tileList[i]; if (!t.H) continue;
      var cx = (t.c + .5) * TY, cz = (t.r + .5) * TY, lo = t.min * ex, hi = t.max < 0 ? 0 : t.max * ex;       // a tile wholly under sea level has its sea surface (y 0) above it: that is what shows
      var cy = (lo + hi) / 2, rad = TY * 0.72 + (hi - lo) / 2;
      var d = Math.sqrt((eye.x - cx) * (eye.x - cx) + (eye.y - cy) * (eye.y - cy) + (eye.z - cz) * (eye.z - cz));
      if (d - rad > maxDist) continue;
      sph.center.set(cx, cy, cz); sph.radius = rad;
      if (!fr.intersectsSphere(sph)) continue;
      cand.push({ t: t, d: Math.max(d - TY * 0.5, 30) });
    }
    cand.sort(function (a, b) { return a.d - b.d; });
    var tris = 0, want = {}, nearCount = 0, tEnd = performance.now() + 7;
    pendingBuilds = false;
    for (var c = 0; c < cand.length; c++) {
      var ct = cand[c].t, dd = cand[c].d;
      var stride = strideOf(dd), lvl = lvlOf(dd);
      if (stride === 1 && ++nearCount > 14) stride = 2;
      var cost = Math.pow(64 / stride, 2) * 2 + 4 * (64 / stride) * 2;
      if (tris + cost > cap.tris && stride < 8) { stride = 8; cost = 128 + 64; }
      tris += cost;
      var key = ct.c * 64 + ct.r; want[key] = true;
      var m = meshes[key];
      if (!m) {
        if (performance.now() > tEnd) { pendingBuilds = true; continue; }
        m = meshes[key] = { t: ct, mesh: null, stride: 0, texLvl: 6, wantLvl: -1, texKey: '', wantKey: '', mat: null, seen: now, exag: -1 };
      }
      m.seen = now;
      if (ct.ws === undefined && waterActive < 8) loadWater(ct);
      // the geometry: built for the picture level that is on it now (the uv scale), swapped for a new level together with its picture
      if (!m.mesh || m.stride !== stride || m.exag !== ex) {
        if (m.mesh && performance.now() > tEnd) pendingBuilds = true;
        else {
          var g = buildGeometry(ct, stride, m.texLvl);
          if (!m.mat) { m.mat = new THREE.MeshLambertMaterial({ color: 0xffffff, map: null }); m.mat.color.setHex(seaColor); }
          if (!m.mesh) { m.mesh = new THREE.Mesh(g, m.mat); m.mesh.frustumCulled = false; scene.add(m.mesh); }
          else { m.mesh.geometry.dispose(); m.mesh.geometry = g; }
          m.stride = stride; m.exag = ex; m.tris = g.userData.tris; built++;
        }
      }
      // the liquids of the tile (once its file is in), at the detail of the terrain
      if (m.mesh && ct.W) {
        var wcls = m.stride >= 4 ? 4 : m.stride;
        if (m.wex !== ex || m.wcls !== wcls || m.wgen !== (ct.wgen || 0)) {
          if (m.wm && performance.now() > tEnd) pendingBuilds = true;
          else { try { rebuildWater(m, ct, wcls, ex); } catch (e) { m.wex = ex; m.wcls = wcls; m.wgen = ct.wgen || 0; errs.push('water ' + ct.c + ',' + ct.r + ': ' + String(e && e.message || e).slice(0, 160)); } }
        }
      }
      // the picture: too many 512 px ones stay at 256
      if (lvl >= 7 && canvasTexCount >= cap.canvas && !(m.texLvl === 7)) lvl = 6;
      if (m.mesh && m.wantLvl !== lvl) {
        m.wantLvl = lvl;
        var blk = lvl >= 6 ? 1 : Math.pow(2, 6 - lvl), tx = Math.floor(ct.c / blk), ty2 = Math.floor(ct.r / blk);
        var key2 = lvl + '/' + tx + '_' + ty2; m.wantKey = key2;
        (function (m, key2, lvlNow, tx, ty2) {
          getTex(lvlNow, tx, ty2, function (tx2) {
            if (m.wantKey !== key2 || !m.mesh) return;
            m.mat.map = tx2; m.mat.color.setHex(0xffffff); m.mat.needsUpdate = true; m.texKey = key2; m.texLvl = lvlNow;
            fillUv(m.mesh.geometry, m.t, m.stride, lvlNow); dirty = true;
          });
        })(m, key2, lvl, tx, ty2);
      }
    }
    // tiles that left the view (not wanted for a couple of seconds) are disposed
    var count = 0, tcount = 0;
    Object.keys(meshes).forEach(function (k) {
      var m = meshes[k];
      if (!want[k] && now - m.seen > 2500) {
        if (m.mesh) { scene.remove(m.mesh); m.mesh.geometry.dispose(); }
        clearWater(m);
        if (m.mat) m.mat.dispose();
        delete meshes[k]; return;
      }
      if (m.mesh) { count++; tcount += m.tris || 0; }
    });
    selStats = { meshes: count, tris: tcount, desired: cand.length };
    releaseOld();
  }

  // ---------------------------------------------------------------- camera
  var flight = null;
  function updateCamera() {
    var t = cam.tilt * Math.PI / 180, a = cam.az * Math.PI / 180, ex = prefs.exag;
    var gy = surfaceYE(cam.tx, cam.tz);
    cam.ty += (gy - cam.ty) * (Math.abs(gy - cam.ty) > 400 ? 1 : 0.25);
    var ex_ = cam.tx + cam.dist * Math.sin(a) * Math.cos(t), ey = cam.ty + cam.dist * Math.sin(t), ez = cam.tz + cam.dist * Math.cos(a) * Math.cos(t);
    var eg = surfaceYE(ex_, ez) + 8; if (ey < eg) ey = eg;
    camera.position.set(ex_, ey, ez);
    camera.lookAt(cam.tx, cam.ty, cam.tz);
    var height = Math.max(ey - eg + 8, 4);
    var near = clamp(Math.min(cam.dist * 0.04, height * 0.5), 1.5, 400);
    var far = clamp(cam.dist * 5 + D().maxDist * 1.5, 30000, 140000);
    if (Math.abs(camera.near - near) > near * 0.05 || Math.abs(camera.far - far) > far * 0.05) { camera.near = near; camera.far = far; camera.updateProjectionMatrix(); }
    camera.updateMatrixWorld(true);
    camera.matrixWorldInverse.copy(camera.matrixWorld).invert();
    if (scene.fog) { scene.fog.near = Math.max(cam.dist * 1.2, D().maxDist * 0.5); scene.fog.far = Math.max(cam.dist * 3.2, D().maxDist * 1.05); }
  }
  function setView(lat, lng, dist, az, tilt, instant) {
    var p = ll2xz(lat, lng);
    var to = { tx: clamp(p[0], 0, 64 * TY), tz: clamp(p[1], 0, 64 * TY), dist: clamp(dist, 40, 42000), az: az == null ? cam.az : az, tilt: clamp(tilt == null ? cam.tilt : tilt, 8, 88) };
    if (instant) { cam.tx = to.tx; cam.tz = to.tz; cam.dist = to.dist; cam.az = to.az; cam.tilt = to.tilt; cam.ty = groundYE(cam.tx, cam.tz); flight = null; dirty = true; return; }
    flight = { from: { tx: cam.tx, tz: cam.tz, dist: cam.dist, az: cam.az, tilt: cam.tilt }, to: to, t0: performance.now(), dur: 900 };
    // the shortest way round for the bearing
    var da = ((to.az - cam.az + 540) % 360) - 180; flight.to.az = cam.az + da;
    dirty = true;
  }
  function stepFlight(now) {
    if (!flight) return;
    var k = clamp((now - flight.t0) / flight.dur, 0, 1), e = k < .5 ? 2 * k * k : 1 - Math.pow(-2 * k + 2, 2) / 2;
    var f = flight.from, t = flight.to;
    cam.tx = f.tx + (t.tx - f.tx) * e; cam.tz = f.tz + (t.tz - f.tz) * e; cam.az = f.az + (t.az - f.az) * e; cam.tilt = f.tilt + (t.tilt - f.tilt) * e;
    // distance in a log blend with a little lift in the middle of a long flight
    var far = Math.hypot(t.tx - f.tx, t.tz - f.tz), lift = Math.min(far * 0.35, 12000) * Math.sin(Math.PI * e);
    cam.dist = Math.exp(Math.log(f.dist) + (Math.log(t.dist) - Math.log(f.dist)) * e) + lift;
    dirty = true;
    if (k >= 1) { flight = null; sendView(); }
  }
  var viewTimer = 0;
  function sendView() {
    clearTimeout(viewTimer);
    viewTimer = setTimeout(function () { var ll = xz2ll(cam.tx, cam.tz); send({ t: 'view3', lat: clamp(ll[0], -255.9, 0), lng: clamp(ll[1], 0, 255.9), dist: cam.dist, az: ((cam.az % 360) + 360) % 360, tilt: cam.tilt, s: vseq }); }, 500);
  }

  // ---------------------------------------------------------------- picking the ground
  var ray = new THREE.Raycaster();
  function mouseRay(mx, my) {
    var r = canvas.getBoundingClientRect();
    var nx = ((mx - r.left) / r.width) * 2 - 1, ny = -((my - r.top) / r.height) * 2 + 1;
    ray.setFromCamera({ x: nx, y: ny }, camera);
    return ray.ray;
  }
  function pickGround(mx, my) {
    var rr = mouseRay(mx, my), o = rr.origin, d = rr.direction, maxT = camera.far * 0.8, t = 0, prevT = 0, step = 2;
    var p = new THREE.Vector3();
    for (var n = 0; n < 900 && t < maxT; n++) {
      p.copy(d).multiplyScalar(t).add(o);
      if (p.x < 0 || p.z < 0 || p.x > 64 * TY || p.z > 64 * TY) { if (d.y >= 0) return null; }
      var gy = groundYE(p.x, p.z);
      if (p.y <= gy) {
        var lo = prevT, hi = t;
        for (var b = 0; b < 12; b++) { var mid = (lo + hi) / 2; p.copy(d).multiplyScalar(mid).add(o); if (p.y <= groundYE(p.x, p.z)) hi = mid; else lo = mid; }
        p.copy(d).multiplyScalar(hi).add(o);
        return { x: p.x, y: p.y, z: p.z, t: hi };
      }
      prevT = t; step = Math.max(2, Math.min((p.y - gy) * 0.5, t * 0.02 + 2)); t += step;
    }
    return null;
  }

  // ---------------------------------------------------------------- layers: points (one GL points object), labels / flight masters (DOM), lines and zone borders (draped lines)
  var LIFT_POINT = 7, LIFT_LINE = 4;
  var domItems = [];            // {el, x, z, y, kind, layer, min, max, sm}
  var pointLayers = {}, lineLayers = {}, ringLayers = {};
  var pointMat = null;

  function makePointMaterial() {
    return new THREE.ShaderMaterial({
      uniforms: { uFade: { value: 12000 } },
      vertexShader: 'attribute vec3 color; attribute float flag; attribute float show; varying vec3 vC; varying float vF; varying float vA; uniform float uFade;\n' +
        'void main(){ vec4 mv = modelViewMatrix * vec4(position, 1.0); gl_Position = projectionMatrix * mv; float d = -mv.z;\n' +
        ' float s = clamp(3200.0 / max(d, 1.0), 4.5, 12.0) * (1.0 + flag * 0.35); gl_PointSize = s * show; vC = color; vF = flag; vA = (1.0 - smoothstep(uFade * 0.6, uFade, d)) * show; }',
      fragmentShader: 'varying vec3 vC; varying float vF; varying float vA;\n' +
        'void main(){ vec2 p = gl_PointCoord - 0.5; float r = length(p); if (r > 0.5 || vA < 0.02) discard;\n' +
        ' vec3 c = mix(vC, vec3(1.0), vF * smoothstep(0.30, 0.4, r)); c = mix(c, vec3(0.02), smoothstep(0.38, 0.5, r) * 0.8); gl_FragColor = vec4(c, vA); }',
      transparent: true, depthWrite: false
    });
  }
  function hexRgb(h) { h = String(h || '#ffffff'); var m = /^#?([0-9a-f]{6})$/i.exec(h); var n = m ? parseInt(m[1], 16) : 0xffffff; return [(n >> 16 & 255) / 255, (n >> 8 & 255) / 255, (n & 255) / 255]; }

  function buildPoints(id, m) {
    var pts = m.pts || [], n = Math.floor(pts.length / 6), styles = m.styles || [];
    var L = { kind: 'points', n: n, lat: new Float32Array(n), lng: new Float32Array(n), x: new Float32Array(n), z: new Float32Array(n), styleIdx: new Int16Array(n), name: new Int32Array(n), tip: new Int32Array(n), link: new Int32Array(n),
      names: m.names || [], tips: m.tips || [], links: m.links || [], styles: styles };
    var pos = new Float32Array(n * 3), col = new Float32Array(n * 3), flag = new Float32Array(n);
    var rgb = styles.map(function (s) { return hexRgb(s.c); });
    for (var i = 0; i < n; i++) {
      var o = i * 6; L.lat[i] = pts[o]; L.lng[i] = pts[o + 1]; var p = ll2xz(pts[o], pts[o + 1]); L.x[i] = p[0]; L.z[i] = p[1];
      L.name[i] = pts[o + 2]; L.tip[i] = pts[o + 3]; L.styleIdx[i] = pts[o + 4]; L.link[i] = pts[o + 5];
      var st = styles[pts[o + 4]] || {}, c = rgb[pts[o + 4]] || [1, 1, 1];
      col[i * 3] = c[0]; col[i * 3 + 1] = c[1]; col[i * 3 + 2] = c[2]; flag[i] = st.f ? 1 : 0;
      pos[i * 3] = p[0]; pos[i * 3 + 2] = p[1];
    }
    var g = new THREE.BufferGeometry();
    g.setAttribute('position', new THREE.BufferAttribute(pos, 3)); g.setAttribute('color', new THREE.BufferAttribute(col, 3)); g.setAttribute('flag', new THREE.BufferAttribute(flag, 1)); g.setAttribute('show', new THREE.BufferAttribute(new Float32Array(n).fill(1), 1));
    if (!pointMat) pointMat = makePointMaterial();
    if (pointLayers[id]) { scene.remove(pointLayers[id].obj); pointLayers[id].obj.geometry.dispose(); }
    var obj = new THREE.Points(g, pointMat); obj.frustumCulled = false; scene.add(obj); L.obj = obj; L.id = id; L.group = styles.map(function (s) { return s.g || null; });
    pointLayers[id] = L; placePoints(L); applyGroups(L);
    return n;
  }
  // points whose style belongs to a switched-off child group (Herbs, Ore ...) are hidden one by one
  function applyGroups(L) {
    var a = L.obj.geometry.attributes.show; if (!a) return;
    for (var i = 0; i < L.n; i++) { var gname = L.group[L.styleIdx[i]]; a.array[i] = gname && !on[gname] ? 0 : 1; }
    a.needsUpdate = true;
  }
  function placePoints(L) {
    var p = L.obj.geometry.attributes.position, a = p.array;
    for (var i = 0; i < L.n; i++) a[i * 3 + 1] = groundYE(L.x[i], L.z[i]) + LIFT_POINT;
    p.needsUpdate = true;
  }

  function buildLabels(id, m) {
    clearDom(id);
    (m.items || []).forEach(function (it) {
      var p = ll2xz(it.lat, it.lng);
      var d = el('div', 'l3' + (it.sm ? ' sm' : ''), '<div class="zli"><b>' + esc(it.name) + '</b>' + (it.sub ? '<span>' + esc(it.sub) + '</span>' : '') + '</div>');
      d.addEventListener('mouseenter', function (e) { showTip(e.clientX, e.clientY, it.name, it.sub ? 'Level ' + it.sub : '', it.link ? 'Click to open its Atlas page' : ''); });
      d.addEventListener('mousemove', function (e) { showTip(e.clientX, e.clientY, it.name, it.sub ? 'Level ' + it.sub : '', it.link ? 'Click to open its Atlas page' : ''); });
      d.addEventListener('mouseleave', hideTip);
      if (it.link) d.addEventListener('click', function (e) { e.stopPropagation(); hideTip(); openLink(it.link); });
      d.addEventListener('mousedown', function (e) { e.stopPropagation(); });
      $('ov').appendChild(d);
      domItems.push({ el: d, x: p[0], z: p[1], y: 0, kind: 'label', layer: id, sm: !!it.sm, name: it.name });
    });
  }
  function buildMarkers(id, m) {
    clearDom(id);
    (m.items || []).forEach(function (it) {
      var p = ll2xz(it.lat, it.lng);
      // instance entrances (it.ic dun / raid) carry a kind group (it.g: switched by its child row) and a label shown when the camera is near; flight masters are the diamonds
      var d = el('div', 'm3', it.ic ? '<div class="inst ' + esc(it.ic) + '">' + (it.ic === 'raid' ? '&#9760;' : '') + '</div>' + (it.lab ? '<div class="instl">' + esc(it.lab) + '</div>' : '') : (window.WmIcons ? window.WmIcons.html(it.f || 'n') : '<div class="fm ' + (it.f || 'n') + '"></div>'));
      var tipf = function (e) { showTip(e.clientX, e.clientY, it.name, it.tip || '', it.extra || ''); };
      d.addEventListener('mouseenter', tipf); d.addEventListener('mousemove', tipf); d.addEventListener('mouseleave', hideTip);
      d.addEventListener('mousedown', function (e) { e.stopPropagation(); });
      if (it.link) d.addEventListener('click', function (e) { e.stopPropagation(); hideTip(); openLink(it.link); });
      if (it.ic) d.addEventListener('contextmenu', function (e) {
        e.preventDefault(); e.stopPropagation(); hideTip();
        var items = [];
        if (it.link) items.push({ text: 'Open its page', run: function () { openLink(it.link); } });
        items.push({ text: 'Navigate here', run: function () { send({ t: 'nav', lat: clamp(it.lat, -255.9, 0), lng: clamp(it.lng, 0, 255.9) }); } });
        menu(e.clientX, e.clientY, it.name, items);
      });
      $('ov').appendChild(d);
      domItems.push({ el: d, x: p[0], z: p[1], y: 0, kind: 'marker', layer: id, name: it.name, g: it.g || null, lab: it.ic ? d.querySelector('.instl') : null });
    });
  }
  function clearDom(id) {
    domItems = domItems.filter(function (it) { if (it.layer === id) { if (it.el.parentNode) it.el.parentNode.removeChild(it.el); return false; } return true; });
  }
  function placeDom() { domItems.forEach(function (it) { it.y = groundYE(it.x, it.z); }); }

  // draped polylines: densified to at most `step` yards between points, each lifted over the ground
  function drape(flatLL, step, lift) {
    var xs = [], zs = [];
    for (var i = 0; i + 1 < flatLL.length; i += 2) { var p = ll2xz(flatLL[i], flatLL[i + 1]); xs.push(p[0]); zs.push(p[1]); }
    var ox = [], oz = [];
    for (var k = 0; k < xs.length; k++) {
      ox.push(xs[k]); oz.push(zs[k]);
      if (k + 1 < xs.length) { var len = Math.hypot(xs[k + 1] - xs[k], zs[k + 1] - zs[k]), n = Math.floor(len / step); for (var j = 1; j <= n; j++) { var f = j / (n + 1); ox.push(xs[k] + (xs[k + 1] - xs[k]) * f); oz.push(zs[k] + (zs[k + 1] - zs[k]) * f); } }
    }
    return { x: ox, z: oz };
  }
  function buildLines(id, m) {
    if (lineLayers[id]) { lineLayers[id].group.children.forEach(function (c) { c.geometry.dispose(); c.material.dispose(); }); scene.remove(lineLayers[id].group); }
    var grp = new THREE.Group(), items = [];
    (m.items || []).forEach(function (it) {
      if (!it.pts || it.pts.length < 4) return;
      var dr = drape(it.pts, 120, 0);
      var g = new THREE.BufferGeometry(); g.setAttribute('position', new THREE.BufferAttribute(new Float32Array(dr.x.length * 3), 3));
      var mat = new THREE.LineBasicMaterial({ color: new THREE.Color(it.c || '#ffffff'), transparent: true, opacity: Math.min(1, (it.o || .85) + .1), fog: true });
      var line = new THREE.Line(g, mat); line.frustumCulled = false; grp.add(line);
      items.push({ line: line, x: dr.x, z: dr.z, lift: 22, g: it.g || null });
    });
    scene.add(grp); lineLayers[id] = { group: grp, items: items }; placeLines(lineLayers[id]);
    return items.length;
  }
  function placeLines(L) {
    L.items.forEach(function (it) {
      var a = it.line.geometry.attributes.position.array, n = it.x.length;
      // flight paths float over the ground: the highest ground within a few samples, plus a lift
      var gh = new Array(n);
      for (var i = 0; i < n; i++) gh[i] = groundYE(it.x[i], it.z[i]);
      for (var j = 0; j < n; j++) {
        var mx = gh[j]; for (var q = Math.max(0, j - 2); q <= Math.min(n - 1, j + 2); q++) if (gh[q] > mx) mx = gh[q];
        a[j * 3] = it.x[j]; a[j * 3 + 1] = mx + it.lift; a[j * 3 + 2] = it.z[j];
      }
      it.line.geometry.attributes.position.needsUpdate = true;
    });
  }

  // zone borders: rings on the plane -> draped line segments per zone, one object each so the zone under the cursor can be lit
  var hoverMat = null, borderMat = null, hoverZone = null;
  function buildRings(id, m) {
    if (ringLayers[id]) { ringLayers[id].zones.forEach(function (z) { z.obj.geometry.dispose(); scene.remove(z.obj); }); }
    if (!borderMat) { borderMat = new THREE.LineBasicMaterial({ color: 0x7dd3fc, transparent: true, opacity: .85 }); hoverMat = new THREE.LineBasicMaterial({ color: 0xffffff, transparent: true, opacity: 1 }); }
    var zones = [];
    (m.items || []).forEach(function (it) {
      var segX = [], segZ = [], rings = [], minC = 1e9, maxC = -1e9, minR = 1e9, maxR = -1e9;
      (it.rings || []).forEach(function (r) {
        var dr = drape(r.concat([r[0], r[1]]), 45, 0);               // close the ring
        for (var i = 0; i + 1 < dr.x.length; i++) { segX.push(dr.x[i], dr.x[i + 1]); segZ.push(dr.z[i], dr.z[i + 1]); }
        var cr = []; for (var k = 0; k + 1 < r.length; k += 2) { var col = r[k + 1] / UNITS, row = -r[k] / UNITS; cr.push(col, row); minC = Math.min(minC, col); maxC = Math.max(maxC, col); minR = Math.min(minR, row); maxR = Math.max(maxR, row); }
        rings.push(cr);
      });
      var g = new THREE.BufferGeometry(); g.setAttribute('position', new THREE.BufferAttribute(new Float32Array(segX.length * 3), 3));
      var obj = new THREE.LineSegments(g, borderMat); obj.frustumCulled = false; scene.add(obj);
      zones.push({ name: it.name, link: it.link, obj: obj, x: segX, z: segZ, rings: rings, box: [minC, minR, maxC, maxR], id: it.id });
    });
    ringLayers[id] = { zones: zones }; placeRings(ringLayers[id]);
    return zones.length;
  }
  function placeRings(L) {
    L.zones.forEach(function (z) {
      var a = z.obj.geometry.attributes.position.array;
      for (var i = 0; i < z.x.length; i++) { a[i * 3] = z.x[i]; a[i * 3 + 1] = groundYE(z.x[i], z.z[i]) + 10; a[i * 3 + 2] = z.z[i]; }
      z.obj.geometry.attributes.position.needsUpdate = true;
    });
  }
  function zoneAtGround(x, z) {
    var col = x / TY, row = z / TY, L = ringLayers.borders; if (!L || !on.borders) return null;
    var best = null;
    L.zones.forEach(function (zn) {
      if (col < zn.box[0] || col > zn.box[2] || row < zn.box[1] || row > zn.box[3]) return;
      var inside = false;
      zn.rings.forEach(function (r) { var n = r.length / 2; for (var i = 0, j = n - 1; i < n; j = i++) { var xi = r[2 * i], yi = r[2 * i + 1], xj = r[2 * j], yj = r[2 * j + 1]; if ((yi > row) !== (yj > row) && col < (xj - xi) * (row - yi) / (yj - yi) + xi) inside = !inside; } });
      if (inside) best = zn;
    });
    return best;
  }
  function setHoverZone(z) {
    if (z === hoverZone) return;
    if (hoverZone) hoverZone.obj.material = borderMat;
    hoverZone = z; if (z) z.obj.material = hoverMat; dirty = true;
  }

  function refreshHeights() {
    Object.keys(pointLayers).forEach(function (k) { placePoints(pointLayers[k]); });
    Object.keys(lineLayers).forEach(function (k) { placeLines(lineLayers[k]); });
    Object.keys(ringLayers).forEach(function (k) { placeRings(ringLayers[k]); });
    placeDom(); dirty = true;
  }

  // ---------------------------------------------------------------- DOM overlays: labels and flight masters follow their ground spot
  var tmpV = new THREE.Vector3();
  function updateOverlays() {
    var w = window.innerWidth, h = window.innerHeight, shown = 0;
    var eye = camera.position, far = cam.dist;
    for (var i = 0; i < domItems.length; i++) {
      var it = domItems[i], e = it.el;
      var show = !!on[it.layer] && (!it.g || !!on[it.g]);
      if (show) {
        tmpV.set(it.x, it.y + (it.kind === 'label' ? 40 : 12), it.z);
        var dx = tmpV.x - eye.x, dy = tmpV.y - eye.y, dz = tmpV.z - eye.z, dist = Math.sqrt(dx * dx + dy * dy + dz * dz);
        tmpV.applyMatrix4(camera.matrixWorldInverse);
        if (tmpV.z >= 0) show = false;
        else {
          if (it.kind === 'label') { show = it.sm ? dist < 14000 && dist > 120 : dist > 1800 && dist < 90000; }
          else show = dist < 60000;
          tmpV.applyMatrix4(camera.projectionMatrix);
          var sx = (tmpV.x * .5 + .5) * w, sy = (-tmpV.y * .5 + .5) * h;
          if (sx < -80 || sx > w + 80 || sy < -40 || sy > h + 40) show = false;
          if (show && it.lab) { var near = dist < 9000; if (near !== it.near) { it.lab.classList.toggle('near', near); it.near = near; } }
          if (show) { e.style.transform = 'translate(' + sx.toFixed(1) + 'px,' + sy.toFixed(1) + 'px)'; e.style.zIndex = String(Math.round(100000 - dist / 2)); e.style.opacity = it.kind === 'label' ? String(clamp(1.4 - dist / (it.sm ? 14000 : 70000), .35, 1)) : '1'; shown++; }
        }
      }
      if (show !== it.vis) { e.style.display = show ? '' : 'none'; it.vis = show; }
    }
    return shown;
  }

  // ---------------------------------------------------------------- the panel: layers + 3D settings + a few buttons
  function buildPanel(list) {
    var hud = $('hud'); hud.innerHTML = '';
    var p = el('div', 'wm-panel');
    var h = el('h4', '', '<span>Layers</span><span aria-hidden="true">&#9662;</span>'); h.tabIndex = 0; h.setAttribute('role', 'button'); h.setAttribute('aria-expanded', 'true');
    var body = el('div', 'body');
    function toggle() { var c = p.classList.toggle('collapsed'); h.setAttribute('aria-expanded', String(!c)); }
    h.addEventListener('click', toggle); h.addEventListener('keydown', function (e) { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); toggle(); } });
    function row(d, parent) {
      var r = el('label', 'wm-row' + (parent ? ' sub' : '')), cb = el('input'); cb.type = 'checkbox';
      var sw = el('span', 'sw'); sw.style.background = d.color || '#888';
      var nm = el('span', 'nm', esc(d.title)), ct = el('span', 'ct');
      if (d.tip) r.title = d.tip;
      r.appendChild(cb); r.appendChild(sw); r.appendChild(nm); r.appendChild(ct);
      cb.addEventListener('change', function () { setOn(d.id, cb.checked); });
      body.appendChild(r); panelRows[d.id] = { row: r, cb: cb, ct: ct, parent: parent }; defs[d.id] = d; if (parent) d.child = true;
    }
    list.forEach(function (d) { row(d, null); (d.children || []).forEach(function (c) { row(c, d.id); }); });
    // 3D settings
    var s = el('div', 'v3set');
    s.innerHTML = '<div class="v3h">3D view</div><div class="v3r"><span>Detail</span><span class="seg" id="v3detail"><button type="button" data-d="low">Low</button><button type="button" data-d="medium">Medium</button><button type="button" data-d="high">High</button></span></div>' +
      '<div class="v3r"><span>Height</span><input id="v3exag" type="range" min="1" max="3" step="0.5" aria-label="Height exaggeration"><b id="v3exagv"></b></div>' +
      '<div class="v3r"><button type="button" id="v3north">North up</button><button type="button" id="v3fit">Whole continent</button></div>';
    body.appendChild(s);
    bldPanel(s);
    p.appendChild(h); p.appendChild(body); hud.appendChild(p);
    ['mousedown', 'wheel', 'click', 'dblclick', 'contextmenu'].forEach(function (ev) { p.addEventListener(ev, function (e) { e.stopPropagation(); }); });
    function syncPrefs() {
      [].forEach.call(s.querySelectorAll('#v3detail button'), function (b) { b.classList.toggle('on', b.getAttribute('data-d') === prefs.detail); });
      $('v3exag').value = prefs.exag; $('v3exagv').textContent = prefs.exag.toFixed(1) + 'x';
    }
    [].forEach.call(s.querySelectorAll('#v3detail button'), function (b) { b.addEventListener('click', function () { prefs.detail = b.getAttribute('data-d'); applyPrefs(true); syncPrefs(); }); });
    $('v3exag').addEventListener('input', function () { prefs.exag = parseFloat(this.value) || 1.5; $('v3exagv').textContent = prefs.exag.toFixed(1) + 'x'; applyPrefs(false); });
    $('v3exag').addEventListener('change', function () { applyPrefs(true); });
    $('v3north').addEventListener('click', function () { flight = { from: { tx: cam.tx, tz: cam.tz, dist: cam.dist, az: cam.az, tilt: cam.tilt }, to: { tx: cam.tx, tz: cam.tz, dist: cam.dist, az: cam.az + ((((0 - cam.az) + 540) % 360) - 180), tilt: cam.tilt }, t0: performance.now(), dur: 500 }; dirty = true; });
    $('v3fit').addEventListener('click', fitAll);
    syncPrefs();
  }
  var prefsTimer = 0;
  function applyPrefs(save) {
    // new detail / height: tiles are rebuilt on the next pass, lines and points re-seated
    renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, software ? 1 : D().dpr)); onResize();
    Object.keys(meshes).forEach(function (k) { meshes[k].exag = -1; meshes[k].wantLvl = -1; });
    refreshHeights(); updateAfter = 0; lastSel2 = 0;
    if (save) { clearTimeout(prefsTimer); prefsTimer = setTimeout(function () { send({ t: 'prefs', detail: prefs.detail, exag: prefs.exag }); }, 400); }
    dirty = true;
  }
  var updateAfter = 0;

  // ---------------------------------------------------------------- layer visibility
  function groupShown(id) { return !!on[id]; }
  function refresh() {
    Object.keys(pointLayers).forEach(function (id) { pointLayers[id].obj.visible = !!on[id]; applyGroups(pointLayers[id]); });
    Object.keys(lineLayers).forEach(function (id) { lineLayers[id].group.visible = !!on[id]; lineLayers[id].items.forEach(function (it) { it.line.visible = !it.g || !!on[it.g]; }); });
    Object.keys(ringLayers).forEach(function (id) { ringLayers[id].zones.forEach(function (z) { z.obj.visible = !!on[id]; }); });
    // point styles that belong to a switched-off child group: hide through the layer object only when the whole layer is off (groups are drawn together)
    Object.keys(panelRows).forEach(function (id) {
      var r = panelRows[id]; r.cb.checked = !!on[id];
      var dis = r.parent && !on[r.parent];
      r.row.classList.toggle('disabled', !!dis); r.row.classList.toggle('off', !on[id] && !dis); r.cb.disabled = !!dis;
    });
    dirty = true;
  }
  function setOn(id, v) {
    on[id] = !!v;
    if (v && defs[id] && !defs[id].child) { var kids = Object.keys(panelRows).filter(function (k) { return panelRows[k].parent === id; }); if (kids.length && !kids.some(function (k) { return on[k]; })) kids.forEach(function (k) { on[k] = true; }); } var def = defs[id];
    if (v && def && !def.child && !layers[id] && !requested[id]) { requested[id] = true; send({ t: 'need', layer: id }); setCount(id, '...'); }
    refresh(); sendLayers();
  }
  var layersTimer = 0;
  function sendLayers() { clearTimeout(layersTimer); layersTimer = setTimeout(function () { var l = []; Object.keys(on).forEach(function (k) { if (on[k]) l.push(k); }); send({ t: 'layers', on: l }); }, 250); }
  function setCount(id, text) { if (panelRows[id]) panelRows[id].ct.textContent = text == null ? '' : text; }

  function layer(m) {
    var id = m.id; if (!defs[id]) return;
    var n = 0;
    if (m.kind === 'points') n = buildPoints(id, m);
    else if (m.kind === 'labels') { buildLabels(id, m); n = (m.items || []).length; }
    else if (m.kind === 'markers') { buildMarkers(id, m); n = (m.items || []).length; }
    else if (m.kind === 'lines') n = buildLines(id, m);
    else if (m.kind === 'rings') n = buildRings(id, m);
    layers[id] = { kind: m.kind }; counts[id] = n; requested[id] = true;
    setCount(id, n === 0 ? 'none' : String(n));
    if (m.note && panelRows[id]) panelRows[id].row.title = m.note;
    placeDom(); refresh();
  }

  // ---------------------------------------------------------------- pointer input
  var drag = null, hoverHit = null, lastMove = 0;
  function onDown(e) {
    hideMenu(); hideTip(); canvas.focus();
    drag = { x: e.clientX, y: e.clientY, sx: e.clientX, sy: e.clientY, btn: e.button, moved: false, pan: e.button === 2 || e.button === 1 || e.shiftKey };
    flight = null; e.preventDefault();
  }
  function onMove(e) {
    if (drag) {
      var dx = e.clientX - drag.x, dy = e.clientY - drag.y;
      if (!drag.moved && Math.hypot(e.clientX - drag.sx, e.clientY - drag.sy) > 4) drag.moved = true;
      if (drag.moved) {
        drag.x = e.clientX; drag.y = e.clientY;
        if (drag.pan) {
          var k = cam.dist * 0.0017, a = cam.az * Math.PI / 180;
          cam.tx -= (Math.cos(a) * dx + Math.sin(a) * dy) * k;
          cam.tz -= (-Math.sin(a) * dx + Math.cos(a) * dy) * k;
          cam.tx = clamp(cam.tx, 0, 64 * TY); cam.tz = clamp(cam.tz, 0, 64 * TY);
        } else { cam.az -= dx * 0.3; cam.tilt = clamp(cam.tilt + dy * 0.25, 8, 88); }
        dirty = true; sendView(); hideTip();
      }
      return;
    }
    var now = performance.now(); if (now - lastMove < 35) return; lastMove = now;
    hover(e.clientX, e.clientY);
  }
  function onUp(e) {
    var d = drag; drag = null; if (!d) return;
    if (!d.moved) {
      if (d.btn === 0) { if (hoverHit && hoverHit.link) { hideTip(); openLink(hoverHit.link); } else if (hoverZ && hoverZ.link) { hideTip(); openLink(hoverZ.link); } }
      else if (d.btn === 2) contextAt(e.clientX, e.clientY);
    }
  }
  var hoverZ = null;
  function hover(mx, my) {
    // 1. points: the nearest on screen within 9 px
    var best = null, bd = 81, vp = new THREE.Matrix4().multiplyMatrices(camera.projectionMatrix, camera.matrixWorldInverse), w = window.innerWidth, h = window.innerHeight, v = new THREE.Vector3();
    Object.keys(pointLayers).forEach(function (id) {
      var L = pointLayers[id]; if (!on[id]) return;
      var a = L.obj.geometry.attributes.position.array;
      for (var i = 0; i < L.n; i++) {
        var gname = L.group[L.styleIdx[i]]; if (gname && !on[gname]) continue;
        v.set(a[i * 3], a[i * 3 + 1], a[i * 3 + 2]).applyMatrix4(vp);
        if (v.z > 1 || v.z < -1) continue;
        var sx = (v.x * .5 + .5) * w, sy = (-v.y * .5 + .5) * h, d = (sx - mx) * (sx - mx) + (sy - my) * (sy - my);
        if (d < bd) { bd = d; best = { L: L, i: i }; }
      }
    });
    hoverHit = null;
    if (best) {
      var L2 = best.L, i2 = best.i, st = L2.styles[L2.styleIdx[i2]] || {};
      var info = { name: L2.names[L2.name[i2]] || '', tip: L2.tip[i2] >= 0 ? L2.tips[L2.tip[i2]] : '', link: L2.link[i2] >= 0 ? L2.links[L2.link[i2]] : '', st: st };
      hoverHit = info; canvas.style.cursor = 'pointer';
      showTip(mx, my, info.name, info.tip, (st.l || '') + (info.link ? ' · click to open' : ''));
      setHoverZone(null); hoverZ = null; return;
    }
    // 2. the zone border layer: the zone under the cursor
    hoverZ = null;
    if (on.borders && ringLayers.borders) {
      var gp = pickGround(mx, my), z = gp ? zoneAtGround(gp.x, gp.z) : null;
      setHoverZone(z); hoverZ = z;
      if (z) { showTip(mx, my, z.name, '', z.link ? 'Click to open its Atlas page' : ''); canvas.style.cursor = z.link ? 'pointer' : ''; return; }
    }
    canvas.style.cursor = ''; hideTip();
  }
  function onWheel(e) {
    e.preventDefault(); flight = null;
    var f = Math.exp(clamp(e.deltaY, -200, 200) * 0.0011), nd = clamp(cam.dist * f, 40, 42000);
    if (nd !== cam.dist) {
      var gp = pickGround(e.clientX, e.clientY);
      if (gp) { var r = nd / cam.dist; cam.tx = clamp(gp.x + (cam.tx - gp.x) * Math.pow(r, 0.8), 0, 64 * TY); cam.tz = clamp(gp.z + (cam.tz - gp.z) * Math.pow(r, 0.8), 0, 64 * TY); }
      cam.dist = nd;
    }
    dirty = true; sendView();
  }
  function contextAt(mx, my) {
    var gp = pickGround(mx, my); if (!gp) return;
    var ll = xz2ll(gp.x, gp.z), w = xz2w(gp.x, gp.z), zn = zoneAtGround(gp.x, gp.z);
    var hd = 'x ' + Math.round(w[0]) + ', y ' + Math.round(w[1]) + ', height ' + Math.round(groundY(gp.x, gp.z)) + (zn ? ' · ' + zn.name : '');
    menu(mx, my, hd, [
      { text: 'Navigate here', run: function () { send({ t: 'nav', lat: clamp(ll[0], -255.9, 0), lng: clamp(ll[1], 0, 255.9) }); } },
      { text: 'Fly here', run: function () { setView(ll[0], ll[1], Math.max(cam.dist * 0.4, 200), null, null, false); sendView(); } }
    ]);
  }
  function onDbl(e) { var gp = pickGround(e.clientX, e.clientY); if (gp) { var ll = xz2ll(gp.x, gp.z); setView(ll[0], ll[1], Math.max(cam.dist * 0.45, 120), null, null, false); sendView(); } }
  function onKey(e) {
    var k = e.key, step = cam.dist * 0.08, a = cam.az * Math.PI / 180, used = true;
    if (k === 'ArrowUp' || k === 'w') { cam.tx -= Math.sin(a) * step; cam.tz -= Math.cos(a) * step; }
    else if (k === 'ArrowDown' || k === 's') { cam.tx += Math.sin(a) * step; cam.tz += Math.cos(a) * step; }
    else if (k === 'ArrowLeft' || k === 'a') { cam.tx -= Math.cos(a) * step; cam.tz += Math.sin(a) * step; }
    else if (k === 'ArrowRight' || k === 'd') { cam.tx += Math.cos(a) * step; cam.tz -= Math.sin(a) * step; }
    else if (k === '+' || k === '=') cam.dist = clamp(cam.dist * 0.85, 40, 42000);
    else if (k === '-' || k === '_') cam.dist = clamp(cam.dist / 0.85, 40, 42000);
    else if (k === 'q') cam.az += 10; else if (k === 'e') cam.az -= 10;
    else if (k === 'PageUp') cam.tilt = clamp(cam.tilt - 5, 8, 88); else if (k === 'PageDown') cam.tilt = clamp(cam.tilt + 5, 8, 88);
    else if (k === 'Escape') { hideMenu(); used = false; }
    else used = false;
    if (used) { e.preventDefault(); flight = null; cam.tx = clamp(cam.tx, 0, 64 * TY); cam.tz = clamp(cam.tz, 0, 64 * TY); dirty = true; sendView(); }
  }

  function fitView(instant) {
    if (!landBounds) return;
    var cl = (landBounds.minCol + landBounds.maxCol + 1) / 2, cr = (landBounds.minRow + landBounds.maxRow + 1) / 2;
    var span = Math.max(landBounds.maxCol - landBounds.minCol + 1, (landBounds.maxRow - landBounds.minRow + 1) * 0.7) * TY;
    setView(-cr * UNITS, cl * UNITS, clamp(span * 1.15, 3000, 42000), 0, 52, instant);
    if (!instant) sendView();
  }
  function fitAll() { fitView(false); }

  // ---------------------------------------------------------------- frame loop
  var fps = 0, fpsFrames = 0, fpsT0 = performance.now(), lastStats = 0, lastSel2 = 0, lastHeightGen = -1, lastRefresh = 0, benching = null;
  function frame(now) { requestAnimationFrame(frame); tick(now); }
  function tick(now) {
    if (!renderer || !inited) return;
    stepFlight(now);
    if (benching) { var t = (now - benching.t0) / 1000; if (benching.last) { var dt = now - benching.last; if (dt > benching.maxDt) benching.maxDt = dt; if (dt > 33.4) benching.slow++; benching.n++; } benching.last = now; cam.az = benching.az0 + t * 24; cam.tilt = 38 + 14 * Math.sin(t * 0.9); dirty = true; }
    if (heightGen !== lastHeightGen && now - lastRefresh > 700) { lastHeightGen = heightGen; lastRefresh = now; refreshHeights(); }
    var need = dirty || inFlight > 0;
    if (!need) { if (now - fpsT0 > 2000) { fpsT0 = now; fpsFrames = 0; } return; }
    dirty = false; frameN++;
    updateCamera();
    if (now - lastSel2 > 120 || pendingBuilds) { lastSel2 = now; updateTiles(now); updateBuildings(now); } else dirty = true;       // the tile pass is throttled: a change that arrives inside the pause (a liquid file, a texture) gets its pass on a later frame
    var shown = updateOverlays();
    shimmer(now);
    renderer.render(scene, camera);
    fpsFrames++;
    if (now - fpsT0 >= 1000) { fps = fpsFrames * 1000 / (now - fpsT0); fpsFrames = 0; fpsT0 = now; }
    if (!ready && selStats.meshes > 0 && frameN > 8 && inFlight === 0) { ready = true; send({ t: 'ready', tiles: selStats.meshes, errors: texErrors }); }
    if (now - lastStats > 3000 && frameN > 10) { lastStats = now; sendStats(); }
    dirty = dirty || flight != null || benching != null;
    if (inFlight > 0 || pendingBuilds || waterActive > 0) dirty = true;      // builds that ran out of their time slice (terrain, liquids) and files still coming go on next frame
  }
  function sendStats() {
    var mem = (performance && performance.memory) ? performance.memory.usedJSHeapSize / 1048576 : 0, inf = renderer.info;
    send({ t: 'stats', fps: Math.round(fps * 10) / 10, mem: Math.round(mem), tris: inf.render.triangles, tiles: selStats.meshes, tex: inf.memory.textures, bld: BLD.drawn });
  }
  function onResize() {
    if (!renderer) return;
    var w = Math.max(1, window.innerWidth), h = Math.max(1, window.innerHeight);
    renderer.setSize(w, h, false); canvas.style.width = w + 'px'; canvas.style.height = h + 'px';
    camera.aspect = w / h; camera.updateProjectionMatrix(); dirty = true;
  }

  // ---------------------------------------------------------------- WebGL start
  function startGl() {
    canvas = $('gl');
    var why = '';
    if (forceNoGl) why = 'WebGL was switched off for a test';
    else if (!window.WebGLRenderingContext) why = 'this browser has no WebGL';
    else if (typeof THREE === 'undefined') why = 'the 3D engine did not load';
    else {
      try {
        renderer = new THREE.WebGLRenderer({ canvas: canvas, antialias: true, alpha: false, powerPreference: 'high-performance' });
        if (!renderer.getContext()) { why = 'WebGL could not be started'; renderer = null; }
      } catch (e) { why = 'WebGL could not be started (' + String(e && e.message || e).slice(0, 100) + ')'; renderer = null; }
    }
    if (!renderer) { send({ t: 'webgl', ok: false, why: why || 'WebGL is not available', renderer: '' }); return false; }
    try {
      var gl = renderer.getContext(), ext = gl.getExtension('WEBGL_debug_renderer_info');
      glInfo = ext ? String(gl.getParameter(ext.UNMASKED_RENDERER_WEBGL)) : String(gl.getParameter(gl.RENDERER));
    } catch (e) { glInfo = 'unknown'; }
    software = /swiftshader|llvmpipe|software|basic render|microsoft basic/i.test(glInfo);
    renderer.outputEncoding = THREE.sRGBEncoding;
    scene = new THREE.Scene(); scene.background = new THREE.Color(0x0b1c20); scene.fog = new THREE.Fog(0x0b1c20, 8000, 20000);
    camera = new THREE.PerspectiveCamera(48, 1, 5, 80000);
    scene.add(new THREE.AmbientLight(0xffffff, 0.66));
    var sun = new THREE.DirectionalLight(0xffffff, 0.5); sun.position.set(-0.55, 0.75, -0.35); scene.add(sun);
    var hemi = new THREE.HemisphereLight(0xcfe8ff, 0x403a30, 0.18); scene.add(hemi);
    // the liquids: translucent, unlit, no depth write, drawn after the terrain (see waterArrays); no global sea plane any more, so a canyon below sea level stays dry
    for (var wk = 1; wk <= 4; wk++) waterMats[wk] = new THREE.MeshBasicMaterial({ color: waterBase[wk].c, transparent: true, opacity: waterBase[wk].o, depthWrite: false, side: THREE.DoubleSide });
    var c = document.createElement('canvas'); c.width = c.height = 4; var g = c.getContext('2d'); g.fillStyle = '#' + ('000000' + seaColor.toString(16)).slice(-6); g.fillRect(0, 0, 4, 4);
    seaTex = new THREE.CanvasTexture(c); seaTex.encoding = THREE.sRGBEncoding;
    // the guard: a lost WebGL context is told to the app, which falls back to the flat map
    canvas.addEventListener('webglcontextlost', function (e) { e.preventDefault(); send({ t: 'webgl', ok: false, why: 'the graphics context was lost', renderer: glInfo.slice(0, 100) }); });
    send({ t: 'webgl', ok: true, why: software ? 'software rendering' : '', renderer: glInfo.slice(0, 100) });
    if (software && !prefs.__user) { prefs.detail = 'low'; }
    renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, software ? 1 : D().dpr));
    onResize();
    canvas.addEventListener('mousedown', onDown); window.addEventListener('mousemove', onMove); window.addEventListener('mouseup', onUp);
    canvas.addEventListener('wheel', onWheel, { passive: false }); canvas.addEventListener('dblclick', onDbl); canvas.addEventListener('contextmenu', function (e) { e.preventDefault(); });
    canvas.addEventListener('keydown', onKey); canvas.addEventListener('mouseleave', function () { hideTip(); setHoverZone(null); });
    window.addEventListener('resize', onResize); window.addEventListener('blur', hideMenu);
    requestAnimationFrame(frame);
    return true;
  }

  // ---------------------------------------------------------------- buildings (WMOs): "Bridges only" / "All buildings"
  // The app (WorldMapBuildingsHost) reads the shapes from the player's game files into <map>/buildings: placements.bin (one 60-byte record per placed building) and m/<FileDataID>.wmb
  // (the simplified model: 3 levels of detail, short positions + sRGB vertex colours + indices; no normals, the page lights them flat). The page only fetches what the app listed as finished
  // (bstate.all / bstate.fresh), draws every model with one InstancedMesh per level of detail, and each pass (every 120 ms, with the tiles) culls placements by distance and frustum, picks
  // the level by distance / size and keeps to a triangle budget and a memory cap, both set by the Detail setting.
  var BDETAIL = {
    low:    { view: 1800, tris: 120000, memMb: 48,  l0: 10, l1: 40, k: 150 },
    medium: { view: 4000, tris: 400000, memMb: 128, l0: 14, l1: 55, k: 250 },
    high:   { view: 8000, tris: 900000, memMb: 256, l0: 20, l1: 80, k: 400 }
  };
  function BD() { return BDETAIL[prefs.detail] || BDETAIL.medium; }
  var BLD = { mode: 'off', base: '', st: null, built: {}, pl: null, plLoading: false, models: {}, buckets: {}, mem: 0, drawn: 0, tris: 0, byLod: [0, 0, 0], fetching: 0, wanted: 0, inView: 0, skipped: 0,
    capOverride: null, mat: null, lut: null, loaded: 0, errors: 0, evicted: 0, requests: 0, passes: 0, maxFetch: 6, ui: null, map: -1 };
  var bM = null, bQ = null, bE = null, bP = null, bS = null, bC = null;
  function bInit3() {
    if (bM) return;
    bM = new THREE.Matrix4(); bQ = new THREE.Quaternion(); bE = new THREE.Euler(0, 0, 0, 'YXZ'); bP = new THREE.Vector3(); bS = new THREE.Vector3(); bC = new THREE.Vector3();
  }
  function bLut() {
    if (BLD.lut) return BLD.lut;
    var l = new Uint8Array(256);
    for (var i = 0; i < 256; i++) { var c = i / 255; l[i] = Math.round((c <= 0.04045 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4)) * 255); }
    return (BLD.lut = l);
  }
  function bMaterial() {
    if (!BLD.mat) BLD.mat = new THREE.MeshPhongMaterial({ vertexColors: true, flatShading: true, shininess: 0, specular: 0x000000, side: THREE.DoubleSide });
    return BLD.mat;
  }

  // ---- placements.bin
  function bLoadPlacements() {
    if (BLD.pl || BLD.plLoading || !BLD.base) return;
    BLD.plLoading = true;
    fetch(BLD.base + '/placements.bin').then(function (r) { return r.ok ? r.arrayBuffer() : null; }).then(function (buf) {
      BLD.plLoading = false;
      if (!buf || buf.byteLength < 8) return;
      var dv = new DataView(buf);
      if (dv.getUint32(0, true) !== 0x31504D57) return;                   // "WMP1"
      var n = dv.getInt32(4, true);
      if (n < 0 || 8 + n * 60 !== buf.byteLength) return;
      var pl = { n: n, fdid: new Int32Array(n), flags: new Uint8Array(n), x: new Float32Array(n), y: new Float32Array(n), z: new Float32Array(n), rx: new Float32Array(n), ry: new Float32Array(n), rz: new Float32Array(n), s: new Float32Array(n),
        cx: new Float32Array(n), cy: new Float32Array(n), cz: new Float32Array(n), rad: new Float32Array(n) };
      for (var i = 0; i < n; i++) {
        var o = 8 + i * 60;
        pl.fdid[i] = dv.getUint32(o, true); pl.flags[i] = dv.getUint32(o + 4, true) & 255;
        pl.x[i] = dv.getFloat32(o + 8, true); pl.y[i] = dv.getFloat32(o + 12, true); pl.z[i] = dv.getFloat32(o + 16, true);
        pl.rx[i] = dv.getFloat32(o + 20, true); pl.ry[i] = dv.getFloat32(o + 24, true); pl.rz[i] = dv.getFloat32(o + 28, true); pl.s[i] = dv.getFloat32(o + 32, true);
        var x0 = dv.getFloat32(o + 36, true), y0 = dv.getFloat32(o + 40, true), z0 = dv.getFloat32(o + 44, true), x1 = dv.getFloat32(o + 48, true), y1 = dv.getFloat32(o + 52, true), z1 = dv.getFloat32(o + 56, true);
        pl.cx[i] = (x0 + x1) / 2; pl.cy[i] = (y0 + y1) / 2; pl.cz[i] = (z0 + z1) / 2;
        pl.rad[i] = Math.max(2, Math.sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0) + (z1 - z0) * (z1 - z0)) / 2);
      }
      BLD.pl = pl; dirty = true;
    }).catch(function () { BLD.plLoading = false; BLD.errors++; });
  }

  // ---- a model file
  function bParse(buf) {
    try {
      var dv = new DataView(buf);
      if (buf.byteLength < 32 || dv.getUint32(0, true) !== 0x31424D57) return null;      // "WMB1"
      var m = { bridge: (dv.getUint8(4) & 1) !== 0, center: [dv.getFloat32(8, true), dv.getFloat32(12, true), dv.getFloat32(16, true)], half: [dv.getFloat32(20, true), dv.getFloat32(24, true), dv.getFloat32(28, true)], lods: [], bytes: 0 };
      var n = dv.getUint8(5), p = 32, lut = bLut();
      if (n > 8) return null;
      for (var k = 0; k < n; k++) {
        var nv = dv.getUint32(p, true), ni = dv.getUint32(p + 4, true); p += 8;
        if (nv > 4000000 || ni > 12000000 || ni % 3 !== 0) return null;
        if (p + nv * 6 > buf.byteLength) return null;
        var pos = new Int16Array(buf, p, nv * 3); p += nv * 6; p = (p + 3) & ~3;
        if (p + nv * 3 > buf.byteLength) return null;
        var src = new Uint8Array(buf, p, nv * 3), col = new Uint8Array(nv * 3); p += nv * 3; p = (p + 3) & ~3;
        for (var i = 0; i < col.length; i++) col[i] = lut[src[i]];
        var w = nv <= 65535 ? 2 : 4;
        if (p + ni * w > buf.byteLength) return null;
        var idx = w === 2 ? new Uint16Array(buf, p, ni) : new Uint32Array(buf, p, ni); p += ni * w; p = (p + 3) & ~3;
        m.lods.push({ nv: nv, ni: ni, pos: pos, col: col, idx: idx, geom: null, bytes: nv * 6 + nv * 3 + ni * w });
        m.bytes += nv * 9 + ni * w;
      }
      m.local = new THREE.Matrix4().compose(new THREE.Vector3(m.center[0], m.center[1], m.center[2]), new THREE.Quaternion(), new THREE.Vector3(m.half[0] / 32767, m.half[1] / 32767, m.half[2] / 32767));
      return m;
    } catch (e) { return null; }
  }
  function bGeom(m, k) {
    var l = m.lods[k]; if (l.geom) return l.geom;
    var g = new THREE.BufferGeometry();
    var a = new THREE.BufferAttribute(l.pos, 3), c = new THREE.BufferAttribute(l.col, 3, true), ix = new THREE.BufferAttribute(l.idx, 1);
    // once on the graphics card the page's copy is let go (the browser's memory is what is scarce)
    var free = function () { this.array = null; };
    a.onUpload(free); c.onUpload(free); ix.onUpload(free);
    g.setAttribute('position', a); g.setAttribute('color', c); g.setIndex(ix);
    l.geom = g; return g;
  }
  function bFetchModel(fdid, e) {
    e.state = 'loading'; BLD.fetching++; BLD.requests++;
    fetch(BLD.base + '/m/' + fdid + '.wmb').then(function (r) { return r.ok ? r.arrayBuffer() : null; }).then(function (buf) {
      var m = buf ? bParse(buf) : null;
      if (!m) { e.state = 'none'; if (buf) BLD.errors++; }
      else if (m.lods.length === 0) e.state = 'none';
      else { e.m = m; e.state = 'ready'; BLD.mem += m.bytes; BLD.loaded++; }
    }).catch(function () { e.state = 'none'; BLD.errors++; }).then(function () { BLD.fetching--; dirty = true; });
  }
  function bFree(fdid, e) {
    for (var k = 0; k < 3; k++) {
      var key = fdid * 4 + k, b = BLD.buckets[key];
      if (b) { scene.remove(b.mesh); b.mesh.dispose(); delete BLD.buckets[key]; }
    }
    if (e.m) { e.m.lods.forEach(function (l) { if (l.geom) { l.geom.dispose(); l.geom = null; } }); BLD.mem -= e.m.bytes; e.m = null; }
    e.state = 'idle';
  }
  function bClear() {
    Object.keys(BLD.models).forEach(function (k) { bFree(+k, BLD.models[k]); });
    BLD.models = {}; BLD.buckets = {}; BLD.mem = 0; BLD.pl = null; BLD.plLoading = false; BLD.drawn = 0; BLD.tris = 0; BLD.byLod = [0, 0, 0];
  }
  function bHideAll() {
    Object.keys(BLD.buckets).forEach(function (k) { var b = BLD.buckets[k]; b.mesh.visible = false; b.mesh.count = 0; });
    BLD.drawn = 0; BLD.tris = 0; BLD.byLod = [0, 0, 0];
  }

  // ---- the pass: which placements are drawn, at which level
  var bScratch = { idx: new Int32Array(0), d: new Float32Array(0) };
  function updateBuildings(now) {
    if (!renderer || !scene) return;
    if (BLD.mode === 'off' || !BLD.pl) { if (BLD.drawn > 0) bHideAll(); return; }
    bInit3();
    var cfg2 = BD(), pl = BLD.pl, n = pl.n, ex = prefs.exag, eye = camera.position, bridgesOnly = BLD.mode === 'bridges';
    var fr = new THREE.Frustum().setFromProjectionMatrix(new THREE.Matrix4().multiplyMatrices(camera.projectionMatrix, camera.matrixWorldInverse));
    var sph = new THREE.Sphere(bC, 1);
    var cand = [], wanted = 0;
    for (var i = 0; i < n; i++) {
      if (bridgesOnly && (pl.flags[i] & 1) === 0) continue;
      if (!BLD.built[pl.fdid[i]]) continue;
      wanted++;
      var r = pl.rad[i], cy = pl.cy[i] * ex, dx = eye.x - pl.cx[i], dy = eye.y - cy, dz = eye.z - pl.cz[i];
      var dist = Math.sqrt(dx * dx + dy * dy + dz * dz), lim = Math.min(cfg2.view, Math.max(250, r * cfg2.k));
      if (dist - r > lim) continue;
      bC.set(pl.cx[i], cy, pl.cz[i]); sph.center.copy(bC); sph.radius = r + 1;
      if (!fr.intersectsSphere(sph)) continue;
      cand.push({ i: i, d: Math.max(dist - r, 1), q: Math.max(dist - r, 1) / r });
    }
    cand.sort(function (a, b) { return a.d - b.d; });
    BLD.wanted = wanted; BLD.inView = cand.length; BLD.passes++;
    var tris = 0, drawn = 0, skipped = 0, byLod = [0, 0, 0], used = {};
    var lists = {};
    for (var c = 0; c < cand.length; c++) {
      var ci = cand[c].i, fd = pl.fdid[ci], e = BLD.models[fd];
      if (!e) e = BLD.models[fd] = { state: 'idle', m: null, last: now };
      if (e.state === 'idle') { if (BLD.fetching < BLD.maxFetch) bFetchModel(fd, e); continue; }
      if (e.state !== 'ready') continue;
      e.last = now;
      var q = cand[c].q, lod = q < cfg2.l0 ? 0 : q < cfg2.l1 ? 1 : 2, last = e.m.lods.length - 1;
      if (lod > last) lod = last;
      var cost = e.m.lods[lod].ni / 3;
      while (tris + cost > cfg2.tris && lod < last) { lod++; cost = e.m.lods[lod].ni / 3; }
      if (tris + cost > cfg2.tris) { skipped++; continue; }
      tris += cost; drawn++; byLod[lod]++;
      var key = fd * 4 + lod; (lists[key] || (lists[key] = [])).push(ci); used[key] = true;
    }
    // one InstancedMesh per (model, level); its matrices: place (position with the height exaggeration, rotation YXZ, scale) x the model's own box
    var DEG = Math.PI / 180;
    Object.keys(lists).forEach(function (key) {
      var list = lists[key], fd = Math.floor(key / 4), lod = key % 4, e = BLD.models[fd], b = BLD.buckets[key];
      if (!b || b.cap < list.length) {
        if (b) { scene.remove(b.mesh); b.mesh.dispose(); }
        var cap = Math.max(4, Math.ceil(list.length * 1.5));
        var mesh = new THREE.InstancedMesh(bGeom(e.m, lod), bMaterial(), cap);
        mesh.frustumCulled = false; mesh.instanceMatrix.setUsage(THREE.DynamicDrawUsage);
        scene.add(mesh); b = BLD.buckets[key] = { mesh: mesh, cap: cap, fd: fd, lod: lod };
      }
      for (var j = 0; j < list.length; j++) {
        var p = list[j];
        bP.set(pl.x[p], pl.y[p] * ex, pl.z[p]); bE.set(pl.rx[p] * DEG, pl.ry[p] * DEG, pl.rz[p] * DEG, 'YXZ'); bQ.setFromEuler(bE); bS.set(pl.s[p], pl.s[p], pl.s[p]);
        bM.compose(bP, bQ, bS); bM.multiply(e.m.local);
        b.mesh.setMatrixAt(j, bM);
      }
      b.mesh.count = list.length; b.mesh.visible = true; b.mesh.instanceMatrix.needsUpdate = true;
    });
    Object.keys(BLD.buckets).forEach(function (key) { if (!used[key]) { var b = BLD.buckets[key]; b.mesh.visible = false; b.mesh.count = 0; } });
    BLD.drawn = drawn; BLD.tris = tris; BLD.byLod = byLod; BLD.skipped = skipped;
    // memory cap: the models not used for a while go first
    var capB = (BLD.capOverride != null ? BLD.capOverride : cfg2.memMb) * 1048576;
    if (BLD.mem > capB) {
      var ents = Object.keys(BLD.models).filter(function (k) { var e = BLD.models[k]; return e.state === 'ready' && now - e.last > 2500; }).sort(function (a, b) { return BLD.models[a].last - BLD.models[b].last; });
      for (var v = 0; v < ents.length && BLD.mem > capB * 0.85; v++) { bFree(+ents[v], BLD.models[ents[v]]); BLD.evicted++; }
    }
    if (BLD.fetching > 0) dirty = true;
  }

  // ---- the panel: the switch and the card
  function bldPanel(s) {
    var row = el('div', 'v3r bl', '<span>Buildings</span>');
    var seg = el('div', 'v3r'); seg.innerHTML = '<span class="seg wide" id="v3bld"><button type="button" data-b="off">Off</button><button type="button" data-b="bridges">Bridges only</button><button type="button" data-b="all">All buildings</button></span>';
    var card = el('div', 'v3b'); card.id = 'v3bcard'; card.hidden = true;
    s.appendChild(row); s.appendChild(seg); s.appendChild(card);
    [].forEach.call(seg.querySelectorAll('button'), function (b) {
      b.addEventListener('click', function () { bSwitch(b.getAttribute('data-b')); });
    });
    BLD.ui = { seg: seg, card: card };
    bSync();
  }
  function bSwitch(mode) {
    if (mode === BLD.mode) return;
    BLD.mode = mode; bSync(); dirty = true;
    if (mode === 'off') { bHideAll(); }
    send({ t: 'buildings', mode: mode });
  }
  function bSync() {
    if (!BLD.ui) return;
    [].forEach.call(BLD.ui.seg.querySelectorAll('button'), function (b) { var on = b.getAttribute('data-b') === BLD.mode; b.classList.toggle('on', on); b.setAttribute('aria-pressed', String(on)); });
    bCard();
  }
  function fmtN(n) { return String(Math.round(n)).replace(/\B(?=(\d{3})+(?!\d))/g, ','); }
  function bCard() {
    if (!BLD.ui) return;
    var c = BLD.ui.card, st = BLD.st, ph = st ? st.phase : 'idle', html = '', cls = 'v3b';
    if (BLD.mode === 'off' || !st || st.mode === 'off') { c.hidden = true; c.innerHTML = ''; return; }
    var acts = [];
    if (ph === 'scanning') html = '<div class="bt">Counting the buildings...</div><div class="bs">This only looks at where they stand.</div>';
    else if (ph === 'ask') {
      html = '<div class="bt">' + esc(st.card || 'Show buildings in 3D.') + '</div><div class="bs">Nothing is uploaded and nothing is written to the WoW folder.</div>';
      acts = [{ id: 'build', text: st.est && st.est.modelsDone > 0 ? 'Continue' : 'Build now', primary: true }, { id: 'later', text: 'Not now' }];
    } else if (ph === 'building' || ph === 'paused') {
      var pct = st.total > 0 ? Math.min(100, st.done * 100 / st.total) : 0;
      html = '<div class="bt">' + esc(st.text || '') + '</div><div class="bar"><i style="width:' + pct.toFixed(1) + '%"></i></div>';
      acts = [{ id: ph === 'paused' ? 'resume' : 'pause', text: ph === 'paused' ? 'Resume' : 'Pause' }, { id: 'cancel', text: 'Cancel' }];
    } else if (ph === 'done') {
      html = '<div class="bs">' + fmtN(st.total) + (BLD.mode === 'bridges' ? ' bridges' : ' buildings') + ' &middot; ' + (st.bytes > 0 ? (st.bytes / 1048576).toFixed(st.bytes < 10485760 ? 1 : 0) + ' MB on disk' : 'ready') + '</div>';
    } else if (ph === 'error') {
      cls += ' err'; html = '<div class="bt">' + esc(st.error || 'The buildings could not be made.') + '</div>'; acts = [{ id: 'build', text: 'Try again', primary: true }];
    } else { c.hidden = true; c.innerHTML = ''; return; }
    c.className = cls; c.innerHTML = html + (acts.length ? '<div class="ba"></div>' : '');
    var ba = c.querySelector('.ba');
    acts.forEach(function (a) {
      var b = el('button', a.primary ? 'pri' : '', esc(a.text)); b.type = 'button'; b.setAttribute('data-a', a.id);
      b.addEventListener('click', function () {
        if (a.id === 'build') send({ t: 'bbuild', mode: BLD.mode });
        else if (a.id === 'later') bSwitch('off');
        else if (a.id === 'pause') send({ t: 'bpause' });
        else if (a.id === 'resume') send({ t: 'bresume' });
        else if (a.id === 'cancel') send({ t: 'bcancel' });
      });
      ba.appendChild(b);
    });
    c.hidden = false;
  }
  // the app's state message
  function bldState(m) {
    BLD.st = m; BLD.map = m.map;
    var mode = m.mode === 'bridges' || m.mode === 'all' ? m.mode : 'off';
    if (mode !== BLD.mode) { BLD.mode = mode; if (mode === 'off') bHideAll(); }
    if (m.all) { BLD.built = {}; m.all.forEach(function (id) { BLD.built[id] = true; }); }
    if (m.fresh) m.fresh.forEach(function (id) { BLD.built[id] = true; });
    if (m.placements && !BLD.pl) bLoadPlacements();
    bSync(); dirty = true; lastSel2 = 0;
  }
  function bldInit(b) {
    bClear();
    BLD.st = null; BLD.built = {}; BLD.mode = 'off'; BLD.base = ''; BLD.errors = 0;
    if (!b) return;
    BLD.mode = b.mode === 'bridges' || b.mode === 'all' ? b.mode : 'off'; BLD.base = String(b.base || '');
    (b.models || []).forEach(function (id) { BLD.built[id] = true; });
    if (b.placements && BLD.mode !== 'off') bLoadPlacements();
  }
  // test hooks: the state of the buildings, and where the bridges stand against the water
  function bldInfo() {
    var ready = 0, loading = 0; Object.keys(BLD.models).forEach(function (k) { var s = BLD.models[k].state; if (s === 'ready') ready++; else if (s === 'loading') loading++; });
    var instances = 0, meshes = 0, visible = 0; Object.keys(BLD.buckets).forEach(function (k) { var b = BLD.buckets[k]; meshes++; if (b.mesh.visible && b.mesh.count > 0) { visible++; instances += b.mesh.count; } });
    var st = BLD.st || {};
    return { mode: BLD.mode, phase: st.phase || '', base: BLD.base, placements: BLD.pl ? BLD.pl.n : 0, built: Object.keys(BLD.built).length, wanted: BLD.wanted, inView: BLD.inView, drawn: BLD.drawn, tris: BLD.tris, byLod: BLD.byLod, skipped: BLD.skipped,
      modelsReady: ready, modelsLoading: loading, meshes: meshes, visibleMeshes: visible, instances: instances, memMb: Math.round(BLD.mem / 1048576 * 10) / 10, evicted: BLD.evicted, requests: BLD.requests, errors: BLD.errors, passes: BLD.passes,
      card: BLD.ui ? !BLD.ui.card.hidden : false, cardText: BLD.ui ? (function () { var t = BLD.ui.card.querySelector('.bt') || BLD.ui.card.querySelector('.bs'); return t ? t.textContent : ''; })() : '', cardAll: BLD.ui ? BLD.ui.card.textContent : '', buttons: BLD.ui ? [].map.call(BLD.ui.card.querySelectorAll('button'), function (b) { return b.textContent; }) : [], drawCalls: renderer ? renderer.info.render.calls : 0 };
  }
  // bridges: the deck (the placement's origin) against the water surface and the ground beneath (all in the exaggerated scene), nearest to a point first
  function bldBridges(x, z, max) {
    var pl = BLD.pl; if (!pl) return [];
    var out = [], ex = prefs.exag;
    for (var i = 0; i < pl.n; i++) {
      if ((pl.flags[i] & 1) === 0) continue;
      var w = waterAtXZ(pl.cx[i], pl.cz[i]), g = groundY(pl.cx[i], pl.cz[i]);
      out.push({ i: i, fdid: pl.fdid[i], x: pl.x[i], z: pl.z[i], cx: pl.cx[i], cz: pl.cz[i], deck: pl.y[i] * ex, water: w === null ? null : w * ex, ground: g * ex, d: Math.hypot(pl.cx[i] - x, pl.cz[i] - z) });
    }
    out.sort(function (a, b) { return a.d - b.d; });
    return out.slice(0, max || 10);
  }

  // ---------------------------------------------------------------- the messages from the app
  // vseq: the last place the app sent the camera to; every view3 report names it (see worldmap.js)
  var vseq = -1;
  function handle(m) {
    if (!m || typeof m.t !== 'string') return;
    if ((m.t === 'init' || m.t === 'goto' || m.t === 'fit') && typeof m.s === 'number') vseq = m.s;
    switch (m.t) {
      case 'init': init(m); break;
      case 'goto': if (inited) { var d = m.dist ? m.dist : zToDist(m.z == null ? 5 : m.z); setView(m.lat, m.lng, d, null, null, false); setPin(m); } break;
      case 'fit': if (inited) fitAll(); break;
      case 'layer': if (inited) layer(m); break;
      case 'layerinfo': setCount(m.id, m.state === 'loading' ? '...' : m.state === 'empty' ? 'none' : String(m.count == null ? '' : m.count)); if (m.note && panelRows[m.id]) panelRows[m.id].row.title = m.note; break;
      case 'toast': toast(m.text, m.ok); break;
      case 'bstate': bldState(m); break;
      case 'on': (m.on || []).forEach(function (k) { on[k] = true; }); refresh(); break;
    }
  }
  if (wv) wv.addEventListener('message', function (e) { handle(e.data); });

  function setPin(m) {
    if (pinObj && pinObj.parentNode) pinObj.parentNode.removeChild(pinObj); pinObj = null; domItems = domItems.filter(function (it) { return it.layer !== '__pin'; });
    if (!m.pin) return;
    var p = ll2xz(m.lat, m.lng), d = el('div', 'm3', '<div class="pin"></div>'); $('ov').appendChild(d); pinObj = d;
    var it = { el: d, x: p[0], z: p[1], y: groundYE(p[0], p[1]), kind: 'marker', layer: '__pin' }; on.__pin = true; domItems.push(it);
  }

  function init(m) {
    cfg = m.map; terrain = m.terrain; forceNoGl = !!m.forceNoGl;
    if (m.prefs) { prefs.detail = DETAIL[m.prefs.detail] ? m.prefs.detail : 'medium'; prefs.exag = clamp(+m.prefs.exag || 1.5, 1, 3); prefs.__user = !!m.prefs.userDetail; }
    if (!renderer && !startGl()) { inited = false; return; }
    // a second init (another continent): clear what the first built
    Object.keys(meshes).forEach(function (k) { var mm = meshes[k]; if (mm.mesh) { scene.remove(mm.mesh); mm.mesh.geometry.dispose(); } clearWater(mm); if (mm.mat) mm.mat.dispose(); }); meshes = {}; waterActive = 0; waterLoaded = 0; waterFailed = 0;
    Object.keys(tex).forEach(function (k) { var e = tex[k]; if (e.tex && !e.shared) try { e.tex.dispose(); } catch (x) { } }); tex = {}; canvasTexCount = 0;
    bldInit(m.buildings);
    Object.keys(pointLayers).forEach(function (k) { scene.remove(pointLayers[k].obj); pointLayers[k].obj.geometry.dispose(); }); pointLayers = {};
    Object.keys(lineLayers).forEach(function (k) { scene.remove(lineLayers[k].group); }); lineLayers = {};
    Object.keys(ringLayers).forEach(function (k) { ringLayers[k].zones.forEach(function (z) { scene.remove(z.obj); z.obj.geometry.dispose(); }); }); ringLayers = {};
    $('ov').innerHTML = ''; domItems = []; layers = {}; requested = {}; counts = {}; panelRows = {}; defs = {}; on = {}; hoverZone = null;
    tileList = []; tileMap = {}; heightsLoaded = 0; heightsFailed = 0; heightGen = 0; ready = false; frameN = 0; built = 0;
    var tl = terrain.tiles || [];
    for (var i = 0; i + 1 < tl.length; i += 2) { var t = { c: tl[i], r: tl[i + 1], H: null, A: null, min: 0, max: 0 }; tileList.push(t); tileMap[t.c * 64 + t.r] = t; }
    holeMap = {}; var hl = terrain.holes || [];
    for (var h = 0; h + 1 < hl.length; h += 2) if (!tileMap[hl[h] * 64 + hl[h + 1]]) holeMap[hl[h] * 64 + hl[h + 1]] = 1;
    buildOuterSea();
    landBounds = { minCol: cfg.minCol, maxCol: cfg.maxCol, minRow: cfg.minRow, maxRow: cfg.maxRow };
    (m.on || []).forEach(function (k) { on[k] = true; });
    buildPanel(m.layers || []);
    // a layer that is on with none of its kinds on (an older saved state) shows all its kinds, never an empty layer
    (m.layers || []).forEach(function (d) { var kids = d.children || []; var any = kids.some(function (c) { return on[c.id]; });
      kids.forEach(function (c) { if (on[d.id] && !any) on[c.id] = true; else if (on[c.id] === undefined) on[c.id] = false; }); });
    document.documentElement.style.setProperty('--sea', cfg.sea || '#081110');
    // the first view
    if (m.view) { var d = m.view.dist ? m.view.dist : zToDist(m.view.z == null ? 5 : m.view.z); setView(m.view.lat, m.view.lng, d, 0, 52, true); setPin(m.view); } else fitView(true);
    inited = true; refresh(); loadHeights();
    (m.layers || []).forEach(function (d) { if (on[d.id]) { requested[d.id] = true; send({ t: 'need', layer: d.id }); setCount(d.id, '...'); } });
    setTimeout(function () { if (!ready && inited) { ready = true; send({ t: 'ready', tiles: selStats.meshes, errors: texErrors }); } }, 9000);
    send({ t: 'inited' }); dirty = true;
  }

  // test / diagnostics hook (read-only, except the camera)
  window.wm3 = {
    // self-test: points of a layer currently drawn (after the kind filters), a layer fed directly, and a layer switch
    shownPoints: function (id) { var L = pointLayers[id]; if (!L || !L.obj.visible) return 0; var a = L.obj.geometry.attributes.show.array, n = 0; for (var i = 0; i < L.n; i++) if (a[i] > 0) n++; return n; },
    feedForTest: function (m) { layer(m); return 1; },
    setOnForTest: function (id, v) { setOn(id, v); return 1; },
    state: function () {
      var inf = renderer ? renderer.info : { render: {}, memory: {} };
      return JSON.stringify({ ready: ready, glOk: !!renderer, renderer: glInfo, software: software, fps: fps, frames: frameN, heightsLoaded: heightsLoaded, heightsFailed: heightsFailed, tilesTotal: tileList.length,
        meshes: selStats.meshes, tris: selStats.tris, desired: selStats.desired, drawCalls: inf.render.calls, textures: inf.memory.textures, geometries: inf.memory.geometries, texCache: Object.keys(tex).length, canvasTex: canvasTexCount,
        texErrors: texErrors, texLoaded: texLoaded, errs: errs.slice(0, 5), prefs: { detail: prefs.detail, exag: prefs.exag }, cam: { tx: cam.tx, tz: cam.tz, ty: cam.ty, az: cam.az, tilt: cam.tilt, dist: cam.dist },
        on: Object.keys(on).filter(function (k) { return on[k]; }), counts: counts, inFlight: inFlight, built: built, js: performance && performance.memory ? Math.round(performance.memory.usedJSHeapSize / 1048576) : 0 });
    },
    setView: function (lat, lng, dist, az, tilt) { setView(lat, lng, dist, az, tilt, true); sendView(); },
    // the liquids: meshes drawn, files read, and what covers a point (surface height in yards or 'null')
    water: function () {
      var byKind = {}, tiles = 0; Object.keys(meshes).forEach(function (k) { var m = meshes[k]; if (m.wm && m.wm.length) { tiles++; m.wm.forEach(function (w) { byKind[w.userData.kind] = (byKind[w.userData.kind] || 0) + 1; }); } });
      var withFile = 0, layersN = 0; tileList.forEach(function (t) { if (t.W) { withFile++; layersN += t.W.length; } });
      var bare = []; tileList.forEach(function (t) { if (t.W && t.W.length === 0 && t.H && t.max < 0 && bare.length < 12) bare.push(t.c + '_' + t.r); });
      return JSON.stringify({ pending: pendingBuilds, bare: bare, meshes: waterMeshCount, tiles: tiles, files: withFile, layers: layersN, terrainMeshes: selStats.meshes, byKind: byKind, loaded: waterLoaded, none: waterFailed, active: waterActive, outer: !!outerSea, flatSea: false });
    },
    waterAt: function (lat, lng) { var p = ll2xz(lat, lng); var h = waterAtXZ(p[0], p[1]); return h === null ? 'null' : String(h); },
    ground: function (lat, lng) { var p = ll2xz(lat, lng); return groundY(p[0], p[1]); },
    // FPS of a continuous orbit for `ms` milliseconds (the camera keeps turning and tilting; tiles stream as it goes)
    bench: function (ms) {
      return new Promise(function (res) {
        var f0 = frameN, t0 = performance.now(); benching = { t0: t0, az0: cam.az, maxDt: 0, slow: 0, n: 0, last: 0 };
        var samples = [];
        var iv = setInterval(function () { samples.push(fps); }, 500);
        setTimeout(function () { clearInterval(iv); var bn = benching; benching = null; var secs = (performance.now() - t0) / 1000, avg = (frameN - f0) / secs; var s = samples.filter(function (x) { return x > 0; }).sort(function (a, b) { return a - b; }); res(JSON.stringify({ avg: avg, min: s.length ? s[0] : 0, p10: s.length ? s[Math.floor(s.length * 0.1)] : 0, frames: frameN - f0, secs: secs, maxFrameMs: bn ? bn.maxDt : 0, slowFrames: bn ? bn.slow : 0, measured: bn ? bn.n : 0 })); }, ms);
      });
    },
    // where the flight masters / points sit against the ground (for the marker placement check)
    markers: function () {
      var out = [];
      domItems.forEach(function (it) { if (it.kind === 'marker' && it.layer !== '__pin') out.push({ name: it.name, y: it.y, ground: groundYE(it.x, it.z), x: it.x, z: it.z }); });
      Object.keys(pointLayers).forEach(function (k) { var L = pointLayers[k], a = L.obj.geometry.attributes.position.array; for (var i = 0; i < Math.min(L.n, 5); i++) out.push({ name: 'point ' + k, y: a[i * 3 + 1], ground: groundYE(L.x[i], L.z[i]), x: L.x[i], z: L.z[i] }); });
      return JSON.stringify(out);
    },
    // the dungeon / raid entrance markers (test): kind group, whether the layer + its kind row are on, whether the marker is on screen now, ground height; `fire` = [index, event name] dispatches a DOM event on one
    instances: function (fire, prefix) {
      var out = [], idx = [];
      domItems.forEach(function (it) { if (it.kind === 'marker' && it.g && it.g.indexOf(prefix || 'instances.') === 0) idx.push(it); });
      idx.forEach(function (it, i) { out.push({ i: i, name: it.name, g: it.g, wanted: !!on[it.layer] && !!on[it.g], vis: it.vis !== false && it.el.style.display !== 'none', near: !!it.near, y: it.y, ground: groundYE(it.x, it.z), html: it.el.innerHTML }); });
      if (fire && idx[fire[0]]) idx[fire[0]].el.dispatchEvent(new MouseEvent(fire[1], { bubbles: true, cancelable: true, clientX: 300, clientY: 300 }));
      return JSON.stringify(out);
    },
    // the routes of a lines layer (test): kind group and whether the line is drawn now
    lines: function (id) { var L = lineLayers[id]; return JSON.stringify(L ? L.items.map(function (it) { return { g: it.g || '', shown: !!(L.group.visible && it.line.visible) }; }) : []); },
    zoneAt: function (lat, lng) { var p = ll2xz(lat, lng), z = zoneAtGround(p[0], p[1]); return z ? z.name : ''; },
    areaAt: function (lat, lng) { var p = ll2xz(lat, lng), col = p[0] / TY, row = p[1] / TY, c = Math.floor(col), r = Math.floor(row), t = tileMap[c * 64 + r]; if (!t || !t.A) return -1; return t.A[Math.min(15, Math.floor((row - r) * 16)) * 16 + Math.min(15, Math.floor((col - c) * 16))]; },
    pickCenter: function () { var g = pickGround(window.innerWidth / 2, window.innerHeight / 2); return g ? JSON.stringify(g) : ''; },
    step: function (n, dt) { var t0 = performance.now(); for (var i = 0; i < (n || 1); i++) { dirty = true; lastSel2 = 0; tick(t0 + i * (dt || 16)); } return frameN; },
    texKeys: function () { return Object.keys(tex).map(function (k) { return k + ':' + tex[k].state; }).join(' '); },
    near: function () { var o = []; Object.keys(meshes).forEach(function (k) { var m = meshes[k]; var d = Math.hypot((m.t.c + .5) * TY - camera.position.x, (m.t.r + .5) * TY - camera.position.z); o.push([d | 0, m.t.c, m.t.r, m.stride, m.texLvl, m.wantLvl, m.wantKey]); }); o.sort(function (a, b) { return a[0] - b[0]; }); return JSON.stringify(o.slice(0, 6)); },
    buildings: function () { return JSON.stringify(bldInfo()); },
    bridges: function (lat, lng, max) { var p = ll2xz(lat, lng); return JSON.stringify(bldBridges(p[0], p[1], max)); },
    buildingClick: function (id) { var b = BLD.ui && BLD.ui.card.querySelector('[data-a="' + id + '"]'); if (b) b.click(); return !!b; },
    buildingCap: function (mb) { BLD.capOverride = mb; dirty = true; return 1; },
    buildingMode: function (mode) { bSwitch(mode); return BLD.mode; },
    xz2ll: xz2ll, ll2xz: ll2xz, groundXZ: function (x, z) { return groundY(x, z); }, distToZ: distToZ, zToDist: zToDist,
    holes: function () { return JSON.stringify({ n: Object.keys(holeMap).length, drawn: !!(holeMesh && holeMesh.parent), rev: cfg && cfg.rev || 0 }); }
  };
  send({ t: 'hello', p: '3d' });
})();
