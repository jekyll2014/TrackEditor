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
    private int _zoom;                  // basemap tile zoom for the draped texture (user-selectable)
    private bool _detailReady;          // gate Detail_Changed until the first build finishes

    private readonly double _cx, _cy;   // extent centre in Web-Mercator metres
    private readonly double _k;         // Mercator -> true ground metres at the centre latitude

    private double[,] _elevations = new double[Grid, Grid];
    private double _minEle, _maxEle;
    private double _sizeX, _sizeY;      // terrain extent in ground metres
    private double _exaggeration = 1.0;

    private readonly Model3DGroup _terrain = new();
    private List<Patch> _patches = new();
    private int _lastBlocked;           // tiles skipped (fetch/decode failed) in the last drape build

    /// <summary>Raised whenever the camera moves so the 2D map can show where the viewer stands.</summary>
    public event Action<double, double, double>? ViewpointChanged; // lat, lon, heading°

    public Map3DWindow(
        (double MinX, double MinY, double MaxX, double MaxY) extent,
        int zoom, int maxZoom, ITileSource tiles, IReadOnlyList<Track> tracks, SrtmService srtm)
    {
        InitializeComponent();
        _extent = extent;
        _tiles = tiles;
        _tracks = tracks;
        _srtm = srtm;
        _maxZoom = maxZoom;

        _cx = (extent.MinX + extent.MaxX) / 2;
        _cy = (extent.MinY + extent.MaxY) / 2;
        var (_, latC) = SphericalMercator.ToLonLat(_cx, _cy);
        _k = Math.Cos(latC * Math.PI / 180.0); // Mercator metres are stretched by 1/cos(lat)

        _sizeX = (extent.MaxX - extent.MinX) * _k;
        _sizeY = (extent.MaxY - extent.MinY) * _k;

        // Default to the 2D view's zoom; each tile is its own patch, so the only limit is tile count.
        int z = Math.Min(zoom, maxZoom);
        while (z > 1 && TileCount(z) > MaxPatches) z--;
        _zoom = z;
        TerrainVisual.Content = _terrain;
        PopulateDetailLevels();

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

            StatusText.Text = StatusLine(withEle);
            _detailReady = true;
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
            list.Add(new DrawTrack
            {
                Pts = pts,
                Color = ParseHex(t.ColorHex),
                Width = (float)Math.Max(2, t.Width),
                MinX = minX, MaxX = maxX, MinY = minY, MaxY = maxY,
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
    private async Task<List<Patch>> BuildDrapeAsync(int zoom, IProgress<string>? progress)
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
                    float X = (float)((tr.Pts[i].X - tileMinX) / span * px);
                    float Y = (float)((tileMaxY - tr.Pts[i].Y) / span * py);
                    if (i == 0) path.MoveTo(X, Y); else path.LineTo(X, Y);
                }
                canvas.DrawPath(path, paint);
            }
            canvas.Flush();
        }

        var brush = new ImageBrush(ToBitmapSource(tile)) { TileMode = TileMode.None, Stretch = Stretch.Fill };
        brush.Freeze();
        var mat = new DiffuseMaterial(brush);
        mat.Freeze();

        return new PatchData { Xs = xs, Ys = ys, BaseZ = bz, Uvs = uv, Indices = indices, Material = mat };
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
        var model = new GeometryModel3D(mesh, d.Material) { BackMaterial = d.Material };
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
        // Drag right turns the view right; drag down tilts towards the horizon.
        if (Math.Abs(dx) > 0) OrbitHorizontal(-dx * 0.4);
        if (Math.Abs(dy) > 0) OrbitVertical(-dy * 0.3);
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
    }

    /// <summary>Compass heading of the view direction, 0° = north, clockwise.</summary>
    private void UpdateHeading()
    {
        var d = Cam.LookDirection;
        double heading = Math.Atan2(d.X, d.Y) * 180.0 / Math.PI;
        if (heading < 0) heading += 360;

        NeedleRotate.Angle = heading;
        HeadingText.Text = $"{heading:F0}°  {Compass(heading)}";

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

        int newZoom = lvl.Zoom;
        DetailCombo.IsEnabled = false;
        StatusText.Text = $"Rendering basemap at z{newZoom} ({TileCount(newZoom)} tiles)…";
        try
        {
            // Build the new drape first; only swap once it succeeds so a failure keeps the old view.
            var progress = new Progress<string>(s => StatusText.Text = s);
            var built = await BuildDrapeAsync(newZoom, progress);

            _terrain.Children.Clear();
            _patches = built;
            foreach (var p in _patches) _terrain.Children.Add(p.Model);
            _zoom = newZoom;

            string blocked = _lastBlocked > 0 ? $"   ·   {_lastBlocked} tile(s) skipped" : "";
            StatusText.Text = $"Terrain {_minEle:F0}–{_maxEle:F0} m   ·   basemap z{_zoom} · {_patches.Count} tiles{blocked}";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Texture render failed: " + ex.Message;
        }
        finally
        {
            DetailCombo.IsEnabled = true;
        }
    }

    /// <summary>Saves the current 3D viewport (map only, without the overlay controls) to a PNG file.</summary>
    private void SaveImage_Click(object sender, RoutedEventArgs e)
    {
        int w = (int)Math.Ceiling(Viewport.ActualWidth), h = (int)Math.Ceiling(Viewport.ActualHeight);
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
            // Render just the Viewport (the compass/nav/status overlays are siblings, so they're excluded).
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(Viewport);
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

    /// <summary>Moves the camera to a lat/lon (used when the viewer icon is dragged on the 2D map).</summary>
    public void SetViewpoint(double lat, double lon)
    {
        var (mx, my) = SphericalMercator.FromLonLat(lon, lat);
        Cam.Position = new Point3D((mx - _cx) * _k, (my - _cy) * _k, Cam.Position.Z);
        UpdateHeading();
    }
}
