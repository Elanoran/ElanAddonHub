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
  var prefs = { detail: 'medium', exag: 1 };
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
  var waterActive = 0, waterLoaded = 0, waterFailed = 0, waterMeshCount = 0, outerSea = null, outerFloor = null, outerFloorMat = null, OUTER_FLOOR_Y = -2;      // yards: the island's sky floor (WorldMapTerrain.SkyFloor), so no tile edge shows a step
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
    if (outerFloor) { scene.remove(outerFloor); outerFloor = null; }
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
    // 2.93: a sea floor under the open sea, lit like a map tile's empty floor (the same colour and material), so the open sea and the sea inside the tiles
    // (an island's sky chunks, the deep water off a coast) are the same blue - before, the open sea showed the scene background through the water
    // the tile pictures are sRGB textures: the floor's colour is the map's sea colour (the pictures' empty sea) taken to linear the same way
    if (!outerFloorMat) outerFloorMat = new THREE.MeshLambertMaterial({ color: 0xffffff });
    outerFloorMat.color.set((cfg && /^#[0-9a-fA-F]{6}$/.test(cfg.sea || '')) ? cfg.sea : '#081110').convertSRGBToLinear();
    outerFloor = new THREE.Mesh(g, outerFloorMat); outerFloor.position.y = OUTER_FLOOR_Y * prefs.exag; outerFloor.renderOrder = 1; outerFloor.frustumCulled = false; outerFloor.name = 'outerFloor'; scene.add(outerFloor);
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
    // following you in the air (a flight, a high floor): the camera looks at your dot, not at the ground under it
    if (follow && meDotIt && meDotIt.zs != null && meDotIt.y > gy && Math.abs(cam.tx - meDotIt.x) + Math.abs(cam.tz - meDotIt.z) < 60) gy = meDotIt.y;
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
  // a mark with its own height (the live dot and events in the air, on a floor or on the water: it.zs, yards) keeps it; everything else stands on the ground
  function placeDom() { domItems.forEach(function (it) { it.y = it.zs != null ? it.zs * prefs.exag : it.water ? surfaceYE(it.x, it.z) : groundYE(it.x, it.z); }); }

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
      items.push({ line: line, x: dr.x, z: dr.z, lift: 22, g: it.g || null, learned: it.learned || '' });
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
    placeDom(); if (meLive()) meDrawLines(); if (mList.length) membersDraw(true); if (outerFloor) outerFloor.position.y = OUTER_FLOOR_Y * prefs.exag; dirty = true;
  }

  // ---------------------------------------------------------------- DOM overlays: labels and flight masters follow their ground spot
  var tmpV = new THREE.Vector3(), tmpV2 = new THREE.Vector3();
  // the lift of your own dot (2.93): close by it sits on its solved height like the paw prints (half a yard up, as they are), further out it rises to the
  // other markers' 12 yards so a hill in front does not hide it; the other markers keep their 12 yards (labels 40)
  function meLift(dist) { return dist <= 400 ? 0.5 : Math.min(12, 0.5 + (dist - 400) * 0.0072); }
  function updateOverlays() {
    var w = window.innerWidth, h = window.innerHeight, shown = 0;
    var eye = camera.position, far = cam.dist, ml = meLift(cam.dist);      // a member's dot ('mem', 2.98) sits like yours, just under it
    for (var i = 0; i < domItems.length; i++) {
      var it = domItems[i], e = it.el;
      var show = !!on[it.layer] && (!it.g || !!on[it.g]);
      if (show) {
        var meLike = it.kind === 'me' || it.kind === 'mem', lift = it.kind === 'label' ? 40 : meLike ? ml : 12; it.lift = lift;
        tmpV.set(it.x, it.y + lift, it.z);
        var dx = tmpV.x - eye.x, dy = tmpV.y - eye.y, dz = tmpV.z - eye.z, dist = Math.sqrt(dx * dx + dy * dy + dz * dz);
        tmpV.applyMatrix4(camera.matrixWorldInverse);
        if (tmpV.z >= 0) show = false;
        else {
          if (it.kind === 'label') { show = it.sm ? dist < 14000 && dist > 120 : dist > 1800 && dist < 90000; }
          else show = meLike || dist < 60000;
          tmpV.applyMatrix4(camera.projectionMatrix);
          var sx = (tmpV.x * .5 + .5) * w, sy = (-tmpV.y * .5 + .5) * h;
          if (sx < -80 || sx > w + 80 || sy < -40 || sy > h + 40) show = false;
          if (show && it.lab) { var near = dist < 9000; if (near !== it.near) { it.lab.classList.toggle('near', near); it.near = near; } }
          if (show && it.arrow && it.prev) {
            // the heading arrow points along your last step as it looks from this camera
            tmpV2.set(it.prev[0], it.y + lift, it.prev[1]).applyMatrix4(camera.matrixWorldInverse).applyMatrix4(camera.projectionMatrix);
            var px = (tmpV2.x * .5 + .5) * w, py = (-tmpV2.y * .5 + .5) * h, ang = Math.atan2(sx - px, -(sy - py)) * 180 / Math.PI;
            it.arrow.style.transform = 'rotate(' + Math.round(ang) + 'deg)';
          }
          if (show) { e.style.transform = 'translate(' + sx.toFixed(1) + 'px,' + sy.toFixed(1) + 'px)'; e.style.zIndex = it.kind === 'me' ? '150000' : it.kind === 'mem' ? '149000' : String(Math.round(100000 - dist / 2)); e.style.opacity = it.kind === 'label' ? String(clamp(1.4 - dist / (it.sm ? 14000 : 70000), .35, 1)) : it.op != null ? String(it.op.toFixed(2)) : '1'; shown++; }
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
      '<div class="v3r"><span>Height</span><input id="v3exag" type="range" min="0.5" max="1.5" step="0.1" aria-label="Height exaggeration (1 = true height)"><b id="v3exagv"></b></div>' +
      '<div class="v3r"><button type="button" id="v3north">North up</button><button type="button" id="v3fit">Whole continent</button></div>';
    body.appendChild(s);
    bldPanel(s);
    objPanel(s);
    p.appendChild(h); p.appendChild(body); hud.appendChild(p);
    ['mousedown', 'wheel', 'click', 'dblclick', 'contextmenu'].forEach(function (ev) { p.addEventListener(ev, function (e) { e.stopPropagation(); }); });
    function syncPrefs() {
      [].forEach.call(s.querySelectorAll('#v3detail button'), function (b) { b.classList.toggle('on', b.getAttribute('data-d') === prefs.detail); });
      $('v3exag').value = prefs.exag; $('v3exagv').textContent = prefs.exag.toFixed(1) + 'x';
    }
    [].forEach.call(s.querySelectorAll('#v3detail button'), function (b) { b.addEventListener('click', function () { prefs.detail = b.getAttribute('data-d'); applyPrefs(true); syncPrefs(); }); });
    $('v3exag').addEventListener('input', function () { prefs.exag = parseFloat(this.value) || 1; $('v3exagv').textContent = prefs.exag.toFixed(1) + 'x'; applyPrefs(false); });
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
    if (v && def && !def.child && !def.live && !layers[id] && !requested[id]) { requested[id] = true; send({ t: 'need', layer: id }); setCount(id, '...'); }
    refresh(); sendLayers();
    if (def && def.live) { if (meLive()) { meDraw(); meRow(); } if (mList.length) membersDraw(true); }
  }
  var layersTimer = 0;
  function sendLayers() { clearTimeout(layersTimer); layersTimer = setTimeout(function () { var l = []; Object.keys(on).forEach(function (k) { if (on[k] && k.indexOf('__') !== 0) l.push(k); }); send({ t: 'layers', on: l }); }, 250); }      // the page's own marks (__pin, __me) are not layers
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
        if (follow) setFollow(false, true);      // dragging (orbit or pan) stops following, like the flat map
        if (mFollow) setMemberFollow(0);
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
      var gp = follow ? null : pickGround(e.clientX, e.clientY);      // following: zoom on you, not on the cursor
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
  function onDbl(e) { var gp = pickGround(e.clientX, e.clientY); if (gp) { if (follow) setFollow(false, true); if (mFollow) setMemberFollow(0); var ll = xz2ll(gp.x, gp.z); setView(ll[0], ll[1], Math.max(cam.dist * 0.45, 120), null, null, false); sendView(); } }
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
    if (used && follow && !(k === '+' || k === '=' || k === '-' || k === '_')) setFollow(false, true);      // moving or turning the camera yourself stops following
    if (used && mFollow && !(k === '+' || k === '=' || k === '-' || k === '_')) setMemberFollow(0);
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
    stepFollow();
    if (meLines && meLineDist > 0 && Math.abs(Math.log(cam.dist / meLineDist)) > 0.3) meDrawLines();      // the ribbon keeps a few pixels wide as you zoom
    else if (meLines && window.WmPrints && window.WmPrints.zoom3d(cam.dist, window.innerHeight) === 2) meDrawLines();      // 2.93: the prints' spacing follows the zoom at once; prints on / off redraws the ribbon
    // the members' trails the same way (2.98)
    if (mList.length && mLineDist > 0 && Math.abs(Math.log(cam.dist / mLineDist)) > 0.3) membersDraw(true);
    else if (mList.length) mList.forEach(function (x) { var P = mPrints[x.id]; if (x.lines && P && P.zoom3d(cam.dist, window.innerHeight) === 2) memberLines(x); });
    if (benching) { var t = (now - benching.t0) / 1000; if (benching.last) { var dt = now - benching.last; if (dt > benching.maxDt) benching.maxDt = dt; if (dt > 33.4) benching.slow++; benching.n++; } benching.last = now; cam.az = benching.az0 + t * 24; cam.tilt = 38 + 14 * Math.sin(t * 0.9); dirty = true; }
    if (heightGen !== lastHeightGen && now - lastRefresh > 700) { lastHeightGen = heightGen; lastRefresh = now; refreshHeights(); }
    var need = dirty || inFlight > 0;
    if (!need) { if (now - fpsT0 > 2000) { fpsT0 = now; fpsFrames = 0; } return; }
    dirty = false; frameN++;
    oProbeFrame(now);
    updateCamera();
    if (now - lastSel2 > 120 || pendingBuilds) { lastSel2 = now; updateTiles(now); updateCityDetail(now); updateBuildings(now); updateObjects(now); updateGround(now); } else dirty = true;       // the tile pass is throttled: a change that arrives inside the pause (a liquid file, a texture) gets its pass on a later frame
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
    canvas.addEventListener('webglcontextlost', function (e) { e.preventDefault(); if (OBJ.level !== 'off') send({ t: 'objects', level: 'off' }); send({ t: 'webgl', ok: false, why: 'the graphics context was lost', renderer: glInfo.slice(0, 100) }); });
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
    fetch(BLD.base + '/m/' + fdid + '.wmb' + (e.rev ? '?r=' + e.rev : '')).then(function (r) { return r.ok ? r.arrayBuffer() : null; }).then(function (buf) {
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
      if (WD.shown[i]) continue;               // 3.2: shown in detail with its real textures
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
    // a model made again while the page shows it (2.89: the big cities, once, after the group rule changed) is fetched again, past the browser's cache
    if (m.fresh) m.fresh.forEach(function (id) { BLD.built[id] = true; var e = BLD.models[id]; if (e && e.state !== 'loading') { var rev = (e.rev || 0) + 1; bFree(id, e); e.rev = rev; } });
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

  // ---------------------------------------------------------------- trees & objects (M2s): "Trees & objects (experimental)" Off / Low / Medium / High (3.2)
  // The app (WorldMapObjectsHost) reads the continent's M2 models (trees, rocks, fences, carts) with their real textures into <map>/objects: tiles.bin, p/<col>_<row>.bin (32 bytes per
  // placement), m/<FileDataID>.m2b (M2Mesh: positions, normals, uvs, indices, one batch per texture / material) and t/<FileDataID>.mtx (ObjTexture: the game's DXT blocks with every mip
  // level, or RGBA). The page only fetches what the app listed as finished (ostate.all / ostate.fresh). Each pass (with the tiles, every 120 ms) takes the placements of the tiles near the
  // camera, keeps those within the level's distance of the eye and in the view, and draws them with one InstancedMesh per model (one draw call per visible batch). Beyond the distance
  // the minimap's painted trees carry the look, so a zoomed-out view costs the same as with the switch Off.
  // Leaves are cut-outs (alpha test); the alpha is raised in the far mip levels so a canopy does not thin out at a distance. Textures are uploaded as stored (no sRGB format needed:
  // the colour is turned linear in the shader). Additive parts (glows) draw without depth writes; modulate parts and parts hidden in the first frame were left out by the app.
  // Needs WebGL 2 and s3tc (every Windows GPU WebView2 runs on has both); without them the switch says why and stays Off. A probe after switching on watches the frame time
  // for 2 seconds and steps one level down when it is slow; a lost graphics context switches it Off (saved) before the app falls back to the flat map.
  var ODETAIL = { low: { dist: 150, memMb: 96, ground: 0 }, medium: { dist: 300, memMb: 192, ground: 300 }, high: { dist: 600, memMb: 384, ground: 600 } };
  var OLEVELS = ['off', 'low', 'medium', 'high'];
  var OBJ = { level: 'off', base: '', st: null, built: {}, tiles: {}, T: {}, models: {}, texs: {}, mats: {}, buckets: {}, fetching: 0, maxFetch: 6, mem: 0, texMem: 0, drawn: 0, tris: 0, inRange: 0,
    errors: 0, loaded: 0, texLoaded: 0, evicted: 0, passes: 0, gpu: null, gpuWhy: '', probe: null, stepped: '', ui: null, capOverride: null, s3tc: null };
  var oM = null, oQ = null, oE = null, oP = null, oS = null, oSph = null;
  function OD() { return ODETAIL[OBJ.level] || null; }
  function oUrl(p) { return OBJ.base + '/' + p; }
  // the graphics check: WebGL 2 and s3tc; software rendering is too slow for this
  function oGpuCheck() {
    if (OBJ.gpu !== null) return OBJ.gpu;
    if (!renderer) { OBJ.gpu = false; OBJ.gpuWhy = 'there is no 3D view'; return false; }
    var gl = renderer.getContext();
    OBJ.s3tc = gl.getExtension('WEBGL_compressed_texture_s3tc');
    if (!renderer.capabilities.isWebGL2) OBJ.gpuWhy = 'this graphics card has no WebGL 2';
    else if (!OBJ.s3tc) OBJ.gpuWhy = 'this graphics card cannot read compressed textures (s3tc)';
    else if (software) OBJ.gpuWhy = 'the 3D view runs on software rendering here';
    OBJ.gpu = !OBJ.gpuWhy;
    return OBJ.gpu;
  }

  // ---- the textures
  function oTexParse(buf) {
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
    var t, bytes = 0;
    lv.forEach(function (l) { bytes += l.data.length; });
    if (fmt === 0) {
      if (lv[0].data.length !== w * h * 4) return null;
      t = new THREE.DataTexture(lv[0].data, w, h, THREE.RGBAFormat); t.generateMipmaps = true; bytes = bytes * 4 / 3;
    } else {
      var f = fmt === 1 ? THREE.RGB_S3TC_DXT1_Format : fmt === 2 ? THREE.RGBA_S3TC_DXT1_Format : fmt === 3 ? THREE.RGBA_S3TC_DXT3_Format : fmt === 5 ? THREE.RGBA_S3TC_DXT5_Format : null;
      if (f === null) return null;
      t = new THREE.CompressedTexture(lv, w, h, f); t.generateMipmaps = false;
    }
    t.minFilter = THREE.LinearMipmapLinearFilter; t.magFilter = THREE.LinearFilter; t.anisotropy = D().aniso; t.flipY = false; t.needsUpdate = true;
    return { tex: t, bytes: bytes };
  }
  function oTexture(fdid, wrap) {
    var key = fdid + '|' + wrap, e = OBJ.texs[key];
    if (e) { e.refs++; return e; }
    e = OBJ.texs[key] = { state: 'loading', tex: null, bytes: 0, refs: 1, waiters: [] };
    fetch(oUrl('t/' + fdid + '.mtx')).then(function (r) { return r.ok ? r.arrayBuffer() : null; }).then(function (buf) {
      var r = buf ? oTexParse(buf) : null;
      if (!r) { e.state = 'none'; if (buf) OBJ.errors++; }
      else {
        r.tex.wrapS = (wrap & 1) ? THREE.RepeatWrapping : THREE.ClampToEdgeWrapping; r.tex.wrapT = (wrap & 2) ? THREE.RepeatWrapping : THREE.ClampToEdgeWrapping;
        e.tex = r.tex; e.bytes = r.bytes; e.state = 'ready'; OBJ.texMem += r.bytes; OBJ.texLoaded++;
      }
    }).catch(function () { e.state = 'none'; OBJ.errors++; }).then(function () { var w = e.waiters; e.waiters = []; w.forEach(function (f) { f(); }); dirty = true; });
    return e;
  }
  function oTexRelease(key) {
    var e = OBJ.texs[key]; if (!e) return;
    if (--e.refs > 0) return;
    if (e.tex) { try { e.tex.dispose(); } catch (x) { } OBJ.texMem -= e.bytes; }
    delete OBJ.texs[key];
  }

  // ---- the materials: the scene's lights and fog; the colour turned linear here (the textures are uploaded as stored), leaves raised in the far mips
  var O_MAP = [
    '#ifdef USE_MAP',
    '  vec4 oTex = texture2D( map, vUv );',
    '  oTex.rgb = pow( oTex.rgb, vec3( 2.2 ) );',
    '  oTex.rgb = mix( vec3( dot( oTex.rgb, vec3( 0.2126, 0.7152, 0.0722 ) ) ), oTex.rgb, uOSat ) * uOTint;',
    '  #ifdef O_CUT',
    '    vec2 oSz = vec2( textureSize( map, 0 ) ); vec2 oDx = dFdx( vUv * oSz ), oDy = dFdy( vUv * oSz );',
    '    oTex.a *= 1.0 + 0.12 * max( 0.0, 0.5 * log2( max( dot( oDx, oDx ), dot( oDy, oDy ) ) ) );',
    '  #endif',
    '  diffuseColor *= oTex;',
    '#endif'].join('\n');
  // the edge of the level's circle around the spot you look at fades out over its last quarter: a fine screen dither (no sorting, works with the cut-outs)
  var O_FADE_V = [
    '#include <begin_vertex>',
    '#ifdef USE_INSTANCING',
    '  vec3 oAt = ( modelMatrix * instanceMatrix * vec4( 0.0, 0.0, 0.0, 1.0 ) ).xyz;',
    '#else',
    '  vec3 oAt = ( modelMatrix * vec4( 0.0, 0.0, 0.0, 1.0 ) ).xyz;',
    '#endif',
    '  vOFade = clamp( ( uOLim - distance( oAt.xz, uOFocus.xz ) ) / ( uOLim * 0.25 ), 0.0, 1.0 );'].join('\n');
  var O_FADE_F = 'if ( vOFade < 0.999 && fract( 52.9829189 * fract( dot( gl_FragCoord.xy, vec2( 0.06711056, 0.00583715 ) ) ) ) > vOFade ) discard;';
  // the look of the models' own textures next to the minimap ground (the game lights them with each zone's own light colours, which this page does not have):
  // saturation and a tint (linear). Chosen with the owner on Goldshire and Redridge (3.2): a little less saturated and slightly greener, so Elwynn's yellow
  // crowns sit with the minimap ground while autumn zones keep their colours
  var O_TINT = [0.9, 0.97, 0.86];
  var OU = { uOFocus: { value: null }, uOLim: { value: 300 }, uOSat: { value: 0.85 }, uOTint: { value: null } };
  function oPatch(m, cut, noFade) {
    m.onBeforeCompile = function (sh) {
      if (!OU.uOFocus.value) OU.uOFocus.value = new THREE.Vector3();
      if (!OU.uOTint.value) OU.uOTint.value = new THREE.Vector3(O_TINT[0], O_TINT[1], O_TINT[2]);
      if (noFade) { sh.uniforms.uOSat = OU.uOSat; sh.uniforms.uOTint = OU.uOTint; sh.fragmentShader = (cut ? '#define O_CUT\n' : '') + 'uniform float uOSat; uniform vec3 uOTint;\n' + sh.fragmentShader.replace('#include <map_fragment>', O_MAP); return; }
      if (!OU.uOTint.value) OU.uOTint.value = new THREE.Vector3(O_TINT[0], O_TINT[1], O_TINT[2]);
      sh.uniforms.uOFocus = OU.uOFocus; sh.uniforms.uOLim = OU.uOLim; sh.uniforms.uOSat = OU.uOSat; sh.uniforms.uOTint = OU.uOTint;
      sh.vertexShader = 'uniform vec3 uOFocus; uniform float uOLim; varying float vOFade;\n' + sh.vertexShader.replace('#include <begin_vertex>', O_FADE_V);
      sh.fragmentShader = (cut ? '#define O_CUT\n' : '') + 'varying float vOFade; uniform float uOSat; uniform vec3 uOTint;\n' + sh.fragmentShader.replace('#include <clipping_planes_fragment>', '#include <clipping_planes_fragment>\n  ' + O_FADE_F).replace('#include <map_fragment>', O_MAP);
    };
    m.customProgramCacheKey = function () { return (cut ? 'o-cut' : 'o') + (noFade ? '-nf' : ''); };
  }
  function oMaterial(b, te, noFade) {
    var tex = te && te.state === 'ready' ? te.tex : null;
    var key = (tex ? b.tex + '|' + b.wrap : 'rgb' + b.rgb) + '|' + b.kind + '|' + (b.two ? 1 : 0) + '|' + (b.unlit ? 1 : 0) + '|' + b.ref + (noFade ? '|nf' : '');
    var m = OBJ.mats[key];
    if (m) return m;
    var col = new THREE.Color(1, 1, 1);
    if (!tex) { var c = b.rgb >= 0 ? b.rgb : 0x8a7a60; col.setRGB(((c >> 16) & 255) / 255, ((c >> 8) & 255) / 255, (c & 255) / 255); col.convertSRGBToLinear(); }
    // the scene's lights add up to about 1.25 on a face turned to the sun (made for the minimap pictures, which are darker): the models' own textures are brought down to match
    if (b.kind !== 'add' && !b.unlit) col.multiplyScalar(0.78);
    var side = b.two ? THREE.DoubleSide : THREE.FrontSide;
    if (b.kind === 'add') m = new THREE.MeshBasicMaterial({ map: tex, color: col, side: side, transparent: true, depthWrite: false, blending: THREE.AdditiveBlending });
    else if (b.unlit) m = new THREE.MeshBasicMaterial({ map: tex, color: col, side: side, alphaTest: b.kind === 'cut' ? b.ref : 0 });
    else m = new THREE.MeshLambertMaterial({ map: tex, color: col, side: side, alphaTest: b.kind === 'cut' ? b.ref : 0 });
    oPatch(m, b.kind === 'cut' && !!tex, noFade);
    OBJ.mats[key] = m;
    return m;
  }

  // ---- a model file
  function oParse(buf) {
    var dv = new DataView(buf);
    if (buf.byteLength < 12 || dv.getUint32(0, true) !== 0x3142324D) return null;          // "M2B1"
    var jl = dv.getInt32(4, true);
    if (jl <= 0 || 8 + jl > buf.byteLength) return null;
    var meta = JSON.parse(new TextDecoder().decode(new Uint8Array(buf, 8, jl)));
    var nv = meta.nv | 0, ni = meta.ni | 0, o = 8 + ((jl + 3) & ~3);
    if (nv <= 0 || nv > 2000000 || ni <= 0 || ni % 3 !== 0 || !Array.isArray(meta.batches)) return null;
    var need = o + nv * 12 + nv * 4 + nv * 8 + ni * (meta.idx16 ? 2 : 4);
    if (need > buf.byteLength) return null;
    var pos = new Float32Array(buf, o, nv * 3); o += nv * 12;
    var nor = new Int8Array(buf, o, nv * 4); o += nv * 4;
    var uv = new Float32Array(buf, o, nv * 2); o += nv * 8;
    var idx = meta.idx16 ? new Uint16Array(buf, o, ni) : new Uint32Array(buf, o, ni);
    var g = new THREE.BufferGeometry();
    var free = function () { this.array = null; };
    var a = new THREE.BufferAttribute(pos, 3), n = new THREE.BufferAttribute(nor, 4, true), u = new THREE.BufferAttribute(uv, 2), ix = new THREE.BufferAttribute(idx, 1);
    [a, n, u, ix].forEach(function (x) { x.onUpload(free); });
    // the normal is 4 signed bytes (the 4th is padding): three reads the first 3 of a 4-wide attribute as the normal
    g.setAttribute('position', a); g.setAttribute('normal', n); g.setAttribute('uv', u); g.setIndex(ix);
    var mn = meta.min || [0, 0, 0], mx = meta.max || [0, 0, 0];
    var rad = Math.max(1, Math.sqrt(Math.pow(mx[0] - mn[0], 2) + Math.pow(mx[1] - mn[1], 2) + Math.pow(mx[2] - mn[2], 2)) / 2);
    meta.batches.forEach(function (b, i) { g.addGroup(b.i0 | 0, b.n | 0, i); });
    return { meta: meta, geo: g, rad: rad, cy: (mn[1] + mx[1]) / 2, tris: ni / 3, bytes: nv * 24 + ni * (meta.idx16 ? 2 : 4), texKeys: [] };
  }
  function oFetchModel(fdid, e, url, noFade) {
    e.state = 'loading'; OBJ.fetching++;
    fetch((url || oUrl('m/' + fdid + '.m2b')) + (e.rev ? '?r=' + e.rev : '')).then(function (r) { return r.ok ? r.arrayBuffer() : null; }).then(function (buf) {
      var m = buf ? oParse(buf) : null;
      if (!m) { e.state = 'none'; if (buf) OBJ.errors++; return; }
      // its textures, then the materials (a texture that does not come is a stand-in colour)
      var waits = 0, done = function () {
        if (--waits > 0) return;
        m.mats = m.meta.batches.map(function (b, i) { return oMaterial(b, m.tes[i], noFade); });
        e.m = m; e.state = 'ready'; OBJ.mem += m.bytes; OBJ.loaded++; dirty = true;
      };
      m.tes = m.meta.batches.map(function (b) {
        if (!(b.tex > 0)) return null;
        var te = oTexture(b.tex, b.wrap | 0); m.texKeys.push(b.tex + '|' + (b.wrap | 0));
        if (te.state === 'loading') { waits++; te.waiters.push(done); }
        return te;
      });
      waits++; done();
    }).catch(function () { e.state = 'none'; OBJ.errors++; }).then(function () { OBJ.fetching--; dirty = true; });
  }
  function oFreeModel(fdid, e) {
    var b = OBJ.buckets[fdid];
    if (b) { scene.remove(b.mesh); b.mesh.dispose(); delete OBJ.buckets[fdid]; }
    if (e.m) { e.m.geo.dispose(); OBJ.mem -= e.m.bytes; e.m.texKeys.forEach(oTexRelease); e.m = null; }
    e.state = 'idle';
  }

  // ---- the placements of a tile
  function oLoadTile(key, T) {
    T.state = 'loading';
    fetch(oUrl('p/' + Math.floor(key / 64) + '_' + (key % 64) + '.bin')).then(function (r) { return r.ok ? r.arrayBuffer() : null; }).then(function (buf) {
      if (!buf || buf.byteLength < 8) { T.state = 'none'; return; }
      var dv = new DataView(buf);
      if (dv.getUint32(0, true) !== 0x3250324D) { T.state = 'none'; OBJ.errors++; return; }          // "M2P2"
      var n = dv.getInt32(4, true);
      if (n < 0 || 8 + n * 36 !== buf.byteLength) { T.state = 'none'; OBJ.errors++; return; }
      T.n = n; T.fdid = new Int32Array(n); T.a = new Float32Array(n * 7); T.fl = new Uint8Array(n);
      for (var i = 0; i < n; i++) { var o = 8 + i * 36; T.fdid[i] = dv.getUint32(o, true); for (var k = 0; k < 7; k++) T.a[i * 7 + k] = dv.getFloat32(o + 4 + k * 4, true); T.fl[i] = dv.getUint32(o + 32, true) & 255; }
      T.state = 'ready';
    }).catch(function () { T.state = 'none'; OBJ.errors++; }).then(function () { dirty = true; });
  }

  function oHideAll() {
    Object.keys(OBJ.buckets).forEach(function (k) { var b = OBJ.buckets[k]; b.mesh.visible = false; b.mesh.count = 0; });
    OBJ.drawn = 0; OBJ.tris = 0; OBJ.inRange = 0;
  }
  function oClear() {
    Object.keys(OBJ.models).forEach(function (k) { oFreeModel(+k, OBJ.models[k]); });
    Object.keys(OBJ.texs).forEach(function (k) { var e = OBJ.texs[k]; if (e.tex) try { e.tex.dispose(); } catch (x) { } });
    Object.keys(OBJ.mats).forEach(function (k) { OBJ.mats[k].dispose(); });
    OBJ.models = {}; OBJ.texs = {}; OBJ.mats = {}; OBJ.buckets = {}; OBJ.T = {}; OBJ.mem = 0; OBJ.texMem = 0; OBJ.drawn = 0; OBJ.tris = 0;
  }

  // ---- the pass
  function updateObjects(now) {
    if (!renderer || !scene) return;
    var od = OD();
    if (!od || !oGpuCheck() || !OBJ.base) { if (OBJ.drawn > 0 || OBJ.inRange > 0) oHideAll(); return; }
    if (!oM) { oM = new THREE.Matrix4(); oQ = new THREE.Quaternion(); oE = new THREE.Euler(0, 0, 0, 'YXZ'); oP = new THREE.Vector3(); oS = new THREE.Vector3(); oSph = new THREE.Sphere(new THREE.Vector3(), 1); }
    OBJ.passes++;
    // the level's circle is around the spot you look at (the camera's target), so a normal map view shows the trees there; a camera further than 8 x the
    // circle from a tree does not draw it (it would be a speck). The shader fades the circle's edge (OU).
    var eye = camera.position, ex = prefs.exag, lim = od.dist, DEG = Math.PI / 180, fx = cam.tx, fz = cam.tz, eyeLim = lim * 8;
    if (!OU.uOFocus.value) OU.uOFocus.value = new THREE.Vector3();
    OU.uOFocus.value.set(fx, 0, fz); OU.uOLim.value = lim;
    var fr = new THREE.Frustum().setFromProjectionMatrix(new THREE.Matrix4().multiplyMatrices(camera.projectionMatrix, camera.matrixWorldInverse));
    // the eye's height over the ground limits what is in range at all (looking down from far above, nothing is)
    var lists = {}, inRange = 0, keepTiles = {};
    var c0 = Math.floor((fx - lim) / TY), c1 = Math.floor((fx + lim) / TY), r0 = Math.floor((fz - lim) / TY), r1 = Math.floor((fz + lim) / TY);
    for (var c = Math.max(0, c0); c <= Math.min(63, c1); c++) for (var r = Math.max(0, r0); r <= Math.min(63, r1); r++) {
      var key = c * 64 + r;
      if (!OBJ.tiles[key]) continue;
      // the tile's square against the distance (on the ground plane; the height is checked per placement)
      var nx = clamp(fx, c * TY, (c + 1) * TY), nz = clamp(fz, r * TY, (r + 1) * TY);
      if (Math.hypot(nx - fx, nz - fz) > lim) continue;
      keepTiles[key] = true;
      var T = OBJ.T[key] || (OBJ.T[key] = { state: 'idle' });
      if (T.state === 'idle') { oLoadTile(key, T); continue; }
      if (T.state !== 'ready') continue;
      T.last = now;
      var a = T.a, lim2 = lim * lim, eye2 = eyeLim * eyeLim;
      for (var i = 0; i < T.n; i++) {
        var px = a[i * 7], py = a[i * 7 + 1] * ex, pz = a[i * 7 + 2], gx = px - fx, gz = pz - fz;
        if (gx * gx + gz * gz > lim2) continue;
        var dx = px - eye.x, dy = py - eye.y, dz = pz - eye.z;
        if (dx * dx + dy * dy + dz * dz > eye2) continue;
        var fd = T.fdid[i];
        if (!OBJ.built[fd]) continue;
        if ((T.fl[i] & 1) && BLD.mode !== 'all') continue;          // a building's furnishing: only with its building
        inRange++;
        var e = OBJ.models[fd] || (OBJ.models[fd] = { state: 'idle', m: null, last: now });
        if (e.state === 'idle') { if (OBJ.fetching < OBJ.maxFetch) oFetchModel(fd, e); continue; }
        if (e.state !== 'ready') continue;
        var s = a[i * 7 + 6];
        oSph.center.set(px, py + e.m.cy * s, pz); oSph.radius = e.m.rad * s + 1;
        if (!fr.intersectsSphere(oSph)) continue;
        e.last = now;
        (lists[fd] || (lists[fd] = [])).push(T, i);
      }
    }
    // one InstancedMesh per model; its matrices: the placement (position with the height exaggeration, rotation YXZ as the buildings, uniform scale)
    var drawn = 0, tris = 0, used = {};
    Object.keys(lists).forEach(function (fdk) {
      var l = lists[fdk], fd = +fdk, e = OBJ.models[fd], b = OBJ.buckets[fd], cnt = l.length / 2;
      if (!b || b.cap < cnt) {
        if (b) { scene.remove(b.mesh); b.mesh.dispose(); }
        var cap = Math.max(8, Math.ceil(cnt * 1.5));
        var mesh = new THREE.InstancedMesh(e.m.geo, e.m.mats, cap);
        mesh.frustumCulled = false; mesh.instanceMatrix.setUsage(THREE.DynamicDrawUsage);
        scene.add(mesh); b = OBJ.buckets[fd] = { mesh: mesh, cap: cap };
      }
      for (var j = 0; j < cnt; j++) {
        var T = l[j * 2], i = l[j * 2 + 1], a = T.a, o = i * 7;
        oP.set(a[o], a[o + 1] * ex, a[o + 2]); oE.set(a[o + 3] * DEG, a[o + 4] * DEG, a[o + 5] * DEG, 'YXZ'); oQ.setFromEuler(oE); oS.set(a[o + 6], a[o + 6], a[o + 6]);
        oM.compose(oP, oQ, oS); b.mesh.setMatrixAt(j, oM);
      }
      b.mesh.count = cnt; b.mesh.visible = true; b.mesh.instanceMatrix.needsUpdate = true;
      used[fd] = true; drawn += cnt; tris += cnt * e.m.tris;
    });
    Object.keys(OBJ.buckets).forEach(function (k) { if (!used[k]) { var b = OBJ.buckets[k]; b.mesh.visible = false; b.mesh.count = 0; } });
    OBJ.drawn = drawn; OBJ.tris = tris; OBJ.inRange = inRange;
    // tiles far behind are let go; models not drawn for a while go when over the memory cap
    Object.keys(OBJ.T).forEach(function (k) { var T = OBJ.T[k]; if (!keepTiles[k] && T.state !== 'loading' && now - (T.last || 0) > 4000) delete OBJ.T[k]; });
    var capB = (OBJ.capOverride != null ? OBJ.capOverride : od.memMb) * 1048576;
    if (OBJ.mem + OBJ.texMem > capB) {
      var ents = Object.keys(OBJ.models).filter(function (k) { var e = OBJ.models[k]; return e.state === 'ready' && !used[k] && now - e.last > 2500; }).sort(function (x, y) { return OBJ.models[x].last - OBJ.models[y].last; });
      for (var v = 0; v < ents.length && OBJ.mem + OBJ.texMem > capB * 0.85; v++) { oFreeModel(+ents[v], OBJ.models[ents[v]]); OBJ.evicted++; }
    }
    if (OBJ.fetching > 0) dirty = true;
    oProbeTick(now);
  }

  // ---------------------------------------------------------------- the real ground near the spot you look at (3.2, the Trees & objects levels Medium and High)
  // The app makes a tile's ground file when it is first asked for (GroundService: objects/g/<col>_<row>.bin, objects/gt/<fdid>.bin; see WorldMapGround.cs for the
  // format). Within the level's ground distance of the spot you look at, a terrain tile wears a second material: the minimap picture's Lambert, patched to paint
  // each chunk with its up-to-4 game textures blended by the game's alpha maps (8 repeats per chunk, as the lab found matched the game), times the game's vertex
  // colours, fading to the picture over the last quarter of the distance. Textures share one array (256 px layers, grown when full); a tile's alpha maps and
  // colours are its own small textures. A chunk without ground keeps the picture.
  var GD = { tiles: {}, arr: null, arrData: null, cap: 0, layerOf: {}, used: 0, texs: {}, loading: 0, maxLoad: 3, shown: 0, errors: 0, made: 0, bright: 0.8 };
  var G_FS = [
    '#ifdef USE_MAP',
    '  vec4 gMini = texture2D( map, vUv );',
    '  vec3 gCol = gMini.rgb;',
    '  vec2 gTl = vGW.xz / 533.3333333 - uGTile;',
    '  float gF = smoothstep( uGLim * 0.75, uGLim, distance( vGW.xz, uOFocus.xz ) );',
    '  if ( gF < 1.0 ) {',
    '    vec2 gC = clamp( floor( gTl * 16.0 ), 0.0, 15.0 ); vec2 gLoc = gTl * 16.0 - gC;',
    '    vec4 gIds = floor( texelFetch( uGL, ivec2( gC ), 0 ) * 255.0 + 0.5 );',
    '    if ( gIds.x < 253.5 ) {',
    '      vec3 gA = texture( uGA, ( gC * 64.0 + clamp( gLoc * 64.0, 0.5, 63.5 ) ) / 1024.0 ).rgb;',
    '      vec4 gW = vec4( max( 0.0, 1.0 - ( gA.r + gA.g + gA.b ) ), gA );',
    '      if ( gIds.y > 254.5 ) gW.y = 0.0; if ( gIds.z > 254.5 ) gW.z = 0.0; if ( gIds.w > 254.5 ) gW.w = 0.0;',
    '      gW /= max( 1e-4, gW.x + gW.y + gW.z + gW.w );',
    '      vec2 gT = gTl * 128.0; vec2 gDx = dFdx( gT ), gDy = dFdy( gT );',
    '      vec3 gS = gW.x * textureGrad( uGT, vec3( gT, gIds.x ), gDx, gDy ).rgb;',
    '      if ( gW.y > 0.0 ) gS += gW.y * textureGrad( uGT, vec3( gT, gIds.y ), gDx, gDy ).rgb;',
    '      if ( gW.z > 0.0 ) gS += gW.z * textureGrad( uGT, vec3( gT, gIds.z ), gDx, gDy ).rgb;',
    '      if ( gW.w > 0.0 ) gS += gW.w * textureGrad( uGT, vec3( gT, gIds.w ), gDx, gDy ).rgb;',
    '      gS = pow( gS, vec3( 2.2 ) ) * clamp( texture( uGC, ( gTl * 128.0 + 0.5 ) / 129.0 ).rgb * ( 255.0 / 127.0 ), 0.0, 2.0 ) * uGBright;',
    '      gCol = mix( gS, gMini.rgb, gF );',
    '    }',
    '  }',
    '  diffuseColor.rgb *= gCol;',
    '#endif'].join('\n');
  function gMaterial(T, m) {
    var mat = new THREE.MeshLambertMaterial({ color: 0xffffff, map: m.mat.map });
    var u = { uGTile: { value: new THREE.Vector2(T.c, T.r) }, uGA: { value: T.alpha }, uGL: { value: T.layers }, uGC: { value: T.color }, uGT: { value: GD.arr }, uGBright: { value: GD.bright } };
    mat.userData.g = u;
    mat.onBeforeCompile = function (sh) {
      if (!OU.uOFocus.value) OU.uOFocus.value = new THREE.Vector3();
      Object.keys(u).forEach(function (k) { sh.uniforms[k] = u[k]; });
      sh.uniforms.uOFocus = OU.uOFocus; sh.uniforms.uGLim = OU.uGLim;
      sh.vertexShader = 'varying vec3 vGW;\n' + sh.vertexShader.replace('#include <begin_vertex>', '#include <begin_vertex>\n  vGW = ( modelMatrix * vec4( transformed, 1.0 ) ).xyz;');
      sh.fragmentShader = 'precision highp sampler2DArray;\nvarying vec3 vGW; uniform vec2 uGTile; uniform sampler2D uGA; uniform sampler2D uGL; uniform sampler2D uGC; uniform sampler2DArray uGT; uniform float uGBright; uniform vec3 uOFocus; uniform float uGLim;\n'
        + sh.fragmentShader.replace('#include <map_fragment>', G_FS);
    };
    mat.customProgramCacheKey = function () { return 'g'; };
    return mat;
  }

  // ---- the texture array: one 256 px layer per ground texture; grown (copied) when full, at most 192 layers
  function gArray(need) {
    if (GD.arr && need <= GD.cap) return GD.arr;
    var cap = Math.min(192, Math.max(32, GD.cap * 2, need)), px = 256 * 256 * 4;
    var data = new Uint8Array(cap * px);
    if (GD.arrData) data.set(GD.arrData.subarray(0, Math.min(GD.arrData.length, data.length)));
    var a = new THREE.DataArrayTexture(data, 256, 256, cap);
    a.format = THREE.RGBAFormat; a.wrapS = a.wrapT = THREE.RepeatWrapping; a.minFilter = THREE.LinearMipmapLinearFilter; a.magFilter = THREE.LinearFilter; a.generateMipmaps = true; a.anisotropy = D().aniso; a.needsUpdate = true;
    if (GD.arr) GD.arr.dispose();
    GD.arr = a; GD.arrData = data; GD.cap = cap;
    Object.keys(GD.tiles).forEach(function (k) { var T = GD.tiles[k]; if (T.mat) T.mat.userData.g.uGT.value = a; });
    return a;
  }
  async function gInflate(u8) {
    var s = new Blob([u8]).stream().pipeThrough(new DecompressionStream('deflate-raw'));
    return new Uint8Array(await new Response(s).arrayBuffer());
  }
  // a ground texture's layer (fetched once); -1 while it comes, 255 when it cannot
  function gLayer(fdid) {
    var e = GD.texs[fdid];
    if (e) return e.layer;
    if (GD.used >= 192) return 255;
    e = GD.texs[fdid] = { layer: -1 };
    fetch(OBJ.base + '/gt/' + fdid + '.bin').then(function (r) { return r.ok ? r.arrayBuffer() : null; }).then(function (buf) {
      if (!buf || buf.byteLength < 12) { e.layer = 255; return null; }
      var dv = new DataView(buf);
      if (dv.getUint32(0, true) !== 0x5447324D || dv.getUint16(4, true) !== 256 || dv.getUint16(6, true) !== 256) { e.layer = 255; GD.errors++; return null; }     // "M2GT"
      return gInflate(new Uint8Array(buf, 8)).then(function (px) {
        if (px.length !== 256 * 256 * 4 || GD.used >= 192) { e.layer = 255; GD.errors++; return; }
        var L = GD.used++; gArray(GD.used); GD.arrData.set(px, L * 256 * 256 * 4); GD.arrDirty = true; e.layer = L; dirty = true;
      });
    }).catch(function () { e.layer = 255; GD.errors++; }).then(function () { gTilesWaiting(); });
    return -1;
  }
  // tiles whose textures came: their layer table is filled in
  function gTilesWaiting() {
    Object.keys(GD.tiles).forEach(function (k) {
      var T = GD.tiles[k]; if (T.state !== 'textures') return;
      var ids = T.fdids.map(gLayer); if (ids.some(function (x) { return x < 0; })) return;
      var lt = new Uint8Array(16 * 16 * 4);
      for (var i = 0; i < 1024; i++) { var s = T.slots[i]; lt[i] = s === 254 ? 254 : s === 255 || s >= ids.length ? 255 : ids[s]; }
      for (var c = 0; c < 256; c++) if (lt[c * 4] === 255) lt[c * 4] = 254;           // a chunk whose first texture could not be read keeps the picture
      T.layers = new THREE.DataTexture(lt, 16, 16, THREE.RGBAFormat); T.layers.magFilter = T.layers.minFilter = THREE.NearestFilter; T.layers.needsUpdate = true;
      T.state = 'ready'; GD.made++; dirty = true;
    });
  }
  // ask the app to make the tile's files (gneed); its answer (gready) starts the fetch. Asked at most once a tile, a few at a time.
  function gAsk(key, T) { T.state = 'asked'; GD.loading++; send({ t: 'gneed', c: T.c, r: T.r }); }
  function gReady(m) {
    var T = GD.tiles[m.c * 64 + m.r];
    if (!T || T.state !== 'asked') return;
    GD.loading--;
    if (!m.ok) { T.state = 'none'; return; }
    gLoadTile(m.c * 64 + m.r, T);
  }
  function gLoadTile(key, T) {
    T.state = 'loading'; GD.loading++;
    fetch(OBJ.base + '/g/' + T.c + '_' + T.r + '.bin').then(function (r) { return r.ok ? r.arrayBuffer() : null; }).then(function (buf) {
      if (!buf || buf.byteLength < 16) { T.state = 'none'; return; }
      var dv = new DataView(buf);
      if (dv.getUint32(0, true) !== 0x3247324D) { T.state = 'none'; GD.errors++; return; }      // "M2G2"
      var n = dv.getUint16(12, true), o = 16; T.fdids = [];
      for (var i = 0; i < n; i++) { T.fdids.push(dv.getUint32(o, true)); o += 4; }
      T.slots = new Uint8Array(buf.slice(o, o + 1024)); o += 1024;
      var al = dv.getInt32(o, true); o += 4; var aZ = new Uint8Array(buf, o, al); o += al;
      var cl = dv.getInt32(o, true); o += 4; var cZ = new Uint8Array(buf, o, cl);
      return Promise.all([gInflate(aZ), gInflate(cZ)]).then(function (r) {
        var a3 = r[0], c3 = r[1];
        if (a3.length !== 1024 * 1024 * 3 || c3.length !== 129 * 129 * 3) { T.state = 'none'; GD.errors++; return; }
        var a4 = new Uint8Array(1024 * 1024 * 4), c4 = new Uint8Array(129 * 129 * 4);
        var N = 1024 * 1024;      // planar: layer 1, then 2, then 3
        for (var p = 0, q = 0; p < N; p++, q += 4) { a4[q] = a3[p]; a4[q + 1] = a3[N + p]; a4[q + 2] = a3[2 * N + p]; a4[q + 3] = 255; }
        for (var p2 = 0, q2 = 0; p2 < c3.length; p2 += 3, q2 += 4) { c4[q2] = c3[p2]; c4[q2 + 1] = c3[p2 + 1]; c4[q2 + 2] = c3[p2 + 2]; c4[q2 + 3] = 255; }
        T.alpha = new THREE.DataTexture(a4, 1024, 1024, THREE.RGBAFormat); T.alpha.magFilter = T.alpha.minFilter = THREE.LinearFilter; T.alpha.needsUpdate = true;
        T.color = new THREE.DataTexture(c4, 129, 129, THREE.RGBAFormat); T.color.magFilter = T.color.minFilter = THREE.LinearFilter; T.color.needsUpdate = true;
        T.state = 'textures'; gArray(1);
        T.fdids.forEach(gLayer); gTilesWaiting();
      });
    }).catch(function () { T.state = 'none'; GD.errors++; }).then(function () { GD.loading--; dirty = true; });
  }
  function gFreeTile(key, T) {
    var m = meshes[key];
    if (m && m.mesh && T.mat && m.mesh.material === T.mat) m.mesh.material = m.mat;
    if (T.mat) T.mat.dispose(); if (T.alpha) T.alpha.dispose(); if (T.color) T.color.dispose(); if (T.layers) T.layers.dispose();
    delete GD.tiles[key];
  }
  function gClear() {
    GD.loading = 0;
    Object.keys(GD.tiles).forEach(function (k) { gFreeTile(k, GD.tiles[k]); });
    if (GD.arr) GD.arr.dispose();
    GD.arr = null; GD.arrData = null; GD.cap = 0; GD.layerOf = {}; GD.used = 0; GD.texs = {}; GD.shown = 0;
  }
  // the pass (with the trees): which tiles wear the ground; tiles far behind let it go
  function updateGround(now) {
    var od = OD(), lim = od && OBJ.gpu ? od.ground : 0;
    var fx = cam.tx, fz = cam.tz, eye = camera.position, shown = 0, keep = {};
    if (!OU.uGLim) OU.uGLim = { value: lim };
    OU.uGLim.value = lim;
    var far = lim > 0 && Math.hypot(eye.x - fx, eye.y - cam.ty, eye.z - fz) > lim * 8;
    Object.keys(meshes).forEach(function (k) {
      var m = meshes[k]; if (!m.mesh) return;
      var t = m.t, nx = clamp(fx, t.c * TY, (t.c + 1) * TY), nz = clamp(fz, t.r * TY, (t.r + 1) * TY);
      var near = lim > 0 && !far && OBJ.base && Math.hypot(nx - fx, nz - fz) <= lim;
      var T = GD.tiles[k];
      if (near) {
        keep[k] = true;
        if (!T) T = GD.tiles[k] = { c: t.c, r: t.r, state: 'idle', last: now };
        T.last = now;
        if (T.state === 'idle' && GD.loading < GD.maxLoad) gAsk(k, T);
        if (T.state === 'ready' && m.mat.map) {
          if (!T.mat) T.mat = gMaterial(T, m);
          if (T.mat.map !== m.mat.map) { T.mat.map = m.mat.map; T.mat.needsUpdate = true; }
          if (m.mesh.material !== T.mat) m.mesh.material = T.mat;
          shown++;
          return;
        }
      }
      if (T && T.mat && m.mesh.material === T.mat) m.mesh.material = m.mat;
    });
    Object.keys(GD.tiles).forEach(function (k) { var T = GD.tiles[k]; if (!keep[k] && T.state !== 'loading' && T.state !== 'asked' && now - (T.last || 0) > 5000) gFreeTile(k, T); });
    GD.shown = shown;
    // the array goes to the graphics card whole (with its mip levels): new textures are sent together, at most every 400 ms (one upload per texture made
    // frames of 100 ms while a new area came in)
    if (GD.arrDirty && GD.arr && now - (GD.uploaded || 0) > 400) { GD.arr.needsUpdate = true; GD.arrDirty = false; GD.uploaded = now; GD.uploads = (GD.uploads || 0) + 1; }
    if (GD.loading > 0 || GD.arrDirty) dirty = true;
  }
  function groundInfo() {
    var st = {}; Object.keys(GD.tiles).forEach(function (k) { var s = GD.tiles[k].state; st[s] = (st[s] || 0) + 1; });
    return { lim: OU.uGLim ? OU.uGLim.value : 0, tiles: st, shown: GD.shown, layers: GD.used, capacity: GD.cap, errors: GD.errors, made: GD.made, bright: GD.bright, uploads: GD.uploads || 0 };
  }

  // ---- the detailed buildings near the spot you look at (3.2 cities): Medium and High swap a building's simplified one-colour shape for its full-detail outside
  // with the real textures (WmoDetail: objects/w/<fdid>.m2b, made by the app on request: wneed -> wready). At most 16 at a time, nearest first; the simplified
  // instance of a building shown in detail is left out of the buildings' pass (WD.shown).
  var WD = { models: {}, meshes: {}, shown: {}, asked: 0, maxAsk: 2, maxShown: 16, drawn: 0, tris: 0 };
  function wReady(m) {
    var e = WD.models[m.fdid];
    if (!e || e.state !== 'asked') return;
    WD.asked--;
    if (!m.ok) { e.state = 'none'; return; }
    oFetchModel(m.fdid, e, oUrl('w/' + m.fdid + '.m2b'), true);
  }
  function updateCityDetail(now) {
    var od = OD(), lim = od && OBJ.gpu ? od.ground : 0, pl = BLD.pl;
    var want = {};
    if (lim > 0 && pl && BLD.mode !== 'off' && OBJ.base) {
      var fx = cam.tx, fz = cam.tz, eye = camera.position, cand = [];
      if (Math.hypot(eye.x - fx, eye.y - cam.ty, eye.z - fz) <= lim * 8)
        for (var i = 0; i < pl.n; i++) {
          if (BLD.mode === 'bridges' && (pl.flags[i] & 1) === 0) continue;
          var d = Math.hypot(pl.cx[i] - fx, pl.cz[i] - fz) - pl.rad[i];
          if (d <= lim) cand.push({ i: i, d: d });
        }
      cand.sort(function (a, b) { return a.d - b.d; });
      for (var c = 0; c < cand.length && c < WD.maxShown; c++) {
        var ci = cand[c].i, fd = pl.fdid[ci], e = WD.models[fd] || (WD.models[fd] = { state: 'idle', m: null, last: now });
        e.last = now;
        if (e.state === 'idle' && WD.asked < WD.maxAsk) { e.state = 'asked'; WD.asked++; send({ t: 'wneed', fdid: fd }); }
        if (e.state === 'ready') want[ci] = true;
      }
    }
    // the meshes: made for the wanted ones, removed for the rest
    var DEG = Math.PI / 180, ex = prefs.exag, tris = 0, n = 0;
    Object.keys(want).forEach(function (k) {
      var i = +k, fd = pl.fdid[i], e = WD.models[fd], mm = WD.meshes[i];
      if (!mm || mm.geometry !== e.m.geo) {
        if (mm) scene.remove(mm);
        mm = WD.meshes[i] = new THREE.Mesh(e.m.geo, e.m.mats); mm.matrixAutoUpdate = false; mm.frustumCulled = false; scene.add(mm); mm.userData.ex = -1;
      }
      if (mm.userData.ex !== ex) {
        mm.matrix.compose(new THREE.Vector3(pl.x[i], pl.y[i] * ex, pl.z[i]), new THREE.Quaternion().setFromEuler(new THREE.Euler(pl.rx[i] * DEG, pl.ry[i] * DEG, pl.rz[i] * DEG, 'YXZ')), new THREE.Vector3(pl.s[i], pl.s[i], pl.s[i]));
        mm.matrixWorld.copy(mm.matrix); mm.userData.ex = ex;
      }
      tris += e.m.tris; n++;
    });
    Object.keys(WD.meshes).forEach(function (k) { if (!want[k]) { scene.remove(WD.meshes[k]); delete WD.meshes[k]; } });
    WD.shown = want; WD.drawn = n; WD.tris = tris;
    // models not wanted for a while are let go (their textures with them)
    Object.keys(WD.models).forEach(function (k) { var e = WD.models[k]; if (e.state === 'ready' && now - e.last > 8000) { var used = false; Object.keys(WD.meshes).forEach(function (j) { if (pl && pl.fdid[j] === +k) used = true; }); if (!used) { oFreeModel(+k, e); e.state = 'idle'; } } });
  }
  function wClear() {
    Object.keys(WD.meshes).forEach(function (k) { scene.remove(WD.meshes[k]); });
    Object.keys(WD.models).forEach(function (k) { var e = WD.models[k]; if (e.m) oFreeModel(+k, e); });
    WD.models = {}; WD.meshes = {}; WD.shown = {}; WD.asked = 0; WD.drawn = 0; WD.tris = 0;
  }

  // ---- the probe: after a switch on (or up), the frame time is watched for 2 seconds once the models are in; slow -> one level down, said in a toast
  function oProbeStart() { OBJ.probe = { since: 0, frames: [], last: 0 }; }
  function oProbeFrame(now) {
    var p = OBJ.probe; if (!p || !p.since) return;
    if (document.visibilityState !== 'visible') { p.frames = []; p.last = 0; return; }        // a hidden page's frames say nothing
    if (p.last) p.frames.push(now - p.last);
    p.last = now; dirty = true;                       // drawn every frame while it measures
  }
  function oProbeTick(now) {
    var p = OBJ.probe; if (!p || !OD()) return;
    if (!p.since) { if (OBJ.fetching === 0 && OBJ.drawn > 0) { p.since = now; dirty = true; } return; }
    if (now - p.since < 2000 || p.frames.length < 20) return;
    var f = p.frames.slice().sort(function (x, y) { return x - y; }), med = f[Math.floor(f.length / 2)];
    OBJ.probe = null; OBJ.lastProbeMs = Math.round(med * 10) / 10;
    if (med > 34) {
      var i = OLEVELS.indexOf(OBJ.level), down = OLEVELS[Math.max(0, i - 1)];
      OBJ.stepped = OBJ.level + '>' + down;
      toast(down === 'off' ? 'Trees & objects are switched off: the 3D view got too slow with them (' + Math.round(med) + ' ms a frame).'
        : 'Trees & objects stepped down to ' + down.charAt(0).toUpperCase() + down.slice(1) + ': the 3D view got slow (' + Math.round(med) + ' ms a frame).', false);
      oSwitch(down, true);
    }
  }

  // ---- the panel: the switch and the card
  function objPanel(s) {
    var row = el('div', 'v3r bl', '<span>Trees &amp; ground <i class="exp">experimental</i></span>');
    var seg = el('div', 'v3r'); seg.innerHTML = '<span class="seg wide" id="v3obj"><button type="button" data-o="off" title="The minimap ground and the buildings, as before">Off</button><button type="button" data-o="low" title="Trees and objects within 150 yards of the spot you look at">Low</button><button type="button" data-o="medium" title="Trees, objects and the real ground within 300 yards of the spot you look at">Medium</button><button type="button" data-o="high" title="Trees, objects and the real ground within 600 yards (a capable graphics card)">High</button></span>';
    var note = el('div', 'v3n'); note.hidden = true;
    var card = el('div', 'v3b'); card.hidden = true;
    s.appendChild(row); s.appendChild(seg); s.appendChild(note); s.appendChild(card);
    [].forEach.call(seg.querySelectorAll('button'), function (b) { b.addEventListener('click', function () { oSwitch(b.getAttribute('data-o')); }); });
    OBJ.ui = { seg: seg, card: card, note: note };
    oSync();
  }
  function oSwitch(level, auto) {
    if (OLEVELS.indexOf(level) < 0 || level === OBJ.level) return;
    if (level !== 'off' && !oGpuCheck()) { oSync(); return; }
    var up = OLEVELS.indexOf(level) > OLEVELS.indexOf(OBJ.level);
    OBJ.level = level; dirty = true; lastSel2 = 0;
    if (level === 'off') { oHideAll(); OBJ.probe = null; }
    else if (up && !auto) oProbeStart();
    oSync();
    send({ t: 'objects', level: level });
  }
  function oSync() {
    if (!OBJ.ui) return;
    var ok = !renderer || oGpuCheck();
    [].forEach.call(OBJ.ui.seg.querySelectorAll('button'), function (b) {
      var lv = b.getAttribute('data-o'), on = lv === OBJ.level;
      b.classList.toggle('on', on); b.setAttribute('aria-pressed', String(on)); b.disabled = !ok && lv !== 'off';
    });
    OBJ.ui.note.hidden = ok; OBJ.ui.note.textContent = ok ? '' : 'Not available: ' + OBJ.gpuWhy + '.';
    oCard();
  }
  function oCard() {
    if (!OBJ.ui) return;
    var c = OBJ.ui.card, st = OBJ.st, ph = st ? st.phase : 'idle', html = '', cls = 'v3b', acts = [];
    if (OBJ.level === 'off' || !st) { c.hidden = true; c.innerHTML = ''; return; }
    if (ph === 'scanning') html = '<div class="bt">Counting the trees and objects...</div><div class="bs">This only looks at where they stand.</div>';
    else if (ph === 'ask') {
      html = '<div class="bt">' + esc(st.card || 'Show trees and objects in 3D.') + '</div><div class="bs">Experimental: they need a capable graphics card. Nothing is uploaded and nothing is written to the WoW folder.</div>';
      acts = [{ id: 'build', text: st.done > 0 ? 'Continue' : 'Build now', primary: true }, { id: 'later', text: 'Not now' }];
    } else if (ph === 'building' || ph === 'paused') {
      var pct = st.total > 0 ? Math.min(100, st.done * 100 / st.total) : 0;
      html = '<div class="bt">' + esc(st.text || '') + '</div><div class="bar"><i style="width:' + pct.toFixed(1) + '%"></i></div>';
      acts = [{ id: ph === 'paused' ? 'resume' : 'pause', text: ph === 'paused' ? 'Resume' : 'Pause' }, { id: 'cancel', text: 'Cancel' }];
    } else if (ph === 'done') {
      html = '<div class="bs">' + fmtN(st.placements) + ' trees and objects &middot; ' + (st.bytes > 0 ? (st.bytes / 1048576).toFixed(st.bytes < 10485760 ? 1 : 0) + ' MB on disk' : 'ready') + '</div>';
    } else if (ph === 'error') {
      cls += ' err'; html = '<div class="bt">' + esc(st.error || 'The trees and objects could not be read.') + '</div>'; acts = [{ id: 'build', text: 'Try again', primary: true }];
    } else { c.hidden = true; c.innerHTML = ''; return; }
    c.className = cls; c.innerHTML = html + (acts.length ? '<div class="ba"></div>' : '');
    var ba = c.querySelector('.ba');
    acts.forEach(function (a) {
      var b = el('button', a.primary ? 'pri' : '', esc(a.text)); b.type = 'button'; b.setAttribute('data-a', a.id);
      b.addEventListener('click', function () {
        if (a.id === 'build') send({ t: 'obuild' });
        else if (a.id === 'later') oSwitch('off');
        else if (a.id === 'pause') send({ t: 'opause' });
        else if (a.id === 'resume') send({ t: 'oresume' });
        else if (a.id === 'cancel') send({ t: 'ocancel' });
      });
      ba.appendChild(b);
    });
    c.hidden = false;
  }
  function oKeys(list, into) { (list || []).forEach(function (k) { if (typeof k === 'number' && k >= 0 && k < 4096) into[k] = true; }); }
  // the app's state message
  function objState(m) {
    OBJ.st = m;
    var lv = OLEVELS.indexOf(m.level) >= 0 ? m.level : 'off';
    if (lv !== OBJ.level) { OBJ.level = lv; if (lv === 'off') oHideAll(); }
    if (m.all) { OBJ.built = {}; m.all.forEach(function (id) { OBJ.built[id] = true; }); }
    if (m.fresh) m.fresh.forEach(function (id) { OBJ.built[id] = true; var e = OBJ.models[id]; if (e && e.state !== 'loading' && e.state !== 'idle') { var rev = (e.rev || 0) + 1; oFreeModel(id, e); e.rev = rev; } });
    if (m.tiles) { OBJ.tiles = {}; oKeys(m.tiles, OBJ.tiles); }
    oSync(); dirty = true; lastSel2 = 0;
  }
  function objInit(b) {
    wClear(); oClear(); gClear();
    OBJ.st = null; OBJ.built = {}; OBJ.tiles = {}; OBJ.level = 'off'; OBJ.base = ''; OBJ.errors = 0; OBJ.probe = null; OBJ.stepped = '';
    if (!b) return;
    OBJ.base = String(b.base || '');
    OBJ.level = OLEVELS.indexOf(b.level) >= 0 ? b.level : 'off';
    (b.models || []).forEach(function (id) { OBJ.built[id] = true; });
    oKeys(b.tiles, OBJ.tiles);
    // a saved level this graphics card cannot draw is put back to Off (and saved), with the reason in the panel
    if (OBJ.level !== 'off' && renderer && !oGpuCheck()) { OBJ.level = 'off'; send({ t: 'objects', level: 'off' }); }
  }
  function objInfo() {
    var ready = 0, loading = 0; Object.keys(OBJ.models).forEach(function (k) { var s = OBJ.models[k].state; if (s === 'ready') ready++; else if (s === 'loading') loading++; });
    var meshes = 0, visible = 0; Object.keys(OBJ.buckets).forEach(function (k) { meshes++; if (OBJ.buckets[k].mesh.visible) visible++; });
    var tiles = 0; Object.keys(OBJ.T).forEach(function (k) { if (OBJ.T[k].state === 'ready') tiles++; });
    var st = OBJ.st || {};
    return { level: OBJ.level, phase: st.phase || '', base: OBJ.base, built: Object.keys(OBJ.built).length, tilesListed: Object.keys(OBJ.tiles).length, tilesLoaded: tiles, inRange: OBJ.inRange, drawn: OBJ.drawn, tris: OBJ.tris,
      modelsReady: ready, modelsLoading: loading, meshes: meshes, visibleMeshes: visible, textures: Object.keys(OBJ.texs).length, texLoaded: OBJ.texLoaded, materials: Object.keys(OBJ.mats).length,
      memMb: Math.round((OBJ.mem + OBJ.texMem) / 1048576 * 10) / 10, evicted: OBJ.evicted, errors: OBJ.errors, passes: OBJ.passes, gpu: OBJ.gpu, gpuWhy: OBJ.gpuWhy, probing: !!OBJ.probe, lastProbeMs: OBJ.lastProbeMs || 0, stepped: OBJ.stepped,
      card: OBJ.ui ? !OBJ.ui.card.hidden : false, cardText: OBJ.ui ? (function () { var t = OBJ.ui.card.querySelector('.bt') || OBJ.ui.card.querySelector('.bs'); return t ? t.textContent : ''; })() : '',
      buttons: OBJ.ui ? [].map.call(OBJ.ui.card.querySelectorAll('button'), function (b) { return b.textContent; }) : [], note: OBJ.ui && !OBJ.ui.note.hidden ? OBJ.ui.note.textContent : '', drawCalls: renderer ? renderer.info.render.calls : 0,
      ground: groundInfo(), cities: { shown: WD.drawn, tris: WD.tris, models: Object.keys(WD.models).filter(function (k) { return WD.models[k].state === 'ready'; }).length, asked: WD.asked } };
  }

  // ---------------------------------------------------------------- the live position layer ("me") in 3D: the trail draped over the ground, your dot, the events, Follow me
  // The same message and rules as the flat page (worldmap.js "the live position layer"): only drawn while the app sends it, nothing stored here. The trail is a ribbon a few
  // pixels wide (its width follows the camera distance), cut into one-minute bands that fade over the 10 minutes; the dot and the event marks are DOM marks on the ground.
  // Follow me keeps the camera's target on you, easing to each new position; dragging (orbit or pan) or the arrow / rotate keys stop it, zooming does not.
  // 2.98: the Lodge members are "walkers" too and go through the same code as you: walkerLines (ribbon + prints at the solved heights), dotItem (the dot with
  // its heading arrow at your height rules: a floor, the water, a flight).
  var ME_STATES = { off: 1, none: 1, ok: 1, elsewhere: 1, unavailable: 1, notshared: 1, oldhub: 1 };
  var me = null, meTrail = [], meAt = 0, meLines = null, meLineDist = 0, meCard = null, follow = false, followTo = null, meDotIt = null, meEvIts = [];
  function finN(v) { return typeof v === 'number' && isFinite(v); }
  // the height the app worked out (LiveElevation): yards when it is not the terrain (a floor, the water, the air), else null = the page's own ground
  function meZ(z, s) { return finN(z) && (s === 1 || s === 2 || s === 3) && Math.abs(z) < 20000 ? z : null; }
  function llOk(a) { return a && a.length === 2 && finN(a[0]) && finN(a[1]) && a[0] <= 0 && a[0] >= -256 && a[1] >= 0 && a[1] <= 256; }
  function colorOk(c) { return /^#[0-9a-fA-F]{6}$/.test(c || '') ? c : '#e8e6e3'; }
  function meEvents(list) {
    var out = [], kinds = (window.WmIcons && window.WmIcons.evKinds) || {};
    (Array.isArray(list) ? list.slice(0, 200) : []).forEach(function (e) {
      if (!e || !llOk([e.lat, e.lng]) || !finN(e.age) || !kinds[e.k]) return;
      out.push({ ll: [e.lat, e.lng], h: meZ(e.z, e.zs), age: Math.max(0, Math.min(600, e.age)), k: e.k, name: String(e.name || '').slice(0, 80), line: String(e.line || '').slice(0, 80),
        c: colorOk(e.c), link: typeof e.link === 'string' ? e.link.slice(0, 40) : '', at: String(e.at || '').slice(0, 5) });
    });
    return out;
  }
  function evWhen(e, extra) { var a = e.age + (extra || 0), mins = Math.round(a / 60); return (e.at ? e.at + ' · ' : '') + (a < 60 ? 'just now' : mins + ' min ago') + (e.link ? ' · click to open' : ''); }
  function evOpacity(age) { return Math.max(0.3, 1 - Math.min(600, age) / 600 * 0.7); }
  function meLive() { return !!(me && me.state !== 'off' && me.state !== 'none'); }
  function meRow() {
    var r = panelRows.trailevents;
    if (r) { r.row.style.display = meLive() ? '' : 'none'; setCount('trailevents', meLive() && me.events ? String(me.events.length) : ''); }
    if (window.WmPrints) window.WmPrints.row(panelRows, meLive() || mList.length > 0);
  }
  // the trail of a "me" message or of a member in "members": points {ll, age, brk, h, k} (k = the index into the app's mv / od)
  function trailOf(m) {
    var t = Array.isArray(m.trail) ? m.trail.slice(0, 2400) : [], pts = [], ev = Array.isArray(m.elev) ? m.elev : [];
    for (var i = 0; i + 3 < t.length; i += 4) { var p = [t[i], t[i + 1]]; if (llOk(p) && finN(t[i + 2])) pts.push({ ll: p, age: Math.max(0, Math.min(600, t[i + 2])), brk: t[i + 3] === 1, h: meZ(ev[i / 2], ev[i / 2 + 1]), k: i / 4 }); }
    return pts;
  }
  function meMsg(m) {
    var st = ME_STATES[m.state] ? m.state : 'none';
    if (st === 'off' || st === 'none') { me = { state: st }; meClear(); meRow(); return; }
    me = { state: st, name: String(m.name || '').slice(0, 48), color: colorOk(m.color), notice: String(m.notice || '').slice(0, 120),
      howto: m.howto === true, howtoText: String(m.howtoText || 'How to turn it on').slice(0, 40), here: llOk(m.here) ? m.here : null, last: llOk(m.last) ? m.last : null, events: meEvents(m.events),
      hh: Array.isArray(m.hz) ? meZ(m.hz[0], m.hz[1]) : null, lh: Array.isArray(m.lz) ? meZ(m.lz[0], m.lz[1]) : null, show: st === 'elsewhere' ? String(m.show || '').slice(0, 72) : '' };
    meTrail = me.trail = trailOf(m); meAt = me.at = Date.now();
    if (window.WmPrints) me.prints = window.WmPrints.take(m);      // 2.88: the paw prints (trailprints.js)
    meDraw(); cardDraw(); meRow();
    if (follow) followAim();
  }
  function dropGroup(g) { if (g) { g.children.forEach(function (c) { c.geometry.dispose(); c.material.dispose(); }); scene.remove(g); } }
  function meDropLines() { if (window.WmPrints) window.WmPrints.hide3d(); dropGroup(meLines); meLines = null; }
  function meDropDom() {
    domItems = domItems.filter(function (it) { if (it.layer === '__me' || it.layer === 'trailevents') { if (it.el.parentNode) it.el.parentNode.removeChild(it.el); return false; } return true; });
    meDotIt = null; meEvIts = [];
  }
  function meClear() {
    if (scene) meDropLines();
    meDropDom(); meTrail = [];
    cardDraw(); dirty = true;
  }
  // a ribbon along one run of the trail (points {ll, h}), `w` yards wide: over the ground where both ends are on the terrain, else from height to height
  // (a flight in the air along its route, a floor of a building, the water), a little above it
  function meRibbonPath(run) {
    var x = [], z = [], y = [], ex = prefs.exag;
    for (var k = 0; k < run.length; k++) {
      var a = run[k], pa = ll2xz(a.ll[0], a.ll[1]), b = k + 1 < run.length ? run[k + 1] : null;
      var ya = a.h != null ? a.h * ex : null;
      x.push(pa[0]); z.push(pa[1]); y.push(ya != null ? ya : surfaceYE(pa[0], pa[1]));
      if (!b) break;
      var pb = ll2xz(b.ll[0], b.ll[1]), yb = b.h != null ? b.h * ex : null, len = Math.hypot(pb[0] - pa[0], pb[1] - pa[1]), n = Math.min(400, Math.floor(len / 10));
      for (var j = 1; j <= n; j++) {
        var f = j / (n + 1), xx = pa[0] + (pb[0] - pa[0]) * f, zz = pa[1] + (pb[1] - pa[1]) * f;
        x.push(xx); z.push(zz);
        y.push(ya == null && yb == null ? surfaceYE(xx, zz) : (ya != null ? ya : surfaceYE(pa[0], pa[1])) * (1 - f) + (yb != null ? yb : surfaceYE(pb[0], pb[1])) * f);
      }
    }
    return { x: x, z: z, y: y };
  }
  function meRibbon(run, w, color, op) {
    var dr = meRibbonPath(run), n = dr.x.length; if (n < 2) return null;
    var pos = new Float32Array(n * 6), idx = [];
    for (var i = 0; i < n; i++) {
      var a = Math.max(0, i - 1), b = Math.min(n - 1, i + 1), dx = dr.x[b] - dr.x[a], dz = dr.z[b] - dr.z[a], l = Math.hypot(dx, dz) || 1, nx = -dz / l * w / 2, nz = dx / l * w / 2;
      var y = dr.y[i] + 2.5;
      pos[i * 6] = dr.x[i] + nx; pos[i * 6 + 1] = y; pos[i * 6 + 2] = dr.z[i] + nz;
      pos[i * 6 + 3] = dr.x[i] - nx; pos[i * 6 + 4] = y; pos[i * 6 + 5] = dr.z[i] - nz;
      if (i + 1 < n) { var k = i * 2; idx.push(k, k + 1, k + 2, k + 1, k + 3, k + 2); }
    }
    var g = new THREE.BufferGeometry(); g.setAttribute('position', new THREE.BufferAttribute(pos, 3)); g.setIndex(idx);
    var mesh = new THREE.Mesh(g, new THREE.MeshBasicMaterial({ color: new THREE.Color(color), transparent: true, opacity: op, depthWrite: false, side: THREE.DoubleSide, polygonOffset: true, polygonOffsetFactor: -2, fog: true }));
    mesh.frustumCulled = false; mesh.renderOrder = 5;
    return mesh;
  }
  // the trail of a walker (you, a member) as a group in the scene: the prints first (P = the walker's own renderer; where they show there is no ribbon, 2.93),
  // then the ribbon in one-minute bands, only on the older part a capped layout left without prints (lineFrom)
  function walkerLines(w, P) {
    var extra = (Date.now() - w.at) / 1000, BAND = 60, run = [], band = -1, grp = new THREE.Group(), wd = clamp(cam.dist * 0.0035, 1.2, 80), tr = w.trail || [];
    var pk = P ? P.lineK3d(on, w, cam.dist, window.innerHeight) : 1;
    if (P) P.draw3d({ THREE: THREE, scene: scene, me: w, trail: tr, extra: extra, on: on, groundYE: groundYE, surfaceYE: surfaceYE, exag: prefs.exag, dist: cam.dist, viewH: window.innerHeight, dirty: function () { dirty = true; } });
    var lf = pk < 1 && P ? P.lineFrom() : Infinity;
    function flush() {
      if (run.length < 2) return;
      if (pk === 0) { var op0 = Math.max(0.12, 0.9 * (1 - (band * BAND) / 600)), c0 = meRibbon(run, wd * 1.9, '#000000', op0 * 0.45), l0 = meRibbon(run, wd, w.color, op0); if (c0) { c0.renderOrder = 4; grp.add(c0); } if (l0) grp.add(l0); return; }
      var op = Math.max(0.12, 0.9 * (1 - (band * BAND) / 600));
      var casing = pk === 1 ? meRibbon(run, wd * 1.9, '#000000', op * 0.45) : null, line = meRibbon(run, pk === 1 ? wd : wd * 0.6, w.color, op * pk);      // pk < 1: paw prints, a faint guide
      if (casing) { casing.renderOrder = 4; grp.add(casing); } if (line) grp.add(line);
    }
    for (var i = 0; i < tr.length; i++) {
      var p = tr[i], b = Math.min(9, Math.floor((p.age + extra) / BAND));
      if (pk === 0 && !P.lineHas(w, p, lf)) { if (run.length && !p.brk) { run.push(p); flush(); } run = []; break; }      // from here on the way has prints
      if (p.brk) { flush(); run = []; band = b; }
      if (b !== band) { flush(); run = run.length ? [run[run.length - 1]] : []; band = b; }
      run.push(p);
    }
    if (pk !== 0 && w.state === 'ok' && w.here && run.length) run.push({ ll: w.here, h: w.hh });
    flush();
    scene.add(grp); dirty = true;
    return grp;
  }
  function meDrawLines() {
    if (!scene) return;
    meDropLines();
    if (!me || !meLive()) return;
    meLineDist = cam.dist;
    meLines = walkerLines(me, window.WmPrints);
  }
  // a walker's dot on the ground: class colour with a heading arrow (cls adds the member's look), grey with a label where the position is not available; at their
  // solved height (a floor, a flight), on the water's surface when swimming with no height from the app (like the ripple prints, not on the sea floor)
  function dotItem(w, at, grey, cls, layer, kind, tipf) {
    var pp = ll2xz(at[0], at[1]), prev = null, tr = w.trail || [];
    if (!grey && tr.length) { var a = tr[tr.length - 1].ll; if (Math.abs(a[0] - at[0]) + Math.abs(a[1] - at[1]) < 1e-9 && tr.length >= 2) a = tr[tr.length - 2].ll; if (Math.abs(a[0] - at[0]) + Math.abs(a[1] - at[1]) >= 1e-6) prev = ll2xz(a[0], a[1]); }
    var html = '<div class="me-dot' + (grey ? ' grey' : '') + (cls ? ' ' + cls : '') + '" style="--c:' + w.color + '">' + (prev ? '<i class="me-arrow"></i>' : '') + '</div>' + (grey ? '<div class="me-lab">' + esc(w.notice || 'Position unavailable here') + '</div>' : '');
    var dd = el('div', 'm3 ' + (kind === 'me' ? 'me3' : 'mem3'), html);
    dd.addEventListener('mouseenter', tipf); dd.addEventListener('mousemove', tipf); dd.addEventListener('mouseleave', hideTip);
    dd.addEventListener('mousedown', function (ev) { ev.stopPropagation(); });
    $('ov').appendChild(dd);
    var hz = grey ? w.lh : w.hh, wet = !grey && hz == null && !!(w.prints && (w.prints.hmv & 16));
    var it = { el: dd, x: pp[0], z: pp[1], zs: hz, water: wet, y: hz != null ? hz * prefs.exag : wet ? surfaceYE(pp[0], pp[1]) : groundYE(pp[0], pp[1]), kind: kind, layer: layer, name: w.name, prev: prev, arrow: dd.querySelector('.me-arrow') };
    on[layer] = true; domItems.push(it);
    return it;
  }
  function meDraw() {
    meDrawLines();
    meDropDom();
    if (!me || !meLive()) return;
    var extra = (Date.now() - meAt) / 1000;
    // the events (under the dot)
    var fan = on.trailevents && me.events && window.WmIcons ? window.WmIcons.evFan(me.events, me.state === 'ok' ? me.here : null) : [];
    if (on.trailevents && me.events && window.WmIcons) me.events.forEach(function (e, ei) {
      var p = ll2xz(e.ll[0], e.ll[1]), d = el('div', 'm3 me3ev', window.WmIcons.ev(e.k, e.c, null, fan[ei]));
      var tipf = function (ev) { showTip(ev.clientX, ev.clientY, e.name, e.line, evWhen(e, (Date.now() - meAt) / 1000)); };
      d.addEventListener('mouseenter', tipf); d.addEventListener('mousemove', tipf); d.addEventListener('mouseleave', hideTip);
      d.addEventListener('mousedown', function (ev) { ev.stopPropagation(); });
      if (e.link) d.addEventListener('click', function (ev) { ev.stopPropagation(); hideTip(); openLink(e.link); });
      $('ov').appendChild(d);
      var it = { el: d, x: p[0], z: p[1], zs: e.h, y: e.h != null ? e.h * prefs.exag : groundYE(p[0], p[1]), kind: 'marker', layer: 'trailevents', name: e.name, op: evOpacity(e.age + extra), ev: e };
      domItems.push(it); meEvIts.push(it);
    });
    var at = me.state === 'ok' ? me.here : me.state === 'unavailable' ? me.last : null;
    if (at) {
      var grey = me.state !== 'ok';
      meDotIt = dotItem(me, at, grey, '', '__me', 'me', function (ev) { showTip(ev.clientX, ev.clientY, me.name || 'You', grey ? (me.notice || 'Position unavailable here') : 'You are here', grey ? 'The trail pauses here' : 'Live from Elan\'s Hub (display only)'); });
      meDotIt.name = 'me';
    }
    dirty = true;
  }
  // the card at the bottom right (the same texts as the flat page): your name, notice, Show <map>, How to turn it on, Follow me; what is followed; and (2.98)
  // "Lodge, live": one row per member who shares their position with you, on any map - click to follow them (another map opens first), click again to stop
  function cardDraw() {
    var live = meLive();
    if (!inited || (!live && !mRows.length)) { if (meCard && meCard.parentNode) meCard.parentNode.removeChild(meCard); meCard = null; return; }
    if (!meCard) {
      meCard = el('div', 'wm-me v3me');
      ['mousedown', 'wheel', 'click', 'dblclick', 'contextmenu'].forEach(function (ev) { meCard.addEventListener(ev, function (e) { e.stopPropagation(); }); });
      document.body.appendChild(meCard);
    }
    var d = meCard, keep = d.querySelector('.mem-rows'), scroll = keep ? keep.scrollTop : 0; d.innerHTML = '';
    if (live) {
      var head = el('div', 'me-h'); var sw = el('span', 'me-sw'); sw.style.background = me.color; head.appendChild(sw); head.appendChild(el('b', '', esc(me.name || 'You')));
      d.appendChild(head);
      if (me.state !== 'ok' && me.notice) d.appendChild(el('div', 'me-n' + (me.state === 'unavailable' ? '' : ' calm'), esc(me.notice)));
      if (me.show) { var sb = el('button', 'me-show', esc(me.show)); sb.type = 'button'; sb.addEventListener('click', function () { send({ t: 'showmap' }); }); d.appendChild(sb); }      // 2.91: open the world map of the map you are on
      if (me.howto) { var a = el('button', 'me-link', esc(me.howtoText)); a.type = 'button'; a.addEventListener('click', function () { send({ t: 'howto' }); }); d.appendChild(a); }
      if (me.state === 'ok' || me.state === 'unavailable') {
        var f = el('button', 'me-follow' + (follow ? ' on' : ''), follow ? 'Following you' : 'Follow me'); f.type = 'button'; f.setAttribute('aria-pressed', String(follow));
        f.addEventListener('click', function () { setFollow(!follow, true); });
        d.appendChild(f);
      }
    }
    var fm = mFollow && rowById(mFollow);
    d.appendChild(el('div', 'me-fl', esc(follow ? 'Following you' : fm ? 'Following ' + fm.name : 'Not following')));
    if (mRows.length) {
      d.appendChild(el('div', 'mem-h', 'Lodge, live'));
      var box = el('div', 'mem-rows');
      mRows.forEach(function (r) {
        var b = el('button', 'mem-row' + (r.id === mFollow ? ' on' : '')); b.type = 'button'; b.setAttribute('aria-pressed', String(r.id === mFollow)); b.setAttribute('data-id', String(r.id));
        var s2 = el('span', 'me-sw'); s2.style.background = r.color; b.appendChild(s2);
        b.appendChild(el('span', 'nm', esc(r.name)));
        if (!r.here) b.appendChild(el('span', 'wh', esc(r.map || 'another map')));
        b.title = (r.zone || '') + (r.zone && !r.here && r.map ? ' · ' : '') + (!r.here && r.map ? r.map : '') + (r.id === mFollow ? ' · click to stop following' : ' · click to follow');
        b.addEventListener('click', function () { rowClick(r); });
        box.appendChild(b);
      });
      d.appendChild(box); box.scrollTop = scroll;
    }
  }
  function followAim() {
    if (me && me.state === 'ok' && me.here) { var p = ll2xz(me.here[0], me.here[1]); followTo = { x: clamp(p[0], 0, 64 * TY), z: clamp(p[1], 0, 64 * TY) }; flight = null; dirty = true; }
  }
  function setFollow(v, tell) {
    follow = !!v; followTo = null;
    if (follow && mFollow) setMemberFollow(0);      // following yourself stops following a member
    if (tell) send({ t: 'follow', on: follow });
    if (follow) followAim();
    cardDraw();
  }
  // one frame of Follow me: the camera's target eases to you (about a third of a second), the bearing, tilt and distance stay yours
  function stepFollow() {
    if ((!follow && !mFollow) || !followTo || drag) return;
    var dx = followTo.x - cam.tx, dz = followTo.z - cam.tz;
    if (Math.abs(dx) + Math.abs(dz) < 0.3) { cam.tx = followTo.x; cam.tz = followTo.z; followTo = null; sendView(); return; }
    var far = Math.hypot(dx, dz) > 3000, k = far ? 1 : 0.14;
    cam.tx += dx * k; cam.tz += dz * k; dirty = true;
  }
  // the trail keeps fading while nothing new arrives (yours and the members')
  setInterval(function () { if (!inited) return; if (me && meLive() && meTrail.length) meDraw(); if (mList.length) membersDraw(true); }, 15000);

  // ---------------------------------------------------------------- the Lodge members in 3D (2.97.1 dots; drawn like you since 2.98)
  // The same message and rules as worldmap.js "members": the members on this map who share with you, each with their trail (ribbon, prints, heights) and a dot
  // with a heading arrow, and the rows of everyone who shares (any map) for the card; nothing kept. Point at a dot for the name, click it for "Follow this
  // member" (the camera keeps them in the middle until you drag, follow yourself, or they leave the map). Each member has their own prints renderer.
  var mList = [], mFollow = 0, mIts = [], mRows = [], mPrints = {}, mLineDist = 0;
  function memberById(id) { for (var i = 0; i < mList.length; i++) if (mList[i].id === id) return mList[i]; return null; }
  function rowById(id) { for (var i = 0; i < mRows.length; i++) if (mRows[i].id === id) return mRows[i]; return null; }
  function idOk(v) { return typeof v === 'number' && v > 0 && v % 1 === 0; }
  function membersMsg(m) {
    var out = [], seen = {}, now = Date.now(), old = {};
    mList.forEach(function (x) { old[x.id] = x; });
    (Array.isArray(m.list) ? m.list.slice(0, 20) : []).forEach(function (x) {
      if (!x || !idOk(x.id) || seen[x.id] || !llOk(x.ll)) return;
      seen[x.id] = 1;
      var w = { id: x.id, name: String(x.name || 'A member').slice(0, 48), color: colorOk(x.color), ll: [x.ll[0], x.ll[1]], here: [x.ll[0], x.ll[1]], state: 'ok', trail: trailOf(x), at: now,
        hh: Array.isArray(x.hz) ? meZ(x.hz[0], x.hz[1]) : null };
      if (window.WmPrints) w.prints = window.WmPrints.take(x);
      // unchanged since the last message (they stand still): their ribbon and prints stay as they are
      var tr = w.trail, last = tr.length ? tr[tr.length - 1] : null;
      w.sig = [tr.length, last ? last.ll.join(',') + ',' + last.age : '', w.here.join(','), w.hh, w.color, w.prints ? w.prints.hmv : ''].join('|');
      var o = old[x.id]; if (o && o.sig === w.sig && o.lines) { w.lines = o.lines; w.at = o.at; w.trail = o.trail; o.lines = null; }
      out.push(w);
    });
    mList.forEach(function (x) { if (x.lines) { dropGroup(x.lines); x.lines = null; } });
    var rows = [], rs = {};
    (Array.isArray(m.rows) ? m.rows.slice(0, 40) : []).forEach(function (r) {
      if (!r || !idOk(r.id) || rs[r.id]) return;
      rs[r.id] = 1;
      rows.push({ id: r.id, name: String(r.name || 'A member').slice(0, 48), color: colorOk(r.color), zone: String(r.zone || '').slice(0, 60), here: r.here === true && !!seen[r.id], map: String(r.map || '').slice(0, 60) });
    });
    var gone = mFollow && !seen[mFollow] ? (memberById(mFollow) || rowById(mFollow) || {}).name : null;
    Object.keys(mPrints).forEach(function (k) { if (!seen[k]) { mPrints[k].dispose(); delete mPrints[k]; } });
    mList = out; mRows = rows;
    if (mFollow && !seen[mFollow]) { setMemberFollow(0); toast((gone || 'That member') + (rs[mFollow] ? ' is no longer on this map' : ' stopped sharing their position'), true); }
    membersDraw(false); meRow(); cardDraw();
    memberAim();
  }
  function memberLines(x) {
    if (x.lines) { dropGroup(x.lines); x.lines = null; }
    if (!scene) return;
    var P = window.WmPrints && window.WmPrints.create ? (mPrints[x.id] || (mPrints[x.id] = window.WmPrints.create())) : null;
    x.lines = walkerLines(x, P);
  }
  // all = their trails again too (the fade, a new height scale, the ribbon's width after a zoom); else only the trails that changed
  function membersDraw(all) {
    domItems = domItems.filter(function (it) { if (it.layer === '__mem') { if (it.el.parentNode) it.el.parentNode.removeChild(it.el); return false; } return true; });
    mIts = [];
    if (!inited) return;
    mLineDist = cam.dist;
    mList.forEach(function (x) {
      if (all || !x.lines) memberLines(x);
      var it = dotItem(x, x.here, false, 'mem-dot' + (x.id === mFollow ? ' following' : ''), '__mem', 'mem', function (ev) { showTip(ev.clientX, ev.clientY, x.name, 'Lodge member, live', x.id === mFollow ? 'Following - click to stop' : 'Click to follow'); });
      function act(ev) {
        ev.stopPropagation(); ev.preventDefault(); hideTip();
        menu(ev.clientX, ev.clientY, x.name, [x.id === mFollow ? { text: 'Stop following', run: function () { setMemberFollow(0); } } : { text: 'Follow this member', run: function () { setMemberFollow(x.id); } }]);
      }
      it.el.addEventListener('click', act); it.el.addEventListener('contextmenu', act);
      it.mid = x.id; mIts.push(it);
    });
    dirty = true;
  }
  function memberAim() {
    var f = mFollow && memberById(mFollow);
    if (!f) return;
    var p = ll2xz(f.ll[0], f.ll[1]); followTo = { x: clamp(p[0], 0, 64 * TY), z: clamp(p[1], 0, 64 * TY) }; flight = null; dirty = true;
  }
  function setMemberFollow(id) {
    id = memberById(id) ? id : 0;
    if (id === mFollow) return;
    mFollow = id;
    if (id && follow) { follow = false; followTo = null; send({ t: 'follow', on: false }); }      // one thing to follow at a time
    send({ t: 'mfollow', id: id });
    if (id) memberAim(); else followTo = null;
    membersDraw(false); cardDraw();
  }
  // a row of the card: follow them here, or (on another map) ask the app to open that map and follow them there; again = stop
  function rowClick(r) {
    if (r.id === mFollow) { setMemberFollow(0); return; }
    if (memberById(r.id)) { setMemberFollow(r.id); return; }
    if (follow) setFollow(false, true);
    toast('Opening ' + (r.map || 'their map') + '...', true);
    send({ t: 'mfollow', id: r.id });
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
      case 'ostate': objState(m); break;
      case 'gready': if (inited && typeof m.c === 'number' && typeof m.r === 'number') gReady(m); break;
      case 'wready': if (inited && typeof m.fdid === 'number') wReady(m); break;
      case 'on': (m.on || []).forEach(function (k) { on[k] = true; }); refresh(); break;
      case 'me': if (inited) meMsg(m); break;
      case 'members': if (inited) membersMsg(m); break;
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
    if (m.prefs) { prefs.detail = DETAIL[m.prefs.detail] ? m.prefs.detail : 'medium'; prefs.exag = clamp(+m.prefs.exag || 1, 0.5, 1.5); prefs.__user = !!m.prefs.userDetail; }
    if (!renderer && !startGl()) { inited = false; return; }
    // a second init (another continent): clear what the first built
    Object.keys(meshes).forEach(function (k) { var mm = meshes[k]; if (mm.mesh) { scene.remove(mm.mesh); mm.mesh.geometry.dispose(); } clearWater(mm); if (mm.mat) mm.mat.dispose(); }); meshes = {}; waterActive = 0; waterLoaded = 0; waterFailed = 0;
    Object.keys(tex).forEach(function (k) { var e = tex[k]; if (e.tex && !e.shared) try { e.tex.dispose(); } catch (x) { } }); tex = {}; canvasTexCount = 0;
    bldInit(m.buildings);
    objInit(m.objects);
    Object.keys(pointLayers).forEach(function (k) { scene.remove(pointLayers[k].obj); pointLayers[k].obj.geometry.dispose(); }); pointLayers = {};
    Object.keys(lineLayers).forEach(function (k) { scene.remove(lineLayers[k].group); }); lineLayers = {};
    Object.keys(ringLayers).forEach(function (k) { ringLayers[k].zones.forEach(function (z) { scene.remove(z.obj); z.obj.geometry.dispose(); }); }); ringLayers = {};
    meDropLines(); if (meCard && meCard.parentNode) meCard.parentNode.removeChild(meCard); meCard = null; me = null; meTrail = []; meDotIt = null; meEvIts = []; follow = m.follow === true; followTo = null;
    mList.forEach(function (x) { dropGroup(x.lines); x.lines = null; }); Object.keys(mPrints).forEach(function (k) { mPrints[k].dispose(); }); mPrints = {};
    mList = []; mIts = []; mRows = [];
    mFollow = idOk(m.mfollow) ? m.mfollow : 0;      // 2.98: their row opened this map: following them goes on here
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
    (m.layers || []).forEach(function (d) { if (on[d.id] && !d.live) { requested[d.id] = true; send({ t: 'need', layer: d.id }); setCount(d.id, '...'); } });
    meRow();
    setTimeout(function () { if (!ready && inited) { ready = true; send({ t: 'ready', tiles: selStats.meshes, errors: texErrors }); } }, 9000);
    send({ t: 'inited' }); dirty = true;
  }

  // test / diagnostics hook (read-only, except the camera)
  window.wm3 = {
    // self-test: points of a layer currently drawn (after the kind filters), a layer fed directly, and a layer switch
    shownPoints: function (id) { var L = pointLayers[id]; if (!L || !L.obj.visible) return 0; var a = L.obj.geometry.attributes.show.array, n = 0; for (var i = 0; i < L.n; i++) if (a[i] > 0) n++; return n; },
    feedForTest: function (m) { layer(m); return 1; },
    feed: function (m) { handle(m); return 1; },
    // the members' dots in 3D (test, 2.97.1): how many, ids, names, whether followed and centred; `followId` = an id -> Follow this member (0 = stop)
    // 2.98: per member their trail (points, ribbon pieces), arrow, dot height against the ground and their prints renderer; the card's rows; `rowId` clicks a row
    members: function (followId, rowId) {
      if (followId != null) setMemberFollow(followId);
      var clicked = false; if (rowId != null && meCard) { var rb = meCard.querySelector('.mem-row[data-id="' + rowId + '"]'); if (rb) { rb.click(); clicked = true; } }
      var f = memberById(mFollow), p = f ? ll2xz(f.ll[0], f.ll[1]) : null, cd = meCard;
      return JSON.stringify({ n: mIts.length, ids: mList.map(function (x) { return x.id; }), names: mList.map(function (x) { return x.name; }), following: mFollow, follow: follow, clicked: clicked,
        centered: !!(p && Math.abs(cam.tx - p[0]) + Math.abs(cam.tz - p[1]) < 2), dots: mIts.filter(function (it) { return it.el && it.el.querySelector('.mem-dot'); }).length,
        walkers: mList.map(function (x) {
          var it = null; mIts.forEach(function (d) { if (d.mid === x.id) it = d; });
          return { id: x.id, points: x.trail.length, ribbons: x.lines ? x.lines.children.length : 0, arrow: !!(it && it.arrow), meDot: !!(it && it.el.querySelector('.me-dot.mem-dot')),
            dotY: it ? Math.round(it.y * 10) / 10 : null, ground: it ? Math.round(groundYE(it.x, it.z) * 10) / 10 : null, water: !!(it && it.water), hh: x.hh,
            trailH: x.trail.slice(-10).map(function (q) { return q.h == null ? null : Math.round(q.h * 10) / 10; }), prints: mPrints[x.id] ? JSON.parse(mPrints[x.id].state3d()) : null };
        }),
        renderers: Object.keys(mPrints).length, card: !!cd, fl: cd && cd.querySelector('.me-fl') ? cd.querySelector('.me-fl').textContent : '', head: cd && cd.querySelector('.mem-h') ? cd.querySelector('.mem-h').textContent : '',
        rows: cd ? [].map.call(cd.querySelectorAll('.mem-row'), function (b) { return { id: +b.getAttribute('data-id'), name: b.querySelector('.nm').textContent, on: b.classList.contains('on'), wh: b.querySelector('.wh') ? b.querySelector('.wh').textContent : '', title: b.title }; }) : [] });
    },
    // the live layer (test): state, dot (grey? on screen?), trail ribbons, events drawn, the card, Follow me and whether the camera is on you; `click` = 'howto' | 'follow'
    me: function (click) {
      var d = meCard;
      if (click && d) { var b = d.querySelector(click === 'howto' ? '.me-link' : click === 'show' ? '.me-show' : '.me-follow'); if (b) b.click(); }
      var h = me && me.here ? ll2xz(me.here[0], me.here[1]) : null, de = meDotIt && meDotIt.el;
      return JSON.stringify({ state: me ? me.state : null, layer: !!meLines || !!meDotIt, dot: !!meDotIt, dotShown: !!(meDotIt && meDotIt.vis), grey: !!(de && de.querySelector('.me-dot.grey')),
        label: de && de.querySelector('.me-lab') ? de.querySelector('.me-lab').textContent : '', arrow: !!(de && de.querySelector('.me-arrow')), lines: meLines ? meLines.children.length : 0, points: meTrail.length,
        panel: !!d, notice: d && d.querySelector('.me-n') ? d.querySelector('.me-n').textContent : '', link: d && d.querySelector('.me-link') ? d.querySelector('.me-link').textContent : '',
        followBtn: !!(d && d.querySelector('.me-follow')), follow: follow, show: d && d.querySelector('.me-show') ? d.querySelector('.me-show').textContent : '', centered: !!(h && Math.abs(cam.tx - h[0]) < 3 && Math.abs(cam.tz - h[1]) < 3), off: h ? Math.round(Math.hypot(cam.tx - h[0], cam.tz - h[1])) : -1,
        ribbonLift: (function () { if (!meLines || !meLines.children.length) return null; var a = meLines.children[meLines.children.length - 1].geometry.attributes.position.array; return Math.round((a[1] - surfaceYE(a[0], a[2])) * 10) / 10; })(),
        // the heights (2.82): the dot's height and the ground under it (exaggerated scene yards), its own solved height (yards, null = the ground), and the ribbons' highest lift over the ground
        dotLift: meDotIt && meDotIt.lift != null ? Math.round(meDotIt.lift * 100) / 100 : null, liftAt: [140, 400, 900, 2000, 6000].map(function (d) { return Math.round(meLift(d) * 100) / 100; }), camDist: Math.round(cam.dist),
        dotWater: !!(meDotIt && meDotIt.water), dotSurface: meDotIt ? Math.round(surfaceYE(meDotIt.x, meDotIt.z) * 10) / 10 : null,
        dotY: meDotIt ? Math.round(meDotIt.y * 10) / 10 : null, dotGround: meDotIt ? Math.round(groundYE(meDotIt.x, meDotIt.z) * 10) / 10 : null, dotZ: meDotIt && meDotIt.zs != null ? meDotIt.zs : null, exag: prefs.exag,
        ribbonMaxLift: (function () { if (!meLines || !meLines.children.length) return null; var mx = -1e9; meLines.children.forEach(function (c) { var a = c.geometry.attributes.position.array; for (var i = 0; i + 2 < a.length; i += 3) mx = Math.max(mx, a[i + 1] - surfaceYE(a[i], a[i + 2])); }); return Math.round(mx * 10) / 10; })(),
        evY: meEvIts.map(function (it) { return Math.round((it.y - groundYE(it.x, it.z)) * 10) / 10; }),
        trailH: meTrail.slice(-40).map(function (p) { return p.h == null ? null : Math.round(p.h * 10) / 10; }) });
    },
    // the events on the trail (test): drawn count, row, kinds; `tip` = index -> point at it and read the tooltip; `open` = index -> click it
    ev: function (tip, open) {
      var r = { drawn: meEvIts.length, shown: meEvIts.filter(function (it) { return it.vis; }).length, have: me && me.events ? me.events.length : 0, on: !!on.trailevents,
        row: !!(panelRows.trailevents && panelRows.trailevents.row.style.display !== 'none'), count: panelRows.trailevents ? panelRows.trailevents.ct.textContent : '',
        kinds: meEvIts.map(function (it) { var q = it.el.querySelector('.me-ev'); return q ? q.getAttribute('data-k') : ''; }), ops: meEvIts.map(function (it) { return it.op; }) };
      if (tip != null && meEvIts[tip]) { meEvIts[tip].el.dispatchEvent(new MouseEvent('mouseenter', { clientX: 220, clientY: 220 })); var t = $('tip'); r.tip = t.hidden ? '' : t.textContent; r.tipHtml = t.innerHTML; hideTip(); }
      if (open != null && meEvIts[open]) meEvIts[open].el.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, clientX: 220, clientY: 220 }));
      return JSON.stringify(r);
    },
    // a drag on the canvas as the person would do it (test): from the middle, dx / dy pixels, orbit (or pan with shift)
    drag: function (dx, dy, pan) {
      var r = canvas.getBoundingClientRect(), x = r.left + r.width / 2, y = r.top + r.height / 2;
      canvas.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true, clientX: x, clientY: y, button: 0, shiftKey: !!pan }));
      window.dispatchEvent(new MouseEvent('mousemove', { bubbles: true, clientX: x + dx / 2, clientY: y + dy / 2 }));
      window.dispatchEvent(new MouseEvent('mousemove', { bubbles: true, clientX: x + dx, clientY: y + dy }));
      window.dispatchEvent(new MouseEvent('mouseup', { bubbles: true, clientX: x + dx, clientY: y + dy, button: 0 }));
      return follow;
    },
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
      return JSON.stringify({ pending: pendingBuilds, bare: bare, meshes: waterMeshCount, tiles: tiles, files: withFile, layers: layersN, terrainMeshes: selStats.meshes, byKind: byKind, loaded: waterLoaded, none: waterFailed, active: waterActive, outer: !!outerSea, flatSea: false,
        outerFloor: !!(outerFloor && outerFloor.parent), outerFloorY: outerFloor ? outerFloor.position.y : null, outerFloorColor: outerFloor ? '#' + outerFloor.material.color.getHexString() : null, tileFloorColor: '#' + new THREE.Color((cfg && cfg.sea) || '#081110').convertSRGBToLinear().getHexString(),
        outerFloorLit: !!(outerFloor && outerFloor.material.isMeshLambertMaterial) });
    },
    // the sea's colour on screen (test, 2.93): one frame is drawn and read back at a point of the sea inside a map tile (ocean over a floor below sea level)
    // and at a point of the open sea outside the tiles, both on screen and as near the middle as can be found; {tile: 0 / outer: 0} when one is not in view
    seaPixels: function () {
      if (!renderer) return 'null';
      updateCamera();
      var w = window.innerWidth, h = window.innerHeight, best = { tile: null, outer: null }, v = new THREE.Vector3();
      function onScreen(x, z) { v.set(x, 0, z).applyMatrix4(camera.matrixWorldInverse); if (v.z >= 0) return null; v.applyMatrix4(camera.projectionMatrix); var sx = (v.x * .5 + .5) * w, sy = (-v.y * .5 + .5) * h; return sx > 20 && sx < w - 300 && sy > 20 && sy < h - 20 ? [sx, sy, Math.hypot(sx - w / 2, sy - h / 2)] : null; }
      // a point the land in front hides is no use: the first terrain hit along the ray must be at the point (within a few yards)
      var rc = new THREE.Raycaster(), land = []; Object.keys(meshes).forEach(function (k) { if (meshes[k].mesh) land.push(meshes[k].mesh); });
      function hidden(x, z, sx, sy) {
        rc.setFromCamera(new THREE.Vector2(sx / w * 2 - 1, -(sy / h) * 2 + 1), camera);
        var hit = rc.intersectObjects(land, false)[0], far = Math.hypot(x - camera.position.x, 0 - camera.position.y, z - camera.position.z);
        return !!(hit && hit.distance < far - 8);
      }
      function keep(k, x, z) { var s2 = onScreen(x, z); if (s2 && (!best[k] || s2[2] < best[k].d) && !hidden(x, z, s2[0], s2[1])) best[k] = { x: x, z: z, sx: s2[0], sy: s2[1], d: s2[2] }; }
      for (var c = 0; c < 64; c++) for (var r = 0; r < 64; r++) {
        var t = tileMap[c * 64 + r], mm = meshes[c * 64 + r];
        if (!t && !holeMap[c * 64 + r]) { for (var q = 0; q < 4; q++) keep('outer', (c + 0.25 + (q & 1) * 0.5) * TY, (r + 0.25 + (q >> 1) * 0.5) * TY); continue; }
        if (!t || !t.H || !t.W || !mm || !mm.wm || !mm.wm.length) continue;
        for (var ch = 0; ch < 256; ch += 3) { var x = (c + ((ch & 15) + 0.5) / 16) * TY, z = (r + ((ch >> 4) + 0.5) / 16) * TY, wa = waterAtXZ(x, z); if (wa !== null && Math.abs(wa) < 0.5 && groundY(x, z) < -1) keep('tile', x, z); }
      }
      if (!best.tile || !best.outer) return JSON.stringify({ tile: best.tile ? 1 : 0, outer: best.outer ? 1 : 0 });
      renderer.render(scene, camera);
      var gl = renderer.getContext(), px = new Uint8Array(4), dpr = renderer.getPixelRatio(), out = {};
      ['tile', 'outer'].forEach(function (k) { var b = best[k]; gl.readPixels(Math.round(b.sx * dpr), Math.round((h - b.sy) * dpr), 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, px); out[k] = [px[0], px[1], px[2]]; out[k + 'At'] = [Math.round(b.sx), Math.round(b.sy), Math.round(Math.hypot(b.x - camera.position.x, b.z - camera.position.z))]; });
      dirty = true;
      return JSON.stringify(out);
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
    lines: function (id) { var L = lineLayers[id]; return JSON.stringify(L ? L.items.map(function (it) { return { g: it.g || '', learned: it.learned || '', color: '#' + it.line.material.color.getHexString(), shown: !!(L.group.visible && it.line.visible) }; }) : []); },
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
    objects: function () { return JSON.stringify(objInfo()); },
    objectsLevel: function (lv, auto) { oSwitch(lv, !!auto); return OBJ.level; },
    objectsClick: function (id) { var b = OBJ.ui && OBJ.ui.card.querySelector('[data-a="' + id + '"]'); if (b) b.click(); return !!b; },
    objectsCap: function (mb) { OBJ.capOverride = mb; dirty = true; return 1; },
    objectsGpu: function (ok, why) { OBJ.gpu = !!ok; OBJ.gpuWhy = ok ? '' : String(why || 'a test'); oSync(); return OBJ.gpu; },
    // the look values (OU) for tuning: saturation and tint (r, g, b, linear); returns them
    objectsLook: function (sat, r, g, b) {
      if (!OU.uOTint.value) OU.uOTint.value = new THREE.Vector3(O_TINT[0], O_TINT[1], O_TINT[2]);
      if (sat != null) OU.uOSat.value = sat; if (r != null) OU.uOTint.value.set(r, g, b); dirty = true;
      return JSON.stringify({ sat: OU.uOSat.value, tint: OU.uOTint.value.toArray() });
    },
    groundState: function () { return JSON.stringify(groundInfo()); },          // 3.2: the real ground's state (ground(lat, lng) below is the terrain height)
    groundBright: function (v) { GD.bright = v; Object.keys(GD.tiles).forEach(function (k) { var T = GD.tiles[k]; if (T.mat) T.mat.userData.g.uGBright.value = v; }); dirty = true; return v; },
    objectsProbe: function () { oProbeStart(); dirty = true; return 1; },
    // what is drawn at a point of the canvas (CSS pixels): the nearest tree / object whose box covers it on screen (the models' vertices are not kept
    // in the page once they are on the graphics card, so this goes by the box), with its parts (batches) and their materials
    objectsPick: function (px, py) {
      if (!renderer || !camera) return 'null';
      var r = canvas.getBoundingClientRect(), m4 = new THREE.Matrix4(), c = new THREE.Vector3(), edge = new THREE.Vector3(), best = null;
      Object.keys(OBJ.buckets).forEach(function (k) {
        var bk = OBJ.buckets[k], e = OBJ.models[k]; if (!bk.mesh.visible || !e || !e.m) return;
        var mn = e.m.meta.min, mx = e.m.meta.max;
        for (var j = 0; j < bk.mesh.count; j++) {
          bk.mesh.getMatrixAt(j, m4);
          c.set((mn[0] + mx[0]) / 2, (mn[1] + mx[1]) / 2, (mn[2] + mx[2]) / 2).applyMatrix4(m4);
          var d = c.distanceTo(camera.position), rad = e.m.rad * new THREE.Vector3().setFromMatrixScale(m4).x;
          edge.copy(c).add(camera.up.clone().multiplyScalar(rad));
          var sc = c.clone().project(camera), se = edge.project(camera);
          if (sc.z > 1) continue;
          var sx = (sc.x + 1) / 2 * r.width, sy = (1 - sc.y) / 2 * r.height, sr = Math.hypot(((se.x + 1) / 2 * r.width) - sx, ((1 - se.y) / 2 * r.height) - sy);
          if (Math.hypot(px - sx, py - sy) <= sr && (!best || d < best.d)) best = { d: d, fd: +k, j: j, at: [Math.round(c.x), Math.round(c.y), Math.round(c.z)], screenR: Math.round(sr) };
        }
      });
      if (!best) return JSON.stringify({ hit: false });
      var e2 = OBJ.models[best.fd];
      return JSON.stringify({ hit: true, fdid: best.fd, instance: best.j, dist: Math.round(best.d), at: best.at, screenR: best.screenR, size: [e2.m.meta.min, e2.m.meta.max],
        parts: e2.m.meta.batches.map(function (b, i) { var mt = e2.m.mats[i], te = e2.m.tes[i];
          return { kind: b.kind, tex: b.tex, unlit: b.unlit, two: b.two, n: b.n, mat: mt.type, blend: mt.blending, color: mt.color.getHexString(), texFormat: te && te.tex ? (te.tex.isCompressedTexture ? te.tex.format : 'rgba') : null }; }) });
    },
    xz2ll: xz2ll, ll2xz: ll2xz, groundXZ: function (x, z) { return groundY(x, z); }, distToZ: distToZ, zToDist: zToDist,
    holes: function () { return JSON.stringify({ n: Object.keys(holeMap).length, drawn: !!(holeMesh && holeMesh.parent), rev: cfg && cfg.rev || 0 }); }
  };
  send({ t: 'hello', p: '3d' });
})();
