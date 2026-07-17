using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BruTile;
using BruTile.Predefined;
using BruTile.Web;
using Mapsui;
using Mapsui.Extensions;
using Mapsui.Layers;
using Mapsui.Nts;
using Mapsui.Projections;
using Mapsui.Styles;
using Mapsui.Tiling.Layers;
using NetTopologySuite.Geometries;
using TrackEditor.Models;
using Color = Mapsui.Styles.Color;

namespace TrackEditor.Services;

/// <summary>Owns all Mapsui layers: the basemap, track lines, vertices, selection, hover and flags.</summary>
public class MapManager : IDisposable
{
    private const int MaxVertexSymbols = 4000;
    private const string UserAgent = "TrackEditor/1.0";

    private readonly Mapsui.UI.Wpf.MapControl _ctrl;
    private readonly MemoryLayer _lineLayer = new() { Name = "TrackLines", Style = null };
    private readonly MemoryLayer _vertexLayer = new() { Name = "TrackVertices", Style = null };
    private readonly MemoryLayer _flagLayer = new() { Name = "Flags", Style = null };
    private readonly MemoryLayer _selLayer = new() { Name = "Selection", Style = null };
    private readonly MemoryLayer _hoverLayer = new() { Name = "Hover", Style = null };
    private ILayer _baseLayer;
    private MbTilesCache _baseCache;
    private BaseMapProvider _provider;
    private int _tileLimitMB;

    public MapManager(Mapsui.UI.Wpf.MapControl ctrl, BaseMapProvider provider = BaseMapProvider.OpenStreetMap, int tileLimitMB = 0)
    {
        _ctrl = ctrl;
        _provider = provider;
        _tileLimitMB = tileLimitMB;
        var map = ctrl.Map;
        _baseLayer = CreateBaseLayer(provider, LimitBytes, out _baseCache);
        map.Layers.Add(_baseLayer);
        map.Layers.Add(_lineLayer);
        map.Layers.Add(_vertexLayer);
        map.Layers.Add(_flagLayer);
        map.Layers.Add(_selLayer);
        map.Layers.Add(_hoverLayer);
    }

    private long LimitBytes => _tileLimitMB > 0 ? (long)_tileLimitMB * 1024 * 1024 : 0;

    /// <summary>Swaps the basemap layer (kept at the bottom of the stack) for a different provider.</summary>
    public void SetBaseMap(BaseMapProvider provider)
    {
        if (provider == _provider) return;
        _provider = provider;
        var map = _ctrl.Map;
        var newLayer = CreateBaseLayer(provider, LimitBytes, out var newCache);
        map.Layers.Remove(_baseLayer);   // the basemap is always the bottom (first) layer
        map.Layers.Insert(0, newLayer);
        var oldCache = _baseCache;
        _baseLayer = newLayer;
        _baseCache = newCache;
        oldCache.Dispose();              // safe: the old layer is detached and the cache no-ops once disposed
        _ctrl.RefreshGraphics();
    }

    /// <summary>Applies a new per-map cache size cap (MB; 0/negative = unlimited) to the active cache.</summary>
    public void SetTileCacheLimit(int limitMB)
    {
        _tileLimitMB = limitMB;
        _baseCache.SetSizeLimit(LimitBytes);
    }

    /// <summary>
    /// Empties the active map's cache and deletes every other provider's .mbtiles file.
    /// Returns the number of bytes removed from disk.
    /// </summary>
    public long ClearTileCache()
    {
        long freed = 0;
        string active = CacheFile(_provider);
        if (File.Exists(active)) freed += new FileInfo(active).Length;
        _baseCache.Clear();

        if (Directory.Exists(TileCacheDir))
            foreach (var f in Directory.GetFiles(TileCacheDir, "*.mbtiles"))
            {
                if (string.Equals(f, active, StringComparison.OrdinalIgnoreCase)) continue;
                try { freed += new FileInfo(f).Length; File.Delete(f); } catch { /* in use / locked */ }
            }
        _ctrl.RefreshGraphics();
        return freed;
    }

    public void Dispose() => _baseCache.Dispose();

    private static string TileCacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TrackEditor", "tilecache");

    private static string CacheFile(BaseMapProvider provider) =>
        Path.Combine(TileCacheDir, provider + ".mbtiles");

    private static ILayer CreateBaseLayer(BaseMapProvider provider, long limitBytes, out MbTilesCache cache)
    {
        var (url, maxZoom, attrText, attrUrl, yAxis) = provider switch
        {
            BaseMapProvider.OpenTopoMap => (
                "https://tile.opentopomap.org/{z}/{x}/{y}.png", 17,
                "© OpenTopoMap (CC-BY-SA), © OpenStreetMap contributors", "https://opentopomap.org/", YAxis.OSM),
            BaseMapProvider.CyclOSM => (
                "https://a.tile-cyclosm.openstreetmap.fr/cyclosm/{z}/{x}/{y}.png", 18,
                "© CyclOSM, © OpenStreetMap contributors", "https://www.cyclosm.org/", YAxis.OSM),
            BaseMapProvider.EsriWorldImagery => (
                "https://services.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}",
                19, "Imagery © Esri, Maxar, Earthstar Geographics", "https://www.esri.com/", YAxis.OSM),
            BaseMapProvider.CartoLight => (
                "https://basemaps.cartocdn.com/light_all/{z}/{x}/{y}.png", 19,
                "© CARTO, © OpenStreetMap contributors", "https://carto.com/attributions", YAxis.OSM),
            _ => (
                "https://tile.openstreetmap.org/{z}/{x}/{y}.png", 19,
                "© OpenStreetMap contributors", "https://www.openstreetmap.org/copyright", YAxis.OSM),
        };

        string name = provider.ToString();
        var schema = new GlobalSphericalMercator(yAxis, 0, maxZoom);
        cache = new MbTilesCache(CacheFile(provider), name, limitBytes);
        var source = new HttpTileSource(schema, url, name: name,
            attribution: new BruTile.Attribution(attrText, attrUrl),
            persistentCache: cache, userAgent: UserAgent);
        return new TileLayer(source) { Name = "Basemap" };
    }

    public static MPoint ToMercator(TrackPoint p)
    {
        var (x, y) = SphericalMercator.FromLonLat(p.Lon, p.Lat);
        return new MPoint(x, y);
    }

    public static (double Lon, double Lat) ScreenToLonLat(Mapsui.UI.Wpf.MapControl ctrl, double screenX, double screenY)
    {
        var world = ctrl.Map.Navigator.Viewport.ScreenToWorld(screenX, screenY);
        return SphericalMercator.ToLonLat(world.X, world.Y);
    }

    public static Color ParseColor(string hex)
    {
        try
        {
            hex = hex.TrimStart('#');
            byte r = Convert.ToByte(hex.Substring(0, 2), 16);
            byte g = Convert.ToByte(hex.Substring(2, 2), 16);
            byte b = Convert.ToByte(hex.Substring(4, 2), 16);
            return new Color(r, g, b);
        }
        catch { return new Color(229, 57, 53); }
    }

    /// <summary>Rebuilds line + vertex + start/end marker features for all visible tracks.</summary>
    public void RebuildTracks(IReadOnlyList<Track> tracks, Track? activeTrack)
    {
        var lineFeatures = new List<IFeature>();
        var vertexFeatures = new List<IFeature>();

        foreach (var track in tracks.Where(t => t.Visible && t.Points.Count > 0))
        {
            bool isActive = ReferenceEquals(track, activeTrack);
            var color = ParseColor(track.ColorHex);

            if (track.Points.Count > 1)
            {
                var coords = track.Points.Select(p =>
                {
                    var (x, y) = SphericalMercator.FromLonLat(p.Lon, p.Lat);
                    return new Coordinate(x, y);
                }).ToArray();
                var lf = new GeometryFeature(new LineString(coords));
                lf.Styles.Add(new VectorStyle
                {
                    Line = new Pen(color, isActive ? track.Width : Math.Max(1, track.Width - 1))
                    {
                        PenStrokeCap = PenStrokeCap.Round,
                        StrokeJoin = StrokeJoin.Round,
                    },
                });
                if (!isActive) lf.Styles.OfType<VectorStyle>().First().Line!.Color =
                    new Color(color.R, color.G, color.B, 160);
                lineFeatures.Add(lf);
            }

            // start / end markers
            vertexFeatures.Add(MarkerFeature(track.Points[0], new Color(46, 125, 50), 0.45));
            if (track.Points.Count > 1)
                vertexFeatures.Add(MarkerFeature(track.Points[^1], new Color(198, 40, 40), 0.45));

            // vertices for the active track only (kept light for huge tracks)
            if (isActive && track.Points.Count <= MaxVertexSymbols)
            {
                var vColor = new Color(color.R, color.G, color.B, 255);
                foreach (var p in track.Points)
                {
                    var f = new GeometryFeature(new NetTopologySuite.Geometries.Point(ToMercator(p).X, ToMercator(p).Y));
                    f.Styles.Add(new SymbolStyle
                    {
                        SymbolType = SymbolType.Ellipse,
                        SymbolScale = 0.15,
                        Fill = new Mapsui.Styles.Brush(Color.White),
                        Outline = new Pen(vColor, 1.5),
                    });
                    vertexFeatures.Add(f);
                }
            }
        }

        _lineLayer.Features = lineFeatures;
        _vertexLayer.Features = vertexFeatures;
        _lineLayer.DataHasChanged();
        _vertexLayer.DataHasChanged();
        _ctrl.RefreshGraphics();
    }

    private static IFeature MarkerFeature(TrackPoint p, Color color, double scale)
    {
        var m = ToMercator(p);
        var f = new GeometryFeature(new NetTopologySuite.Geometries.Point(m.X, m.Y));
        f.Styles.Add(new SymbolStyle
        {
            SymbolType = SymbolType.Ellipse,
            SymbolScale = scale,
            Fill = new Mapsui.Styles.Brush(color),
            Outline = new Pen(Color.White, 2),
        });
        return f;
    }

    public void SetSelection(Track? track, IEnumerable<int> indices)
    {
        var features = new List<IFeature>();
        if (track is not null)
        {
            foreach (int i in indices.Take(MaxVertexSymbols))
            {
                if (i < 0 || i >= track.Points.Count) continue;
                var m = ToMercator(track.Points[i]);
                var f = new GeometryFeature(new NetTopologySuite.Geometries.Point(m.X, m.Y));
                f.Styles.Add(new SymbolStyle
                {
                    SymbolType = SymbolType.Ellipse,
                    SymbolScale = 0.3,
                    Fill = new Mapsui.Styles.Brush(new Color(255, 235, 59)),
                    Outline = new Pen(new Color(33, 33, 33), 2),
                });
                features.Add(f);
            }
        }
        _selLayer.Features = features;
        _selLayer.DataHasChanged();
        _ctrl.RefreshGraphics();
    }

    public void SetHover(TrackPoint? point)
    {
        var features = new List<IFeature>();
        if (point is not null)
        {
            var m = ToMercator(point);
            var f = new GeometryFeature(new NetTopologySuite.Geometries.Point(m.X, m.Y));
            f.Styles.Add(new SymbolStyle
            {
                SymbolType = SymbolType.Ellipse,
                SymbolScale = 0.35,
                Fill = new Mapsui.Styles.Brush(new Color(3, 169, 244, 180)),
                Outline = new Pen(Color.White, 2),
            });
            features.Add(f);
        }
        _hoverLayer.Features = features;
        _hoverLayer.DataHasChanged();
        _ctrl.RefreshGraphics();
    }

    /// <summary>Renders pre-computed (point, text) flags with a small triangle marker + label.</summary>
    public void SetFlags(IEnumerable<(TrackPoint Point, string Text)> flags)
    {
        var features = new List<IFeature>();
        foreach (var (p, text) in flags)
        {
            var m = ToMercator(p);
            var f = new GeometryFeature(new NetTopologySuite.Geometries.Point(m.X, m.Y));
            f.Styles.Add(new SymbolStyle
            {
                SymbolType = SymbolType.Triangle,
                SymbolScale = 0.25,
                Fill = new Mapsui.Styles.Brush(new Color(255, 152, 0)),
                Outline = new Pen(Color.Black, 1),
            });
            f.Styles.Add(new LabelStyle
            {
                Text = text,
                Font = new Font { Size = 11 },
                ForeColor = Color.Black,
                BackColor = new Mapsui.Styles.Brush(new Color(255, 253, 231, 230)),
                Offset = new Offset(0, -14),
                HorizontalAlignment = LabelStyle.HorizontalAlignmentEnum.Center,
                VerticalAlignment = LabelStyle.VerticalAlignmentEnum.Bottom,
            });
            features.Add(f);
        }
        _flagLayer.Features = features;
        _flagLayer.DataHasChanged();
        _ctrl.RefreshGraphics();
    }

    public void ClearFlags() => SetFlags(Array.Empty<(TrackPoint, string)>());

    public void ZoomToTracks(IReadOnlyList<Track> tracks)
    {
        var pts = tracks.Where(t => t.Visible).SelectMany(t => t.Points).ToList();
        if (pts.Count == 0) return;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in pts)
        {
            var m = ToMercator(p);
            minX = Math.Min(minX, m.X); maxX = Math.Max(maxX, m.X);
            minY = Math.Min(minY, m.Y); maxY = Math.Max(maxY, m.Y);
        }
        double padX = Math.Max((maxX - minX) * 0.1, 200);
        double padY = Math.Max((maxY - minY) * 0.1, 200);
        _ctrl.Map.Navigator.ZoomToBox(new MRect(minX - padX, minY - padY, maxX + padX, maxY + padY));
    }

    /// <summary>Screen-space position of a track point, or null when the viewport is not ready.</summary>
    public MPoint? WorldToScreen(TrackPoint p)
    {
        var viewport = _ctrl.Map.Navigator.Viewport;
        if (viewport.Width <= 0) return null;
        var m = ToMercator(p);
        return viewport.WorldToScreen(m);
    }

    /// <summary>Finds the nearest point of <paramref name="track"/> within <paramref name="maxPx"/> pixels of a screen position.</summary>
    public int FindNearestPointIndex(Track track, double screenX, double screenY, double maxPx)
    {
        int best = -1;
        double bestDist = maxPx;
        for (int i = 0; i < track.Points.Count; i++)
        {
            var s = WorldToScreen(track.Points[i]);
            if (s is null) return -1;
            double d = Math.Sqrt((s.X - screenX) * (s.X - screenX) + (s.Y - screenY) * (s.Y - screenY));
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    /// <summary>Finds the visible track whose polyline passes within <paramref name="maxPx"/> pixels of a screen position.</summary>
    public Track? FindNearestTrack(IReadOnlyList<Track> tracks, double screenX, double screenY, double maxPx)
    {
        Track? best = null;
        double bestDist = maxPx;
        foreach (var track in tracks.Where(t => t.Visible && t.Points.Count > 1))
        {
            MPoint? prev = null;
            foreach (var p in track.Points)
            {
                var s = WorldToScreen(p);
                if (s is null) return null;
                if (prev is not null)
                {
                    double d = GeoMath.PointToSegmentDist(screenX, screenY, prev.X, prev.Y, s.X, s.Y);
                    if (d < bestDist) { bestDist = d; best = track; }
                }
                prev = s;
            }
        }
        return best;
    }

    public (double CenterX, double CenterY, double Resolution) ViewportState()
    {
        var v = _ctrl.Map.Navigator.Viewport;
        return (v.CenterX, v.CenterY, v.Resolution);
    }
}
