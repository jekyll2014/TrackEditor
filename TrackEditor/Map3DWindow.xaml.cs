using BruTile;

using HelixToolkit.Wpf;

using Mapsui.Projections;

using SkiaSharp;

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

using TrackEditor.Core.Models;
using TrackEditor.Core.Services;
using TrackEditor.Core.Skia;

namespace TrackEditor;

/// <summary>
/// Standalone 3D view of the region currently shown on the 2D map: an SRTM-sampled terrain mesh
/// with the basemap draped over it. Each native basemap tile is draped as its own textured patch
/// over its Mercator footprint (elevation bilinear-sampled from the shared grid, so adjacent patches
/// meet without cracks) and the track overlays are baked into each tile's texture. Detail is bounded
/// by how many tiles cover the extent, not by a single texture's pixel size.
/// </summary>
public partial class Map3DWindow : Window
{
    private const int Grid = 160;              // terrain resolution (Grid x Grid vertices)
    private const double Origin = 20037508.342789244; // Web-Mercator half-extent (m)
    private const int MaxPatches = 3000;       // hard cap on tiles/patches per detail level
    private const int HeavyPatches = 400;      // above this a level is allowed but flagged slow

    private readonly (double MinX, double MinY, double MaxX, double MaxY) _extent;
    private readonly ITileSource _tiles;
    private readonly IReadOnlyList<Track> _tracks;
    private readonly SrtmService _srtm;
    private readonly int _maxZoom;

    // Gradient colouring applies to this one track only (the 2D map's active track); null = none.
    private readonly Track? _gradientTrack;
    private readonly GradientMetric _gradientMetric;
    private readonly bool _paceMode;
    private int _zoom;                  // basemap tile zoom for the draped texture (user-selectable)
    private bool _detailReady;          // gate Detail_Changed until the first build finishes

    private readonly double _cx, _cy;   // extent centre in Web-Mercator metres
    private readonly double _latC, _lonC; // extent centre in degrees (for the sun's position)
    private readonly double _k;         // Mercator -> true ground metres at the centre latitude

    private double[,] _elevations = new double[Grid, Grid];
    private double _minEle, _maxEle;
    private double _sizeX, _sizeY;      // terrain extent in ground metres
    private double _exaggeration = 1.0;

    private readonly Model3DGroup _terrain = new();
    private List<Patch> _patches = new();
    private int _lastBlocked;           // tiles skipped (fetch/decode failed) in the last drape build

    // Always-on-top flag billboards (waypoints + optional track points), drawn in the overlay viewport.
    private BillboardTextGroupVisual3D? _waypointFlags;
    private BillboardTextGroupVisual3D? _pointFlags;
    private bool _flagsReady;           // gates flag builds until the terrain heights exist
    private const int MaxPointFlags = 2000; // safety cap on interval flags (protects very long tracks)

    // Solar lighting: seeded from a track timestamp; the slider then moves the sun across that day (UTC).
    private DateTime? _sunSeed;         // seed instant (UTC); null when no point carries a time
    private DateTime _sunDate;          // day used for the sun's declination
    private bool _sunReady;             // gates the slider handler until the seed is applied

    // Cast shadows: a per-cell "lit" mask (1 = sun, 0 = shadow) ray-marched over the elevation grid toward the
    // sun, baked into the drape as darkening. WPF 3D has no GPU shadows, so hill-cast shadows are computed on the
    // heightfield instead. Recomputing re-bakes the drape, so slider drags are debounced.
    private double[,]? _shadowGrid;     // null = no cast shadows baked
    private bool _shadowApplied;        // true when the current patches were baked with a shadow mask
    private bool _drapeBusy;            // a drape (re)build is in flight — serialise the next one
    private System.Windows.Threading.DispatcherTimer? _sunTimer; // debounces shadow re-bakes during interaction
    private CancellationTokenSource? _sunCts; // cancels an in-flight shadow render when the time changes again
    private const int ShadowMaxAlpha = 135; // how dark a fully-shadowed texel gets (ambient still lights it)
    private const int ShadowRebakeDelayMs = 1200; // quiet period before a shadow render starts

    /// <summary>Raised whenever the camera moves so the 2D map can show where the viewer stands.</summary>
    public event Action<double, double, double>? ViewpointChanged; // lat, lon, heading°

    public Map3DWindow(
        (double MinX, double MinY, double MaxX, double MaxY) extent,
        int zoom, int maxZoom, ITileSource tiles, IReadOnlyList<Track> tracks, SrtmService srtm,
        Track? gradientTrack = null, GradientMetric gradientMetric = GradientMetric.None, bool paceMode = false,
        DateTime? sunTime = null)
    {
        InitializeComponent();
        _extent = extent;
        _tiles = tiles;
        _tracks = tracks;
        _srtm = srtm;
        _maxZoom = maxZoom;
        _gradientTrack = gradientTrack;
        _gradientMetric = gradientMetric;
        _paceMode = paceMode;
        _sunSeed = sunTime;

        _cx = (extent.MinX + extent.MaxX) / 2;
        _cy = (extent.MinY + extent.MaxY) / 2;
        (_lonC, _latC) = SphericalMercator.ToLonLat(_cx, _cy);
        _k = Math.Cos(_latC * Math.PI / 180.0); // Mercator metres are stretched by 1/cos(lat)

        _sizeX = (extent.MaxX - extent.MinX) * _k;
        _sizeY = (extent.MaxY - extent.MinY) * _k;

        // Default to the 2D view's zoom; each tile is its own patch, so the only limit is tile count.
        int z = Math.Min(zoom, maxZoom);
        while (z > 1 && TileCount(z) > MaxPatches) z--;
        _zoom = z;
        TerrainVisual.Content = _terrain;
        PopulateDetailLevels();

        _sunTimer = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromMilliseconds(ShadowRebakeDelayMs) };
        _sunTimer.Tick += async (_, _) => { _sunTimer!.Stop(); await StartShadowRebakeAsync(); };
        InitSun();

        // Google-Earth-style mouse mapping: left drag pans (Helix), right drag orbits/tilts and the
        // wheel zooms (both handled here so the behaviour is identical to the on-screen buttons).
        Viewport.CameraMode = CameraMode.Inspect;
        Viewport.CameraRotationMode = CameraRotationMode.Turntable;
        Viewport.PanGesture = new MouseGesture(MouseAction.LeftClick);
        Viewport.IsRotationEnabled = false;
        Viewport.IsZoomEnabled = false;
        Viewport.CameraChanged += (_, _) => UpdateHeading();

        Viewport.PreviewMouseWheel += Viewport_PreviewMouseWheel;
        Viewport.PreviewMouseRightButtonDown += Viewport_RightDown;
        Viewport.PreviewMouseMove += Viewport_MouseMoveOrbit;
        Viewport.PreviewMouseRightButtonUp += Viewport_RightUp;

        Loaded += async (_, _) => await BuildAsync();
    }

    // ======================= terrain build =======================

    private async Task BuildAsync()
    {
        StatusText.Text = "Fetching elevation…";
        try
        {
            await EnsureSrtmTilesAsync();

            StatusText.Text = "Sampling terrain…";
            int withEle = await Task.Run(SampleElevations);

            StatusText.Text = "Rendering map tiles…";
            var progress = new Progress<string>(s => StatusText.Text = s);
            _patches = await BuildDrapeAsync(_zoom, progress);
            foreach (var p in _patches) _terrain.Children.Add(p.Model);

            ResetView_Click(this, new RoutedEventArgs());

            _flagsReady = true;
            BuildFlags();

            StatusText.Text = StatusLine(withEle);
            _detailReady = true;

            // If the sun was switched on while the terrain was still building, bake its shadows now.
            if (ChkSun.IsChecked == true) await StartShadowRebakeAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = "3D build failed: " + ex.Message;
        }
    }

    private string StatusLine(int withEle)
    {
        string terrain = withEle == 0
            ? "No SRTM elevation for this area — terrain is flat (set an SRTM folder in Settings)"
            : $"Terrain {_minEle:F0}–{_maxEle:F0} m";
        string blocked = _lastBlocked > 0 ? $"   ·   {_lastBlocked} tile(s) skipped" : "";
        return $"{terrain}   ·   basemap z{_zoom} · {_patches.Count} tiles{blocked}";
    }

    /// <summary>Downloads the 1°x1° SRTM tiles covering the extent (no-op unless auto-download is on).</summary>
    private async Task EnsureSrtmTilesAsync()
    {
        var (lonMin, latMin) = SphericalMercator.ToLonLat(_extent.MinX, _extent.MinY);
        var (lonMax, latMax) = SphericalMercator.ToLonLat(_extent.MaxX, _extent.MaxY);
        var coords = new List<(double Lat, double Lon)>();
        for (int la = (int)Math.Floor(latMin); la <= (int)Math.Floor(latMax); la++)
            for (int lo = (int)Math.Floor(lonMin); lo <= (int)Math.Floor(lonMax); lo++)
                coords.Add((la + 0.5, lo + 0.5));
        try { await _srtm.EnsureTilesAsync(coords); } catch { /* offline → sample what we have */ }
    }

    /// <summary>Fills the elevation grid; returns how many samples had real data.</summary>
    private int SampleElevations()
    {
        var grid = new double[Grid, Grid];
        int found = 0;
        double min = double.MaxValue, max = double.MinValue;

        for (int j = 0; j < Grid; j++)
        {
            double my = _extent.MinY + (_extent.MaxY - _extent.MinY) * j / (Grid - 1.0);
            for (int i = 0; i < Grid; i++)
            {
                double mx = _extent.MinX + (_extent.MaxX - _extent.MinX) * i / (Grid - 1.0);
                var (lon, lat) = SphericalMercator.ToLonLat(mx, my);
                double ele = _srtm.GetElevation(lat, lon) ?? double.NaN;
                if (!double.IsNaN(ele))
                {
                    found++;
                    min = Math.Min(min, ele);
                    max = Math.Max(max, ele);
                }
                grid[i, j] = ele;
            }
        }

        if (found == 0) { min = max = 0; }
        // Flat-fill any voids so the mesh stays continuous.
        double fill = found == 0 ? 0 : min;
        for (int j = 0; j < Grid; j++)
            for (int i = 0; i < Grid; i++)
                if (double.IsNaN(grid[i, j])) grid[i, j] = fill;

        _elevations = grid;
        _minEle = min;
        _maxEle = max;
        return found;
    }

    /// <summary>Bilinear terrain elevation (m) at a Mercator point, from the Grid×Grid sample grid.</summary>
    private double SampleEle(double mx, double my)
    {
        double gx = (mx - _extent.MinX) / (_extent.MaxX - _extent.MinX) * (Grid - 1);
        double gy = (my - _extent.MinY) / (_extent.MaxY - _extent.MinY) * (Grid - 1);
        int x0 = Math.Clamp((int)Math.Floor(gx), 0, Grid - 1);
        int y0 = Math.Clamp((int)Math.Floor(gy), 0, Grid - 1);
        int x1 = Math.Min(Grid - 1, x0 + 1), y1 = Math.Min(Grid - 1, y0 + 1);
        double fx = gx - x0, fy = gy - y0;
        double a = _elevations[x0, y0], b = _elevations[x1, y0];
        double c = _elevations[x0, y1], d = _elevations[x1, y1];
        return (a * (1 - fx) + b * fx) * (1 - fy) + (c * (1 - fx) + d * fx) * fy;
    }

    // ======================= per-tile drape =======================

    /// <summary>Number of basemap tiles covering the extent at a zoom (= number of patches).</summary>
    private long TileCount(int zoom) => MapExporter.EstimateSize(_extent, zoom, 1.0).Tiles;

    /// <summary>Track polyline reduced to Web-Mercator metres plus a bbox, for the tile-texture bake.</summary>
    private sealed class DrawTrack
    {
        public (double X, double Y)[] Pts = null!;
        public SKColor Color;
        public float Width;
        public double MinX, MaxX, MinY, MaxY;
        /// <summary>Per-segment gradient runs (inclusive point ranges + colour), or null for a solid line.</summary>
        public (int Start, int End, SKColor Color)[]? Runs;
    }

    /// <summary>Projects every visible track to Mercator metres once, shared by all tile bakes.</summary>
    private List<DrawTrack> BuildDrawTracks()
    {
        var list = new List<DrawTrack>();
        foreach (var t in _tracks)
        {
            if (!t.Visible || t.Points.Count < 2) continue;
            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            var pts = new (double X, double Y)[t.Points.Count];
            for (int i = 0; i < t.Points.Count; i++)
            {
                var (x, y) = SphericalMercator.FromLonLat(t.Points[i].Lon, t.Points[i].Lat);
                pts[i] = (x, y);
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
            }

            // The gradient track bakes as red→blue runs (matching the 2D map); others stay solid.
            var grad = ReferenceEquals(t, _gradientTrack)
                ? TrackGradient.Compute(t.Points, _gradientMetric, _paceMode)
                : null;
            var runs = grad is null
                ? null
                : TrackGradient.ColorRuns(grad.Goodness)
                    .Select(r => (r.Start, r.End, new SKColor(r.R, r.G, r.B))).ToArray();

            list.Add(new DrawTrack
            {
                Pts = pts,
                Color = ParseHex(t.ColorHex),
                Width = (float)Math.Max(2, t.Width),
                MinX = minX, MaxX = maxX, MinY = minY, MaxY = maxY,
                Runs = runs,
            });
        }
        return list;
    }

    /// <summary>Vertex arrays + baked texture for one tile patch (built off the UI thread).</summary>
    private sealed class PatchData
    {
        public double[] Xs = null!, Ys = null!, BaseZ = null!;
        public System.Windows.Point[] Uvs = null!;
        public int[] Indices = null!;
        public Material Material = null!;
    }

    /// <summary>A live terrain patch: its mesh plus the constant geometry needed to re-exaggerate it.</summary>
    private sealed class Patch
    {
        public GeometryModel3D Model = null!;
        public MeshGeometry3D Mesh = null!;
        public double[] Xs = null!, Ys = null!, BaseZ = null!;
    }

    /// <summary>
    /// Fetches every tile covering the extent at <paramref name="zoom"/> and builds one textured patch
    /// per tile. Tiles that fail to fetch/decode are skipped (left as a gap), like the PNG export.
    /// </summary>
    private async Task<List<Patch>> BuildDrapeAsync(int zoom, IProgress<string>? progress, CancellationToken ct = default)
    {
        long count = TileCount(zoom);
        if (count > MaxPatches)
            throw new Exception($"Detail z{zoom} needs {count} tiles (max {MaxPatches}) — pick a lower detail.");

        double res = MapExporter.ResolutionAtZoom(zoom), span = 256 * res;
        int tx0 = (int)Math.Floor((_extent.MinX + Origin) / span), tx1 = (int)Math.Floor((_extent.MaxX + Origin) / span);
        int ty0 = (int)Math.Floor((Origin - _extent.MaxY) / span), ty1 = (int)Math.Floor((Origin - _extent.MinY) / span);
        int worldTiles = 1 << zoom;

        var tracks = BuildDrawTracks();
        var patches = new List<Patch>();
        int blocked = 0, done = 0;
        long total = (long)(tx1 - tx0 + 1) * (ty1 - ty0 + 1);

        for (int ty = ty0; ty <= ty1; ty++)
            for (int tx = tx0; tx <= tx1; tx++)
            {
                ct.ThrowIfCancellationRequested(); // stop promptly when a newer sun time supersedes this render
                done++;
                if (ty < 0 || ty >= worldTiles) continue;              // nothing above/below the world
                int wx = ((tx % worldTiles) + worldTiles) % worldTiles; // wrap across the antimeridian
                progress?.Report($"Rendering tiles… {done}/{total}");

                byte[]? bytes;
                try { bytes = await _tiles.GetTileAsync(new TileInfo { Index = new TileIndex(wx, ty, zoom) }); }
                catch { bytes = null; }
                if (bytes is null) { blocked++; continue; }

                int ftx = tx, fty = ty;
                PatchData? data = await Task.Run(() => BuildPatchData(bytes, ftx, fty, span, tracks));
                if (data is null) { blocked++; continue; }
                patches.Add(BuildPatchModel(data));
            }

        _lastBlocked = blocked;
        return patches;
    }

    /// <summary>
    /// Builds the geometry + baked texture for one tile, clipped to the view extent. Runs off the UI
    /// thread: it produces raw vertex arrays and a frozen material only (no live Media3D objects).
    /// </summary>
    private PatchData? BuildPatchData(byte[] bytes, int tx, int ty, double span, List<DrawTrack> tracks)
    {
        using var tile = SKBitmap.Decode(bytes);
        if (tile is null) return null;

        double tileMinX = tx * span - Origin, tileMaxX = (tx + 1) * span - Origin;
        double tileMaxY = Origin - ty * span, tileMinY = Origin - (ty + 1) * span;

        // Clip the patch to the view extent so edge tiles don't hang past the terrain.
        double pMinX = Math.Max(tileMinX, _extent.MinX), pMaxX = Math.Min(tileMaxX, _extent.MaxX);
        double pMinY = Math.Max(tileMinY, _extent.MinY), pMaxY = Math.Min(tileMaxY, _extent.MaxY);
        if (pMaxX <= pMinX || pMaxY <= pMinY) return null; // tile outside the extent

        // Subdivide the patch to roughly match the elevation grid so the drape follows the terrain.
        double cellX = (_extent.MaxX - _extent.MinX) / (Grid - 1), cellY = (_extent.MaxY - _extent.MinY) / (Grid - 1);
        int nx = Math.Clamp((int)Math.Ceiling((pMaxX - pMinX) / cellX) + 1, 2, 32);
        int ny = Math.Clamp((int)Math.Ceiling((pMaxY - pMinY) / cellY) + 1, 2, 32);

        var xs = new double[nx * ny];
        var ys = new double[nx * ny];
        var bz = new double[nx * ny];
        var uv = new System.Windows.Point[nx * ny];
        for (int j = 0; j < ny; j++)
        {
            double my = pMinY + (pMaxY - pMinY) * j / (ny - 1.0);
            for (int i = 0; i < nx; i++)
            {
                double mx = pMinX + (pMaxX - pMinX) * i / (nx - 1.0);
                int n = j * nx + i;
                xs[n] = (mx - _cx) * _k;   // east  → +X
                ys[n] = (my - _cy) * _k;   // north → +Y
                bz[n] = SampleEle(mx, my); // elevation before exaggeration
                // Tile image row 0 is north (top): U across the tile, V flips so north = 0.
                uv[n] = new System.Windows.Point((mx - tileMinX) / span, (tileMaxY - my) / span);
            }
        }

        var indices = new int[(nx - 1) * (ny - 1) * 6];
        int idx = 0;
        for (int j = 0; j < ny - 1; j++)
            for (int i = 0; i < nx - 1; i++)
            {
                int a = j * nx + i, b = a + 1, c = a + nx + 1, d = a + nx;
                indices[idx++] = a; indices[idx++] = b; indices[idx++] = c; // CCW seen from +Z
                indices[idx++] = a; indices[idx++] = c; indices[idx++] = d;
            }

        // Bake the crossing track segments onto the tile bitmap, in tile-pixel space.
        int px = tile.Width, py = tile.Height;
        using (var canvas = new SKCanvas(tile))
        {
            foreach (var tr in tracks)
            {
                if (tr.MaxX < tileMinX || tr.MinX > tileMaxX || tr.MaxY < tileMinY || tr.MinY > tileMaxY) continue;

                float TX(int i) => (float)((tr.Pts[i].X - tileMinX) / span * px);
                float TY(int i) => (float)((tileMaxY - tr.Pts[i].Y) / span * py);

                if (tr.Runs is not null)
                {
                    // Black casing under the runs so the gradient reads over the draped basemap.
                    using (var casePaint = new SKPaint
                    {
                        Style = SKPaintStyle.Stroke,
                        Color = SKColors.Black,
                        StrokeWidth = tr.Width + 2,
                        IsAntialias = true,
                        StrokeCap = SKStrokeCap.Round,
                        StrokeJoin = SKStrokeJoin.Round,
                    })
                    using (var casePath = new SKPath())
                    {
                        for (int i = 0; i < tr.Pts.Length; i++)
                        {
                            if (i == 0) casePath.MoveTo(TX(i), TY(i)); else casePath.LineTo(TX(i), TY(i));
                        }
                        canvas.DrawPath(casePath, casePaint);
                    }

                    foreach (var (start, end, col) in tr.Runs)
                    {
                        using var runPaint = new SKPaint
                        {
                            Style = SKPaintStyle.Stroke,
                            Color = col,
                            StrokeWidth = tr.Width,
                            IsAntialias = true,
                            StrokeCap = SKStrokeCap.Round,
                            StrokeJoin = SKStrokeJoin.Round,
                        };
                        using var runPath = new SKPath();
                        for (int i = start; i <= end; i++)
                        {
                            if (i == start) runPath.MoveTo(TX(i), TY(i)); else runPath.LineTo(TX(i), TY(i));
                        }
                        canvas.DrawPath(runPath, runPaint);
                    }
                    continue;
                }

                using var paint = new SKPaint
                {
                    Style = SKPaintStyle.Stroke,
                    Color = tr.Color,
                    StrokeWidth = tr.Width,
                    IsAntialias = true,
                    StrokeCap = SKStrokeCap.Round,
                    StrokeJoin = SKStrokeJoin.Round,
                };
                using var path = new SKPath();
                for (int i = 0; i < tr.Pts.Length; i++)
                {
                    if (i == 0) path.MoveTo(TX(i), TY(i)); else path.LineTo(TX(i), TY(i));
                }
                canvas.DrawPath(path, paint);
            }

            // Terrain cast shadows: darken texels the sun can't reach (baked on top of the map + tracks).
            if (_shadowGrid is not null)
                BakeShadow(canvas, px, py, tileMinX, tileMaxX, tileMinY, tileMaxY);

            canvas.Flush();
        }

        var brush = new ImageBrush(ToBitmapSource(tile)) { TileMode = TileMode.None, Stretch = Stretch.Fill };
        brush.Freeze();
        var mat = new DiffuseMaterial(brush);
        mat.Freeze();

        return new PatchData { Xs = xs, Ys = ys, BaseZ = bz, Uvs = uv, Indices = indices, Material = mat };
    }

    /// <summary>Plain grey underside so looking "into" the terrain (from below, or through a gap) reads as the
    /// inside of the surface rather than a mirror of the map.</summary>
    private static readonly Material InnerMaterial = CreateInnerMaterial();

    private static Material CreateInnerMaterial()
    {
        var m = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)));
        m.Freeze();
        return m;
    }

    /// <summary>Assembles a live patch (mesh + model) from patch data on the UI thread.</summary>
    private Patch BuildPatchModel(PatchData d)
    {
        var mesh = new MeshGeometry3D
        {
            Positions = BuildPositions(d.Xs, d.Ys, d.BaseZ, _exaggeration),
            TextureCoordinates = new PointCollection(d.Uvs),
            TriangleIndices = new Int32Collection(d.Indices),
        };
        // Front = draped map; back (underside) = plain grey so the interior is unmistakable.
        var model = new GeometryModel3D(mesh, d.Material) { BackMaterial = InnerMaterial };
        return new Patch { Model = model, Mesh = mesh, Xs = d.Xs, Ys = d.Ys, BaseZ = d.BaseZ };
    }

    private static Point3DCollection BuildPositions(double[] xs, double[] ys, double[] bz, double exag)
    {
        var pos = new Point3DCollection(xs.Length);
        for (int n = 0; n < xs.Length; n++) pos.Add(new Point3D(xs[n], ys[n], bz[n] * exag));
        return pos;
    }

    private static BitmapSource ToBitmapSource(SKBitmap bmp)
    {
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 90);
        using var ms = new MemoryStream(data.ToArray());
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.StreamSource = ms;
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    private static SKColor ParseHex(string hex)
    {
        try
        {
            hex = hex.TrimStart('#');
            return new SKColor(
                Convert.ToByte(hex.Substring(0, 2), 16),
                Convert.ToByte(hex.Substring(2, 2), 16),
                Convert.ToByte(hex.Substring(4, 2), 16));
        }
        catch { return new SKColor(0xE5, 0x39, 0x35); }
    }

    // ======================= camera =======================

    private void ResetView_Click(object sender, RoutedEventArgs e)
    {
        double span = Math.Max(_sizeX, _sizeY);
        double top = (_maxEle > 0 ? _maxEle : 0) * _exaggeration;
        // Stand south of the area, above the highest ground, looking north and downward.
        Cam.Position = new Point3D(0, -_sizeY * 0.75, top + span * 0.45);
        Cam.LookDirection = new Vector3D(0, _sizeY * 0.75, -(span * 0.35));
        Cam.UpDirection = new Vector3D(0, 0, 1);
        UpdateHeading();
    }

    private double StepSize => Math.Max(Math.Max(_sizeX, _sizeY) * 0.06, 25);

    /// <summary>Unit vectors for "forward" and "right" projected onto the ground plane.</summary>
    private (Vector3D Fwd, Vector3D Right) GroundAxes()
    {
        var f = new Vector3D(Cam.LookDirection.X, Cam.LookDirection.Y, 0);
        if (f.Length < 1e-6) f = new Vector3D(0, 1, 0);
        f.Normalize();
        var r = new Vector3D(f.Y, -f.X, 0); // 90° clockwise from forward
        return (f, r);
    }

    private void Translate(Vector3D delta)
    {
        Cam.Position += delta;
        UpdateHeading();
    }

    private void MoveForward_Click(object sender, RoutedEventArgs e) => Translate(GroundAxes().Fwd * StepSize);
    private void MoveBack_Click(object sender, RoutedEventArgs e) => Translate(-GroundAxes().Fwd * StepSize);
    private void MoveRight_Click(object sender, RoutedEventArgs e) => Translate(GroundAxes().Right * StepSize);
    private void MoveLeft_Click(object sender, RoutedEventArgs e) => Translate(-GroundAxes().Right * StepSize);
    private void MoveUp_Click(object sender, RoutedEventArgs e) => Translate(new Vector3D(0, 0, StepSize));
    private void MoveDown_Click(object sender, RoutedEventArgs e) => Translate(new Vector3D(0, 0, -StepSize));

    // ======================= rotation / tilt / zoom =======================

    private const double RotateStepDeg = 15;
    private const double TiltStepDeg = 8;
    private const double ZoomStep = 1.25;

    /// <summary>The ground point the camera is looking at — the pivot for orbiting and zooming.</summary>
    private Point3D Target => Cam.Position + Cam.LookDirection;

    /// <summary>Re-aims the camera at <paramref name="target"/> from a new offset, keeping Z up.</summary>
    private void ApplyOrbit(Point3D target, Vector3D offset)
    {
        Cam.Position = target + offset;
        Cam.LookDirection = target - Cam.Position;
        Cam.UpDirection = new Vector3D(0, 0, 1);
        UpdateHeading();
    }

    /// <summary>Horizontal rotation: swings the camera around the target about the vertical axis.</summary>
    private void OrbitHorizontal(double deg)
    {
        var target = Target;
        var offset = Cam.Position - target;
        var m = new Matrix3D();
        m.Rotate(new Quaternion(new Vector3D(0, 0, 1), deg));
        ApplyOrbit(target, m.Transform(offset));
    }

    /// <summary>
    /// Vertical rotation: raises/lowers the camera along its orbit. Clamped between just above the
    /// ground and near-overhead so the view can never flip past vertical.
    /// </summary>
    private void OrbitVertical(double deg)
    {
        var target = Target;
        var offset = Cam.Position - target;
        double r = offset.Length;
        if (r < 1e-6) return;

        double horiz = Math.Sqrt(offset.X * offset.X + offset.Y * offset.Y);
        double elevation = Math.Atan2(offset.Z, horiz) * 180.0 / Math.PI;
        double newElev = Math.Clamp(elevation + deg, 2, 88) * Math.PI / 180.0;
        double azimuth = Math.Atan2(offset.Y, offset.X);

        ApplyOrbit(target, new Vector3D(
            r * Math.Cos(newElev) * Math.Cos(azimuth),
            r * Math.Cos(newElev) * Math.Sin(azimuth),
            r * Math.Sin(newElev)));
    }

    /// <summary>Zoom by moving the camera along its view ray; distance is clamped to the terrain size.</summary>
    private void ZoomBy(double factor)
    {
        var target = Target;
        var offset = Cam.Position - target;
        double r = offset.Length;
        if (r < 1e-6) return;

        double span = Math.Max(_sizeX, _sizeY);
        double clamped = Math.Clamp(r * factor, Math.Max(span * 0.005, 15), span * 8);
        offset.Normalize();
        ApplyOrbit(target, offset * clamped);
    }

    // Orbiting the camera anticlockwise about +Z swings the view clockwise, so the signs are flipped
    // to make "rotate right" actually turn the heading to the right (N -> E -> S -> W).
    private void RotateLeft_Click(object sender, RoutedEventArgs e) => OrbitHorizontal(RotateStepDeg);
    private void RotateRight_Click(object sender, RoutedEventArgs e) => OrbitHorizontal(-RotateStepDeg);
    private void TiltUp_Click(object sender, RoutedEventArgs e) => OrbitVertical(TiltStepDeg);
    private void TiltDown_Click(object sender, RoutedEventArgs e) => OrbitVertical(-TiltStepDeg);
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomBy(1 / ZoomStep);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomBy(ZoomStep);

    /// <summary>Mouse wheel zooms in/out (one notch per detent).</summary>
    private void Viewport_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        ZoomBy(e.Delta > 0 ? 1 / ZoomStep : ZoomStep);
        e.Handled = true;
    }

    // Right-drag orbits: horizontal drag rotates, vertical drag tilts.
    private System.Windows.Point _orbitFrom;
    private bool _orbiting;

    private void Viewport_RightDown(object sender, MouseButtonEventArgs e)
    {
        _orbiting = true;
        _orbitFrom = e.GetPosition(Viewport);
        Viewport.CaptureMouse();
        e.Handled = true;
    }

    private void Viewport_MouseMoveOrbit(object sender, MouseEventArgs e)
    {
        if (!_orbiting || e.RightButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(Viewport);
        double dx = p.X - _orbitFrom.X, dy = p.Y - _orbitFrom.Y;
        _orbitFrom = p;
        // Drag right turns the view right; pushing the mouse away tilts towards the horizon.
        if (Math.Abs(dx) > 0) OrbitHorizontal(-dx * 0.4);
        if (Math.Abs(dy) > 0) OrbitVertical(dy * 0.3);
    }

    private void Viewport_RightUp(object sender, MouseButtonEventArgs e)
    {
        if (!_orbiting) return;
        _orbiting = false;
        Viewport.ReleaseMouseCapture();
        e.Handled = true;
    }

    /// <summary>Re-applies vertical exaggeration to every terrain patch (heights held in each patch's BaseZ).</summary>
    private void Exaggeration_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _exaggeration = e.NewValue;
        if (ExagLabel is not null) ExagLabel.Text = $"{_exaggeration:0.0}×";
        foreach (var p in _patches)
            p.Mesh.Positions = BuildPositions(p.Xs, p.Ys, p.BaseZ, _exaggeration);
        BuildFlags(); // flag heights follow the exaggerated terrain
        if (ChkSun?.IsChecked == true) ScheduleShadowRebake(); // shadows follow the exaggerated relief too
    }

    /// <summary>Compass heading of the view direction, 0° = north, clockwise.</summary>
    private void UpdateHeading()
    {
        var d = Cam.LookDirection;
        double heading = Math.Atan2(d.X, d.Y) * 180.0 / Math.PI;
        if (heading < 0) heading += 360;

        NeedleRotate.Angle = heading;
        HeadingText.Text = $"{heading:F0}°  {Compass(heading)}";

        SyncFlagCamera(); // keep the flag overlay locked to the main camera

        // Report where the camera stands so the 2D map can draw the viewer icon.
        var (lon, lat) = SphericalMercator.ToLonLat(_cx + Cam.Position.X / _k, _cy + Cam.Position.Y / _k);
        ViewpointChanged?.Invoke(lat, lon, heading);
    }

    // ======================= basemap detail (tile zoom) =======================

    /// <summary>A selectable basemap detail level = one tile zoom, labelled with its ground scale.</summary>
    private sealed record DetailLevel(int Zoom, string Label)
    {
        public override string ToString() => Label;
    }

    /// <summary>
    /// Offers the tile zooms for the draped basemap, each labelled with its ground scale. Detail is
    /// bounded by how many tiles cover the extent (each tile is its own patch), not by texture size:
    /// a heavy level (many tiles) is flagged ⚠ and a level past the hard cap is shown disabled ⛔.
    /// </summary>
    private void PopulateDetailLevels()
    {
        if (DetailCombo is null) return;

        int hi = Math.Max(_maxZoom, _zoom);
        int lo = Math.Max(1, Math.Min(_zoom, _maxZoom) - 6);

        DetailCombo.Items.Clear();
        DetailLevel? current = null;
        DetailLevel? lastEnabled = null;
        for (int z = lo; z <= hi; z++)
        {
            long tiles = TileCount(z);
            string scale = MapExporter.ScaleLabel(MapExporter.MetersPerTile(_extent, z));
            if (tiles > MaxPatches)
            {
                DetailCombo.Items.Add(new ComboBoxItem
                {
                    Content = $"z{z} · {scale} ⛔",
                    IsEnabled = false,
                    FontSize = 10,
                });
                continue;
            }
            string flag = tiles > HeavyPatches ? " ⚠" : "";
            var item = new DetailLevel(z, $"z{z} · {scale}{flag}");
            DetailCombo.Items.Add(item);
            lastEnabled = item;
            if (z == _zoom) current = item;
        }
        DetailCombo.SelectedItem = current ?? lastEnabled;
    }

    /// <summary>Rebuilds the drape at the chosen tile zoom, keeping the same terrain and region.</summary>
    private async void Detail_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_detailReady || DetailCombo.SelectedItem is not DetailLevel lvl || lvl.Zoom == _zoom) return;

        DetailCombo.IsEnabled = false;
        try { await SwapDrapeAsync(lvl.Zoom, $"Rendering basemap at z{lvl.Zoom} ({TileCount(lvl.Zoom)} tiles)…"); }
        finally { DetailCombo.IsEnabled = true; }
    }

    /// <summary>Rebuilds the drape at <paramref name="zoom"/> with the current shadow mask, swapping it in only on
    /// success (a failure keeps the old view). Serialised so overlapping requests can't corrupt the scene.</summary>
    private async Task SwapDrapeAsync(int zoom, string startStatus, CancellationToken ct = default)
    {
        if (_drapeBusy) return;
        _drapeBusy = true;
        StatusText.Text = startStatus;
        try
        {
            var progress = new Progress<string>(s => StatusText.Text = s);
            var built = await BuildDrapeAsync(zoom, progress, ct);
            ct.ThrowIfCancellationRequested(); // discard a render the user has already superseded

            _terrain.Children.Clear();
            _patches = built;
            foreach (var p in _patches) _terrain.Children.Add(p.Model);
            _zoom = zoom;
            _shadowApplied = _shadowGrid is not null;

            string blocked = _lastBlocked > 0 ? $"   ·   {_lastBlocked} tile(s) skipped" : "";
            string shade = _shadowApplied ? "   ·   ☀ shadows" : "";
            StatusText.Text = $"Terrain {_minEle:F0}–{_maxEle:F0} m   ·   basemap z{_zoom} · {_patches.Count} tiles{blocked}{shade}";
        }
        catch (OperationCanceledException)
        {
            // A newer sun time cancelled this render; the next one will refresh the status.
        }
        catch (Exception ex)
        {
            StatusText.Text = "Texture render failed: " + ex.Message;
        }
        finally
        {
            _drapeBusy = false;
        }
    }

    /// <summary>Saves the current 3D viewport (map only, without the overlay controls) to a PNG file.</summary>
    private void SaveImage_Click(object sender, RoutedEventArgs e)
    {
        int w = (int)Math.Ceiling(MapScene.ActualWidth), h = (int)Math.Ceiling(MapScene.ActualHeight);
        if (w < 1 || h < 1) { StatusText.Text = "Nothing to save yet."; return; }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "PNG image|*.png",
            DefaultExt = ".png",
            FileName = "track-3d-view.png",
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            // Render the terrain + flag overlay together (the compass/nav/status HUD are siblings, so excluded).
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(MapScene);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(dlg.FileName);
            encoder.Save(fs);
            StatusText.Text = $"Saved {w}×{h}px → {Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Save failed: " + ex.Message;
        }
    }

    private static string Compass(double deg) =>
        new[] { "N", "NE", "E", "SE", "S", "SW", "W", "NW" }[(int)Math.Round(deg / 45.0) % 8];

    // ======================= flags (always-on-top billboards) =======================

    /// <summary>Copies the live camera to the flag overlay's camera so the two viewports project identically
    /// (the flags then sit exactly over their ground positions while drawing on top of the terrain).</summary>
    private void SyncFlagCamera()
    {
        if (FlagCam is null) return;
        FlagCam.Position = Cam.Position;
        FlagCam.LookDirection = Cam.LookDirection;
        FlagCam.UpDirection = Cam.UpDirection;
        FlagCam.FieldOfView = Cam.FieldOfView;
        FlagCam.NearPlaneDistance = Cam.NearPlaneDistance;
        FlagCam.FarPlaneDistance = Cam.FarPlaneDistance;
    }

    private void Flags_Toggled(object sender, RoutedEventArgs e) => BuildFlags();
    private void PointFlags_Changed(object sender, SelectionChangedEventArgs e) => BuildFlags();

    /// <summary>(Re)builds the waypoint and track-point flag billboards from the current toggles. Waypoints are
    /// labelled by name; track-point flags (opt-in) mark the active track's distance, thinned to stay readable.
    /// The billboards live in a separate overlay locked to the main camera, so they always face the observer and
    /// stay visible through hills. Positions sit on the exaggerated terrain and only cover the region in view.</summary>
    private void BuildFlags()
    {
        if (!_flagsReady || FlagViewport is null) return;

        if (_waypointFlags is not null) { FlagViewport.Children.Remove(_waypointFlags); _waypointFlags = null; }
        if (_pointFlags is not null) { FlagViewport.Children.Remove(_pointFlags); _pointFlags = null; }

        if (ChkWaypointFlags?.IsChecked == true)
        {
            var items = new List<BillboardTextItem>();
            foreach (var t in _tracks)
            {
                if (!t.Visible) continue;
                foreach (var p in t.Points)
                    if (p.IsWaypoint && FlagPosition(p.Lat, p.Lon) is Point3D pos)
                        items.Add(new BillboardTextItem { Text = p.Name!, Position = pos });
            }
            if (items.Count > 0)
                _waypointFlags = AddFlagGroup(items,
                    bg: Brushes.White, fg: Brushes.Black,
                    pin: new SolidColorBrush(Color.FromRgb(106, 27, 154)), fontSize: 12);
        }

        // "Track points" marks the active track (the one carrying the gradient) with distance flags placed at
        // the interval chosen in the combo (interpolated along the line, so spacing is exact) plus the finish.
        double interval = PointFlagIntervalM();
        if (interval > 0 && _gradientTrack is { } act && act.Points.Count > 1)
        {
            var pts = act.Points;
            var cum = GeoMath.CumulativeDistancesM(pts);
            int n = pts.Count;
            double total = cum[^1];
            var items = new List<BillboardTextItem>();

            int seg = 0;
            for (double d = interval; d < total - 1 && items.Count < MaxPointFlags; d += interval)
            {
                while (seg < n - 2 && cum[seg + 1] < d) seg++;
                double span = cum[seg + 1] - cum[seg];
                double t = span > 1e-6 ? (d - cum[seg]) / span : 0;
                double lat = pts[seg].Lat + (pts[seg + 1].Lat - pts[seg].Lat) * t;
                double lon = pts[seg].Lon + (pts[seg + 1].Lon - pts[seg].Lon) * t;
                if (FlagPosition(lat, lon) is Point3D pos)
                    items.Add(new BillboardTextItem { Text = FormatDist(d), Position = pos });
            }
            if (FlagPosition(pts[^1].Lat, pts[^1].Lon) is Point3D finish) // always flag the finish distance
                items.Add(new BillboardTextItem { Text = FormatDist(total), Position = finish });

            if (items.Count > 0)
                _pointFlags = AddFlagGroup(items,
                    bg: new SolidColorBrush(Color.FromArgb(235, 255, 253, 231)), fg: Brushes.Black,
                    pin: Brushes.DimGray, fontSize: 10);
        }
    }

    /// <summary>The selected track-point flag spacing in metres, or 0 when "Off".</summary>
    private double PointFlagIntervalM() =>
        CmbPointFlags?.SelectedItem is System.Windows.Controls.ComboBoxItem it
        && it.Tag is string s && int.TryParse(s, out int m) ? m : 0;

    private static string FormatDist(double meters) =>
        meters < 1000 ? $"{meters:0} m" : $"{meters / 1000:0.##} km";

    /// <summary>Builds one styled billboard group (label + pin) and adds it to the overlay viewport.</summary>
    private BillboardTextGroupVisual3D AddFlagGroup(List<BillboardTextItem> items, Brush bg, Brush fg, Brush pin, double fontSize)
    {
        var group = new BillboardTextGroupVisual3D
        {
            Items = items,
            Background = bg,
            Foreground = fg,
            BorderBrush = pin,
            BorderThickness = new Thickness(1),
            PinBrush = pin,
            PinWidth = 2,
            Padding = new Thickness(4, 2, 4, 2),
            FontSize = fontSize,
            FontWeight = FontWeights.SemiBold,
            Offset = new Vector(0, -28), // float the label above its ground point; the pin drops to the point
        };
        FlagViewport!.Children.Add(group);
        return group;
    }

    /// <summary>The 3D position of a lat/lon on the exaggerated terrain, or null when it lies outside the viewed
    /// extent (so off-screen tracks don't scatter flags at the map edges).</summary>
    private Point3D? FlagPosition(double lat, double lon)
    {
        var (mx, my) = SphericalMercator.FromLonLat(lon, lat);
        if (mx < _extent.MinX || mx > _extent.MaxX || my < _extent.MinY || my > _extent.MaxY) return null;
        return new Point3D((mx - _cx) * _k, (my - _cy) * _k, SampleEle(mx, my) * _exaggeration);
    }

    /// <summary>Moves the camera to a lat/lon (used when the viewer icon is dragged on the 2D map).</summary>
    public void SetViewpoint(double lat, double lon)
    {
        var (mx, my) = SphericalMercator.FromLonLat(lon, lat);
        Cam.Position = new Point3D((mx - _cx) * _k, (my - _cy) * _k, Cam.Position.Z);
        UpdateHeading();
    }

    // ======================= solar lighting =======================

    /// <summary>Seeds the sun from a track timestamp (the selected point, else the first timed point). With no
    /// timestamps anywhere the feature can't be positioned, so the checkbox is disabled.</summary>
    private void InitSun()
    {
        _sunSeed ??= _tracks.SelectMany(t => t.Points).Select(p => p.Time).FirstOrDefault(t => t.HasValue);

        if (_sunSeed is DateTime seed)
        {
            // GPX timestamps are UTC; treat an unspecified kind as such, and fold a local one to UTC.
            DateTime utc = seed.Kind == DateTimeKind.Local
                ? seed.ToUniversalTime()
                : DateTime.SpecifyKind(seed, DateTimeKind.Utc);
            _sunDate = utc.Date;
            _sunReady = false;
            SunSlider.Value = utc.TimeOfDay.TotalHours;
            SunDate.SelectedDate = _sunDate;
            _sunReady = true;
            ChkSun.IsEnabled = true;
        }
        else
        {
            _sunDate = DateTime.UtcNow.Date;
            SunDate.SelectedDate = _sunDate;
            ChkSun.IsEnabled = false;
            ChkSun.IsChecked = false;
        }
        ApplySun();
    }

    private async void Sun_Toggled(object sender, RoutedEventArgs e)
    {
        ApplySun();
        _sunTimer?.Stop();       // toggling should add/clear shadows at once, not after the debounce
        await StartShadowRebakeAsync();
    }

    private void SunTime_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_sunReady) ApplySun(); // updates the light live; shadows re-bake once the drag settles
    }

    private void SunDate_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_sunReady || SunDate.SelectedDate is not DateTime d) return;
        _sunDate = d.Date; // season sets the sun's arc; re-aim the light and re-bake shadows
        ApplySun();
    }

    /// <summary>Cancels any shadow render already running for an older time and restarts the quiet-period
    /// countdown, so a rapidly dragged slider re-bakes only once, ~1.2 s after it settles.</summary>
    private void ScheduleShadowRebake()
    {
        if (_sunTimer is null) return;
        _sunCts?.Cancel();  // abandon a render started for a now-stale time
        _sunTimer.Stop();
        _sunTimer.Start();
    }

    /// <summary>Starts a fresh, cancellable shadow render, superseding any previous one.</summary>
    private async Task StartShadowRebakeAsync()
    {
        _sunCts?.Cancel(); // stop whatever render is running for the old time
        var cts = new CancellationTokenSource();
        _sunCts = cts;
        try { await RebakeShadowsAsync(cts.Token); }
        catch (OperationCanceledException) { /* superseded by a newer time — drop it */ }
        finally
        {
            // Clear the field before disposing so a later Cancel() can't hit a disposed source; a newer
            // render may already have replaced it, in which case leave that one in place.
            if (ReferenceEquals(_sunCts, cts)) _sunCts = null;
            cts.Dispose();
        }
    }

    /// <summary>Applies the current sun state: either the even default daylight, or a directional light aimed
    /// from the sun's real position at the chosen UTC time, with a dim ambient fill so shadowed slopes still
    /// read. Slopes facing the sun brighten and those turned away darken, so relief shows as sun/shade.</summary>
    private void ApplySun()
    {
        bool on = ChkSun.IsChecked == true && ChkSun.IsEnabled;
        if (SunSlider is not null) SunSlider.IsEnabled = on;
        if (SunDate is not null) SunDate.IsEnabled = on;

        if (!on)
        {
            SolarLightVisual.Content = null;
            SetDefaultSun(true);
            SunLabel.Text = _sunSeed is null ? "no time in track" : "off";
            ScheduleShadowRebake();
            return;
        }

        SetDefaultSun(false); // the computed sun replaces the flat daylight

        DateTime utc = DateTime.SpecifyKind(_sunDate + TimeSpan.FromHours(SunSlider.Value), DateTimeKind.Utc);
        var (azDeg, altDeg) = SolarPosition.AltAz(utc, _latC, _lonC);

        double az = azDeg * Math.PI / 180.0, alt = altDeg * Math.PI / 180.0;
        // Direction the light travels (from the sun toward the ground) in scene axes: +X east, +Y north, +Z up.
        var dir = new Vector3D(-Math.Cos(alt) * Math.Sin(az), -Math.Cos(alt) * Math.Cos(az), -Math.Sin(alt));

        var group = new Model3DGroup();
        if (altDeg > 0)
        {
            // Full white overhead, fading toward the horizon but never to nothing at dawn/dusk.
            double lit = 0.20 + 0.80 * Math.Sin(alt);
            byte g = (byte)Math.Clamp(lit * 255, 0, 255);
            group.Children.Add(new DirectionalLight(Color.FromRgb(g, g, g), dir));
            group.Children.Add(new AmbientLight(Color.FromRgb(96, 96, 96))); // lift the shaded faces
        }
        else
        {
            group.Children.Add(new AmbientLight(Color.FromRgb(38, 44, 66))); // night: dim, cool
        }
        group.Freeze();
        SolarLightVisual.Content = group;

        string alten = altDeg > 0 ? $"alt {altDeg:0}°" : "below horizon";
        SunLabel.Text = $"{utc:HH:mm} UTC · {alten}";
        ScheduleShadowRebake();
    }

    /// <summary>Adds or removes the flat default daylight (a Visual3D, so it can't just be hidden).</summary>
    private void SetDefaultSun(bool present)
    {
        bool inTree = Viewport.Children.Contains(DefaultSun);
        if (present && !inTree) Viewport.Children.Insert(0, DefaultSun);
        else if (!present && inTree) Viewport.Children.Remove(DefaultSun);
    }

    // ======================= terrain cast shadows =======================

    /// <summary>Recomputes the cast-shadow mask for the current sun and, if it changed the scene, re-bakes the
    /// drape. Runs the ray-march off the UI thread. No-op until the terrain exists (the first build bakes with
    /// whatever mask is set). When the sun is off or below the horizon the mask is cleared.</summary>
    private async Task RebakeShadowsAsync(CancellationToken ct = default)
    {
        if (!_flagsReady) return; // terrain not built yet
        if (_drapeBusy) { ScheduleShadowRebake(); return; } // a rebuild is running — retry after it settles

        double[,]? grid = null;
        if (ChkSun.IsChecked == true && ChkSun.IsEnabled)
        {
            DateTime utc = DateTime.SpecifyKind(_sunDate + TimeSpan.FromHours(SunSlider.Value), DateTimeKind.Utc);
            var (azDeg, altDeg) = SolarPosition.AltAz(utc, _latC, _lonC);
            if (altDeg > 0)
            {
                double exag = _exaggeration;
                grid = await Task.Run(() => ComputeShadowGrid(azDeg, altDeg, exag, ct), ct);
            }
        }

        ct.ThrowIfCancellationRequested();
        if (grid is null && !_shadowApplied) { _shadowGrid = null; return; } // nothing baked, nothing to clear

        _shadowGrid = grid;
        await SwapDrapeAsync(_zoom, grid is null ? "Clearing shadows…" : "Casting terrain shadows…", ct);
    }

    /// <summary>Ray-marches the elevation grid toward the sun: each cell is shadowed when some nearer terrain
    /// rises above the straight line to the sun. Heights use the current exaggeration so shadows match the relief
    /// on screen. Returns a per-cell mask (1 = lit, 0 = shadowed).</summary>
    private double[,] ComputeShadowGrid(double azDeg, double altDeg, double exag, CancellationToken ct = default)
    {
        double az = azDeg * Math.PI / 180.0, alt = altDeg * Math.PI / 180.0;
        double toE = Math.Cos(alt) * Math.Sin(az), toN = Math.Cos(alt) * Math.Cos(az);
        double horiz = Math.Sqrt(toE * toE + toN * toN);

        var lit = new double[Grid, Grid];
        if (horiz < 1e-9) // sun straight overhead → nothing is shadowed
        {
            for (int j = 0; j < Grid; j++) for (int i = 0; i < Grid; i++) lit[i, j] = 1.0;
            return lit;
        }

        // Heights at display scale, and the ground size of one grid cell.
        var h = new double[Grid, Grid];
        double hmax = double.MinValue;
        for (int j = 0; j < Grid; j++)
            for (int i = 0; i < Grid; i++)
            {
                double v = _elevations[i, j] * exag;
                h[i, j] = v;
                if (v > hmax) hmax = v;
            }

        double cellE = _sizeX / (Grid - 1), cellN = _sizeY / (Grid - 1);
        double ue = toE / horiz, un = toN / horiz;   // unit horizontal step toward the sun
        double step = Math.Min(cellE, cellN);         // march ~one cell at a time
        double stepE = ue * step, stepN = un * step;
        double dz = Math.Tan(alt) * step;             // ray rise per step
        double eps = Math.Max(0.5, (_maxEle - _minEle) * 0.003) * exag; // bias against self-shadow acne
        int maxSteps = (int)(Grid * 1.6);

        System.Threading.Tasks.Parallel.For(0, Grid, new System.Threading.Tasks.ParallelOptions { CancellationToken = ct }, j =>
        {
            for (int i = 0; i < Grid; i++)
            {
                double ex = i * cellE, ny = j * cellN, rz = h[i, j] + eps;
                bool sunlit = true;
                for (int k = 1; k <= maxSteps; k++)
                {
                    ex += stepE; ny += stepN; rz += dz;
                    if (rz > hmax) break;                    // ray cleared all terrain → lit
                    double fi = ex / cellE, fj = ny / cellN;
                    if (fi < 0 || fi > Grid - 1 || fj < 0 || fj > Grid - 1) break; // left the grid → lit
                    if (BilinearH(h, fi, fj) > rz) { sunlit = false; break; }      // blocked by terrain
                }
                lit[i, j] = sunlit ? 1.0 : 0.0;
            }
        });

        return Smooth(lit); // soften the binary mask so shadow edges aren't jagged
    }

    /// <summary>One 3×3 box blur pass, to give the binary shadow mask soft edges.</summary>
    private static double[,] Smooth(double[,] m)
    {
        var o = new double[Grid, Grid];
        for (int j = 0; j < Grid; j++)
            for (int i = 0; i < Grid; i++)
            {
                double sum = 0; int n = 0;
                for (int dj = -1; dj <= 1; dj++)
                    for (int di = -1; di <= 1; di++)
                    {
                        int ii = i + di, jj = j + dj;
                        if (ii < 0 || ii >= Grid || jj < 0 || jj >= Grid) continue;
                        sum += m[ii, jj]; n++;
                    }
                o[i, j] = sum / n;
            }
        return o;
    }

    private static double BilinearH(double[,] g, double fi, double fj)
    {
        int i0 = Math.Clamp((int)Math.Floor(fi), 0, Grid - 1), j0 = Math.Clamp((int)Math.Floor(fj), 0, Grid - 1);
        int i1 = Math.Min(i0 + 1, Grid - 1), j1 = Math.Min(j0 + 1, Grid - 1);
        double ti = fi - i0, tj = fj - j0;
        double a = g[i0, j0], b = g[i1, j0], c = g[i0, j1], d = g[i1, j1];
        return (a * (1 - ti) + b * ti) * (1 - tj) + (c * (1 - ti) + d * ti) * tj;
    }

    /// <summary>Lit fraction (1 = full sun, 0 = full shadow) at a Mercator point, or 1 outside the grid.</summary>
    private double SampleShadow(double mx, double my)
    {
        if (_shadowGrid is null) return 1.0;
        if (mx < _extent.MinX || mx > _extent.MaxX || my < _extent.MinY || my > _extent.MaxY) return 1.0;
        double fi = (mx - _extent.MinX) / (_extent.MaxX - _extent.MinX) * (Grid - 1);
        double fj = (my - _extent.MinY) / (_extent.MaxY - _extent.MinY) * (Grid - 1);
        return BilinearH(_shadowGrid, fi, fj);
    }

    /// <summary>Draws a low-res shadow layer (translucent black where shadowed) over one tile, stretched with
    /// smoothing. The mask is sampled from the shared grid so neighbouring tiles stay seamless.</summary>
    private void BakeShadow(SKCanvas canvas, int px, int py,
        double tileMinX, double tileMaxX, double tileMinY, double tileMaxY)
    {
        const int N = 32;
        using var layer = new SKBitmap(N, N);
        for (int sy = 0; sy < N; sy++)
        {
            double my = tileMaxY - (tileMaxY - tileMinY) * sy / (N - 1);
            for (int sx = 0; sx < N; sx++)
            {
                double mx = tileMinX + (tileMaxX - tileMinX) * sx / (N - 1);
                byte a = (byte)Math.Clamp((1.0 - SampleShadow(mx, my)) * ShadowMaxAlpha, 0, 255);
                layer.SetPixel(sx, sy, new SKColor(0, 0, 0, a));
            }
        }
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
        canvas.DrawBitmap(layer, new SKRect(0, 0, px, py), paint);
    }
}
