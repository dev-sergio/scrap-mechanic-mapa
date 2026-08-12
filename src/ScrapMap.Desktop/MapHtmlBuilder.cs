using System.IO;
using System.Net;
using System.Text.Json;
using ScrapMap.Core.Models;

namespace ScrapMap.Desktop;

internal static class MapHtmlBuilder
{
    private const string DesktopAssetBaseUrl = "https://appassets.scrapmap";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string Build(
        WorldSnapshot snapshot,
        TerrainOverlayData? terrain,
        MapViewState? viewState = null,
        string assetBaseUrl = DesktopAssetBaseUrl,
        long? hostRevision = null)
    {
        var terrainPayload = terrain is null ? null : new
        {
            worldUrl = CombineUrl(assetBaseUrl, terrain.WorldAssetPath),
            terrain.Width,
            terrain.Height,
            terrain.CellPixelSize,
            terrain.WorldXMin,
            terrain.WorldXMax,
            terrain.WorldYMin,
            terrain.WorldYMax,
            terrain.WorldCellCount
        };
        var payload = new
        {
            saveName = Path.GetFileNameWithoutExtension(snapshot.SavePath),
            game = snapshot.Game,
            terrain = terrainPayload,
            hostRevision,
            initialState = viewState,
            exploredCells = snapshot.ExploredCells,
            resources = snapshot.Resources.Select(resource => new
            {
                resource.Id,
                resource.X,
                resource.Y,
                resource.Z,
                resource.DisplayName,
                resource.Category,
                resource.Color
            }),
            creations = snapshot.Creations.Select(creation => new
            {
                creation.Id,
                creation.X,
                creation.Y,
                creation.MinX,
                creation.MaxX,
                creation.MinY,
                creation.MaxY,
                creation.BodyCount,
                creation.ShapeCount,
                creation.Category,
                parts = creation.Parts.Take(12)
            })
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions).Replace("</script", "<\\/script", StringComparison.OrdinalIgnoreCase);
        var leafletCssUrl = CombineUrl(assetBaseUrl, "/Assets/Web/leaflet.css");
        var leafletScriptUrl = CombineUrl(assetBaseUrl, "/Assets/Web/leaflet.js");
        return $$"""
            <!doctype html>
            <html lang="pt-BR">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width,initial-scale=1">
              <link rel="stylesheet" href="{{leafletCssUrl}}">
              <style>
                :root { color-scheme: dark; font-family: "Segoe UI", sans-serif; }
                html, body, #map { width: 100%; height: 100%; margin: 0; background: #101419; }
                .leaflet-container { background: #101419; font-family: "Segoe UI", sans-serif; }
                .leaflet-control-layers, .leaflet-bar a, .leaflet-popup-content-wrapper, .leaflet-popup-tip {
                  color: #eef2f5; background: #1b232b; border-color: #394754;
                }
                .leaflet-control-layers { border: 1px solid #394754; border-radius: 8px; box-shadow: 0 8px 24px #0008; }
                .leaflet-control-layers-toggle { filter: invert(1); }
                .leaflet-control-zoom a { color: #eef2f5; }
                .leaflet-control-attribution { display: none; }
                .leaflet-tooltip { color: #eef2f5; background: #151b21; border: 1px solid #394754; box-shadow: none; }
                .leaflet-tooltip::before { border-top-color: #394754; }
                .hud {
                  position: absolute; z-index: 900; left: 16px; top: 16px; min-width: 220px;
                  padding: 14px 16px; border: 1px solid #34414d; border-radius: 10px;
                  background: #151b21eF; box-shadow: 0 8px 30px #0007; pointer-events: none;
                }
                .hud h1 { margin: 0 0 4px; color: #f5a524; font-size: 18px; }
                .hud .meta { color: #9da9b5; font-size: 12px; }
                .hud .scope-note { max-width: 300px; margin-top: 8px; color: #d6dde3; font-size: 11px; line-height: 1.35; }
                .hud .counts { display: flex; gap: 18px; margin-top: 11px; }
                .hud strong { display: block; color: #f2f4f6; font-size: 17px; }
                .coords {
                  position: absolute; z-index: 900; right: 14px; bottom: 14px; padding: 7px 10px;
                  border: 1px solid #34414d; border-radius: 6px; color: #cbd4dc; background: #151b21e8;
                  font: 12px Consolas, monospace; pointer-events: none;
                }
                .popup-title { margin-bottom: 6px; color: #f5a524; font-weight: 700; }
                .popup-line { color: #cbd4dc; line-height: 1.5; }
                .popup-parts { max-width: 300px; margin-top: 7px; color: #9da9b5; font-size: 11px; }
                .grid-label { color: #566676; background: transparent; border: 0; box-shadow: none; font: 10px Consolas; }
              </style>
            </head>
            <body>
              <div id="map"></div>
              <section class="hud">
                <h1 id="worldName"></h1>
                <div class="meta" id="worldMeta"></div>
                <div class="scope-note" id="scopeNote"></div>
                <div class="counts">
                  <div><strong id="resourceCount"></strong><span class="meta">recursos</span></div>
                  <div><strong id="creationCount"></strong><span class="meta">construções</span></div>
                  <div><strong id="cellCount"></strong><span class="meta">células persistidas</span></div>
                </div>
              </section>
              <div class="coords" id="coords">X — · Y —</div>
              <script src="{{leafletScriptUrl}}"></script>
              <script>
                const world = {{json}};
                if (world.hostRevision !== null) {
                  try {
                    const savedState = localStorage.getItem('scrapMapLanState');
                    if (savedState) world.initialState = JSON.parse(savedState);
                  } catch { }
                }
                const esc = value => String(value).replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
                const number = value => Number(value).toLocaleString('pt-BR', { maximumFractionDigits: 1 });
                const map = L.map('map', {
                  crs: L.CRS.Simple,
                  preferCanvas: true,
                  zoomControl: false,
                  minZoom: -5,
                  maxZoom: 3,
                  zoomSnap: 0.25
                });
                L.control.zoom({ position: 'bottomleft' }).addTo(map);

                document.getElementById('worldName').textContent = world.saveName;
                document.getElementById('worldMeta').textContent = `Seed ${world.game.seed} · Save v${world.game.savegameVersion}`;
                document.getElementById('resourceCount').textContent = world.resources.length.toLocaleString('pt-BR');
                document.getElementById('creationCount').textContent = world.creations.length.toLocaleString('pt-BR');
                document.getElementById('cellCount').textContent = world.exploredCells.length.toLocaleString('pt-BR');

                const points = [];
                const registeredPoints = [];
                const overlays = {};
                const resourceGroups = {};
                const canvasRenderer = L.canvas({ padding: 0.5 });
                const cellSize = 64;
                const terrain = world.terrain;
                const baseLayers = {};
                let activeBaseLayerName = null;
                let completeBaseLayerName = null;
                let registeredBaseLayerName = null;
                let terrainBounds = null;

                const projectWorld = (x, y) => {
                  if (!terrain) return [y, x];
                  const cellX = x / cellSize;
                  const cellY = y / cellSize;
                  const pixelX = (cellX - terrain.worldXMin) * terrain.cellPixelSize;
                  const pixelY = (terrain.worldYMax + 1 - cellY) * terrain.cellPixelSize;
                  return [terrain.height - pixelY, pixelX];
                };
                const unprojectWorld = latlng => {
                  if (!terrain) return { x: latlng.lng, y: latlng.lat };
                  const pixelY = terrain.height - latlng.lat;
                  return {
                    x: (latlng.lng / terrain.cellPixelSize + terrain.worldXMin) * cellSize,
                    y: (terrain.worldYMax + 1 - pixelY / terrain.cellPixelSize) * cellSize
                  };
                };

                if (terrain) {
                  terrainBounds = L.latLngBounds([[0, 0], [terrain.height, terrain.width]]);
                  const fogRects = world.exploredCells.map(cell => {
                    const x = (cell.x - terrain.worldXMin) * terrain.cellPixelSize;
                    const y = (terrain.worldYMax - cell.y) * terrain.cellPixelSize;
                    const reveal = terrain.cellPixelSize * 3;
                    return `<rect x="${x - terrain.cellPixelSize - 0.5}" y="${y - terrain.cellPixelSize - 0.5}" width="${reveal + 1}" height="${reveal + 1}" rx="${terrain.cellPixelSize * 0.35}"/>`;
                  }).join('');
                  const fogSvg = `<svg xmlns="http://www.w3.org/2000/svg" width="${terrain.width}" height="${terrain.height}" viewBox="0 0 ${terrain.width} ${terrain.height}"><defs><mask id="fog"><rect width="100%" height="100%" fill="white"/><g fill="black">${fogRects}</g></mask></defs><rect width="100%" height="100%" fill="#101419" mask="url(#fog)"/></svg>`;
                  const fogUrl = `data:image/svg+xml;charset=utf-8,${encodeURIComponent(fogSvg)}`;
                  const registeredTerrain = L.layerGroup([
                    L.imageOverlay(terrain.worldUrl, terrainBounds, { interactive: false }),
                    L.imageOverlay(fogUrl, terrainBounds, { interactive: false })
                  ]);
                  const completeTerrain = L.layerGroup([
                    L.imageOverlay(terrain.worldUrl, terrainBounds, { interactive: false })
                  ]);
                  const noTerrain = L.layerGroup();
                  registeredBaseLayerName = `Área registrada no save · ${world.exploredCells.length.toLocaleString('pt-BR')} células`;
                  completeBaseLayerName = `Mundo completo · ${(terrain.worldXMax - terrain.worldXMin + 1)} × ${(terrain.worldYMax - terrain.worldYMin + 1)} células`;
                  baseLayers[registeredBaseLayerName] = registeredTerrain;
                  baseLayers[completeBaseLayerName] = completeTerrain;
                  baseLayers['Sem terreno'] = noTerrain;
                  activeBaseLayerName = world.initialState?.baseLayer && baseLayers[world.initialState.baseLayer]
                    ? world.initialState.baseLayer
                    : registeredBaseLayerName;
                  baseLayers[activeBaseLayerName].addTo(map);
                  if (activeBaseLayerName === completeBaseLayerName) {
                    points.push(terrainBounds.getSouthWest(), terrainBounds.getNorthEast());
                  }
                }

                const updateScopeNote = name => {
                  const note = document.getElementById('scopeNote');
                  if (!terrain) {
                    note.textContent = 'Terreno visual não disponível para este seed.';
                  } else if (name === completeBaseLayerName) {
                    note.textContent = 'Mostrando o mundo inteiro gerado. Isso inclui áreas onde você ainda não passou.';
                  } else if (name === registeredBaseLayerName) {
                    note.textContent = 'Aproximação das áreas visitadas: o jogo não grava uma trilha GPS, apenas células persistidas no save.';
                  } else {
                    note.textContent = 'Terreno oculto; somente as camadas marcadas serão exibidas.';
                  }
                };
                updateScopeNote(activeBaseLayerName);

                // The save does not contain a GPS trail. Persisted spatial cells are the
                // closest reliable representation of the part of the world already loaded.
                const exploredRegion = L.layerGroup();
                for (const cell of world.exploredCells) {
                  const minX = cell.x * cellSize;
                  const minY = cell.y * cellSize;
                  const maxX = minX + cellSize;
                  const maxY = minY + cellSize;
                  const corners = [
                    projectWorld(minX, minY),
                    projectWorld(maxX, minY),
                    projectWorld(maxX, maxY),
                    projectWorld(minX, maxY)
                  ];
                  registeredPoints.push(corners[0], corners[2]);
                  if (!terrain || activeBaseLayerName === registeredBaseLayerName) points.push(corners[0], corners[2]);
                  const intensity = Math.min(0.4, 0.18 + Math.log10(cell.persistedEntityCount + 1) * 0.07);
                  L.polygon(corners, {
                    renderer: canvasRenderer,
                    color: '#42657a',
                    weight: 0.65,
                    opacity: 0.8,
                    fillColor: '#23485b',
                    fillOpacity: intensity,
                    interactive: true
                  }).bindTooltip(`Célula ${cell.x}, ${cell.y}`, { sticky: true }).addTo(exploredRegion);
                }
                overlays['Células persistidas'] = exploredRegion;
                if (world.initialState?.activeLayers?.includes('Células persistidas')) exploredRegion.addTo(map);

                const categories = [...new Set(world.resources.map(item => item.category))].sort();
                for (const category of categories) resourceGroups[category] = L.layerGroup();

                for (const resource of world.resources) {
                  const position = projectWorld(resource.x, resource.y);
                  if (!world.exploredCells.length) points.push(position);
                  L.circleMarker(position, {
                    renderer: canvasRenderer,
                    radius: ['Árvores', 'Pedras', 'Outros'].includes(resource.category) ? 2.2 : 4.2,
                    color: resource.color,
                    fillColor: resource.color,
                    fillOpacity: 0.82,
                    weight: 0.6
                  }).bindPopup(`
                    <div class="popup-title">${esc(resource.displayName)}</div>
                    <div class="popup-line">X ${number(resource.x)} · Y ${number(resource.y)} · Z ${number(resource.z)}</div>
                    <div class="popup-parts">Registro #${resource.id}</div>
                  `).addTo(resourceGroups[resource.category]);
                }

                for (const category of categories) {
                  const count = world.resources.filter(item => item.category === category).length;
                  const label = `${category} (${count.toLocaleString('pt-BR')})`;
                  overlays[label] = resourceGroups[category];
                  if (world.initialState?.activeLayers?.includes(category)) resourceGroups[category].addTo(map);
                }

                const creationGroups = {};
                for (const creation of world.creations) {
                  creationGroups[creation.category] ??= L.layerGroup();
                  const position = projectWorld(creation.x, creation.y);
                  if (!world.exploredCells.length) points.push(position);
                  const color = creation.category === 'Veículos' ? '#00d4ff' : creation.category === 'Nave inicial' ? '#ff7b00' : creation.category === 'Peças soltas' ? '#c77dff' : '#f5a524';
                  const parts = creation.parts.length ? creation.parts.map(esc).join(' · ') : 'Peças não catalogadas';
                  const popup = `
                    <div class="popup-title">${esc(creation.category)} #${creation.id}</div>
                    <div class="popup-line">X ${number(creation.x)} · Y ${number(creation.y)}</div>
                    <div class="popup-line">${creation.shapeCount} peça(s)</div>
                    <div class="popup-line">${creation.bodyCount} corpo(s) físico(s)</div>
                    <div class="popup-parts">${parts}</div>
                  `;
                  L.polygon([
                    projectWorld(creation.minX, creation.minY),
                    projectWorld(creation.maxX, creation.minY),
                    projectWorld(creation.maxX, creation.maxY),
                    projectWorld(creation.minX, creation.maxY)
                  ], {
                    color, weight: 1.3, fillColor: color, fillOpacity: 0.14
                  }).bindPopup(popup).addTo(creationGroups[creation.category]);
                  L.circleMarker(position, {
                    radius: Math.min(8, 3.5 + Math.log2(creation.shapeCount + 1)),
                    color: '#101419', fillColor: color, fillOpacity: 0.95, weight: 1.5
                  }).bindPopup(popup).addTo(creationGroups[creation.category]);
                }

                for (const [category, group] of Object.entries(creationGroups)) {
                  const count = world.creations.filter(item => item.category === category).length;
                  const label = `${category} (${count})`;
                  overlays[label] = group;
                  if (world.initialState?.activeLayers?.includes(category)) group.addTo(map);
                }

                const bounds = points.length ? L.latLngBounds(points) : L.latLngBounds([[-100, -100], [100, 100]]);
                const padded = bounds.pad(0.06);
                const registeredBounds = registeredPoints.length
                  ? L.latLngBounds(registeredPoints).pad(0.06)
                  : padded;
                if (!terrain) {
                  const grid = L.layerGroup();
                  const west = Math.floor(padded.getWest() / 256) * 256;
                  const east = Math.ceil(padded.getEast() / 256) * 256;
                  const south = Math.floor(padded.getSouth() / 256) * 256;
                  const north = Math.ceil(padded.getNorth() / 256) * 256;
                  for (let x = west; x <= east; x += 256) {
                    L.polyline([[south, x], [north, x]], { color: '#4e6070', opacity: 0.22, weight: 1, interactive: false }).addTo(grid);
                  }
                  for (let y = south; y <= north; y += 256) {
                    L.polyline([[y, west], [y, east]], { color: '#4e6070', opacity: 0.22, weight: 1, interactive: false }).addTo(grid);
                  }
                  overlays['Grade de coordenadas'] = grid;
                  if (world.initialState?.activeLayers?.includes('Grade de coordenadas')) grid.addTo(map);
                }

                L.control.layers(baseLayers, overlays, { collapsed: false, position: 'topright' }).addTo(map);
                if (world.initialState?.center && Number.isFinite(world.initialState.zoom)) {
                  map.setView([world.initialState.center.latitude, world.initialState.center.longitude], world.initialState.zoom);
                } else {
                  map.fitBounds(padded);
                }
                map.on('baselayerchange', event => {
                  activeBaseLayerName = event.name;
                  updateScopeNote(event.name);
                  if (event.name === completeBaseLayerName && terrainBounds) {
                    map.fitBounds(terrainBounds.pad(-0.015));
                  } else if (event.name === registeredBaseLayerName) {
                    map.fitBounds(registeredBounds);
                  }
                });
                map.on('mousemove', event => {
                  const worldPosition = unprojectWorld(event.latlng);
                  document.getElementById('coords').textContent = `X ${number(worldPosition.x)} · Y ${number(worldPosition.y)}`;
                });
                window.scrapMapGetState = () => {
                  const center = map.getCenter();
                  return {
                    center: { latitude: center.lat, longitude: center.lng },
                    zoom: map.getZoom(),
                    activeLayers: Object.entries(overlays)
                      .filter(([, layer]) => map.hasLayer(layer))
                      .map(([label]) => label.replace(/\s+\([^)]*\)$/, '')),
                    baseLayer: activeBaseLayerName
                  };
                };

                if (world.hostRevision !== null) {
                  const persistLanState = () => {
                    try {
                      localStorage.setItem('scrapMapLanState', JSON.stringify(window.scrapMapGetState()));
                    } catch { }
                  };
                  map.on('moveend zoomend overlayadd overlayremove baselayerchange', persistLanState);
                  window.addEventListener('beforeunload', persistLanState);
                  window.setInterval(async () => {
                    try {
                      const response = await fetch('/api/revision', { cache: 'no-store' });
                      if (!response.ok) return;
                      const status = await response.json();
                      if (status.revision !== world.hostRevision) {
                        persistLanState();
                        window.location.reload();
                      }
                    } catch { }
                  }, 2500);
                }
              </script>
            </body>
            </html>
            """;
    }

    public static string BuildEmpty(string title, string? detail = null, bool waitForLanMap = false)
    {
        var safeTitle = WebUtility.HtmlEncode(title);
        var safeDetail = WebUtility.HtmlEncode(detail ?? "Abra o Scrap Mechanic e crie um mundo Survival primeiro.");
        var reloadScript = waitForLanMap
            ? "<script>setInterval(async()=>{try{const r=await fetch('/api/revision',{cache:'no-store'});const s=await r.json();if(s.revision>0)location.reload()}catch{}},1500)</script>"
            : string.Empty;
        return $$"""
            <!doctype html><html lang="pt-BR"><head><meta charset="utf-8"><style>
            html,body{height:100%;margin:0;background:#101419;color:#f2f4f6;font-family:"Segoe UI",sans-serif}
            body{display:grid;place-items:center}.box{text-align:center;max-width:600px;padding:40px}
            h1{color:#f5a524}p{color:#9da9b5;line-height:1.6}
            </style></head><body><div class="box"><h1>{{safeTitle}}</h1><p>{{safeDetail}}</p></div>{{reloadScript}}</body></html>
            """;
    }

    private static string CombineUrl(string baseUrl, string path) =>
        string.IsNullOrWhiteSpace(baseUrl)
            ? path
            : $"{baseUrl.TrimEnd('/')}/{path.TrimStart('/')}";
}

internal sealed record MapViewState(MapCenter Center, double Zoom, IReadOnlyList<string> ActiveLayers, string? BaseLayer = null);

internal sealed record MapCenter(double Latitude, double Longitude);
