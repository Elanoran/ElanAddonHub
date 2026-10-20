/* Elan's Outpost - World map, the side panel shared by the flat page (worldmap.js) and the 3D page (view3d.js) (3.2).
   Layers are shown in collapsible groups (the app's catalog says each layer's group), the faction rows (fm.horde, routes.alliance, ...) are not listed:
   one switch at the top (All / Alliance / Horde) turns them on and off together; neutral ones always follow "on". A page can add a second tab (the 3D view's
   settings and its build cards) so the layer list stays short. Which groups are open is remembered in this browser profile (a convenience only: it may be
   empty). The page keeps the layer state; this only draws it and calls back. */
(function () {
  'use strict';
  function el(tag, cls, html) { var e = document.createElement(tag); if (cls) e.className = cls; if (html != null) e.innerHTML = html; return e; }
  function esc(s) { return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]; }); }
  var KEY = 'wm.panel.closed';
  function loadClosed() { try { var v = JSON.parse(localStorage.getItem(KEY) || 'null'); return v && typeof v === 'object' ? v : null; } catch (e) { return null; } }
  function saveClosed(v) { try { localStorage.setItem(KEY, JSON.stringify(v)); } catch (e) { /* storage off: groups just open as they start */ } }
  var FACTIONS = [{ id: 'all', title: 'All' }, { id: 'a', title: 'Alliance' }, { id: 'h', title: 'Horde' }];

  // opts: { list, on (the page's state object), set(changes: {id: bool}), tab: { title, fill(container) } or null }
  // returns { root, rows (id -> {row, cb, ct, parent}), defs, sync() }
  function build(opts) {
    var list = opts.list || [], on = opts.on, rows = {}, defs = {}, groups = [], gIndex = {};
    var closed = loadClosed();
    var root = el('div', 'wm-panel');
    var head = el('div', 'wm-head');
    var tabs = el('div', 'wm-tabs');
    var tLayers = el('button', 'on', 'Layers'); tLayers.type = 'button';
    tabs.appendChild(tLayers);
    var tOther = null;
    if (opts.tab) { tOther = el('button', '', esc(opts.tab.title)); tOther.type = 'button'; tabs.appendChild(tOther); }
    var fold = el('button', 'wm-fold', '&#9662;'); fold.type = 'button'; fold.title = 'Hide the panel'; fold.setAttribute('aria-expanded', 'true');
    head.appendChild(tabs); head.appendChild(fold);
    var body = el('div', 'body'), pane2 = null;
    var fac = el('div', 'wm-fac'); fac.setAttribute('role', 'group'); fac.setAttribute('aria-label', 'Faction');
    FACTIONS.forEach(function (f) { var b = el('button', '', f.title); b.type = 'button'; b.setAttribute('data-f', f.id); b.addEventListener('click', function () { setFaction(f.id); }); fac.appendChild(b); });
    body.appendChild(fac);
    root.appendChild(head); root.appendChild(body);
    if (opts.tab) { pane2 = el('div', 'body wm-pane2'); pane2.hidden = true; root.appendChild(pane2); opts.tab.fill(pane2); }
    function showTab(second) {
      tLayers.classList.toggle('on', !second); if (tOther) tOther.classList.toggle('on', second);
      body.hidden = second; if (pane2) pane2.hidden = !second;
    }
    tLayers.addEventListener('click', function () { if (root.classList.contains('collapsed')) toggleRoot(); showTab(false); });
    if (tOther) tOther.addEventListener('click', function () { if (root.classList.contains('collapsed')) toggleRoot(); showTab(true); });
    function toggleRoot() { var c = root.classList.toggle('collapsed'); fold.setAttribute('aria-expanded', String(!c)); fold.title = c ? 'Show the panel' : 'Hide the panel'; }
    fold.addEventListener('click', toggleRoot);

    // ---- groups and rows
    function group(id, title) {
      if (gIndex[id]) return gIndex[id];
      var g = { id: id, title: title, layers: [] };
      g.box = el('div', 'wm-group');
      g.hd = el('button', 'wm-ghd', '<span class="chev" aria-hidden="true">&#9656;</span><span class="gt">' + esc(title) + '</span><span class="gc"></span>'); g.hd.type = 'button';
      g.list = el('div', 'wm-glist');
      g.box.appendChild(g.hd); g.box.appendChild(g.list); body.appendChild(g.box);
      g.hd.addEventListener('click', function () { var open = g.box.classList.toggle('open'); g.hd.setAttribute('aria-expanded', String(open)); var c = loadClosed() || {}; c[id] = !open; saveClosed(c); });
      groups.push(g); gIndex[id] = g; return g;
    }
    function row(d, parent, into) {
      var r = el('label', 'wm-row' + (parent ? ' sub' : '')), cb = el('input'); cb.type = 'checkbox';
      var sw = el('span', 'sw'); sw.style.background = d.color || '#888';
      var nm = el('span', 'nm', esc(d.title)), ct = el('span', 'ct');
      if (d.tip) r.title = d.tip;
      r.appendChild(cb); r.appendChild(sw); r.appendChild(nm); r.appendChild(ct);
      cb.addEventListener('change', function () { var c = {}; c[d.id] = cb.checked; opts.set(c); });
      if (d.faction) r.hidden = true;          // drawn through the faction switch
      into.appendChild(r);
      rows[d.id] = { row: r, cb: cb, ct: ct, parent: parent }; defs[d.id] = d; if (parent) d.child = true;
    }
    list.forEach(function (d) {
      var g = group(d.group || 'other', d.groupTitle || 'More');
      g.layers.push(d);
      row(d, null, g.list); (d.children || []).forEach(function (c) { row(c, d.id, g.list); });
    });
    // a group opens as it was left; the first time, the groups with something on are open (decided at the first sync: the page fills in what is on after building)
    var placed = false;
    function place() {
      placed = true;
      groups.forEach(function (g) {
        var anyOn = g.layers.some(function (d) { return on[d.id]; });
        var open = closed && closed.hasOwnProperty(g.id) ? !closed[g.id] : anyOn;
        g.box.classList.toggle('open', open); g.hd.setAttribute('aria-expanded', String(open));
      });
    }

    // ---- the faction switch: the faction rows of every layer that has them
    function factionRows() { return Object.keys(defs).filter(function (k) { return defs[k].faction; }); }
    function currentFaction() {
      var h = false, a = false, any = false;
      factionRows().forEach(function (k) { if (!on[k]) return; any = true; if (defs[k].faction === 'h') h = true; if (defs[k].faction === 'a') a = true; });
      return !any || (h && a) || (!h && !a) ? 'all' : h ? 'h' : 'a';
    }
    function setFaction(f) {
      var c = {};
      factionRows().forEach(function (k) { var x = defs[k].faction; c[k] = f === 'all' || x === 'n' || x === f; });
      opts.set(c);
    }

    function sync() {
      if (!placed && Object.keys(on).length) place();
      groups.forEach(function (g) {
        var n = 0, total = 0;
        g.layers.forEach(function (d) { total++; if (on[d.id]) n++; });
        g.hd.querySelector('.gc').textContent = n > 0 ? n + ' / ' + total : '';
      });
      var f = currentFaction();
      [].forEach.call(fac.querySelectorAll('button'), function (b) { var o = b.getAttribute('data-f') === f; b.classList.toggle('on', o); b.setAttribute('aria-pressed', String(o)); });
    }
    sync();
    return { root: root, rows: rows, defs: defs, sync: sync, showTab: showTab };
  }

  // ---- the loading steps: a small card at the bottom left while anything is still coming in, each step with its count; gone a moment after the last one
  // steps: [{ id, title, done, total (0 = unknown), busy }]; a step that is not busy and done shows a tick
  function loader() {
    var card = el('div', 'wm-load'); card.hidden = true; card.setAttribute('role', 'status'); card.setAttribute('aria-live', 'polite');
    document.body.appendChild(card);
    var hideAt = 0, shownOnce = false, lastHtml = '';
    function update(steps) {
      var busy = steps.some(function (s) { return s.busy; }), now = Date.now();
      if (busy) { hideAt = 0; }
      else if (!card.hidden && !hideAt) hideAt = now + 1500;
      if (!busy && (card.hidden || (hideAt && now >= hideAt))) { card.hidden = true; return; }
      var html = '<div class="lt">' + (busy ? 'Loading the map' : 'Ready') + '</div>' + steps.map(function (s) {
        var cnt = s.total > 0 ? Math.min(s.done, s.total).toLocaleString() + ' / ' + s.total.toLocaleString() : s.done > 0 ? s.done.toLocaleString() : '';
        return '<div class="ls' + (s.busy ? ' busy' : ' ok') + '"><span class="li" aria-hidden="true">' + (s.busy ? '' : '&#10003;') + '</span><span class="ln">' + esc(s.title) + '</span><span class="lc">' + cnt + '</span></div>';
      }).join('');
      if (html !== lastHtml) { card.innerHTML = html; lastHtml = html; }
      card.hidden = false; shownOnce = true;
    }
    return { update: update, el: card };
  }

  window.WmPanel = { build: build, loader: loader };
})();
