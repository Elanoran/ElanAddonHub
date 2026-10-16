/* Elan's Outpost - World map marker emblems, shared by the flat page and the 3D page (one table, no external files, no game art).
   Three small original emblems for the flight masters, each a few path commands on a 24 x 24 grid:
     a  Alliance: a plain heater shield with one upward chevron stroke, blue with a light gold rim
     h  Horde: a round badge with eight short spikes (a jagged ring), red with a dark rim
     n  Neutral: a round coin with a pair of scales, gold with a darker rim
   The fills are the same colours as the layer panel's swatches (WorldMapLayers.Catalog). */
(function () {
  'use strict';
  function spikes(cx, cy, ro, ri, n) {
    var d = '';
    for (var i = 0; i < n * 2; i++) {
      var a = -Math.PI / 2 + i * Math.PI / n, r = i % 2 ? ri : ro;
      d += (i ? 'L' : 'M') + (cx + Math.cos(a) * r).toFixed(2) + ' ' + (cy + Math.sin(a) * r).toFixed(2);
    }
    return d + 'Z';
  }
  var F = {
    a: { fill: '#5aa9ff', rim: '#f2d98a', rimW: 1.8, body: 'M12 2.2L20.8 5V12.2C20.8 17 17 20.6 12 22C7 20.6 3.2 17 3.2 12.2V5Z', mark: 'M7.6 13.4L12 8.6L16.4 13.4', markC: '#fff', markW: 2.2, title: 'Alliance' },
    h: { fill: '#ff6b5e', rim: '#4a1411', rimW: 1.6, body: spikes(12, 12, 10.6, 7.4, 8), mark: 'M8.4 12H15.6M12 8.4V15.6', markC: '#3a0e0b', markW: 1.8, title: 'Horde' },
    n: { fill: '#f0b429', rim: '#5a430f', rimW: 1.6, body: 'M12 2.6A9.4 9.4 0 1 1 11.99 2.6Z', mark: 'M12 6.8V16.4M7.8 8.6H16.2M6.6 13L8.6 9L10.6 13ZM13.4 13L15.4 9L17.4 13Z', markC: '#4a350a', markW: 1.4, title: 'Neutral' }
  };
  function svg(f, size) {
    var s = F[f] || F.n, px = size || 20;
    return '<svg class="fmsvg" viewBox="0 0 24 24" width="' + px + '" height="' + px + '" aria-hidden="true">' +
      '<path d="' + s.body + '" fill="' + s.fill + '" stroke="' + s.rim + '" stroke-width="' + s.rimW + '" stroke-linejoin="round"/>' +
      '<path d="' + s.mark + '" fill="none" stroke="' + s.markC + '" stroke-width="' + s.markW + '" stroke-linecap="round" stroke-linejoin="round"/></svg>';
  }
  // the marks on your live trail (kill, loot ...): a small dark disc with a ring and a line glyph in the event's colour (loot: the item's quality colour)
  var EV = {
    kill: 'M8 8L16 16M16 8L8 16',
    loot: 'M12 6.5L16.5 12L12 17.5L7.5 12Z',
    herb: 'M7.5 16.5C7.5 10.5 10.5 7.5 16.5 7.5C16.5 13.5 13.5 16.5 7.5 16.5ZM7.5 16.5L12 12',
    ore: 'M6.5 16.5L10 9.5L13 12L17.5 8V16.5Z',
    skin: 'M8.5 7.5H15.5L17.5 11L15 16.5H9L6.5 11Z',
    level: 'M12 17V7.5M8 11.5L12 7.5L16 11.5',
    died: 'M8.5 17.5V11A3.5 3.5 0 0 1 15.5 11V17.5ZM12 11V15M10.2 12.8H13.8',
    quest: 'M7.5 12.5L10.5 15.5L16.5 8.5',
    area: 'M12 6.5L13.6 10.3L17.5 10.6L14.6 13.2L15.5 17.2L12 15.1L8.5 17.2L9.4 13.2L6.5 10.6L10.4 10.3Z',
    flight: 'M5.5 13L12 8.5L18.5 13M8.5 16L12 13.5L15.5 16'
  };
  function evSvg(k, c, size) {
    var px = size || 18, col = /^#[0-9a-fA-F]{6}$/.test(c || '') ? c : '#e8e6e3', d = EV[k] || EV.quest;
    return '<svg class="evsvg" viewBox="0 0 24 24" width="' + px + '" height="' + px + '" aria-hidden="true"><circle cx="12" cy="12" r="10.4" fill="#121418" fill-opacity=".92" stroke="' + col + '" stroke-width="1.8"/>' +
      '<path d="' + d + '" fill="none" stroke="' + col + '" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/></svg>';
  }
  window.WmIcons = { factions: F, svg: svg, html: function (f) { return '<div class="fmv ' + (F[f] ? f : 'n') + '" title="">' + svg(f) + '</div>'; },
    evKinds: EV,
    ev: function (k, c, op, off) {
      var o = off || [0, 0];
      return '<div class="me-ev" data-k="' + (EV[k] ? k : 'quest') + '" style="opacity:' + (op == null ? 1 : op) + (o[0] || o[1] ? ';left:' + o[0] + 'px;top:' + o[1] + 'px' : '') + '">' + evSvg(k, c) + '</div>';
    },
    // marks on the same spot (a kill and its loot, or anything where you stand now, under your dot) fan out around it so each one can be seen and pointed at:
    // events = [{ll:[lat,lng]}], here = your dot or null -> one [dx, dy] pixel offset per event (0, 0 for a mark alone on its spot)
    evFan: function (events, here) {
      var key = function (ll) { return ll[0].toFixed(5) + ',' + ll[1].toFixed(5); }, groups = {}, hk = here ? key(here) : null, out = [];
      events.forEach(function (e, i) { var k = key(e.ll); (groups[k] = groups[k] || []).push(i); });
      events.forEach(function () { out.push([0, 0]); });
      Object.keys(groups).forEach(function (k) {
        var g = groups[k], ring = k === hk;
        if (g.length < 2 && !ring) return;
        g.forEach(function (idx, j) {
          var r = j < 8 ? 15 : 28, n = j < 8 ? Math.min(8, g.length) : Math.min(16, g.length - 8), a = -Math.PI / 2 + (j < 8 ? j : j - 8) * 2 * Math.PI / Math.max(n, 1);
          out[idx] = [Math.round(Math.cos(a) * r), Math.round(Math.sin(a) * r)];
        });
      });
      return out;
    } };
})();
