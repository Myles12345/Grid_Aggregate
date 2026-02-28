using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NetTopologySuite.Geometries;
using SpatialAggregation;

namespace SpatialAggregation
{
    /// <summary>
    /// Exports aggregation results as a self-contained HTML file.
    /// Opens in any browser — no server required.
    ///
    /// Features:
    ///   • Leaflet map with OpenStreetMap tiles
    ///   • Coloured grid rectangles per hour (red = NetDemand, blue = NetSupply,
    ///     green = Balanced, grey = Unsupported)
    ///   • Opacity scales with abs(Net) so hotter zones are more vivid
    ///   • Hour slider (00:00 – 23:00) with live stats panel
    ///   • Click any cell for a popup with full supply/demand breakdown
    /// </summary>
    public static class MapExporter
    {
        // -------------------------------------------------------------------
        // Inverse Web Mercator (EPSG:3857 → WGS84)
        // -------------------------------------------------------------------

        static double MercToLon(double x) =>
            x / 20037508.342789244 * 180.0;

        static double MercToLat(double y) =>
            Math.Atan(Math.Sinh(y * Math.PI / 20037508.342789244)) * 180.0 / Math.PI;

        // -------------------------------------------------------------------
        // Public export entry point
        // -------------------------------------------------------------------

        public static void ExportHtml(
            IReadOnlyList<HourlyZoneResult> results,
            Envelope boundingBox,
            double gridSize,
            string outputPath)
        {
            // ----- Build the JS data array ---------------------------------
            // Each element: {hour, cx, cy, supply, effectiveSupply, demand, net,
            //                status, bounds:[minLon,minLat,maxLon,maxLat]}
            var sb = new StringBuilder();
            sb.Append('[');
            bool first = true;

            foreach (var r in results)
            {
                double mercMinX = boundingBox.MinX + r.CellX * gridSize;
                double mercMinY = boundingBox.MinY + r.CellY * gridSize;
                double mercMaxX = mercMinX + gridSize;
                double mercMaxY = mercMinY + gridSize;

                double minLon = MercToLon(mercMinX);
                double minLat = MercToLat(mercMinY);
                double maxLon = MercToLon(mercMaxX);
                double maxLat = MercToLat(mercMaxY);

                if (!first) sb.Append(',');
                sb.Append(
                    $"{{\"h\":{r.Hour},\"cx\":{r.CellX},\"cy\":{r.CellY}," +
                    $"\"s\":{r.Supply},\"es\":{r.EffectiveSupply}," +
                    $"\"d\":{r.Demand},\"n\":{r.Net}," +
                    $"\"st\":\"{r.Status}\"," +
                    $"\"b\":[{minLon:F6},{minLat:F6},{maxLon:F6},{maxLat:F6}]}}");
                first = false;
            }
            sb.Append(']');

            // ----- Map centre and zoom -------------------------------------
            double centerLon = MercToLon((boundingBox.MinX + boundingBox.MaxX) / 2);
            double centerLat = MercToLat((boundingBox.MinY + boundingBox.MaxY) / 2);

            // Estimate a sensible zoom from the bounding box width in degrees.
            double spanLon  = MercToLon(boundingBox.MaxX) - MercToLon(boundingBox.MinX);
            int    zoom     = spanLon < 0.05  ? 14
                            : spanLon < 0.2   ? 12
                            : spanLon < 1.0   ? 10
                            : 8;

            string html = BuildHtml(sb.ToString(), centerLat, centerLon, zoom);
            File.WriteAllText(outputPath, html, Encoding.UTF8);
        }

        // -------------------------------------------------------------------
        // HTML / JS template
        // -------------------------------------------------------------------

        static string BuildHtml(string dataJson, double centerLat, double centerLon, int zoom)
        {
            return
$$"""
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8"/>
  <meta name="viewport" content="width=device-width, initial-scale=1"/>
  <title>Grid Aggregate — Supply / Demand Map</title>
  <link rel="stylesheet"
        href="https://unpkg.com/leaflet@1.9.4/dist/leaflet.css"
        crossorigin=""/>
  <style>
    html, body { margin: 0; padding: 0; height: 100%; font-family: 'Segoe UI', sans-serif; background: #1a1a2e; }
    #map { position: absolute; inset: 0; }

    /* ---- Control panel ---- */
    #panel {
      position: fixed; top: 12px; right: 12px; z-index: 1000;
      background: rgba(22, 33, 62, 0.95);
      color: #e0e0e0;
      padding: 14px 18px;
      border-radius: 10px;
      box-shadow: 0 4px 20px rgba(0,0,0,0.5);
      min-width: 230px;
      border: 1px solid rgba(255,255,255,0.1);
    }
    #panel h3 { margin: 0 0 10px; font-size: 13px; letter-spacing: 1px; text-transform: uppercase; color: #a0c4ff; }
    #hourLabel { font-size: 22px; font-weight: 700; display: block; margin-bottom: 4px; color: #fff; }
    #hourSlider { width: 100%; margin: 6px 0 10px; accent-color: #a0c4ff; }
    .stat-row { display: flex; justify-content: space-between; align-items: center; font-size: 12px; margin: 3px 0; }
    .dot { display: inline-block; width: 11px; height: 11px; border-radius: 3px; margin-right: 6px; vertical-align: middle; }
    hr { border: none; border-top: 1px solid rgba(255,255,255,0.12); margin: 10px 0; }
    #statsPanel { margin-top: 6px; }

    /* ---- Capacity note ---- */
    #capacityNote { font-size: 10px; color: #888; margin-top: 8px; line-height: 1.4; }
  </style>
</head>
<body>
<div id="map"></div>
<div id="panel">
  <h3>Grid Aggregate</h3>
  <span id="hourLabel">00:00</span>
  <input type="range" id="hourSlider" min="0" max="23" value="0"/>
  <div id="statsPanel">
    <div class="stat-row"><span><span class="dot" style="background:#e74c3c"></span>Net Demand</span><span id="cntDemand">—</span></div>
    <div class="stat-row"><span><span class="dot" style="background:#2980b9"></span>Net Supply</span><span id="cntSupply">—</span></div>
    <div class="stat-row"><span><span class="dot" style="background:#27ae60"></span>Balanced</span><span id="cntBalanced">—</span></div>
    <div class="stat-row"><span><span class="dot" style="background:#555"></span>Unsupported</span><span id="cntUnsupported">—</span></div>
  </div>
  <hr/>
  <div id="capacityNote">Effective supply = raw driver events × capacity factor (default 2×/hr). Balanced = supply capacity equals demand.</div>
</div>

<script src="https://unpkg.com/leaflet@1.9.4/dist/leaflet.js" crossorigin=""></script>
<script>
// ── Data ────────────────────────────────────────────────────────────────────
const DATA = {{dataJson}};

// Pre-compute max |net| across all records for opacity scaling.
const MAX_NET = Math.max(...DATA.map(f => Math.abs(f.n)), 1);

// ── Map init ────────────────────────────────────────────────────────────────
const map = L.map('map', { zoomControl: true })
             .setView([{{centerLat:F5}}, {{centerLon:F5}}], {{zoom}});

L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
  attribution: '© <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a>',
  maxZoom: 19
}).addTo(map);

// ── Colour helper ────────────────────────────────────────────────────────────
function cellColor(status, net) {
  const t = Math.min(Math.abs(net) / MAX_NET, 1);
  const alpha = (0.25 + 0.65 * t).toFixed(2);
  if (status === 'NetDemand')  return `rgba(231,76,60,${alpha})`;
  if (status === 'NetSupply')  return `rgba(41,128,185,${alpha})`;
  if (status === 'Balanced')   return `rgba(39,174,96,0.55)`;
  return `rgba(100,100,100,0.3)`;   // Unsupported
}

// ── Layer management ─────────────────────────────────────────────────────────
let activeLayer = null;

function render(hour) {
  if (activeLayer) { map.removeLayer(activeLayer); activeLayer = null; }

  const features = DATA.filter(f => f.h === hour);
  const rects    = features.map(f => {
    const [[minLon, minLat, maxLon, maxLat]] = [f.b];
    const rect = L.rectangle(
      [[minLat, minLon], [maxLat, maxLon]],
      {
        color:       'rgba(0,0,0,0.25)',
        weight:      0.8,
        fillColor:   cellColor(f.st, f.n),
        fillOpacity: 1
      }
    );

    const netSign  = f.n > 0 ? '+' : '';
    const netColor = f.n > 0 ? '#5dade2' : f.n < 0 ? '#ec7063' : '#58d68d';
    rect.bindPopup(
      `<b>Cell (${f.cx}, ${f.cy})</b><br/>` +
      `<b>Hour:</b> ${String(hour).padStart(2,'0')}:00<br/>` +
      `<b>Drivers (raw):</b> ${f.s}<br/>` +
      `<b>Effective supply:</b> ${f.es}<br/>` +
      `<b>Ride requests:</b> ${f.d}<br/>` +
      `<b>Net:</b> <span style="color:${netColor};font-weight:700">${netSign}${f.n}</span><br/>` +
      `<b>Status:</b> ${f.st}`
    );
    return rect;
  });

  activeLayer = L.layerGroup(rects).addTo(map);

  // Update sidebar counts.
  document.getElementById('cntDemand').textContent    = features.filter(f => f.st === 'NetDemand').length;
  document.getElementById('cntSupply').textContent    = features.filter(f => f.st === 'NetSupply').length;
  document.getElementById('cntBalanced').textContent  = features.filter(f => f.st === 'Balanced').length;
  document.getElementById('cntUnsupported').textContent = features.filter(f => f.st === 'Unsupported').length;
}

// ── Slider ───────────────────────────────────────────────────────────────────
const slider    = document.getElementById('hourSlider');
const hourLabel = document.getElementById('hourLabel');

slider.addEventListener('input', () => {
  const h = parseInt(slider.value, 10);
  hourLabel.textContent = String(h).padStart(2,'0') + ':00';
  render(h);
});

// Initial render.
render(0);
</script>
</body>
</html>
""";
        }
    }
}
