using BruTile;

using HelixToolkit.Wpf;

using Mapsui.Projections;

using SkiaSharp;

using System.IO;
using System.Windows;
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
/// with the rendered basemap (including the track overlays) draped over it as a texture.
/// </summary>
public partial class Map3DWindow : Window
{
    private const int Grid = 160;          // terrain resolution (Grid x Grid vertices)
    private const int MaxTexturePx = 2048; // keep the draped texture a sane size

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
    private MeshGeometry3D? _mesh;
    private Material? _material;

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

        // Pick a texture zoom that keeps the bitmap under MaxTexturePx.
        int z = Math.Min(zoom, maxZoom);
        while (z > 1 && MapExporter.EstimateSize(extent, z, 1.0).W > MaxTexturePx) z--;
        _zoom = z;
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

            StatusText.Text = "Rendering map texture…";
            _material = await BuildMaterialAsync();

            RebuildMesh();
            ResetView_Click(this, new RoutedEventArgs());

            StatusText.Text = withEle == 0
                ? "No SRTM elevation for this area — terrain is flat (set an SRTM folder in Settings)"
                : $"Terrain {_minEle:F0}–{_maxEle:F0} m   ({withEle * 100 / (Grid * Grid)}% sampled)";
            _detailReady = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = "3D build failed: " + ex.Message;
        }
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

    private async Task<Material> BuildMaterialAsync()
    {
        using var bmp = await MapExporter.RenderAsync(
            _tiles, _extent, _zoom, 1.0, _tracks, drawScaleBar: false);
        var src = ToBitmapSource(bmp);
        var brush = new ImageBrush(src) { TileMode = TileMode.None, Stretch = Stretch.Fill };
        brush.Freeze();
        var mat = new DiffuseMaterial(brush);
        mat.Freeze();
        return mat;
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

    /// <summary>(Re)builds the terrain mesh from the sampled grid at the current vertical exaggeration.</summary>
    private void RebuildMesh()
    {
        var positions = new Point3DCollection(Grid * Grid);
        var texcoords = new PointCollection(Grid * Grid);

        for (int j = 0; j < Grid; j++)
        {
            double my = _extent.MinY + (_extent.MaxY - _extent.MinY) * j / (Grid - 1.0);
            double y = (my - _cy) * _k;
            for (int i = 0; i < Grid; i++)
            {
                double mx = _extent.MinX + (_extent.MaxX - _extent.MinX) * i / (Grid - 1.0);
                double x = (mx - _cx) * _k;
                positions.Add(new Point3D(x, y, _elevations[i, j] * _exaggeration));
                // Texture row 0 is the north edge, grid row 0 is the south edge, so flip V.
                texcoords.Add(new System.Windows.Point(i / (Grid - 1.0), 1.0 - j / (Grid - 1.0)));
            }
        }

        var indices = new Int32Collection((Grid - 1) * (Grid - 1) * 6);
        for (int j = 0; j < Grid - 1; j++)
            for (int i = 0; i < Grid - 1; i++)
            {
                int a = j * Grid + i, b = a + 1, c = a + Grid + 1, d = a + Grid;
                indices.Add(a); indices.Add(b); indices.Add(c); // CCW seen from +Z
                indices.Add(a); indices.Add(c); indices.Add(d);
            }

        var mesh = new MeshGeometry3D
        {
            Positions = positions,
            TextureCoordinates = texcoords,
            TriangleIndices = indices,
        };
        mesh.Freeze();
        _mesh = mesh;

        TerrainVisual.Content = new GeometryModel3D(mesh, _material ?? new DiffuseMaterial(Brushes.LightGray))
        {
            BackMaterial = _material,
        };
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

    private void Exaggeration_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _exaggeration = e.NewValue;
        if (ExagLabel is not null) ExagLabel.Text = $"{_exaggeration:0.0}×";
        if (_mesh is not null) RebuildMesh();
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

    // ======================= basemap detail (texture zoom) =======================

    private const int MaxDetailPx = 4096; // upper bound for the on-demand draped texture

    /// <summary>A selectable basemap detail level = one tile zoom, labelled with its ground scale.</summary>
    private sealed record DetailLevel(int Zoom, string Label)
    {
        public override string ToString() => Label;
    }

    /// <summary>
    /// Offers the tile zooms that render the current region within a sane texture size, so the 3D map can
    /// be draped with finer or coarser basemap detail than the 2D view. The auto-picked zoom is preselected.
    /// </summary>
    private void PopulateDetailLevels()
    {
        if (DetailCombo is null) return;

        int hi = _maxZoom;
        while (hi > 1 && MapExporter.EstimateSize(_extent, hi, 1.0).W > MaxDetailPx) hi--;
        int lo = hi;
        while (lo > 2 && MapExporter.EstimateSize(_extent, lo - 1, 1.0).W >= 384) lo--;
        lo = Math.Max(lo, hi - 6);        // keep the list short
        lo = Math.Min(lo, _zoom);         // always include the current default
        hi = Math.Max(hi, _zoom);

        DetailCombo.Items.Clear();
        DetailLevel? current = null;
        for (int z = lo; z <= hi; z++)
        {
            var item = new DetailLevel(z, $"z{z} · {MapExporter.ScaleLabel(MapExporter.MetersPerTile(_extent, z))}");
            DetailCombo.Items.Add(item);
            if (z == _zoom) current = item;
        }
        DetailCombo.SelectedItem = current ?? DetailCombo.Items[^1];
    }

    /// <summary>Re-renders the draped texture at the chosen zoom, keeping the same terrain and region.</summary>
    private async void Detail_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_detailReady || DetailCombo.SelectedItem is not DetailLevel lvl || lvl.Zoom == _zoom) return;

        _zoom = lvl.Zoom;
        DetailCombo.IsEnabled = false;
        StatusText.Text = $"Rendering basemap at z{_zoom}…";
        try
        {
            _material = await BuildMaterialAsync();
            if (TerrainVisual.Content is GeometryModel3D gm)
            {
                gm.Material = _material;
                gm.BackMaterial = _material;
            }
            else RebuildMesh();
            StatusText.Text = $"Terrain {_minEle:F0}–{_maxEle:F0} m   ·   basemap z{_zoom}";
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
