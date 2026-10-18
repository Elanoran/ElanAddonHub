// Elan's Outpost - world map: the paw-print trail (2.88). Shared by the flat page (worldmap.js) and the 3D page (view3d.js).
// Your live trail, drawn as little prints in your class colour once you zoom in far enough (zoomed out it stays the plain line; where the prints show there
// is no line under them since 2.93 - the prints are the trail, and the flight's dots draw its way):
//   on foot   alternating left / right prints along the way you went (paws; hooves for tauren, bony prints for the undead, tiny ones for gnomes)
//   mounted   bigger hoofprints, further apart
//   flight    a light dotted line with now and then a small feather
//   swimming  little ripple rings
//   ghost     faint wisps
//   hunters   a second, smaller line of pet paws trotting beside your own tracks (while the Hub says your pet is out; an older Hub can't say, so always)
// The prints sit at fixed distances of the trail's odometer (the app's "od": yards walked so far), so they stay put while you walk on and while old points
// drop off the end of the 10 minutes. The spacing steps in powers of two with the zoom, so a print is always some 16-32 pixels from the next.
// Drawing: 2D = one canvas in its own pane (above the trail line, below the event marks and your dot), 3D = one InstancedMesh of flat quads lying on the
// ground (tilted to the slope) at the solved height, with a small canvas atlas of the shapes. Nothing is allocated per frame; at most MAX prints.
// The shapes are drawn here as plain vector paths (no image files). New prints fade in unless Windows animations are off (prefers-reduced-motion).
// 2.98: the Lodge members' trails are drawn exactly like yours: window.WmPrints is your own renderer, WmPrints.create() makes one more for a member (its
// own state, canvas and mesh; a smaller cap), and dispose() takes it away again. The layout, shapes, colours and fades are the same code for everyone.
(function () {
  'use strict';
  var YU = 1600 / 3 / 4;                 // yards per plane unit (lat / lng): an ADT tile is 533.33 yards and 4 units
  var BASE = 2.5, MAXS = 20, MINPX = 16; // the spacing ladder: 2.5, 5, 10, 20 yards; wider than 20 = zoomed out, the line stays
  var MINPX3 = 24;                       // 3D: a little roomier (the prints are seen at a slant)
  var MAX = 5000;                        // prints at most (newest kept)
  var MEMBER_CAP = 1500;                 // a Lodge member's renderer (2.98): up to 20 of them
  var LINE_K = 0;                        // the trail line's opacity while prints are shown: none (2.93) - the prints are the trail; zoomed out (no prints) it is the line again
  var BIT = { taxi: 4, indoors: 8, swim: 16, mount: 32, pet: 64, dead: 128 };
  // shape ids (also the atlas cells of the 3D page)
  var SH = { paw: 0, hoof: 1, bone: 2, shoe: 3, dot: 4, feather: 5, ripple: 6, wisp: 7 };
  var FOOT = { paw: SH.paw, hoof: SH.hoof, bone: SH.bone, tiny: SH.paw };

  var MI = { walk: 0, mount: 1, taxi: 2, swim: 3, ghost: 4 }, MODES = ['walk', 'mount', 'taxi', 'swim', 'ghost'];
  function modeOf(bits) {
    bits = bits | 0;
    if (bits & BIT.taxi) return 'taxi';
    if (bits & BIT.dead) return 'ghost';
    if (bits & BIT.swim) return 'swim';
    if (bits & BIT.mount) return 'mount';
    return 'walk';
  }
  // the print streams of a mode: spacing (x the ladder's step), phase, shape, size (x step), sideways offset (x step, alternating), opacity
  function streams(mode, style, bits) {
    var out = [], tiny = style.foot === 'tiny';
    if (mode === 'walk') out.push({ m: tiny ? 0.7 : 1, ph: 0, sh: FOOT[style.foot] != null ? FOOT[style.foot] : SH.paw, sz: tiny ? 0.42 : 0.6, lat: tiny ? 0.14 : 0.2, al: 1, alt: true });
    else if (mode === 'mount') out.push({ m: 2, ph: 0, sh: SH.shoe, sz: 0.85, lat: 0.26, al: 1, alt: true });
    else if (mode === 'swim') out.push({ m: 1.5, ph: 0, sh: SH.ripple, sz: 0.7, lat: 0.07, al: 0.85, alt: true });
    else if (mode === 'ghost') out.push({ m: 1.5, ph: 0, sh: SH.wisp, sz: 0.62, lat: 0.12, al: 0.5, alt: true, ghost: true });
    else if (mode === 'taxi') out.push({ m: 0.75, ph: 0, sh: SH.dot, sz: 0.16, lat: 0, al: 0.7, alt: false, taxi: true });
    // the pet trots on your right, a little out of step with you
    if (style.hunter && (mode === 'walk' || mode === 'mount') && (!style.petKnown || (bits & BIT.pet)))
      out.push({ m: mode === 'mount' ? 1.4 : 0.85, ph: 0.5, sh: SH.paw, sz: 0.34, lat: 0.07, side: 0.66, al: 0.85, alt: true, pet: true });
    return out;
  }

  function lin(v) { return v <= 0.04045 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4); }
  function srgb(v) { return v <= 0.0031308 ? v * 12.92 : 1.055 * Math.pow(v, 1 / 2.4) - 0.055; }
  function fade(age) { return Math.max(0.12, 0.9 * (1 - Math.min(600, age) / 600)); }
  function hexRgb(c) { var v = parseInt(String(c || '#e8e6e3').slice(1), 16); return isFinite(v) ? [(v >> 16) & 255, (v >> 8) & 255, v & 255] : [232, 230, 227]; }
  function ghostOf(c) { var r = hexRgb(c); return 'rgb(' + Math.round(r[0] * 0.45 + 140) + ',' + Math.round(r[1] * 0.45 + 140) + ',' + Math.round(r[2] * 0.45 + 140) + ')'; }

  // ---- the shapes: unit box -0.5..0.5, facing up (-y). Built once.
  function ell(p, cx, cy, rx, ry, rot) { p.moveTo(cx + rx * Math.cos(rot), cy + rx * Math.sin(rot)); p.ellipse(cx, cy, rx, ry, rot, 0, Math.PI * 2); p.closePath(); }
  function shapePaths() {
    var P = [];
    // paw: a soft three-lobed pad and four toes in an arc
    var paw = new Path2D();
    paw.moveTo(-0.27, 0.27); paw.bezierCurveTo(-0.33, 0.08, -0.15, -0.04, 0, -0.04); paw.bezierCurveTo(0.15, -0.04, 0.33, 0.08, 0.27, 0.27);
    paw.bezierCurveTo(0.23, 0.42, 0.1, 0.38, 0, 0.36); paw.bezierCurveTo(-0.1, 0.38, -0.23, 0.42, -0.27, 0.27); paw.closePath();
    ell(paw, -0.33, -0.13, 0.095, 0.125, -0.5); ell(paw, -0.12, -0.33, 0.1, 0.13, -0.15); ell(paw, 0.12, -0.33, 0.1, 0.13, 0.15); ell(paw, 0.33, -0.13, 0.095, 0.125, 0.5);
    P[SH.paw] = paw;
    // cloven hoof (tauren): two half-moons
    var hoof = new Path2D();
    hoof.moveTo(-0.07, -0.46); hoof.bezierCurveTo(-0.22, -0.36, -0.31, -0.04, -0.28, 0.22); hoof.bezierCurveTo(-0.27, 0.42, -0.12, 0.46, -0.08, 0.34); hoof.bezierCurveTo(-0.06, 0.1, -0.06, -0.2, -0.07, -0.46); hoof.closePath();
    hoof.moveTo(0.07, -0.46); hoof.bezierCurveTo(0.22, -0.36, 0.31, -0.04, 0.28, 0.22); hoof.bezierCurveTo(0.27, 0.42, 0.12, 0.46, 0.08, 0.34); hoof.bezierCurveTo(0.06, 0.1, 0.06, -0.2, 0.07, -0.46); hoof.closePath();
    P[SH.hoof] = hoof;
    // bony foot (undead): a heel bone, three thin toe bones fanning out, each with a knuckle
    var bone = new Path2D();
    ell(bone, 0, 0.29, 0.14, 0.16, 0);
    ell(bone, -0.19, -0.06, 0.07, 0.17, -0.38); ell(bone, 0, -0.1, 0.07, 0.19, 0); ell(bone, 0.19, -0.06, 0.07, 0.17, 0.38);
    ell(bone, -0.31, -0.3, 0.085, 0.085, 0); ell(bone, 0, -0.4, 0.085, 0.085, 0); ell(bone, 0.31, -0.3, 0.085, 0.085, 0);
    P[SH.bone] = bone;
    // horseshoe (mounted): a thick U with the toe ahead and the heels behind, four nail holes, a little frog in the middle
    var shoe = new Path2D(), cy = 0.02, op = 0.62;
    shoe.moveTo(-0.36 * Math.cos(op), cy + 0.46 * Math.sin(op));
    shoe.ellipse(0, cy, 0.36, 0.46, 0, Math.PI - op, 2 * Math.PI + op, false);
    shoe.ellipse(0, cy, 0.2, 0.29, 0, 2 * Math.PI + op, Math.PI - op, true);
    shoe.closePath();
    [-2.25, -1.85, -1.29, -0.89].forEach(function (t) { ell(shoe, 0.28 * Math.cos(t), cy + 0.375 * Math.sin(t), 0.035, 0.035, 0); });
    shoe.moveTo(0, -0.04); shoe.lineTo(0.055, 0.22); shoe.lineTo(-0.055, 0.22); shoe.closePath();
    P[SH.shoe] = shoe;
    // dot (a flight)
    var dot = new Path2D(); ell(dot, 0, 0, 0.5, 0.5, 0); P[SH.dot] = dot;
    // feather: two vanes with the shaft showing between them, and the quill
    var fe = new Path2D();
    fe.moveTo(-0.025, -0.48); fe.bezierCurveTo(-0.24, -0.32, -0.24, 0.12, -0.025, 0.34); fe.closePath();
    fe.moveTo(0.025, -0.48); fe.bezierCurveTo(0.24, -0.3, 0.22, 0.14, 0.025, 0.34); fe.closePath();
    fe.moveTo(-0.018, 0.3); fe.lineTo(0.018, 0.3); fe.lineTo(0.012, 0.5); fe.lineTo(-0.012, 0.5); fe.closePath();
    P[SH.feather] = fe;
    // ripple: two rings (filled even-odd)
    var rp = new Path2D(); ell(rp, 0, 0, 0.5, 0.42, 0); ell(rp, 0, 0, 0.41, 0.34, 0); ell(rp, 0, 0, 0.24, 0.2, 0); ell(rp, 0, 0, 0.16, 0.13, 0);
    P[SH.ripple] = rp;
    // wisp (a ghost): a soft drop with a curling tail
    var w = new Path2D();
    w.moveTo(0, -0.46); w.bezierCurveTo(0.26, -0.46, 0.3, -0.06, 0.06, 0.1); w.bezierCurveTo(-0.1, 0.22, 0.08, 0.36, 0.24, 0.4);
    w.bezierCurveTo(0.02, 0.5, -0.22, 0.32, -0.17, 0.12); w.bezierCurveTo(-0.32, -0.06, -0.24, -0.46, 0, -0.46); w.closePath();
    P[SH.wisp] = w;
    return P;
  }
  var PATHS = null;
  function paths() { if (!PATHS && typeof Path2D !== 'undefined') PATHS = shapePaths(); return PATHS; }

  // animations: off with Windows animations off (prefers-reduced-motion), or by a test
  var mq = null; try { mq = window.matchMedia ? window.matchMedia('(prefers-reduced-motion: reduce)') : null; } catch (e) { }
  var forceAnim = null;
  function animOn() { return forceAnim !== null ? forceAnim : !(mq && mq.matches); }
  var FADE_MS = 420;
  function popK(t) { return t >= 1 ? 1 : t <= 0 ? 0 : 1 - Math.pow(1 - t, 3); }

  function atlas() {
    var cv = document.createElement('canvas'), C = 128; cv.width = cv.height = C * 4;
    var g = cv.getContext('2d'), P = paths();
    for (var id = 0; id < 8; id++) {
      var cx = (id % 4) * C + C / 2, cy = Math.floor(id / 4) * C + C / 2, k = C * 0.82;
      g.setTransform(k, 0, 0, k, cx, cy);
      g.lineJoin = 'round'; g.lineWidth = 7 / k; g.strokeStyle = 'rgba(0,0,0,0.6)'; g.fillStyle = '#ffffff';
      if (id !== SH.dot && id !== SH.wisp) g.stroke(P[id]);
      g.fill(P[id], 'evenodd');
    }
    return cv;
  }
  var VS = 'attribute float aCell; attribute float aAlpha; attribute vec3 aCol; varying vec2 vUv; varying float vA; varying vec3 vCol;\n#include <fog_pars_vertex>\n' +
    'void main() { float cx = mod(aCell, 4.0); float cy = floor(aCell / 4.0 + 0.01); vUv = vec2((cx + uv.x) / 4.0, (3.0 - cy + uv.y) / 4.0); vA = aAlpha; vCol = aCol;\n' +
    ' vec4 mvPosition = modelViewMatrix * instanceMatrix * vec4(position, 1.0); gl_Position = projectionMatrix * mvPosition;\n#include <fog_vertex>\n}';
  var FS = 'uniform sampler2D map; varying vec2 vUv; varying float vA; varying vec3 vCol;\n#include <fog_pars_fragment>\n' +
    'void main() { vec4 t = texture2D(map, vUv); float a = t.a * vA; if (a < 0.02) discard; gl_FragColor = vec4(t.rgb * vCol, a);\n#include <encodings_fragment>\n#include <fog_fragment>\n}';
  var TEX3 = null;      // the shapes' atlas texture, one for every renderer
  function atlasTex(THREE) { if (!TEX3) { TEX3 = new THREE.CanvasTexture(atlas()); TEX3.anisotropy = 4; } return TEX3; }
  // ================================================================ one renderer (yours, or a member's): its own arrays, canvas and mesh
  // opt: {cap: prints at most, pane: the flat page's pane for its canvas (z order), z: that pane's z-index}
  function make(opt) {
  opt = opt || {};
  var CAP = opt.cap || MAX, PANE = opt.pane || 'mePrints', PANEZ = opt.z || 450;
  // the streams per mode and pet bit, built once per style (layout runs per segment, so nothing is allocated there)
  var SC = { foot: null, hunter: null, petKnown: null, t: [] };
  function streamsOf(mode, style, bits) {
    var foot = style.foot || 'paw', hu = !!style.hunter, pk = !!style.petKnown;
    if (SC.foot !== foot || SC.hunter !== hu || SC.petKnown !== pk) {
      SC.foot = foot; SC.hunter = hu; SC.petKnown = pk; SC.t = [];
      var st = { foot: foot, hunter: hu, petKnown: pk };
      for (var m = 0; m < MODES.length; m++) { SC.t[m * 2] = streams(MODES[m], st, 0); SC.t[m * 2 + 1] = streams(MODES[m], st, BIT.pet); }
    }
    return SC.t[MI[mode] * 2 + ((bits & BIT.pet) ? 1 : 0)];
  }
  // the step of the ladder for `ppy` pixels per yard (0 = too far out: no prints)
  function ladder(ppy, minpx) {
    if (!(ppy > 0) || !isFinite(ppy)) return 0;
    var s = BASE, mp = minpx || MINPX; while (s * ppy < mp && s <= MAXS) s *= 2;
    return s > MAXS ? 0 : s;
  }

  // ---- the layout (pure, shared by both pages). Input arrays (reused): planar yards x right / y down, odometer, move bits, age (s), run break, height (NaN = ground)
  var IN = { x: [], y: [], od: [], bits: [], age: [], brk: [], h: [], n: 0 };
  // output arrays (reused): position, angle (0 = facing up / north, clockwise), shape, size (yards), opacity factor, age, odometer, ghost, pet, segment + fraction
  var OUT = { x: [], y: [], a: [], sh: [], sz: [], al: [], age: [], od: [], gh: [], pet: [], seg: [], f: [], side: [], mode: [], n: 0, lineFrom: -Infinity };
  function layout(s, style) {
    var o = OUT, inp = IN, n = 0;
    o.n = 0; if (!(s > 0) || inp.n < 2) return 0;
    // newest first, so the cap keeps the newest prints
    for (var i = inp.n - 1; i >= 1 && n < CAP; i--) {
      if (inp.brk[i]) continue;                                   // a break: no prints across the gap
      var oa = inp.od[i - 1], ob = inp.od[i];
      if (!(ob > oa)) continue;
      var x0 = inp.x[i - 1], y0 = inp.y[i - 1], dx = inp.x[i] - x0, dy = inp.y[i] - y0, len = Math.sqrt(dx * dx + dy * dy);
      if (!(len > 1e-6)) continue;
      var fx = dx / len, fy = dy / len, ang = Math.atan2(fx, -fy), rx = -fy, ry = fx;
      var bits = inp.bits[i], mode = modeOf(bits), list = streamsOf(mode, style, bits);
      for (var q = 0; q < list.length && n < CAP; q++) {
        var st = list[q], step = s * st.m;
        // the multiples of the step inside (oa, ob], newest first
        var kHi = Math.floor((ob - st.ph * step) / step), kLo = Math.floor((oa - st.ph * step) / step) + 1;
        for (var k = kHi; k >= kLo && n < CAP; k--) {
          var d = (k + st.ph) * step, f = (d - oa) / (ob - oa);
          if (f < 0 || f > 1) continue;
          var side = st.alt ? ((k & 1) ? 1 : -1) : 0, off = (st.side || 0) * s + side * st.lat * s;
          var sh = st.sh, sz = st.sz * s, al = st.al, a = ang;
          if (st.taxi && k % 10 === 0) { sh = SH.feather; sz = 0.55 * s; al = 0.9; a = ang + ((k / 10) & 1 ? 0.5 : -0.5); }
          o.x[n] = x0 + dx * f + rx * off; o.y[n] = y0 + dy * f + ry * off; o.a[n] = a; o.sh[n] = sh; o.sz[n] = sz; o.al[n] = al;
          o.age[n] = inp.age[i - 1] + (inp.age[i] - inp.age[i - 1]) * f; o.od[n] = d; o.gh[n] = st.ghost ? 1 : 0; o.pet[n] = st.pet ? 1 : 0;
          o.seg[n] = i; o.f[n] = f; o.side[n] = side; o.mode[n] = MI[mode];
          n++;
        }
      }
    }
    o.n = n;
    // capped (a long flight far zoomed in): the older part of the trail has no prints, so the line still draws there (lineFrom)
    var lo = Infinity; if (n >= CAP) for (var j = 0; j < n; j++) if (o.od[j] < lo) lo = o.od[j];
    o.lineFrom = n >= CAP ? lo : -Infinity;
    return n;
  }
  // fill the input from the page's trail points ({ll:[lat,lng], age, brk, k, h}) + the app's mv / od, and the stretch from the last point to you
  function input(me, trail, extra) {
    var pr = me && me.prints, inp = IN, n = 0;
    inp.n = 0; if (!pr || !trail) return 0;
    for (var i = 0; i < trail.length; i++) {
      var p = trail[i], k = p.k;
      var od = pr.od && k != null ? pr.od[k] : NaN, bits = pr.mv && k != null ? pr.mv[k] : 0;
      inp.x[n] = p.ll[1] * YU; inp.y[n] = -p.ll[0] * YU; inp.age[n] = p.age + extra; inp.brk[n] = p.brk || n === 0 ? 1 : 0;
      inp.bits[n] = typeof bits === 'number' ? bits : 0; inp.h[n] = p.h != null ? p.h : NaN;
      // no odometer from the app (should not happen): count it here
      if (typeof od !== 'number' || !isFinite(od)) od = n > 0 ? inp.od[n - 1] + Math.hypot(inp.x[n] - inp.x[n - 1], inp.y[n] - inp.y[n - 1]) : 0;
      inp.od[n] = od; n++;
    }
    if (n > 0 && me.state === 'ok' && me.here) {
      var hx = me.here[1] * YU, hy = -me.here[0] * YU, step = Math.hypot(hx - inp.x[n - 1], hy - inp.y[n - 1]);
      if (step > 0.05 && step <= 200) {
        inp.x[n] = hx; inp.y[n] = hy; inp.age[n] = extra; inp.brk[n] = 0; inp.bits[n] = typeof pr.hmv === 'number' ? pr.hmv : inp.bits[n - 1];
        inp.h[n] = me.hh != null ? me.hh : NaN; inp.od[n] = inp.od[n - 1] + step; n++;
      }
    }
    inp.n = n;
    return n;
  }
  function take(m) {
    var p = m && m.prints;
    if (!p || typeof p !== 'object') return null;
    var foot = { paw: 1, hoof: 1, bone: 1, tiny: 1 }[p.foot] ? p.foot : 'paw';
    return { mv: Array.isArray(m.mv) ? m.mv : [], od: Array.isArray(m.od) ? m.od : [], hmv: typeof m.hmv === 'number' ? m.hmv : 0, style: { foot: foot, hunter: p.hunter === true, petKnown: p.petKnown === true } };
  }
  // ================================================================ the flat page (Leaflet)
  var R2 = { map: null, cv: null, ctx: null, s: 0, on: false, me: null, trail: null, extra: 0, color: '#e8e6e3', ghost: '#ccc', redraw: null, onMove: null,
    lastOd: -1, newFrom: Infinity, t0: 0, raf: 0, frames: 0, painted: 0, w: 0, h: 0, dpr: 1, style: null };
  function ppy2d(map) { return Math.pow(2, map.getZoom()) / YU; }
  function attach2d(map, redraw) {
    R2.redraw = redraw;
    if (R2.map === map && R2.cv) return;
    R2.map = map; R2.lastOd = -1;
    var pane = map.getPane(PANE) || map.createPane(PANE);
    pane.style.zIndex = PANEZ; pane.style.pointerEvents = 'none';
    var cv = document.createElement('canvas'); cv.className = 'me-prints'; cv.style.position = 'absolute'; cv.style.left = '0'; cv.style.top = '0'; cv.setAttribute('aria-hidden', 'true');
    pane.appendChild(cv); R2.cv = cv; R2.ctx = cv.getContext('2d');
    R2.onMove = function () {
      if (R2.map !== map) return;
      var s = R2.on ? ladder(ppy2d(map)) : 0;
      if ((s > 0) !== (R2.s > 0) && R2.redraw) { R2.redraw(); return; }      // prints appear / go: the line changes too
      if (s !== R2.s) { var lf = OUT.lineFrom; R2.s = s; relayout2d(); if (OUT.lineFrom !== lf && R2.redraw) { R2.redraw(); return; } }      // capped: the line's part moved
      paint2d();
    };
    map.on('moveend zoomend resize viewreset', R2.onMove);
  }
  function relayout2d() {
    input(R2.me, R2.trail, R2.extra);
    layout(R2.s, R2.style || { foot: 'paw' });
  }
  // the line's opacity factor (worldmap.js: 1 = as before; LINE_K = prints are shown, the line is a faint guide)
  function lineK2d(map, on, me) { return map && on && on.trailprints && me && me.prints ? (ladder(ppy2d(map)) > 0 ? LINE_K : 1) : 1; }
  // where the prints show the line is not drawn (LINE_K 0); only a capped layout (MAX prints, the newest kept) leaves the older way without prints, and there
  // the line stays: lineFrom() = the odometer below which it is drawn (-Infinity = nowhere), lineHas(me, p, from) = trail point p is on that part
  function lineFrom() { return OUT.n > 0 ? OUT.lineFrom : -Infinity; }
  function lineHas(me, p, from) {
    var od = me && me.prints && me.prints.od && p && p.k != null ? me.prints.od[p.k] : NaN;
    return typeof od === 'number' && isFinite(od) && od < from;
  }
  function draw2d(map, me, trail, extra, on, redraw) {
    attach2d(map, redraw);
    R2.on = !!(on && on.trailprints) && !!(me && me.prints);
    R2.me = me; R2.trail = trail; R2.extra = extra; R2.color = me && me.color || '#e8e6e3'; R2.ghost = ghostOf(R2.color); R2.style = me && me.prints ? me.prints.style : null;
    R2.s = R2.on ? ladder(ppy2d(map)) : 0;
    relayout2d();
    // the prints that are new since the last message fade in
    var mx = -1; for (var i = 0; i < OUT.n; i++) if (OUT.od[i] > mx) mx = OUT.od[i];
    R2.newFrom = R2.lastOd >= 0 && animOn() ? R2.lastOd : Infinity; if (mx > R2.lastOd || OUT.n === 0 || mx < R2.lastOd - 400) R2.lastOd = mx;
    R2.t0 = Date.now();
    paint2d();
    if (R2.newFrom !== Infinity && !R2.raf && OUT.n) R2.raf = requestAnimationFrame(anim2d);
  }
  function anim2d() {
    R2.raf = 0; R2.frames++;
    paint2d();
    if (Date.now() - R2.t0 < FADE_MS && animOn()) R2.raf = requestAnimationFrame(anim2d); else { R2.newFrom = Infinity; paint2d(); }
  }
  function clear2d() { R2.me = null; R2.trail = null; R2.on = false; R2.s = 0; OUT.n = 0; IN.n = 0; R2.lastOd = -1; if (R2.cv) { R2.cv.width = 0; R2.cv.height = 0; R2.w = 0; R2.h = 0; } }
  // a member's renderer goes (they stopped sharing, left the map): its canvas, its listener and its 3D mesh
  function dispose() {
    clear2d();
    if (R2.raf) { cancelAnimationFrame(R2.raf); R2.raf = 0; }
    if (R2.map && R2.onMove) R2.map.off('moveend zoomend resize viewreset', R2.onMove);
    if (R2.cv && R2.cv.parentNode) R2.cv.parentNode.removeChild(R2.cv);
    R2.map = null; R2.cv = null; R2.ctx = null;
    if (R3.raf) { cancelAnimationFrame(R3.raf); R3.raf = 0; }
    if (R3.mesh) { if (R3.scene) R3.scene.remove(R3.mesh); R3.mesh.geometry.dispose(); R3.mesh.material.dispose(); }
    R3.mesh = null; R3.scene = null; R3.ctx = null; R3.n = 0;
  }
  function paint2d() {
    var map = R2.map, cv = R2.cv, ctx = R2.ctx; if (!map || !cv || !ctx) return;
    var size = map.getSize(), pad = 0.3, w = Math.round(size.x * (1 + 2 * pad)), h = Math.round(size.y * (1 + 2 * pad)), dpr = window.devicePixelRatio || 1;
    var tl = map.containerPointToLayerPoint([-size.x * pad, -size.y * pad]);
    L.DomUtil.setPosition(cv, tl);
    R2.painted = 0;
    var P = paths(), z = map.getZoom(), sc = Math.pow(2, z), org = map.getPixelOrigin(), k = sc / YU, ox = -org.x - tl.x, oy = -org.y - tl.y, any = false;
    // nothing to paint in view (prints off, zoomed out, or all elsewhere): the canvas holds no pixels at all (cheap with many members' renderers)
    if (R2.on && R2.s > 0 && P) for (var j = 0; j < OUT.n && !any; j++) { var qx = OUT.x[j] * k + ox, qy = OUT.y[j] * k + oy; any = qx >= -24 && qy >= -24 && qx <= w + 24 && qy <= h + 24; }
    if (!any) { if (cv.width) { cv.width = 0; cv.height = 0; R2.w = 0; R2.h = 0; } return; }
    if (R2.w !== w || R2.h !== h || R2.dpr !== dpr) { cv.width = Math.round(w * dpr); cv.height = Math.round(h * dpr); cv.style.width = w + 'px'; cv.style.height = h + 'px'; R2.w = w; R2.h = h; R2.dpr = dpr; }
    ctx.setTransform(1, 0, 0, 1, 0, 0); ctx.clearRect(0, 0, cv.width, cv.height);
    var now = Date.now(), dt = (now - R2.t0) / FADE_MS, extraNow = (now - R2.t0) / 1000, fill = '';
    ctx.lineJoin = 'round'; ctx.strokeStyle = 'rgba(0,0,0,0.55)';
    for (var i = OUT.n - 1; i >= 0; i--) {                       // oldest first: newer prints on top
      var px = OUT.x[i] * k + ox, py = OUT.y[i] * k + oy;
      if (px < -24 || py < -24 || px > w + 24 || py > h + 24) continue;
      var sz = OUT.sz[i] * k, a = fade(OUT.age[i] + extraNow) * OUT.al[i];
      if (OUT.od[i] > R2.newFrom) { var t = popK(dt); a *= t; sz *= 0.6 + 0.4 * t; }
      if (a <= 0.01 || sz < 1) continue;
      var c = Math.cos(OUT.a[i]) * sz * dpr, s = Math.sin(OUT.a[i]) * sz * dpr;
      ctx.setTransform(c, s, -s, c, px * dpr, py * dpr);
      var f = OUT.gh[i] ? R2.ghost : R2.color; if (f !== fill) { ctx.fillStyle = f; fill = f; }
      ctx.globalAlpha = a;
      if (!OUT.gh[i] && OUT.sh[i] !== SH.dot) { ctx.lineWidth = 1.7 / sz; ctx.stroke(P[OUT.sh[i]]); }
      ctx.fill(P[OUT.sh[i]], 'evenodd');
      R2.painted++;
    }
    ctx.globalAlpha = 1; ctx.setTransform(1, 0, 0, 1, 0, 0);
  }
  // the panel row "Paw prints": only there while the live layer is (like "Events on my trail")
  function row(panelRows, live) { var r = panelRows && panelRows.trailprints; if (r) r.row.style.display = live ? '' : 'none'; }

  // ================================================================ the 3D page (three.js)
  var R3 = { mesh: null, scene: null, s: 0, n: 0, on: false, lastOd: -1, newFrom: Infinity, t0: 0, raf: 0, frames: 0, ctx: null, cap: CAP, lift: 0.5, refade: false, relayouts: 0, zoomCalls: 0 };
  function ensure3d(THREE, scene) {
    if (R3.mesh && R3.scene === scene) return R3.mesh;
    if (R3.mesh && R3.scene) R3.scene.remove(R3.mesh);
    var geo = new THREE.PlaneGeometry(1, 1); geo.rotateX(-Math.PI / 2);          // flat, facing up; the texture's top points to -z (= forward)
    geo.setAttribute('aCell', new THREE.InstancedBufferAttribute(new Float32Array(R3.cap), 1));
    geo.setAttribute('aAlpha', new THREE.InstancedBufferAttribute(new Float32Array(R3.cap), 1));
    geo.setAttribute('aCol', new THREE.InstancedBufferAttribute(new Float32Array(R3.cap * 3), 3));
    var tex = atlasTex(THREE);
    var mat = new THREE.ShaderMaterial({ uniforms: THREE.UniformsUtils.merge([THREE.UniformsLib.fog, { map: { value: null } }]), vertexShader: VS, fragmentShader: FS,
      transparent: true, depthWrite: false, fog: true, side: THREE.DoubleSide, polygonOffset: true, polygonOffsetFactor: -4, polygonOffsetUnits: -4 });
    mat.uniforms.map.value = tex;
    var mesh = new THREE.InstancedMesh(geo, mat, R3.cap); mesh.count = 0; mesh.frustumCulled = false; mesh.renderOrder = 6; mesh.name = 'trailprints';
    scene.add(mesh); R3.mesh = mesh; R3.scene = scene;
    return mesh;
  }
  // pixels per yard at the camera's target for a perspective camera (fov in degrees)
  function ppy3d(dist, viewH, fov) { return viewH / (2 * dist * Math.tan((fov || 48) * Math.PI / 360)); }
  function lineK3d(on, me, dist, viewH) { return on && on.trailprints && me && me.prints ? (ladder(ppy3d(dist, viewH), MINPX3) > 0 ? LINE_K : 1) : 1; }
  function hide3d() { if (R3.mesh) { R3.mesh.count = 0; R3.mesh.visible = false; } R3.n = 0; R3.on = false; R3.lastOd = -1; }
  // ctx: { THREE, scene, me, trail, extra, on, groundYE(x, z), surfaceYE(x, z), exag, dist, viewH, dirty() }
  function draw3d(ctx) {
    R3.ctx = ctx;
    var me = ctx.me, mesh = ensure3d(ctx.THREE, ctx.scene);
    R3.on = !!(ctx.on && ctx.on.trailprints && me && me.prints);
    R3.s = R3.on ? ladder(ppy3d(ctx.dist, ctx.viewH), MINPX3) : 0;
    input(me, ctx.trail, ctx.extra);
    var n = R3.s > 0 ? layout(R3.s, me.prints.style) : 0;
    R3.n = n;
    var mx = -1; for (var i = 0; i < n; i++) if (OUT.od[i] > mx) mx = OUT.od[i];
    R3.newFrom = R3.lastOd >= 0 && animOn() ? R3.lastOd : Infinity; if (mx > R3.lastOd || n === 0 || mx < R3.lastOd - 400) R3.lastOd = mx;
    R3.t0 = R3.at = Date.now(); R3.refade = false;
    place3d(0);
    mesh.visible = n > 0;
    if (R3.newFrom !== Infinity && n && !R3.raf) R3.raf = requestAnimationFrame(anim3d);
  }
  function anim3d() {
    R3.raf = 0; R3.frames++;
    var t = (Date.now() - R3.t0) / (R3.refade ? REFADE_MS : FADE_MS);
    alpha3d(t);
    if (t < 1 && animOn()) R3.raf = requestAnimationFrame(anim3d); else { R3.newFrom = Infinity; R3.refade = false; alpha3d(1); }
  }
  // every frame of the 3D page (2.93): the spacing ladder follows the camera distance at once (before, the prints were laid out again only when the distance
  // had changed about 35 %, so a slow zoom left the spacing off for a moment). Nothing happens while the step stays; a new step lays the prints out again in
  // the arrays already there (no allocation) and they ease in from 40 % over a quarter second (at once with Windows animations off).
  // Returns 0 = nothing to do, 1 = laid out again, 2 = prints appear or go: the page redraws (the trail line comes or goes with them).
  var REFADE_MS = 260;
  function zoom3d(dist, viewH) {
    R3.zoomCalls++;
    var ctx = R3.ctx; if (!ctx || !R3.on || !R3.mesh) return 0;
    var s = ladder(ppy3d(dist, viewH), MINPX3);
    if (s === R3.s) return 0;
    var was = R3.s; R3.s = s; ctx.dist = dist; ctx.viewH = viewH;
    if ((s > 0) !== (was > 0)) return 2;
    var me = ctx.me; input(me, ctx.trail, ctx.extra);
    R3.n = layout(s, me.prints.style); R3.relayouts++;
    var anim = animOn(); R3.newFrom = anim ? -Infinity : Infinity; R3.refade = anim; R3.t0 = Date.now();
    place3d();
    R3.mesh.visible = R3.n > 0;
    if (anim && R3.n && !R3.raf) R3.raf = requestAnimationFrame(anim3d);
    return 1;
  }
  // the heights of a print: the solved height (a floor, the water, the air) between its two trail points, else the ground (or the water surface when swimming)
  var Y3 = { y: 0, ground: false };      // y3d's answer (one object, reused: place3d runs per print)
  function y3d(ctx, i, x, z) {
    var sg = OUT.seg[i], f = OUT.f[i], ha = IN.h[sg - 1], hb = IN.h[sg], ex = ctx.exag;
    if (isNaN(ha) && isNaN(hb)) { Y3.y = OUT.mode[i] === MI.swim ? ctx.surfaceYE(x, z) : ctx.groundYE(x, z); Y3.ground = OUT.mode[i] !== MI.swim; return Y3; }
    var ax = IN.x[sg - 1], az = IN.y[sg - 1], bx = IN.x[sg], bz = IN.y[sg];
    var ya = !isNaN(ha) ? ha * ex : ctx.surfaceYE(ax, az), yb = !isNaN(hb) ? hb * ex : ctx.surfaceYE(bx, bz);
    Y3.y = ya * (1 - f) + yb * f; Y3.ground = false; return Y3;
  }
  var NRM = [0, 1, 0];
  function normalAt(ctx, x, z) {
    var e = 1.5, hx = (ctx.groundYE(x + e, z) - ctx.groundYE(x - e, z)) / (2 * e), hz = (ctx.groundYE(x, z + e) - ctx.groundYE(x, z - e)) / (2 * e);
    var l = Math.sqrt(hx * hx + 1 + hz * hz); NRM[0] = -hx / l; NRM[1] = 1 / l; NRM[2] = -hz / l;
    return NRM;
  }
  function place3d() {
    var ctx = R3.ctx, mesh = R3.mesh, n = R3.n; if (!ctx || !mesh) return;
    var M = mesh.instanceMatrix.array, ga = mesh.geometry.attributes, cell = ga.aCell.array, col = ga.aCol.array;
    // the page renders with sRGB output: the class colour goes in linear, so it comes out exactly as the dot's
    var c = hexRgb(ctx.me && ctx.me.color), cr = lin(c[0] / 255), cg = lin(c[1] / 255), cb = lin(c[2] / 255), wr = lin(c[0] / 255 * 0.45 + 0.55), wg = lin(c[1] / 255 * 0.45 + 0.55), wb = lin(c[2] / 255 * 0.45 + 0.55);
    for (var i = 0; i < n; i++) {
      var x = OUT.x[i], z = OUT.y[i], hy = y3d(ctx, i, x, z), nr = hy.ground ? normalAt(ctx, x, z) : (NRM[0] = 0, NRM[1] = 1, NRM[2] = 0, NRM);
      var nx = nr[0], ny = nr[1], nz = nr[2], sz = OUT.sz[i];
      // forward on the ground: the travel direction (angle 0 = -z), flattened onto the slope
      var fx = Math.sin(OUT.a[i]), fz = -Math.cos(OUT.a[i]), dp = fx * nx + fz * nz;
      var tx = fx - nx * dp, ty = -ny * dp, tz = fz - nz * dp, tl = Math.sqrt(tx * tx + ty * ty + tz * tz) || 1; tx /= tl; ty /= tl; tz /= tl;
      var rx = ty * nz - tz * ny, ry = tz * nx - tx * nz, rz = tx * ny - ty * nx;   // right = forward x normal
      var o = i * 16, lift = R3.lift + sz * 0.03;
      M[o] = rx * sz; M[o + 1] = ry * sz; M[o + 2] = rz * sz; M[o + 3] = 0;
      M[o + 4] = nx * sz; M[o + 5] = ny * sz; M[o + 6] = nz * sz; M[o + 7] = 0;
      M[o + 8] = -tx * sz; M[o + 9] = -ty * sz; M[o + 10] = -tz * sz; M[o + 11] = 0;
      M[o + 12] = x + nx * lift; M[o + 13] = hy.y + ny * lift; M[o + 14] = z + nz * lift; M[o + 15] = 1;
      cell[i] = OUT.sh[i];
      if (OUT.gh[i]) { col[i * 3] = wr; col[i * 3 + 1] = wg; col[i * 3 + 2] = wb; }
      else { col[i * 3] = cr; col[i * 3 + 1] = cg; col[i * 3 + 2] = cb; }
    }
    mesh.count = n;
    mesh.instanceMatrix.needsUpdate = true; ga.aCell.needsUpdate = true; ga.aCol.needsUpdate = true;
    alpha3d(R3.newFrom === Infinity ? 1 : 0);
  }
  function alpha3d(t) {
    var mesh = R3.mesh, n = R3.n; if (!mesh) return;
    var al = mesh.geometry.attributes.aAlpha.array, k = R3.refade ? 0.4 + 0.6 * popK(t) : popK(t), extraNow = (Date.now() - (R3.at || R3.t0)) / 1000;
    for (var i = 0; i < n; i++) { var a = fade(OUT.age[i] + extraNow) * OUT.al[i]; if (OUT.od[i] > R3.newFrom) a *= k; al[i] = a; }
    mesh.geometry.attributes.aAlpha.needsUpdate = true;
    if (R3.ctx && R3.ctx.dirty) R3.ctx.dirty();
  }

  // ================================================================ tests / diagnostics (read-only)
  function summary(n) {
    var kinds = {}, modes = {}, pets = 0;
    for (var i = 0; i < n; i++) { var kk = ['paw', 'hoof', 'bone', 'shoe', 'dot', 'feather', 'ripple', 'wisp'][OUT.sh[i]]; kinds[kk] = (kinds[kk] || 0) + 1; modes[MODES[OUT.mode[i]]] = (modes[MODES[OUT.mode[i]]] || 0) + 1; if (OUT.pet[i]) pets++; }
    return { kinds: kinds, modes: modes, pets: pets };
  }
  function list(n) {
    var out = [];
    for (var i = 0; i < n; i++) out.push({ x: Math.round(OUT.x[i] * 100) / 100, y: Math.round(OUT.y[i] * 100) / 100, a: Math.round(OUT.a[i] * 1000) / 1000, k: ['paw', 'hoof', 'bone', 'shoe', 'dot', 'feather', 'ripple', 'wisp'][OUT.sh[i]],
      sz: Math.round(OUT.sz[i] * 100) / 100, od: Math.round(OUT.od[i] * 100) / 100, side: OUT.side[i], pet: !!OUT.pet[i], ghost: !!OUT.gh[i], mode: MODES[OUT.mode[i]], age: Math.round(OUT.age[i] * 10) / 10, al: OUT.al[i] });
    return out;
  }
  return {
    take: take, draw2d: draw2d, clear2d: clear2d, lineK2d: lineK2d, row: row, draw3d: draw3d, hide3d: hide3d, lineK3d: lineK3d, zoom3d: zoom3d, lineFrom: lineFrom, lineHas: lineHas, dispose: dispose,
    // test: the layout of a made-up trail. pts = [{x, y, od, bits, age, brk}] (planar yards), style = {foot, hunter, petKnown}
    layoutForTest: function (pts, s, style) {
      IN.n = 0;
      (pts || []).forEach(function (p, i) { IN.x[i] = p.x; IN.y[i] = p.y; IN.od[i] = p.od; IN.bits[i] = p.bits || 0; IN.age[i] = p.age || 0; IN.brk[i] = p.brk || i === 0 ? 1 : 0; IN.h[i] = NaN; IN.n = i + 1; });
      var n = layout(s, style || { foot: 'paw' });
      return JSON.stringify(list(n));
    },
    ladderForTest: function (ppy) { return ladder(ppy); },
    ladder3dForTest: function (dist, viewH) { return ladder(ppy3d(dist, viewH), MINPX3); },
    lineFromForTest: function () { return OUT.lineFrom === -Infinity ? null : OUT.lineFrom; },
    pathsForTest: function () { return paths(); },
    zoomForTest: function (z) { return ladder(Math.pow(2, z) / YU); },
    setAnimForTest: function (v) { forceAnim = v === null || v === undefined ? null : !!v; return animOn(); },
    fadeForTest: function (age) { return fade(age); },
    state2d: function (withList) {
      var m = summary(R2.on && R2.s > 0 ? OUT.n : 0);
      var r = { canvas: !!R2.cv, on: R2.on, s: R2.s, n: R2.on && R2.s > 0 ? OUT.n : 0, painted: R2.painted, color: R2.color, ghost: R2.ghost, lineK: lineK2d(R2.map, R2.on ? { trailprints: 1 } : null, R2.me), anim: animOn(), frames: R2.frames,
        zoom: R2.map ? R2.map.getZoom() : null, kinds: m.kinds, modes: m.modes, pets: m.pets, paneZ: R2.cv ? +R2.cv.parentNode.style.zIndex : 0 };
      if (withList) r.list = list(r.n);
      return JSON.stringify(r);
    },
    // the colour of the canvas pixel at a container point (test: the prints are painted in the class colour)
    pixel2d: function (cx, cy) {
      if (!R2.cv || !R2.map) return null;
      var size = R2.map.getSize(), x = Math.round((cx + size.x * 0.3) * R2.dpr), y = Math.round((cy + size.y * 0.3) * R2.dpr), d = R2.ctx.getImageData(x, y, 1, 1).data, best = [d[0], d[1], d[2], d[3]];
      return JSON.stringify(best);
    },
    // the painted colour of print i (newest = 0) on the flat page: the canvas pixel a little behind its centre (inside a paw's pad), with its container point
    pixelOf2d: function (i) {
      if (!R2.cv || !R2.map || i < 0 || i >= OUT.n) return null;
      var bx = -Math.sin(OUT.a[i]) * 0.18 * OUT.sz[i], by = Math.cos(OUT.a[i]) * 0.18 * OUT.sz[i];
      var cp = R2.map.latLngToContainerPoint([-(OUT.y[i] + by) / YU, (OUT.x[i] + bx) / YU]), px = JSON.parse(this.pixel2d(cp.x, cp.y));
      return JSON.stringify({ x: cp.x, y: cp.y, rgba: px, k: ['paw', 'hoof', 'bone', 'shoe', 'dot', 'feather', 'ripple', 'wisp'][OUT.sh[i]], age: OUT.age[i] });
    },
    // the 3D prints: count, kinds, and how each lies: its height over the ground (the solved height where there is one), its up vector against the slope
    state3d: function (withList) {
      var m = summary(R3.n), ctx = R3.ctx, worstY = 0, worstN = 0, maxTilt = 0, air = 0, ground = 0;
      if (R3.mesh && ctx) {
        var M = R3.mesh.instanceMatrix.array;
        for (var i = 0; i < R3.n; i++) {
          var o = i * 16, sz = OUT.sz[i], ux = M[o + 4] / sz, uy = M[o + 5] / sz, uz = M[o + 6] / sz, x = OUT.x[i], z = OUT.y[i], hy = y3d(ctx, i, x, z);
          var y = M[o + 13] - uy * (R3.lift + sz * 0.03);
          worstY = Math.max(worstY, Math.abs(y - hy.y));
          if (hy.ground) {
            ground++;
            // the slope measured again with a finer step: the print's up should be the ground's normal
            var e = 0.5, hx = (ctx.groundYE(x + e, z) - ctx.groundYE(x - e, z)) / (2 * e), hz = (ctx.groundYE(x, z + e) - ctx.groundYE(x, z - e)) / (2 * e), l = Math.sqrt(hx * hx + 1 + hz * hz);
            var dot = (ux * -hx + uy + uz * -hz) / l; worstN = Math.max(worstN, Math.acos(Math.min(1, dot)) * 180 / Math.PI);
            maxTilt = Math.max(maxTilt, Math.acos(Math.min(1, uy)) * 180 / Math.PI);
          } else if (y - ctx.groundYE(x, z) > 20) air++;
        }
      }
      var r = { on: R3.on, s: R3.s, n: R3.n, count: R3.mesh ? R3.mesh.count : 0, relayouts: R3.relayouts, zoomCalls: R3.zoomCalls, refade: R3.refade, lineFrom: OUT.lineFrom === -Infinity ? null : OUT.lineFrom,
        want: R3.ctx ? ladder(ppy3d(R3.ctx.dist, R3.ctx.viewH), MINPX3) : null, visible: !!(R3.mesh && R3.mesh.visible), inScene: !!(R3.mesh && R3.scene && R3.mesh.parent === R3.scene), kinds: m.kinds, modes: m.modes, pets: m.pets,
        worstY: Math.round(worstY * 1000) / 1000, worstNormalDeg: Math.round(worstN * 100) / 100, maxTiltDeg: Math.round(maxTilt * 100) / 100, air: air, ground: ground, anim: animOn(), frames: R3.frames,
        color: R3.mesh && R3.n ? [R3.mesh.geometry.attributes.aCol.array[0], R3.mesh.geometry.attributes.aCol.array[1], R3.mesh.geometry.attributes.aCol.array[2]].map(function (v) { return Math.round(srgb(v) * 255); }) : null };
      if (withList) r.list = list(R3.n);
      return JSON.stringify(r);
    }
  };
  }      // make()

  window.WmPrints = make();
  // a member's renderer: the same drawing with its own state; fewer prints at most, and on the flat page a pane just under yours
  window.WmPrints.create = function () { return make({ cap: MEMBER_CAP, pane: 'memPrints', z: 449 }); };
})();
