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
  window.WmIcons = { factions: F, svg: svg, html: function (f) { return '<div class="fmv ' + (F[f] ? f : 'n') + '" title="">' + svg(f) + '</div>'; } };
})();
