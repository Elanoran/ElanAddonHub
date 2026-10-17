/* Elan's Outpost - World map page. Runs inside the app's sandboxed WebView2, served from https://worldmap.outpost.local/_page/.
   It knows nothing but what the app sends it (window.chrome.webview messages) and the tile files of the continent. Coordinates:
   the plane is Leaflet's CRS.Simple with lng = col * 4 and lat = -row * 4 (4 units per ADT map tile; see WorldMapMath.cs). Everything the app sends is
   already in lat/lng; this file only needs the world <-> plane formulas to show the cursor position in yards. */
(function () {
  'use strict';
  var UNITS = 4, YARDS = 1600 / 3, MAXZ = 9, NATIVE = 7;
  var wv = window.chrome && window.chrome.webview;
  function send(o) { if (wv) { try { wv.postMessage(o); } catch (e) { /* the app is gone */ } } }
  // a script error is told to the app (logged, and in the world map's "Copy details"); the first few only
  var errs = [];
  function reportErr(msg) { msg = String(msg || 'unknown error').slice(0, 200); errs.push(msg); if (errs.length <= 5) send({ t: 'error', msg: msg }); }
  window.addEventListener('error', function (e) { reportErr((e.message || e) + (e.lineno ? ' (line ' + e.lineno + ')' : '')); });
  window.addEventListener('unhandledrejection', function (e) { reportErr('unhandled: ' + (e.reason && e.reason.message || e.reason)); });
  function w2ll(x, y) { var col = 32 - y / YARDS, row = 32 - x / YARDS; return [-row * UNITS, col * UNITS]; }
  function ll2w(lat, lng) { var col = lng / UNITS, row = -lat / UNITS; return [(32 - row) * YARDS, (32 - col) * YARDS]; }
  function $(id) { return document.getElementById(id); }
  function el(tag, cls, html) { var e = document.createElement(tag); if (cls) e.className = cls; if (html != null) e.innerHTML = html; return e; }
  function esc(s) { return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]; }); }

  var map = null, tileLayer = null, cfg = null, ready = false, tileErrors = 0, tilesLoaded = 0;
  var defs = {};          // layer id -> {id,title,color,children,...}
  var on = {};            // layer id -> true
  var data = {};          // layer id -> {kind, ...}
  var groups = {};        // layer id -> L.layerGroup (labels, markers, lines)
  var counts = {};
  var requested = {};
  var panelRows = {};
  var pin = null, landBounds = null;

  // ---------------------------------------------------------------- toast, tip, context menu
  var toastTimer = 0;
  function toast(text, ok) {
    var t = $('toast'); t.textContent = text; t.className = 'show' + (ok === false ? ' bad' : '');
    clearTimeout(toastTimer); toastTimer = setTimeout(function () { t.className = ''; }, 3200);
  }
  function showTip(x, y, name, line, extra) {
    var t = $('tip'); t.innerHTML = '<b>' + esc(name) + '</b>' + (line ? '<span>' + esc(line) + '</span>' : '') + (extra ? '<i>' + esc(extra) + '</i>' : '');
    t.hidden = false;
    var w = t.offsetWidth, h = t.offsetHeight, px = x + 14, py = y + 16;
    if (px + w > window.innerWidth - 4) px = x - w - 10;
    if (py + h > window.innerHeight - 4) py = y - h - 10;
    t.style.left = Math.max(4, px) + 'px'; t.style.top = Math.max(4, py) + 'px';
  }
  function hideTip() { $('tip').hidden = true; }
  function hideMenu() { $('ctx').hidden = true; }

  function menu(x, y, header, items) {
    var m = $('ctx'); m.innerHTML = '';
    if (header) m.appendChild(el('div', 'hd', esc(header)));
    items.forEach(function (it) {
      if (it === '-') { m.appendChild(el('div', 'sep')); return; }
      var b = el('button', '', esc(it.text)); b.type = 'button'; b.setAttribute('role', 'menuitem');
      b.addEventListener('click', function () { hideMenu(); it.run(); });
      m.appendChild(b);
    });
    m.hidden = false;
    var w = m.offsetWidth, h = m.offsetHeight;
    m.style.left = Math.max(4, Math.min(x, window.innerWidth - w - 4)) + 'px';
    m.style.top = Math.max(4, Math.min(y, window.innerHeight - h - 4)) + 'px';
    var f = m.querySelector('button'); if (f) f.focus();
  }

  // ---------------------------------------------------------------- the point layers (one canvas, grid clustering per zoom)
  var cv = null, ctx2 = null, drawQueued = false, hits = [], lastHit = null;
  var SHAPES = {
    circle: function (c, x, y, r) { c.beginPath(); c.arc(x, y, r, 0, 6.2832); },
    square: function (c, x, y, r) { c.beginPath(); c.rect(x - r, y - r, r * 2, r * 2); },
    diamond: function (c, x, y, r) { r *= 1.25; c.beginPath(); c.moveTo(x, y - r); c.lineTo(x + r, y); c.lineTo(x, y + r); c.lineTo(x - r, y); c.closePath(); },
    triangle: function (c, x, y, r) { r *= 1.2; c.beginPath(); c.moveTo(x, y - r); c.lineTo(x + r, y + r * .8); c.lineTo(x - r, y + r * .8); c.closePath(); },
    star: function (c, x, y, r) { r *= 1.5; c.beginPath(); for (var i = 0; i < 8; i++) { var a = i * Math.PI / 4 - Math.PI / 2, rr = i % 2 ? r * .45 : r; c.lineTo(x + Math.cos(a) * rr, y + Math.sin(a) * rr); } c.closePath(); }
  };

  function initCanvas() {
    cv = el('canvas', 'wm-pts'); cv.style.cssText = 'position:absolute;left:0;top:0;pointer-events:none;z-index:450';
    map.getContainer().appendChild(cv); ctx2 = cv.getContext('2d');
  }
  function queueDraw() { if (!drawQueued) { drawQueued = true; requestAnimationFrame(draw); } }

  function draw() {
    drawQueued = false;
    if (!map || !cv) return;
    var size = map.getSize(), dpr = window.devicePixelRatio || 1;
    if (cv.width !== Math.round(size.x * dpr) || cv.height !== Math.round(size.y * dpr)) { cv.width = Math.round(size.x * dpr); cv.height = Math.round(size.y * dpr); cv.style.width = size.x + 'px'; cv.style.height = size.y + 'px'; }
    ctx2.setTransform(dpr, 0, 0, dpr, 0, 0); ctx2.clearRect(0, 0, size.x, size.y);
    hits = [];
    var z = map.getZoom(), s = Math.pow(2, z), c = map.getCenter();
    var mx = 24 / s, hw = size.x / 2 / s + mx, hh = size.y / 2 / s + mx;
    var cell = z >= 7 ? 12 : z >= 5.5 ? 24 : z >= 4 ? 34 : 44;
    var cl = new Map(), total = 0;
    for (var id in data) {
      var L = data[id];
      if (L.kind !== 'points' || !on[id]) continue;
      var pts = L.pts, n = pts.length / 6;
      for (var i = 0; i < n; i++) {
        var o = i * 6, lat = pts[o], lng = pts[o + 1];
        if (lng < c.lng - hw || lng > c.lng + hw || lat < c.lat - hh || lat > c.lat + hh) continue;
        var st = L.styles[pts[o + 4]];
        if (st.g && !on[st.g]) continue;
        var x = size.x / 2 + (lng - c.lng) * s, y = size.y / 2 + (c.lat - lat) * s;
        var key = Math.floor(x / cell) * 100003 + Math.floor(y / cell);
        var g = cl.get(key);
        if (!g) { g = { x: 0, y: 0, n: 0, L: L, i: i, st: st, mixed: false, la0: lat, la1: lat, ln0: lng, ln1: lng, id: id }; cl.set(key, g); }
        else { if (g.st !== st) g.mixed = true; if (lat < g.la0) g.la0 = lat; if (lat > g.la1) g.la1 = lat; if (lng < g.ln0) g.ln0 = lng; if (lng > g.ln1) g.ln1 = lng; }
        g.x += x; g.y += y; g.n++; total++;
      }
    }
    var singles = [], clusters = [];
    cl.forEach(function (g) { (g.n === 1 ? singles : clusters).push(g); });
    singles.forEach(function (g) {
      var st = g.st, r = st.f ? 5 : 4;
      ctx2.globalAlpha = st.f ? 1 : .85;
      SHAPES[st.s] ? SHAPES[st.s](ctx2, g.x, g.y, r) : SHAPES.circle(ctx2, g.x, g.y, r);
      ctx2.fillStyle = st.c; ctx2.fill(); ctx2.lineWidth = st.f ? 1.6 : 1; ctx2.strokeStyle = st.f ? '#fff' : 'rgba(0,0,0,.75)'; ctx2.stroke();
      hits.push({ x: g.x, y: g.y, r: r + 3, g: g });
    });
    ctx2.globalAlpha = 1;
    clusters.forEach(function (g) {
      var x = g.x / g.n, y = g.y / g.n, r = Math.min(20, 9 + Math.log(g.n) * 2.2);
      ctx2.beginPath(); ctx2.arc(x, y, r, 0, 6.2832);
      ctx2.fillStyle = g.mixed ? '#3b4a63' : g.st.c; ctx2.globalAlpha = .9; ctx2.fill(); ctx2.globalAlpha = 1;
      ctx2.lineWidth = 2; ctx2.strokeStyle = 'rgba(255,255,255,.85)'; ctx2.stroke();
      ctx2.fillStyle = '#fff'; ctx2.font = '600 11px "Segoe UI", sans-serif'; ctx2.textAlign = 'center'; ctx2.textBaseline = 'middle';
      ctx2.shadowColor = 'rgba(0,0,0,.8)'; ctx2.shadowBlur = 3; ctx2.fillText(g.n > 999 ? Math.round(g.n / 100) / 10 + 'k' : String(g.n), x, y + .5); ctx2.shadowBlur = 0;
      hits.push({ x: x, y: y, r: r + 2, g: g, cluster: true });
    });
    drawn = { points: singles.length, clusters: clusters.length, visible: total };
  }
  var drawn = { points: 0, clusters: 0, visible: 0 };

  function hitAt(px, py) {
    var best = null, bd = 1e9;
    for (var i = 0; i < hits.length; i++) {
      var h = hits[i], dx = h.x - px, dy = h.y - py, d = dx * dx + dy * dy;
      if (d <= h.r * h.r && d < bd) { bd = d; best = h; }
    }
    return best;
  }
  function pointInfo(h) {
    var g = h.g, L = g.L, o = g.i * 6, p = L.pts;
    return { name: L.names[p[o + 2]] || '', tip: p[o + 3] >= 0 ? L.tips[p[o + 3]] : '', link: p[o + 5] >= 0 ? L.links[p[o + 5]] : '', st: L.styles[p[o + 4]] };
  }

  // ---------------------------------------------------------------- DOM layers: zone labels, flight masters, route lines
  function ensureGroup(id) { if (!groups[id]) groups[id] = L.layerGroup(); return groups[id]; }
  function clearGroup(id) { if (groups[id]) groups[id].clearLayers(); }

  function openLink(link) { if (link) send({ t: 'open', id: link }); }
  function bindHover(layer, name, line, extra, link) {
    layer.on('mousemove', function (e) { var oe = e.originalEvent; showTip(oe.clientX, oe.clientY, name, line, extra); });
    layer.on('mouseout', hideTip);
    if (link) layer.on('click', function (e) { L.DomEvent.stopPropagation(e); hideTip(); openLink(link); });
  }

  function buildLabels(id, d) {
    clearGroup(id); var g = ensureGroup(id);
    (d.items || []).forEach(function (it) {
      var ic = L.divIcon({ className: 'zl' + (it.sm ? ' small' : ''), html: '<div class="zli"><b>' + esc(it.name) + '</b>' + (it.sub ? '<span>' + esc(it.sub) + '</span>' : '') + '</div>', iconSize: [0, 0] });
      var m = L.marker([it.lat, it.lng], { icon: ic, keyboard: false, riseOnHover: true, zIndexOffset: it.sm ? 0 : 100 });
      m._minZ = it.minZ == null ? 0 : it.minZ; m._maxZ = it.maxZ == null ? 99 : it.maxZ;
      bindHover(m, it.name, it.sub ? 'Level ' + it.sub : '', it.link ? 'Click to open its Atlas page' : '', it.link);
      g.addLayer(m);
    });
    applyLabelZoom();
  }
  function applyLabelZoom() {
    if (!map) return;
    var z = map.getZoom();
    ['zones'].forEach(function (id) {
      if (!groups[id]) return;
      groups[id].eachLayer(function (m) {
        var e = m.getElement && m.getElement(); if (!e) return;
        e.classList.toggle('hide', z < m._minZ || z > m._maxZ);
      });
    });
  }

  function buildMarkers(id, d) {
    clearGroup(id); var g = ensureGroup(id);
    (d.items || []).forEach(function (it) {
      // instance entrances (it.ic dun / raid) carry a kind group (it.g: switched by its child row) and a label; flight masters are the plain diamonds
      var html = it.ic ? '<div class="inst ' + esc(it.ic) + '">' + (it.ic === 'raid' ? '&#9760;' : '') + '</div>' + (it.lab ? '<div class="instl">' + esc(it.lab) + '</div>' : '') : (window.WmIcons ? window.WmIcons.html(it.f || 'n') : '<div class="fm ' + (it.f || 'n') + '"></div>');
      var ic = L.divIcon({ className: 'fmi', html: html, iconSize: [0, 0] });
      var m = L.marker([it.lat, it.lng], { icon: ic, keyboard: false, zIndexOffset: it.ic ? 350 : 300 });
      m._g = it.g || null;
      bindHover(m, it.name, it.tip || '', it.extra || '', it.link);
      if (it.ic) m.on('contextmenu', function (e) {
        L.DomEvent.stop(e); hideTip();
        var oe = e.originalEvent, items = [];
        if (it.link) items.push({ text: 'Open its page', run: function () { openLink(it.link); } });
        items.push({ text: 'Navigate here', run: function () { send({ t: 'nav', lat: it.lat, lng: it.lng }); } });
        menu(oe.clientX, oe.clientY, it.name, items);
      });
      g.addLayer(m);
    });
  }
  // markers of a kind group (Dungeons, Raids) follow their child row; the labels of the entrances need some zoom
  function applyMarkerGroups() {
    if (!map) return;
    Object.keys(groups).forEach(function (id) {
      groups[id].eachLayer(function (m) {
        if (m._g === undefined || m._g === null) return;
        var e = m.getElement && m.getElement(); if (e) e.style.display = on[m._g] ? '' : 'none';
      });
    });
    Object.keys(lineKinds).forEach(function (id) {
      var g = groups[id]; if (!g) return;
      lineKinds[id].forEach(function (e) { var want = !!on[e.g], has = g.hasLayer(e.layer); if (want && !has) g.addLayer(e.layer); else if (!want && has) g.removeLayer(e.layer); });
    });
    map.getContainer().classList.toggle('instlab', map.getZoom() >= 3.5);
  }

  // zone borders: one polygon per zone (all its rings: the even-odd fill leaves holes out), a thin line that tints the zone under the cursor
  function buildRings(id, d) {
    clearGroup(id); var g = ensureGroup(id);
    var base = { color: '#7dd3fc', weight: 1.4, opacity: .8, fill: true, fillColor: '#7dd3fc', fillOpacity: 0, interactive: true, smoothFactor: 0.6 };
    (d.items || []).forEach(function (it) {
      var rings = (it.rings || []).map(function (r) { var ll = []; for (var i = 0; i + 1 < r.length; i += 2) ll.push([r[i], r[i + 1]]); return ll; });
      if (!rings.length) return;
      var pg = L.polygon(rings, base);
      pg.on('mouseover', function () { pg.setStyle({ fillOpacity: .17, weight: 2.4, opacity: 1 }); });
      pg.on('mousemove', function (e) { var oe = e.originalEvent; showTip(oe.clientX, oe.clientY, it.name, '', it.link ? 'Click to open its Atlas page' : ''); });
      pg.on('mouseout', function () { pg.setStyle({ fillOpacity: 0, weight: 1.4, opacity: .8 }); hideTip(); });
      if (it.link) pg.on('click', function (e) { L.DomEvent.stopPropagation(e); hideTip(); openLink(it.link); });
      g.addLayer(pg);
    });
  }

  var lineKinds = {};      // layer id -> [{layer, g}]: routes (and their time labels) that belong to a kind row (Horde ...); canvas lines have no element to hide, so they leave the group
  function buildLines(id, d) {
    clearGroup(id); var g = ensureGroup(id); lineKinds[id] = [];
    (d.items || []).forEach(function (it) {
      var ll = []; for (var i = 0; i + 1 < it.pts.length; i += 2) ll.push([it.pts[i], it.pts[i + 1]]);
      if (ll.length < 2) return;
      var pl = L.polyline(ll, { color: it.c || '#fff', weight: it.w || 2, opacity: it.o || .85, dashArray: it.d || null, interactive: true, smoothFactor: 1 });
      bindHover(pl, it.name || '', it.tip || '', it.extra || '', it.link);
      g.addLayer(pl);
      if (it.g) lineKinds[id].push({ layer: pl, g: it.g, learned: it.learned || '' });
      if (it.label && it.mid) {
        var ic = L.divIcon({ className: 'rli', html: '<div class="rl">' + esc(it.label) + '</div>', iconSize: [0, 0] });
        var m = L.marker(it.mid, { icon: ic, interactive: false, keyboard: false }); m._minZ = it.labelZ == null ? 3.5 : it.labelZ; m._line = true;
        g.addLayer(m);
        if (it.g) lineKinds[id].push({ layer: m, g: it.g });
      }
    });
    applyLineLabels(); applyMarkerGroups();
  }
  function applyLineLabels() {
    if (!map) return;
    var z = map.getZoom();
    Object.keys(groups).forEach(function (id) {
      groups[id].eachLayer(function (m) { if (!m._line) return; var e = m.getElement && m.getElement(); if (e) e.style.display = z >= m._minZ ? '' : 'none'; });
    });
  }

  // ---------------------------------------------------------------- layer visibility + panel
  function groupShown(id) { return !!on[id]; }
  function refresh() {
    Object.keys(groups).forEach(function (id) {
      var shown = groupShown(id), g = groups[id], has = map.hasLayer(g);
      if (shown && !has) g.addTo(map); else if (!shown && has) map.removeLayer(g);
    });
    applyLabelZoom(); applyLineLabels(); applyMarkerGroups(); queueDraw();
    Object.keys(panelRows).forEach(function (id) {
      var r = panelRows[id]; r.cb.checked = !!on[id];
      var parent = r.parent; var dis = parent && !on[parent];
      r.row.classList.toggle('disabled', !!dis); r.row.classList.toggle('off', !on[id] && !dis);
      r.cb.disabled = !!dis;
    });
  }
  function setOn(id, v) {
    on[id] = !!v;
    if (v && defs[id] && !defs[id].child) { var kids = Object.keys(panelRows).filter(function (k) { return panelRows[k].parent === id; }); if (kids.length && !kids.some(function (k) { return on[k]; })) kids.forEach(function (k) { on[k] = true; }); }
    var def = defs[id];
    if (v && def && !def.child && !def.live && !data[id] && !requested[id]) { requested[id] = true; send({ t: 'need', layer: id }); setCount(id, '...'); }
    refresh(); sendLayers();
    if (def && def.live && me && map && me.state !== 'off' && me.state !== 'none') { meDraw(); meRow(); }
  }
  var layersTimer = 0;
  function sendLayers() {
    clearTimeout(layersTimer);
    layersTimer = setTimeout(function () { var l = []; Object.keys(on).forEach(function (k) { if (on[k]) l.push(k); }); send({ t: 'layers', on: l }); }, 250);
  }
  function setCount(id, text) { if (panelRows[id]) panelRows[id].ct.textContent = text == null ? '' : text; }

  function buildPanel(list) {
    var C = L.Control.extend({
      options: { position: 'topright' },
      onAdd: function () {
        var p = el('div', 'wm-panel'); L.DomEvent.disableClickPropagation(p); L.DomEvent.disableScrollPropagation(p);
        var h = el('h4', '', '<span>Layers</span><span aria-hidden="true">▾</span>'); h.tabIndex = 0; h.setAttribute('role', 'button'); h.setAttribute('aria-expanded', 'true');
        var body = el('div', 'body');
        function toggle() { var c = p.classList.toggle('collapsed'); h.setAttribute('aria-expanded', String(!c)); }
        h.addEventListener('click', toggle); h.addEventListener('keydown', function (e) { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); toggle(); } });
        function row(d, parent) {
          var r = el('label', 'wm-row' + (parent ? ' sub' : ''));
          var cb = el('input'); cb.type = 'checkbox';
          var sw = el('span', 'sw'); sw.style.background = d.color || '#888';
          var nm = el('span', 'nm', esc(d.title)); var ct = el('span', 'ct');
          if (d.tip) r.title = d.tip;
          r.appendChild(cb); r.appendChild(sw); r.appendChild(nm); r.appendChild(ct);
          cb.addEventListener('change', function () { setOn(d.id, cb.checked); });
          body.appendChild(r);
          panelRows[d.id] = { row: r, cb: cb, ct: ct, parent: parent };
          defs[d.id] = d; if (parent) d.child = true;
        }
        list.forEach(function (d) { row(d, null); (d.children || []).forEach(function (c) { row(c, d.id); }); });
        p.appendChild(h); p.appendChild(body);
        return p;
      }
    });
    new C().addTo(map);
  }

  // ---------------------------------------------------------------- the live position layer ("me"): your dot, a fading trail, a notice, Follow me
  // Only drawn while the app sends it (Settings > Show my live position on the world map). Nothing here is stored; the app keeps the trail in memory.
  var ME_STATES = { off: 1, none: 1, ok: 1, elsewhere: 1, unavailable: 1, notshared: 1, oldhub: 1 };
  var me = null, meGroup = null, meDot = null, meCtrl = null, follow = false, meAt = 0, meTrail = [], meEvMarkers = [];
  // the events on the trail (Elan's Hub 1.16.0+): checked again here, drawn only with the "Events on my trail" row on
  function meEvents(list) {
    var out = [], kinds = (window.WmIcons && window.WmIcons.evKinds) || {};
    (Array.isArray(list) ? list.slice(0, 200) : []).forEach(function (e) {
      if (!e || !llOk([e.lat, e.lng]) || !fin(e.age) || !kinds[e.k]) return;
      out.push({ ll: [e.lat, e.lng], age: Math.max(0, Math.min(600, e.age)), k: e.k, name: String(e.name || '').slice(0, 80), line: String(e.line || '').slice(0, 80),
        c: /^#[0-9a-fA-F]{6}$/.test(e.c || '') ? e.c : '#e8e6e3', link: typeof e.link === 'string' ? e.link.slice(0, 40) : '', at: String(e.at || '').slice(0, 5) });
    });
    return out;
  }
  function evWhen(e, extra) {
    var a = e.age + (extra || 0), mins = Math.round(a / 60);
    return (e.at ? e.at + ' · ' : '') + (a < 60 ? 'just now' : mins + ' min ago') + (e.link ? ' · click to open' : '');
  }
  function evOpacity(age) { return Math.max(0.3, 1 - Math.min(600, age) / 600 * 0.7); }
  // the panel row "Events on my trail" is only there while the live layer is
  function meRow() {
    var r = panelRows.trailevents; if (!r) return;
    var live = !!(me && me.state !== 'off' && me.state !== 'none');
    r.row.style.display = live ? '' : 'none';
    setCount('trailevents', live && me.events ? String(me.events.length) : '');
    if (window.WmPrints) window.WmPrints.row(panelRows, live);
  }
  function fin(v) { return typeof v === 'number' && isFinite(v); }
  function llOk(a) { return a && a.length === 2 && fin(a[0]) && fin(a[1]) && a[0] <= 0 && a[0] >= -256 && a[1] >= 0 && a[1] <= 256; }
  function meClear() {
    if (meGroup && map) map.removeLayer(meGroup);
    meGroup = null; meDot = null; meTrail = [];
    if (meCtrl && map) map.removeControl(meCtrl);
    meCtrl = null; meEvMarkers = [];
    if (window.WmPrints) window.WmPrints.clear2d();
  }
  function meMsg(m) {
    var st = ME_STATES[m.state] ? m.state : 'none';
    if (st === 'off' || st === 'none') { me = { state: st }; meClear(); meRow(); return; }
    var t = Array.isArray(m.trail) ? m.trail.slice(0, 2400) : [], pts = [];
    for (var i = 0; i + 3 < t.length; i += 4) { var p = [t[i], t[i + 1]]; if (llOk(p) && fin(t[i + 2])) pts.push({ ll: p, age: Math.max(0, Math.min(600, t[i + 2])), brk: t[i + 3] === 1, k: i / 4 }); }
    me = { state: st, name: String(m.name || '').slice(0, 48), color: /^#[0-9a-fA-F]{6}$/.test(m.color || '') ? m.color : '#e8e6e3', notice: String(m.notice || '').slice(0, 120),
      howto: m.howto === true, howtoText: String(m.howtoText || 'How to turn it on').slice(0, 40), here: llOk(m.here) ? m.here : null, last: llOk(m.last) ? m.last : null, events: meEvents(m.events), show: st === 'elsewhere' ? String(m.show || '').slice(0, 72) : '' };
    meTrail = pts; meAt = Date.now();
    if (window.WmPrints) me.prints = window.WmPrints.take(m);      // 2.88: the paw prints (trailprints.js)
    meDraw(); meRow();
    if (follow && map && me.state === 'ok' && me.here) map.panTo(me.here, { animate: false });
  }
  function heading() {
    var n = meTrail.length; if (!me || !me.here || n < 1) return null;
    var a = meTrail[n - 1].ll, b = me.here;
    if (n >= 2 && Math.abs(a[0] - b[0]) + Math.abs(a[1] - b[1]) < 1e-9) a = meTrail[n - 2].ll;
    var dLat = b[0] - a[0], dLng = b[1] - a[1];
    if (Math.abs(dLat) + Math.abs(dLng) < 1e-6) return null;
    return Math.round(Math.atan2(dLng, dLat) * 180 / Math.PI);
  }
  function meDraw() {
    if (!map || !me) return;
    if (!meGroup) { meGroup = L.layerGroup(); meGroup.addTo(map); }
    meGroup.clearLayers(); meDot = null;
    // the trail: runs of points (a break starts a new run), cut into age bands that fade out over the 10 minutes
    var extra = (Date.now() - meAt) / 1000, BAND = 60, run = [], band = -1;
    function flush() {
      if (run.length < 2) return;
      var op = Math.max(0.12, 0.9 * (1 - (band * BAND) / 600)), pk = window.WmPrints ? window.WmPrints.lineK2d(map, on, me) : 1;      // pk < 1: paw prints are shown, the line is a faint guide
      if (pk === 1) meGroup.addLayer(L.polyline(run, { color: '#000', weight: 6, opacity: op * 0.45, interactive: false, lineCap: 'round', lineJoin: 'round' }));      // a dark casing: readable on any tile
      meGroup.addLayer(L.polyline(run, { color: me.color, weight: pk === 1 ? 3 : 1.5, opacity: op * pk, interactive: false, lineCap: 'round', lineJoin: 'round', className: 'me-trail' }));
    }
    for (var i = 0; i < meTrail.length; i++) {
      var p = meTrail[i], b = Math.min(9, Math.floor((p.age + extra) / BAND));
      if (p.brk) { flush(); run = []; band = b; }
      if (b !== band) { flush(); run = run.length ? [run[run.length - 1]] : []; band = b; }
      run.push(p.ll);
    }
    if (me.state === 'ok' && me.here && run.length) run.push(me.here);
    flush();
    if (window.WmPrints) window.WmPrints.draw2d(map, me, meTrail, extra, on, meDraw);
    // the events: small marks on the trail, fading with it; point at one for what and when, click it for its Atlas page
    meEvMarkers = [];
    var fan = on.trailevents && me.events && window.WmIcons ? window.WmIcons.evFan(me.events, me.state === 'ok' ? me.here : null) : [];
    if (on.trailevents && me.events && window.WmIcons) me.events.forEach(function (e, ei) {
      var mk = L.marker(e.ll, { icon: L.divIcon({ className: 'me-ev-i', html: window.WmIcons.ev(e.k, e.c, evOpacity(e.age + extra).toFixed(2), fan[ei]), iconSize: [0, 0] }), keyboard: false, interactive: true, zIndexOffset: 950 });
      bindHover(mk, e.name, e.line, evWhen(e, extra), e.link);
      meGroup.addLayer(mk); meEvMarkers.push(mk);
    });
    // the dot: class colour with a heading arrow; grey with a label where the position is not available
    var at = me.state === 'ok' ? me.here : me.state === 'unavailable' ? me.last : null;
    if (at) {
      var grey = me.state !== 'ok', hd = grey ? null : heading();
      var html = '<div class="me-dot' + (grey ? ' grey' : '') + '" style="--c:' + me.color + '">' + (hd === null ? '' : '<i class="me-arrow" style="transform:rotate(' + hd + 'deg)"></i>') + '</div>'
        + (grey ? '<div class="me-lab">' + esc(me.notice || 'Position unavailable here') + '</div>' : '');
      meDot = L.marker(at, { icon: L.divIcon({ className: 'me-i', html: html, iconSize: [0, 0] }), keyboard: false, interactive: true, zIndexOffset: 1000 });
      bindHover(meDot, me.name || 'You', grey ? (me.notice || 'Position unavailable here') : 'You are here', grey ? 'The trail pauses here' : 'Live from Elan\'s Hub (display only)', null);
      meGroup.addLayer(meDot);
    }
    meCtrlDraw();
  }
  function meCtrlDraw() {
    if (!map || !me) return;
    if (!meCtrl) {
      var C = L.Control.extend({ options: { position: 'bottomright' }, onAdd: function () { var d = el('div', 'wm-me'); L.DomEvent.disableClickPropagation(d); L.DomEvent.disableScrollPropagation(d); this._d = d; return d; } });
      meCtrl = new C(); meCtrl.addTo(map);
    }
    var d = meCtrl._d; d.innerHTML = '';
    var head = el('div', 'me-h'); var sw = el('span', 'me-sw'); sw.style.background = me.color; head.appendChild(sw); head.appendChild(el('b', '', esc(me.name || 'You')));
    d.appendChild(head);
    var showNotice = me.state !== 'ok';
    if (showNotice && me.notice) d.appendChild(el('div', 'me-n' + (me.state === 'unavailable' ? '' : ' calm'), esc(me.notice)));
    if (me.show) {      // 2.91: the map you are on has a world map of its own: open it (the app picks it from your position)
      var sb = el('button', 'me-show', esc(me.show)); sb.type = 'button';
      sb.addEventListener('click', function () { send({ t: 'showmap' }); });
      d.appendChild(sb);
    }
    if (me.howto) {
      var a = el('button', 'me-link', esc(me.howtoText)); a.type = 'button';
      a.addEventListener('click', function () { send({ t: 'howto' }); });
      d.appendChild(a);
    }
    if (me.state === 'ok' || me.state === 'unavailable') {
      var f = el('button', 'me-follow' + (follow ? ' on' : ''), follow ? 'Following you' : 'Follow me'); f.type = 'button'; f.setAttribute('aria-pressed', String(follow));
      f.addEventListener('click', function () { setFollow(!follow, true); });
      d.appendChild(f);
    }
  }
  function setFollow(v, tell) {
    follow = !!v;
    if (tell) send({ t: 'follow', on: follow });
    if (follow && map && me && me.state === 'ok' && me.here) map.panTo(me.here, { animate: false });
    meCtrlDraw();
  }
  // the trail keeps fading while nothing new arrives
  setInterval(function () { if (me && meTrail.length && map) meDraw(); }, 15000);

  // ---------------------------------------------------------------- the messages from the app
  // vseq: the last place the app sent the map to; every view report names it, so the app can drop a report that was on its way before that goto
  var vseq = -1;
  function handle(m) {
    if (!m || typeof m.t !== 'string') return;
    if ((m.t === 'init' || m.t === 'goto' || m.t === 'fit') && typeof m.s === 'number') vseq = m.s;
    switch (m.t) {
      case 'init': init(m); break;
      case 'goto': go(m); break;
      case 'fit': if (map && landBounds) { if (pin) { map.removeLayer(pin); pin = null; } map.fitBounds(landBounds, { animate: false, padding: [10, 10] }); } break;
      case 'layer': layer(m); break;
      case 'layerinfo': setCount(m.id, m.state === 'loading' ? '...' : m.state === 'empty' ? 'none' : String(m.count == null ? '' : m.count)); if (m.note && panelRows[m.id]) panelRows[m.id].row.title = m.note; break;
      case 'toast': toast(m.text, m.ok); break;
      case 'on': (m.on || []).forEach(function (k) { on[k] = true; }); refresh(); break;
      case 'me': meMsg(m); break;
    }
  }
  if (wv) wv.addEventListener('message', function (e) { handle(e.data); });

  function layer(m) {
    var id = m.id; if (!defs[id]) return;
    var d = { kind: m.kind };
    if (m.kind === 'points') {
      d.pts = new Float32Array(m.pts || []); d.names = m.names || []; d.tips = m.tips || []; d.links = m.links || []; d.styles = m.styles || [];
      counts[id] = d.pts.length / 6; data[id] = d;
    } else if (m.kind === 'labels') { data[id] = d; buildLabels(id, m); counts[id] = (m.items || []).length; }
    else if (m.kind === 'markers') { data[id] = d; buildMarkers(id, m); counts[id] = (m.items || []).length; }
    else if (m.kind === 'lines') { data[id] = d; buildLines(id, m); counts[id] = (m.items || []).length; }
    else if (m.kind === 'rings') { data[id] = d; buildRings(id, m); counts[id] = (m.items || []).length; }
    requested[id] = true;
    setCount(id, counts[id] === 0 ? 'none' : String(counts[id]));
    if (m.note && panelRows[id]) panelRows[id].row.title = m.note;
    refresh();
  }

  function go(m) {
    if (!map) return;
    map.setView([m.lat, m.lng], Math.max(0, Math.min(MAXZ, m.z == null ? 5 : m.z)), { animate: false });
    if (pin) { map.removeLayer(pin); pin = null; }
    if (m.pin) {
      pin = L.marker([m.lat, m.lng], { icon: L.divIcon({ className: 'fmi', html: '<div class="pin"></div>', iconSize: [0, 0] }), interactive: !!m.pin.name, keyboard: false, zIndexOffset: 900 });
      if (m.pin.name) bindHover(pin, m.pin.name, m.pin.tip || '', '', null);
      pin.addTo(map);
    }
  }

  function init(m) {
    cfg = m.map;
    follow = m.follow === true; meGroup = null; meDot = null; meCtrl = null; me = null; meTrail = [];
    if (map) { map.remove(); map = null; data = {}; groups = {}; counts = {}; requested = {}; panelRows = {}; defs = {}; on = {}; }
    $('map').innerHTML = '';
    document.documentElement.style.setProperty('--sea', cfg.sea || '#081110');
    var bounds = L.latLngBounds([-(cfg.maxRow + 1) * UNITS, cfg.minCol * UNITS], [-cfg.minRow * UNITS, (cfg.maxCol + 1) * UNITS]);
    map = L.map('map', { crs: L.CRS.Simple, minZoom: 0, maxZoom: MAXZ, zoomSnap: 0.25, zoomDelta: 0.5, wheelPxPerZoomLevel: 90, zoomAnimation: false, markerZoomAnimation: false, fadeAnimation: false,
      attributionControl: false, zoomControl: true, preferCanvas: true, maxBoundsViscosity: .6, keyboard: true, inertia: true });
    landBounds = bounds;
    var fit = map.getBoundsZoom(bounds, false);
    map.setMinZoom(Math.max(0, Math.floor((fit - 1) * 4) / 4));
    map.setMaxBounds(bounds.pad(.35));
    tileLayer = L.tileLayer(m.map.base + '/z{z}/{x}_{y}.jpg' + (m.map.rev ? '?r=' + m.map.rev : ''), { tileSize: 256, minNativeZoom: 0, maxNativeZoom: NATIVE, minZoom: 0, maxZoom: MAXZ, noWrap: true, bounds: bounds.pad(.02), keepBuffer: 3, updateWhenZooming: false, errorTileUrl: 'data:image/gif;base64,R0lGODlhAQABAAAAACH5BAEKAAEALAAAAAABAAEAAAICTAEAOw==' });
    tileLayer.on('tileerror', function () { tileErrors++; });
    tileLayer.on('tileload', function () { tilesLoaded++; });
    tileLayer.once('load', function () { if (!ready) { ready = true; send({ t: 'ready', tiles: tilesLoaded, errors: tileErrors }); } });
    tileLayer.addTo(map);
    initCanvas();
    // layer panel
    (m.on || []).forEach(function (k) { on[k] = true; });
    buildPanel(m.layers || []);
    // a layer that is on with none of its kinds on (an older saved state) shows all its kinds, never an empty layer
    (m.layers || []).forEach(function (d) { var kids = d.children || []; var any = kids.some(function (c) { return on[c.id]; });
      kids.forEach(function (c) { if (on[d.id] && !any) on[c.id] = true; else if (on[c.id] === undefined) on[c.id] = false; }); });
    // coordinates + scale
    var Co = L.Control.extend({ options: { position: 'bottomleft' }, onAdd: function () { var d = el('div', 'wm-coords', '&nbsp;'); this._d = d; return d; } });
    var co = new Co(); co.addTo(map);
    var Sc = L.Control.extend({ options: { position: 'bottomleft' }, onAdd: function () { var d = el('div', 'wm-scale', ''); this._d = d; return d; } });
    var sc = new Sc(); sc.addTo(map);
    function scale() {
      var yardsPerPx = (YARDS / UNITS) / Math.pow(2, map.getZoom());
      var nice = [10, 20, 50, 100, 200, 500, 1000, 2000, 5000], best = 10;
      for (var i = 0; i < nice.length; i++) if (nice[i] / yardsPerPx <= 140) best = nice[i];
      sc._d.innerHTML = best.toLocaleString('en-US') + ' yd<i style="width:' + Math.round(best / yardsPerPx) + 'px"></i>';
    }
    map.on('zoomend', function () { scale(); applyLabelZoom(); applyLineLabels(); applyMarkerGroups(); }); scale();
    map.on('mousemove', function (e) {
      var w = ll2w(e.latlng.lat, e.latlng.lng);
      co._d.textContent = 'x ' + Math.round(w[0]).toLocaleString('en-US') + '   y ' + Math.round(w[1]).toLocaleString('en-US');
      var h = hitAt(e.containerPoint.x, e.containerPoint.y);
      if (h) {
        var oe = e.originalEvent;
        if (h.cluster) { var g = h.g; showTip(oe.clientX, oe.clientY, g.n + ' ' + (g.mixed ? 'places' : (g.st.l || 'places').toLowerCase()), '', 'Click to zoom in'); }
        else { var pi = pointInfo(h); showTip(oe.clientX, oe.clientY, pi.name, pi.tip, (pi.st.l || '') + (pi.link ? ' · click to open' : '')); }
        map.getContainer().style.cursor = 'pointer'; lastHit = h;
      } else if (lastHit) { hideTip(); map.getContainer().style.cursor = ''; lastHit = null; }
    });
    map.on('mouseout', function () { hideTip(); lastHit = null; });
    map.on('click', function (e) {
      hideMenu();
      var h = hitAt(e.containerPoint.x, e.containerPoint.y); if (!h) return;
      if (h.cluster) { var g = h.g; var b = L.latLngBounds([g.la0, g.ln0], [g.la1, g.ln1]); hideTip(); if (g.la0 === g.la1 && g.ln0 === g.ln1) map.setView(b.getCenter(), Math.min(MAXZ, map.getZoom() + 2)); else map.fitBounds(b.pad(.3), { maxZoom: Math.min(MAXZ, map.getZoom() + 3) }); }
      else { var pi = pointInfo(h); if (pi.link) { hideTip(); openLink(pi.link); } }
    });
    map.on('contextmenu', function (e) {
      var w = ll2w(e.latlng.lat, e.latlng.lng), ll = e.latlng;
      menu(e.originalEvent.clientX, e.originalEvent.clientY, 'x ' + Math.round(w[0]) + ', y ' + Math.round(w[1]), [
        { text: 'Navigate here', run: function () { send({ t: 'nav', lat: ll.lat, lng: ll.lng }); } },
        { text: 'Zoom in here', run: function () { map.setView(ll, Math.min(MAXZ, map.getZoom() + 2)); } }
      ]);
    });
    map.on('movestart zoomstart', function () { hideMenu(); hideTip(); });
    map.on('dragstart', function () { if (follow) setFollow(false, true); });      // dragging the map yourself stops following
    map.on('move zoom resize', queueDraw);
    var vt = 0;
    map.on('moveend', function () {
      queueDraw(); applyLabelZoom();
      clearTimeout(vt); vt = setTimeout(function () { var c = map.getCenter(); send({ t: 'view', lat: c.lat, lng: c.lng, z: map.getZoom(), s: vseq }); }, 500);
    });
    document.addEventListener('keydown', function (e) { if (e.key === 'Escape') hideMenu(); });
    window.addEventListener('blur', hideMenu);
    // first view
    if (m.view) go(m.view); else map.fitBounds(bounds, { animate: false, padding: [10, 10] });
    ready = false; refresh(); meRow();
    (m.layers || []).forEach(function (d) { if (on[d.id] && !d.live) { requested[d.id] = true; send({ t: 'need', layer: d.id }); setCount(d.id, '...'); } });
    // a tile layer with no tiles to fetch still reports ready
    setTimeout(function () { if (!ready) { ready = true; send({ t: 'ready', tiles: tilesLoaded, errors: tileErrors }); } }, 6000);
    send({ t: 'inited' });
  }

  // test / diagnostics hook (read-only)
  window.wm = {
    w2ll: w2ll, ll2w: ll2w,
    // the zone borders layer (test): how many polygons, and what pointing at one does to its fill
    borders: function (hoverIndex) {
      var g = groups.borders, polys = []; if (g) g.eachLayer(function (l) { if (l.getLatLngs) polys.push(l); });
      var r = { n: polys.length, rings: polys.reduce(function (a, p) { return a + p.getLatLngs().length; }, 0), onMap: !!(g && map.hasLayer(g)) };
      if (hoverIndex != null && polys[hoverIndex]) { var p = polys[hoverIndex]; r.before = p.options.fillOpacity; p.fire('mouseover'); r.hover = p.options.fillOpacity; p.fire('mouseout'); r.after = p.options.fillOpacity; }
      return JSON.stringify(r);
    },
    // the entrance markers (test): name, icon class, kind group, whether shown, label text; `fire` = [index, event] fires a marker event (click / contextmenu)
    markers: function (id, fire) {
      var g = groups[id], out = []; if (!g) return '[]';
      var ms = []; g.eachLayer(function (m) { ms.push(m); });
      ms.forEach(function (m, i) {
        var e = m.getElement && m.getElement();
        out.push({ i: i, ic: e ? (e.querySelector('.inst') || {}).className || '' : '', g: m._g || '', shown: !!(e && e.style.display !== 'none' && map.hasLayer(g)), lab: e && e.querySelector('.instl') ? e.querySelector('.instl').textContent : '', labVisible: !!(e && e.querySelector('.instl') && getComputedStyle(e.querySelector('.instl')).display !== 'none') });
      });
      if (fire && ms[fire[0]]) ms[fire[0]].fire(fire[1], { latlng: ms[fire[0]].getLatLng(), originalEvent: { clientX: 200, clientY: 200, preventDefault: function () {}, stopPropagation: function () {} } });
      return JSON.stringify(out);
    },
    // the routes of a lines layer by kind (test): [{g, shown}] - shown = the route is in the layer's group (its kind row is on and the layer is on the map)
    feed: function (m) { handle(m); return 1; },      // test: the app's message as if it had come from the app
    setOn: function (id, v) { setOn(id, v); return 1; },      // test: a layer row switched
    // the live layer (test): state, dot (grey?), trail lines and points, notice, how-to link, follow, heading; `click` = 'howto' | 'follow' presses that button
    // the events on the trail (test): how many are drawn, whether the row shows, and `tip` = index -> point at that mark and read the tooltip; `open` = index -> click it
    ev: function (tip, open) {
      var r = { drawn: meEvMarkers.length, have: me && me.events ? me.events.length : 0, on: !!on.trailevents, row: !!(panelRows.trailevents && panelRows.trailevents.row.style.display !== 'none'),
        count: panelRows.trailevents ? panelRows.trailevents.ct.textContent : '', kinds: meEvMarkers.map(function (mk) { var e = mk.getElement && mk.getElement(); var q = e && e.querySelector('.me-ev'); return q ? q.getAttribute('data-k') : ''; }) };
      if (tip != null && meEvMarkers[tip]) { meEvMarkers[tip].fire('mousemove', { originalEvent: { clientX: 220, clientY: 220 } }); var t = $('tip'); r.tip = t.hidden ? '' : t.textContent; r.tipHtml = t.innerHTML; meEvMarkers[tip].fire('mouseout'); }
      if (open != null && meEvMarkers[open]) meEvMarkers[open].fire('click', { originalEvent: { clientX: 220, clientY: 220 } });
      return JSON.stringify(r);
    },
    me: function (click) {
      var d = meCtrl && meCtrl._d;
      if (click && d) { var b = d.querySelector(click === 'howto' ? '.me-link' : click === 'show' ? '.me-show' : '.me-follow'); if (b) b.click(); }
      var lines = 0; if (meGroup) meGroup.eachLayer(function (l) { if (l.getLatLngs) lines++; });
      var de = meDot && meDot.getElement && meDot.getElement();
      var c = map ? map.getCenter() : null, h = me && me.here;
      return JSON.stringify({ state: me ? me.state : null, layer: !!(meGroup && map && map.hasLayer(meGroup)), dot: !!meDot, grey: !!(de && de.querySelector('.me-dot.grey')),
        label: de && de.querySelector('.me-lab') ? de.querySelector('.me-lab').textContent : '', arrow: !!(de && de.querySelector('.me-arrow')), heading: heading(),
        lines: lines, points: meTrail.length, panel: !!d, notice: d && d.querySelector('.me-n') ? d.querySelector('.me-n').textContent : '',
        link: d && d.querySelector('.me-link') ? d.querySelector('.me-link').textContent : '', followBtn: !!(d && d.querySelector('.me-follow')), follow: follow, show: d && d.querySelector('.me-show') ? d.querySelector('.me-show').textContent : '',
        centered: !!(c && h && Math.abs(c.lat - h[0]) < 0.05 && Math.abs(c.lng - h[1]) < 0.05) });
    },
    lines: function (id) {
      var g = groups[id]; if (!g || !lineKinds[id]) return '[]';
      return JSON.stringify(lineKinds[id].filter(function (e) { return !e.layer._line; }).map(function (e) { return { g: e.g, learned: e.learned || '', dash: e.layer.options.dashArray || '', color: e.layer.options.color || '', shown: !!(map.hasLayer(g) && g.hasLayer(e.layer)) }; }));
    },
    state: function () {
      return JSON.stringify({ mode: '2d', ready: ready, zoom: map ? map.getZoom() : null, center: map ? [map.getCenter().lat, map.getCenter().lng] : null, tiles: tilesLoaded, tileErrors: tileErrors,
        on: Object.keys(on).filter(function (k) { return on[k]; }), counts: counts, drawn: drawn, version: L.version });
    }
  };
  send({ t: 'hello', p: '2d' });
})();
