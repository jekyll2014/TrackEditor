using Microsoft.Win32;

using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

using TrackEditor.Core.Models;
using TrackEditor.Core.Services;
using TrackEditor.Core.Services.RaceAnalysis;
using TrackEditor.Core.Skia;
using TrackEditor.Services;

namespace TrackEditor;

public partial class MainWindow : Window
{
    /// <summary>Edit combines the old Draw and Insert modes: click appends, drag moves, double-click inserts.</summary>
    private enum EditMode { View, Edit, Measure }

    private static readonly (string Name, string Hex)[] Palette =
    {
        ("Red", "#E53935"), ("Blue", "#1E88E5"), ("Green", "#43A047"), ("Orange", "#FB8C00"),
        ("Purple", "#8E24AA"), ("Magenta", "#D81B60"), ("Brown", "#6D4C41"), ("Black", "#212121"),
        ("Teal", "#00897B"), ("Navy", "#283593"),
    };

    private readonly TrackDocument _doc = new();
    private readonly SrtmService _srtm = new();
    private readonly OnlineElevationService _online = new();
    private AppSettings _settings = new();
    private bool _elevBusy;
    private readonly MapManager _mapMgr;
    private readonly DispatcherTimer _viewportTimer;

    private Track? _active;
    private double[] _cumDist = Array.Empty<double>();
    private double[] _cumGain = Array.Empty<double>(); // cumulative ascent (m) to each point, for gain flags
    private double?[] _speeds = Array.Empty<double?>();
    private EditMode _mode = EditMode.View;
    private readonly List<(double Lat, double Lon)> _measurePts = new(); // multi-point map measurement
    private readonly RoutingService _router = new();
    private Map3DWindow? _map3D; // non-null while the 3D view is open
    private bool _syncingUi;
    private bool _syncingRoute; // guards the toolbar Route combo while it is set programmatically
    private bool _syncingBaseMap; // guards the toolbar Map combo while it is set programmatically
    private bool _syncingGradient; // guards the toolbar Gradient combo while it is set programmatically
    private int _paletteCursor;

    public MainWindow()
    {
        InitializeComponent();
        _settings = AppSettings.Load();
        _mapMgr = new MapManager(MapCtrl, _settings.BaseMap, _settings.ParamsFor(_settings.BaseMap).TileCacheLimitMB);
        Closed += (_, _) =>
        {
            var (cx, cy, res) = _mapMgr.ViewportState();
            _mapMgr.Dispose(); // checkpoint/close the MBTiles cache cleanly
            SessionStore.Save(new SessionStore.Session
            {
                Active = ActiveIndex(),
                Tracks = _doc.Tracks,
                Map = res > 0 ? new SessionStore.Viewport { CenterX = cx, CenterY = cy, Resolution = res } : null,
            });
        };
        BuildColorCombo();
        BuildRouteCombo();
        ApplySettings();
        SetupPlotMenu();

        _viewportTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _viewportTimer.Tick += ViewportTimer_Tick;
        _viewportTimer.Start();

        RefreshPlots();
        UpdateCommandStates(); // start with the right things greyed out (no track loaded yet)

        // Restore the last session, then open anything passed on the command line — local paths and
        // http(s) URLs alike, so a track can be opened straight from a link.
        Loaded += (_, _) =>
        {
            RestoreSession();
            var args = Environment.GetCommandLineArgs().Skip(1).ToArray();

            var files = args.Where(File.Exists).ToArray();
            if (files.Length > 0) LoadFiles(files);

            foreach (string arg in args.Where(IsHttpUrl)) _ = LoadFromUrlAsync(arg);
        };
    }

    private void RestoreSession()
    {
        var session = SessionStore.Load();
        if (session is null || session.Tracks.Count == 0) return;

        _doc.Tracks.AddRange(session.Tracks);
        _paletteCursor = _doc.Tracks.Count; // continue the palette past restored tracks
        _active = session.Active >= 0 && session.Active < _doc.Tracks.Count
            ? _doc.Tracks[session.Active]
            : _doc.Tracks.FirstOrDefault();
        RefreshAll();
        // Stay on the map piece the user last viewed; only fall back to framing the tracks when no
        // viewport was saved (e.g. a session file from before this was tracked).
        if (session.Map is { Resolution: > 0 } vp)
            _mapMgr.RestoreViewport(vp.CenterX, vp.CenterY, vp.Resolution);
        else
            _mapMgr.ZoomToTracks(_doc.Tracks);
        StatusInfo.Text = $"Restored {_doc.Tracks.Count} track(s) from last session";
    }

    // ======================= settings =======================

    /// <summary>True when SRTM is enabled in settings and the tile folder actually exists.</summary>
    private bool SrtmActive => _settings.SrtmEnabled && _srtm.IsAvailable;

    /// <summary>Push the current settings into the elevation services and the basemap.</summary>
    private void ApplySettings()
    {
        _srtm.Folder = _settings.SrtmFolder;
        _srtm.AutoDownload = _settings.SrtmAutoDownload;
        _online.Provider = _settings.OnlineProvider;
        _online.OpenTopoDataset = _settings.OpenTopoDataset;
        _mapMgr.SetBaseMap(_settings.BaseMap, _settings.ParamsFor(_settings.BaseMap).TileCacheLimitMB);
        _mapMgr.SetWaypointColors(_settings.WaypointLabelBackHex, _settings.WaypointLabelTextHex);
        _mapMgr.SetGradient(_settings.GradientMetric, _settings.PaceMode, _settings.GradeUnit);
        _router.Profile = _settings.RoutingProfile;
        SyncRouteCombo();
        SyncBaseMapCombo();
        SyncGradientCombo();
        ApplyColumnVisibility();
        SyncFlagContentChecks();
    }

    /// <summary>The toolbar Route combo is "Off" plus every routing profile.</summary>
    private void BuildRouteCombo()
    {
        AutoRouteCombo.Items.Add("Off");
        foreach (var p in RoutingService.Profiles) AutoRouteCombo.Items.Add(p);
    }

    /// <summary>Reflects the current auto-route state (off / which profile) in the toolbar combo.</summary>
    private void SyncRouteCombo()
    {
        if (AutoRouteCombo is null) return;
        _syncingRoute = true;
        AutoRouteCombo.SelectedItem =
            _settings.AutoRoute && RoutingService.Profiles.Contains(_settings.RoutingProfile)
                ? _settings.RoutingProfile
                : "Off";
        _syncingRoute = false;
    }

    /// <summary>Reflects the active base map in the toolbar Map combo (items are ordered to match the enum).</summary>
    private void SyncBaseMapCombo()
    {
        if (BaseMapCombo is null) return;
        _syncingBaseMap = true;
        BaseMapCombo.SelectedIndex = (int)_settings.BaseMap;
        _syncingBaseMap = false;
    }

    /// <summary>Reflects the active gradient metric in the toolbar combo (items are ordered to match the enum).</summary>
    private void SyncGradientCombo()
    {
        if (GradientCombo is null) return;
        _syncingGradient = true;
        GradientCombo.SelectedIndex = (int)_settings.GradientMetric;
        _syncingGradient = false;
    }

    /// <summary>Switches the metric that gradient-colours the active track, straight from the toolbar.</summary>
    private void GradientCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingGradient || GradientCombo.SelectedIndex < 0) return;
        var metric = (GradientMetric)GradientCombo.SelectedIndex;
        if (metric == _settings.GradientMetric) return;
        _settings.GradientMetric = metric;
        _mapMgr.SetGradient(metric, _settings.PaceMode, _settings.GradeUnit);
        _mapMgr.RebuildTracks(_doc.Tracks, _active);
        UpdateGradientLegend();
        _settings.Save();
        StatusInfo.Text = metric == GradientMetric.None
            ? "Gradient colouring off — the active track uses its own colour"
            : $"Gradient colouring: {metric} — active track, red = fast/easy, blue = slow/hard";
    }

    /// <summary>Switches the active base map (and applies that map's cache cap) straight from the toolbar.</summary>
    private void BaseMapCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingBaseMap || BaseMapCombo.SelectedIndex < 0) return;
        var provider = (BaseMapProvider)BaseMapCombo.SelectedIndex;
        if (provider == _settings.BaseMap) return;
        _settings.BaseMap = provider;
        _mapMgr.SetBaseMap(provider, _settings.ParamsFor(provider).TileCacheLimitMB);
        _settings.Save();
        StatusInfo.Text = $"Base map: {(BaseMapCombo.SelectedItem as ComboBoxItem)?.Content}";
    }

    /// <summary>Runtime control: "Off" draws straight segments, any profile turns auto-route on with it.</summary>
    private void AutoRouteCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingRoute || AutoRouteCombo.SelectedItem is not string sel) return;
        if (sel == "Off")
        {
            _settings.AutoRoute = false;
            StatusInfo.Text = "Auto-route off — new points connect in a straight line";
        }
        else
        {
            _settings.AutoRoute = true;
            _settings.RoutingProfile = sel;
            _router.Profile = sel;
            StatusInfo.Text = sel == "river"
                ? "Auto-route on (river) — new points follow waterways (rivers, canals)"
                : $"Auto-route on ({sel}) — new points follow paths";
        }
        _settings.Save();
    }

    /// <summary>
    /// Appends a routed leg from the track's last point to the clicked one. Falls back to a plain
    /// straight segment when the routing service can't help (offline, no route, rate limited).
    /// </summary>
    private async Task AppendRoutedAsync(double lat, double lon)
    {
        if (_active is null || _active.Points.Count == 0) return;
        var last = _active.Points[^1];
        var from = (last.Lat, last.Lon);

        _doc.Snapshot(ActiveIndex());
        BeginBusy("Routing…");
        List<TrackPoint>? route;
        try { route = await _router.RouteAsync(from, (lat, lon)); }
        finally { EndBusy(); }

        if (_active is null) return; // the active track could have changed while awaiting

        if (route is null || route.Count < 2)
        {
            var p = new TrackPoint { Lat = lat, Lon = lon };
            if (SrtmActive && _srtm.GetElevation(lat, lon) is double ele) { p.Ele = ele; _active.ElevationEstimated = true; }
            _active.Points.Add(p);
            StatusInfo.Text = "No route found — added a straight segment";
        }
        else
        {
            // Routers return very dense geometry, so optionally thin it before it lands in the track.
            int raw = route.Count;
            if (_settings.AutoRouteSimplify && _settings.AutoRouteToleranceM > 0 && route.Count > 2)
            {
                var keep = GeoMath.DouglasPeucker(route, _settings.AutoRouteToleranceM);
                // Douglas-Peucker keeps the endpoints, so index 0 still duplicates the last track point.
                if (keep.Count >= 2) route = keep.Select(i => route[i]).ToList();
            }

            // route[0] duplicates the current last point, so start at 1.
            for (int i = 1; i < route.Count; i++) _active.Points.Add(route[i]);
            if (route.Any(p => p.Ele is not null)) _active.ElevationEstimated = true;
            StatusInfo.Text = raw != route.Count
                ? $"Routed +{route.Count - 1} points ({_router.Profile}, simplified from {raw - 1} at {_settings.AutoRouteToleranceM:0.#} m)"
                : $"Routed +{route.Count - 1} points ({_router.Profile})";
        }

        RefreshAll();
        SelectPointInGrid(_active.Points.Count - 1);
    }

    /// <summary>Shows/hides the optional points-list columns. A column appears only when the user has it
    /// enabled AND the active track actually carries that data — empty columns hide themselves (Lat/Lon/Km
    /// always have values; the index column always shows).</summary>
    private void ApplyColumnVisibility()
    {
        if (ColWaypoint is null) return; // during InitializeComponent
        static Visibility V(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
        var pts = _active?.Points;
        bool Has(Func<TrackPoint, bool> f) => pts is not null && pts.Count > 0 && pts.Any(f);

        ColWaypoint.Visibility = V(_settings.ColWaypoint && Has(p => p.IsWaypoint));
        ColLat.Visibility = V(_settings.ColLat);
        ColLon.Visibility = V(_settings.ColLon);
        ColEle.Visibility = V(_settings.ColEle && Has(p => p.Ele is not null));
        ColTime.Visibility = V(_settings.ColTime && Has(p => p.Time is not null));
        ColDist.Visibility = V(_settings.ColDist);
        ColHr.Visibility = V(_settings.ColHr && Has(p => p.Hr is not null));
        ColCad.Visibility = V(_settings.ColCad && Has(p => p.Cad is not null));
        ColTemp.Visibility = V(_settings.ColTemp && Has(p => p.Temp is not null));
        ColSurface.Visibility = V(_settings.ColSurface && Has(p => !string.IsNullOrEmpty(p.Surface)));
    }

    /// <summary>Shows/hides the profile-plot series checkboxes so only channels the active track carries
    /// (with 2+ samples) are offered. Speed follows timestamps; altitude follows elevation.</summary>
    private void ApplyPlotSeriesVisibility()
    {
        if (ChkAlt is null) return; // during InitializeComponent
        static Visibility V(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
        var pts = _active?.Points;
        bool Has(Func<TrackPoint, bool> f) => pts is not null && pts.Count > 1 && pts.Count(f) > 1;

        ChkAlt.Visibility = V(Has(p => p.Ele is not null));
        ChkSpeed.Visibility = V(Has(p => p.Time is not null));
        ChkHr.Visibility = V(Has(p => p.Hr is not null));
        ChkCad.Visibility = V(Has(p => p.Cad is not null));
        ChkTemp.Visibility = V(Has(p => p.Temp is not null));
    }

    /// <summary>Points-list column menu: reflect current settings and grey out channels the active
    /// track lacks, so the toggles match what can actually be shown.</summary>
    private void ColumnsMenu_Opened(object sender, RoutedEventArgs e)
    {
        var pts = _active?.Points;
        bool Has(Func<TrackPoint, bool> f) => pts is not null && pts.Count > 0 && pts.Any(f);
        void Set(MenuItem mi, bool @checked, bool enabled) { mi.IsChecked = @checked; mi.IsEnabled = enabled; }

        Set(ColMnuWaypoint, _settings.ColWaypoint, Has(p => p.IsWaypoint));
        Set(ColMnuLat, _settings.ColLat, true);
        Set(ColMnuLon, _settings.ColLon, true);
        Set(ColMnuEle, _settings.ColEle, Has(p => p.Ele is not null));
        Set(ColMnuTime, _settings.ColTime, Has(p => p.Time is not null));
        Set(ColMnuDist, _settings.ColDist, true);
        Set(ColMnuHr, _settings.ColHr, Has(p => p.Hr is not null));
        Set(ColMnuCad, _settings.ColCad, Has(p => p.Cad is not null));
        Set(ColMnuTemp, _settings.ColTemp, Has(p => p.Temp is not null));
        Set(ColMnuSurface, _settings.ColSurface, Has(p => !string.IsNullOrEmpty(p.Surface)));
    }

    /// <summary>Toggles a points-list column on/off (persisted), then re-applies column visibility.</summary>
    private void ColumnToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not string tag) return;
        bool on = mi.IsChecked;
        switch (tag)
        {
            case "Waypoint": _settings.ColWaypoint = on; break;
            case "Lat": _settings.ColLat = on; break;
            case "Lon": _settings.ColLon = on; break;
            case "Ele": _settings.ColEle = on; break;
            case "Time": _settings.ColTime = on; break;
            case "Dist": _settings.ColDist = on; break;
            case "Hr": _settings.ColHr = on; break;
            case "Cad": _settings.ColCad = on; break;
            case "Temp": _settings.ColTemp = on; break;
            case "Surface": _settings.ColSurface = on; break;
        }
        _settings.Save();
        ApplyColumnVisibility();
    }

    private void ClearTileCache_Click(object sender, RoutedEventArgs e)
    {
        // Owner is the settings dialog when invoked from there, otherwise the main window.
        Window owner = sender as Window ?? this;
        if (MessageBox.Show(owner,
                "Delete all cached map tiles for every basemap? They will re-download as you browse.",
                "Clear tile cache", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        long freed = _mapMgr.ClearTileCache();
        StatusInfo.Text = $"Tile cache cleared ({freed / (1024.0 * 1024.0):F1} MB freed)";
    }

    /// <summary>Opens (or re-focuses) the 3D view for the region currently shown on the 2D map.</summary>
    private void Open3D_Click(object sender, RoutedEventArgs e)
    {
        if (_map3D is not null) { _map3D.Activate(); return; }

        // Seed the sun from the selected point's timestamp, if one is selected and timed.
        DateTime? sunTime = null;
        if (_active is not null)
            foreach (var i in SelectedIndices())
                if (i >= 0 && i < _active.Points.Count && _active.Points[i].Time is DateTime t) { sunTime = t; break; }

        var win = new Map3DWindow(
            _mapMgr.ViewportExtent(),
            _mapMgr.CurrentZoomLevel(),
            _mapMgr.BaseMaxZoom,
            _mapMgr.BaseTileSource,
            _doc.Tracks.ToList(),
            _srtm,
            gradientTrack: _active,
            gradientMetric: _settings.GradientMetric,
            paceMode: _settings.PaceMode,
            sunTime: sunTime)
        { Owner = this };

        // Keep the 2D viewer marker in step with the 3D camera.
        win.ViewpointChanged += (lat, lon, heading) =>
            Dispatcher.Invoke(() => _mapMgr.SetViewer(lat, lon, heading));
        win.Closed += (_, _) => { _map3D = null; _mapMgr.ClearViewer(); };

        _map3D = win;
        win.Show();
        StatusInfo.Text = "3D view opened — drag the teal marker on the map to move the viewpoint";
    }

    private HelpWindow? _help; // reused so repeated opens don't stack windows

    private void Help_Click(object sender, RoutedEventArgs e)
    {
        if (_help is null)
        {
            _help = new HelpWindow { Owner = this };
            _help.Closed += (_, _) => _help = null;
            _help.Show();
        }
        else _help.Activate();
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        MessageBox.Show(this,
            $"TrackEditor {v?.ToString(3)}\n\n" +
            "A desktop editor for GPX / KML / KMZ tracks with an interactive map, " +
            "elevation & speed profile, real-path auto-routing and a 3D terrain view.\n\n" +
            "Built with WPF (.NET 9), Mapsui, ScottPlot, HelixToolkit, SkiaSharp and SharpKml.\n" +
            "Map data © OpenStreetMap contributors. Routing by BRouter.\n\n" +
            "See Help ▸ User Guide (F1) for the full guide.",
            "About TrackEditor", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow(_settings) { Owner = this };
        // The dialog hosts the "Clear tile cache" button but the cache lives on the map manager.
        dlg.ClearTileCacheRequested += (_, _) => ClearTileCache_Click(dlg, new RoutedEventArgs());
        if (dlg.ShowDialog() != true) return;
        _settings = dlg.Result;
        _settings.Save();
        ApplySettings();
        RefreshAll();
        StatusInfo.Text = "Settings updated";
        // If a source was just enabled, fill any tracks still missing elevation (fills gaps only).
        FillElevationAfterLoad(_doc.Tracks);
    }

    // ======================= race analysis =======================

    private void RaceMenu_Opened(object sender, RoutedEventArgs e)
    {
        MenuAnalyzeRace.IsEnabled = HasTracks;
        MenuApplyRaceModel.IsEnabled = HasActive && (_active?.Points.Count ?? 0) >= 2;
        MenuEvalSurface.IsEnabled = HasActive && (_active?.Points.Count ?? 0) >= 2;
    }

    private async void EvalSurface_Click(object sender, RoutedEventArgs e)
    {
        if (_active is null || _active.Points.Count < 2)
        {
            StatusInfo.Text = "Evaluate surface: select a track with at least two points";
            return;
        }
        var track = _active;
        BeginBusy("Evaluating surface via routing…");
        try
        {
            var res = await SurfaceInference.InferAsync(track, new RoutingService());
            if (!res.Routed)
            {
                StatusInfo.Text = "Surface: routing unavailable (offline / rate-limited) — nothing filled.";
                return;
            }
            _doc.Snapshot(ActiveIndex());
            int filled = 0;
            for (int i = 0; i < track.Points.Count && i < res.PerPointType.Length; i++)
                if (res.PerPointType[i] is string s) { track.Points[i].Surface = s; filled++; }
            RefreshPointsGrid();
            StatusInfo.Text = $"Surface filled on {filled}/{track.Points.Count} points " +
                              $"({res.Coverage * 100:F0}% with a speed multiplier).";
        }
        catch (Exception ex)
        {
            StatusInfo.Text = "Surface evaluation failed: " + ex.Message;
        }
        finally
        {
            EndBusy();
        }
    }

    private void AnalyzeRace_Click(object sender, RoutedEventArgs e)
    {
        if (_doc.Tracks.Count == 0) { StatusInfo.Text = "Analyze race: open at least one recorded track first"; return; }
        new AnalyzeRaceWindow(_doc.Tracks) { Owner = this }.ShowDialog();
    }

    private void ApplyRaceModel_Click(object sender, RoutedEventArgs e)
    {
        if (_active is null || _active.Points.Count < 2)
        {
            StatusInfo.Text = "Apply race model: select a track with at least two points";
            return;
        }
        var dlg = new ApplyRaceModelWindow(_active, _doc.Tracks, _settings) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.PredictedTrack is null) return;

        var predicted = dlg.PredictedTrack;
        _doc.Snapshot(ActiveIndex());
        predicted.ColorHex = Palette[_paletteCursor++ % Palette.Length].Hex; // distinct colour from the source
        _doc.Tracks.Add(predicted);
        _active = predicted;
        RefreshAll();
        _mapMgr.ZoomToTracks(new[] { predicted });
        StatusInfo.Text = $"Added predicted track “{predicted.Name}”";
    }

    private void Merge_Click(object sender, RoutedEventArgs e)
    {
        if (_active is null || _active.Points.Count < 2)
        {
            StatusInfo.Text = "Merge tracks: select the base track (with at least two points) first";
            return;
        }
        if (_doc.Tracks.Count < 2)
        {
            StatusInfo.Text = "Merge tracks: open a second track to merge with";
            return;
        }
        var dlg = new MergeTracksWindow(_active, _doc.Tracks) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.MergedTrack is null) return;

        var merged = dlg.MergedTrack;
        _doc.Snapshot(ActiveIndex());
        merged.ColorHex = Palette[_paletteCursor++ % Palette.Length].Hex;   // distinct colour from the sources
        _doc.Tracks.Add(merged);
        _active = merged;
        RefreshAll();
        _mapMgr.ZoomToTracks(new[] { merged });
        StatusInfo.Text = $"Added merged track “{merged.Name}”";
    }

    // ======================= file operations =======================

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Tracks (*.gpx;*.tcx;*.fit;*.kml;*.kmz)|*.gpx;*.tcx;*.fit;*.kml;*.kmz|" +
                     "GPX|*.gpx|TCX|*.tcx|FIT|*.fit|KML/KMZ|*.kml;*.kmz|All files|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog() != true) return;
        LoadFiles(dlg.FileNames);
    }

    private void LoadFiles(IReadOnlyList<string> files)
    {
        var loaded = new List<Track>();
        foreach (string file in files)
        {
            try
            {
                var tracks = System.IO.Path.GetExtension(file).ToLowerInvariant() switch
                {
                    ".gpx" => GpxIo.Load(file),
                    ".tcx" => TcxIo.Load(file),
                    ".fit" => FitIo.Load(file),
                    _ => KmlIo.Load(file),
                };
                foreach (var t in tracks) t.SourceFile = file;
                loaded.AddRange(tracks);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to load {file}:\n{ex.Message}", "Open",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        AddLoadedTracks(loaded);
    }

    /// <summary>Adds freshly parsed tracks to the document and brings every view up to date.</summary>
    private void AddLoadedTracks(IReadOnlyList<Track> loaded)
    {
        if (loaded.Count == 0) return;

        _doc.Snapshot(ActiveIndex());
        foreach (var t in loaded)
        {
            t.ColorHex = Palette[_paletteCursor++ % Palette.Length].Hex;
            t.ResetBaseline(); // clean, as loaded from the file
            _doc.Tracks.Add(t);
        }
        _active = loaded[0];
        RefreshAll();
        _mapMgr.ZoomToTracks(loaded);
        StatusInfo.Text = $"Loaded {loaded.Count} track(s), {loaded.Sum(t => t.Points.Count)} points";
        // Bake heights into tracks that lack them (SRTM w/ optional download, then online) — once, not per refresh.
        FillElevationAfterLoad(loaded, isInitialLoad: true);
    }

    // ======================= loading from a URL =======================

    private static readonly HttpClient DownloadHttp = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>True for an absolute http(s) address — what the URL loader accepts.</summary>
    private static bool IsHttpUrl(string s) =>
        Uri.TryCreate(s, UriKind.Absolute, out var u)
        && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    private void OpenUrl_Click(object sender, RoutedEventArgs e)
    {
        string? url = InputDialog.Ask(this, "Open from URL",
            "Address of a GPX, TCX, FIT, KML or KMZ file:", "https://");
        if (url is not null) _ = LoadFromUrlAsync(url);
    }

    /// <summary>
    /// Downloads a track file and loads it. The format follows the URL's extension where it has a
    /// usable one, otherwise it is sniffed from the bytes — download links often end in a query
    /// string or an opaque id rather than ".gpx".
    /// </summary>
    private async Task LoadFromUrlAsync(string url)
    {
        if (!IsHttpUrl(url))
        {
            MessageBox.Show(this, $"Not a valid http(s) address:\n{url}", "Open from URL",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var uri = new Uri(url);

        BeginBusy($"Downloading {uri.Host}…");
        try
        {
            byte[] bytes = await DownloadHttp.GetByteArrayAsync(uri);
            string name = System.IO.Path.GetFileNameWithoutExtension(uri.LocalPath);
            if (string.IsNullOrWhiteSpace(name)) name = uri.Host;

            var loaded = ParseTrackBytes(bytes, System.IO.Path.GetExtension(uri.LocalPath), name);
            if (loaded.Count == 0)
            {
                MessageBox.Show(this, "The download contained no tracks.", "Open from URL",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            // Deliberately no SourceFile: a downloaded track has no local path, so Save must prompt
            // rather than silently writing somewhere.
            AddLoadedTracks(loaded);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Failed to download {url}:\n{ex.Message}", "Open from URL",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { EndBusy(); }
    }

    /// <summary>Parses downloaded bytes as GPX/TCX/FIT/KML/KMZ, using the extension if it says, else the content.</summary>
    private static List<Track> ParseTrackBytes(byte[] bytes, string extension, string baseName)
    {
        string ext = extension.ToLowerInvariant();
        using var ms = new MemoryStream(bytes);

        if (ext == ".gpx") return GpxIo.Load(ms, baseName);
        if (ext == ".tcx") return TcxIo.Load(ms, baseName);
        if (ext == ".fit") return FitIo.Load(ms, baseName);
        if (ext == ".kmz") return KmlIo.Load(ms, isKmz: true, baseName);
        if (ext == ".kml") return KmlIo.Load(ms, isKmz: false, baseName);

        // FIT is binary: bytes 8..11 are the ASCII signature ".FIT".
        if (bytes.Length >= 12 && bytes[8] == '.' && bytes[9] == 'F' && bytes[10] == 'I' && bytes[11] == 'T')
            return FitIo.Load(ms, baseName);
        if (bytes.Length > 1 && bytes[0] == 'P' && bytes[1] == 'K') // zip magic => KMZ
            return KmlIo.Load(ms, isKmz: true, baseName);
        // Text: GPX, TCX and KML are all XML — match on the root element.
        string head = System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 2048));
        if (head.Contains("<gpx", StringComparison.OrdinalIgnoreCase)) return GpxIo.Load(ms, baseName);
        if (head.Contains("TrainingCenterDatabase", StringComparison.OrdinalIgnoreCase)) return TcxIo.Load(ms, baseName);
        return KmlIo.Load(ms, isKmz: false, baseName);
    }

    private void SaveActive_Click(object sender, RoutedEventArgs e)
    {
        if (_active is null) return;
        SaveTracks(new[] { _active }, _active.Name);
    }

    private void SaveAll_Click(object sender, RoutedEventArgs e)
    {
        if (_doc.Tracks.Count == 0) return;
        SaveTracks(_doc.Tracks, "tracks");
    }

    private void SaveTracks(IEnumerable<Track> tracks, string suggestedName)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "GPX|*.gpx|TCX|*.tcx|FIT course|*.fit",
            FileName = string.Join("_", suggestedName.Split(System.IO.Path.GetInvalidFileNameChars())) + ".gpx",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var saved = tracks.ToList();
            // Prefer the typed extension; if none of ours, follow the chosen filter (1=GPX, 2=TCX, 3=FIT).
            string path = dlg.FileName;
            string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            if (ext is not (".gpx" or ".tcx" or ".fit"))
            {
                ext = dlg.FilterIndex switch { 2 => ".tcx", 3 => ".fit", _ => ".gpx" };
                path = System.IO.Path.ChangeExtension(path, ext);
            }
            switch (ext)
            {
                case ".tcx": TcxIo.Save(path, saved); break;
                case ".fit": FitIo.Save(path, saved); break;
                default: GpxIo.Save(path, saved); break;
            }
            // Saving establishes a new clean baseline and source for the written tracks.
            foreach (var t in saved) { t.SourceFile = path; t.ResetBaseline(); }
            RefreshTracksList();
            StatusInfo.Text = $"Saved {path}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Save", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Adds a "Reset plot" entry to the profile plot's right-click menu. (Saving is
    /// already covered by ScottPlot's built-in "Save Image" item, so we don't duplicate it.)</summary>
    private void SetupPlotMenu()
    {
        ProfilePlot.Menu?.Add("Reset plot (fit all)", _ =>
        {
            ProfilePlot.Plot.Axes.AutoScale();
            ProfilePlot.Refresh();
        });
    }

    private async void ExportMap_Click(object sender, RoutedEventArgs e)
    {
        var extent = _mapMgr.ViewportExtent();
        var dlg = new ExportMapWindow(extent, _mapMgr.CurrentZoomLevel(), _mapMgr.BaseMaxZoom) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        var save = new SaveFileDialog { Filter = "PNG image|*.png", FileName = "map.png" };
        if (save.ShowDialog() != true) return;

        BeginBusy("Exporting map…");
        try
        {
            await MapExporter.ExportAsync(_mapMgr.BaseTileSource, extent, dlg.Zoom, dlg.Scale,
                _doc.Tracks, save.FileName, BusyProgress(),
                gradientTrack: _active, gradientMetric: _settings.GradientMetric, paceMode: _settings.PaceMode);
            StatusInfo.Text = $"Exported {save.FileName}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Export Map", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { EndBusy(); }
    }

    // ======================= helpers =======================

    private int ActiveIndex() => _active is null ? -1 : _doc.Tracks.IndexOf(_active);

    private List<int> SelectedIndices() =>
        PointsGrid.SelectedItems.Cast<PointRow>().Select(r => r.Index).OrderBy(i => i).ToList();

    private void SetActive(Track? track)
    {
        _active = track;
        _cumDist = _active is not null ? GeoMath.CumulativeDistancesM(_active.Points) : Array.Empty<double>();
        _cumGain = _active is not null ? CumulativeAscentM(_active.Points) : Array.Empty<double>();
        _speeds = _active is not null ? GeoMath.SpeedsMps(_active.Points) : Array.Empty<double?>();
    }

    /// <summary>
    /// Cumulative ascent (metres climbed) at each point, using the same small-noise hysteresis as the
    /// statistics panel (<see cref="TrackStatistics.EleThresholdM"/>) so the final flag matches the Ascent total.
    /// Points without elevation carry forward the running total.
    /// </summary>
    private static double[] CumulativeAscentM(IReadOnlyList<TrackPoint> pts)
    {
        var gain = new double[pts.Count];
        double running = 0;
        double? refEle = null;
        for (int i = 0; i < pts.Count; i++)
        {
            if (pts[i].Ele is double e)
            {
                if (refEle is double r)
                {
                    double diff = e - r;
                    if (diff >= TrackStatistics.EleThresholdM) { running += diff; refEle = e; }
                    else if (diff <= -TrackStatistics.EleThresholdM) { refEle = e; }
                }
                else refEle = e;
            }
            gain[i] = running;
        }
        return gain;
    }

    /// <summary>Full UI refresh after any document mutation.</summary>
    private void RefreshAll()
    {
        if (_active is not null && !_doc.Tracks.Contains(_active))
            _active = _doc.Tracks.FirstOrDefault();
        SetActive(_active);

        RefreshTracksList();
        RefreshPointsGrid();
        _mapMgr.RebuildTracks(_doc.Tracks, _active);
        _mapMgr.SetSelection(null, Array.Empty<int>());
        UpdateFlags();
        UpdateGradientLegend();
        RefreshPlots();
        RefreshStats();
        UpdateUndoButtons();
    }

    private static System.Windows.Media.Brush? _legendBrush;

    /// <summary>Shows/hides the gradient colour legend to match the active track's current gradient (if any),
    /// filling in its caption and the fast/slow (red/blue) end labels.</summary>
    private void UpdateGradientLegend()
    {
        if (GradientLegend is null) return; // during InitializeComponent
        var g = _mapMgr?.ActiveGradient;
        if (g is null)
        {
            GradientLegend.Visibility = Visibility.Collapsed;
            return;
        }
        LegendCaption.Text = g.Caption;
        LegendLow.Text = g.LowLabel;    // blue end (slow / hard / climbing)
        LegendHigh.Text = g.HighLabel;  // red end (fast / easy / descending)
        LegendBar.Fill = _legendBrush ??= BuildLegendBrush();
        GradientLegend.Visibility = Visibility.Visible;
    }

    /// <summary>A horizontal blue→red bar sampled from the same ramp the map uses, so the legend matches the line.</summary>
    private static System.Windows.Media.Brush BuildLegendBrush()
    {
        var brush = new System.Windows.Media.LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(1, 0),
        };
        for (int i = 0; i <= 10; i++)
        {
            double t = i / 10.0;
            var (r, gg, b) = TrackGradient.Color(t);
            brush.GradientStops.Add(new System.Windows.Media.GradientStop(
                System.Windows.Media.Color.FromRgb(r, gg, b), t));
        }
        brush.Freeze();
        return brush;
    }

    private void UpdateUndoButtons()
    {
        BtnUndo.IsEnabled = _doc.CanUndo;
        BtnRedo.IsEnabled = _doc.CanRedo;
    }

    // ======================= tracks list =======================

    private void RefreshTracksList()
    {
        _syncingUi = true;
        try
        {
            var rows = _doc.Tracks.Select(t => new TrackRow(t)).ToList();
            TracksList.ItemsSource = rows;
            TracksList.SelectedItem = rows.FirstOrDefault(r => ReferenceEquals(r.T, _active));

            if (_active is not null)
            {
                WidthSlider.Value = _active.Width;
                WidthLabel.Text = ((int)_active.Width).ToString();
                foreach (ComboBoxItem item in ColorCombo.Items)
                    if ((string)item.Tag == _active.ColorHex) { ColorCombo.SelectedItem = item; break; }
            }
        }
        finally { _syncingUi = false; }
    }

    /// <summary>Syncs the track list's selection and the colour/width controls to the active track WITHOUT
    /// rebuilding the list. Rebuilding ItemsSource (as RefreshTracksList does) discards the focused container,
    /// which is why arrow-key navigation used to lose focus after one keypress.</summary>
    private void SyncActiveTrackUi()
    {
        _syncingUi = true;
        try
        {
            if (TracksList.ItemsSource is IEnumerable<TrackRow> rows)
            {
                var match = rows.FirstOrDefault(r => ReferenceEquals(r.T, _active));
                if (!ReferenceEquals(TracksList.SelectedItem, match)) TracksList.SelectedItem = match;
            }
            if (_active is not null)
            {
                WidthSlider.Value = _active.Width;
                WidthLabel.Text = ((int)_active.Width).ToString();
                foreach (ComboBoxItem item in ColorCombo.Items)
                    if ((string)item.Tag == _active.ColorHex) { ColorCombo.SelectedItem = item; break; }
            }
        }
        finally { _syncingUi = false; }
    }

    private void TracksList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingUi) return;
        if (TracksList.SelectedItem is TrackRow row) MakeActive(row.T);
    }

    /// <summary>Switches the active track and refreshes every view. No-op if already active.</summary>
    private void MakeActive(Track t)
    {
        // While a join is pending, picking a track completes the join instead of switching to it.
        if (_joinFrom is not null) { JoinWith(t); return; }
        if (ReferenceEquals(_active, t)) return;
        SetActive(t);
        SyncActiveTrackUi();   // sync selection/controls without rebuilding the list, so keyboard focus survives
        RefreshPointsGrid();
        _mapMgr.RebuildTracks(_doc.Tracks, _active);
        _mapMgr.SetSelection(null, Array.Empty<int>());
        UpdateFlags();
        UpdateGradientLegend();
        RefreshPlots();
        RefreshStats();
    }

    private void TrackVisible_Click(object sender, RoutedEventArgs e)
    {
        _mapMgr.RebuildTracks(_doc.Tracks, _active);
        UpdateFlags();
        UpdateGradientLegend();
    }

    private void BuildColorCombo()
    {
        foreach (var (name, hex) in Palette)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new Rectangle
            {
                Width = 12,
                Height = 12,
                Margin = new Thickness(0, 0, 4, 0),
                Fill = new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(hex)),
            });
            panel.Children.Add(new TextBlock { Text = name });
            ColorCombo.Items.Add(new ComboBoxItem { Content = panel, Tag = hex });
        }
    }

    private void ColorCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingUi || _active is null || ColorCombo.SelectedItem is not ComboBoxItem item) return;
        _active.ColorHex = (string)item.Tag;
        RefreshTracksList();
        _mapMgr.RebuildTracks(_doc.Tracks, _active);
        RefreshPlots();
    }

    private void WidthSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingUi || _active is null || WidthLabel is null) return;
        _active.Width = Math.Round(WidthSlider.Value);
        WidthLabel.Text = ((int)_active.Width).ToString();
        _mapMgr.RebuildTracks(_doc.Tracks, _active);
    }

    // ======================= points grid =======================

    private void RefreshPointsGrid()
    {
        _syncingUi = true;
        try
        {
            var rows = new List<PointRow>();
            if (_active is not null)
            {
                for (int i = 0; i < _active.Points.Count; i++)
                {
                    var p = _active.Points[i];
                    rows.Add(new PointRow
                    {
                        Index = i,
                        LatStr = p.Lat.ToString("F5", CultureInfo.InvariantCulture),
                        LonStr = p.Lon.ToString("F5", CultureInfo.InvariantCulture),
                        EleStr = p.Ele is double ele ? ele.ToString("F0") : "",
                        TimeStr = p.Time is DateTime t ? t.ToLocalTime().ToString("HH:mm:ss") : "",
                        DistStr = (_cumDist[i] / 1000).ToString("F2"),
                        NameStr = p.Name ?? "",
                        HrStr = p.Hr is int hr ? hr.ToString() : "",
                        CadStr = p.Cad is int cad ? cad.ToString() : "",
                        TempStr = p.Temp is double tp ? tp.ToString("F0") : "",
                        SurfaceStr = p.Surface ?? "",
                        IsWaypoint = p.IsWaypoint,
                    });
                }
            }
            PointsGrid.ItemsSource = rows;
        }
        finally { _syncingUi = false; }
        ApplyColumnVisibility(); // re-hide columns the (possibly changed) active track has no data for
    }

    private void PointsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Skipped while a programmatic (map/plot) selection is mid-flight: SelectPointInGrid mutates
        // SelectedItems row by row, and syncing on every one both thrashed the map/plot (slow on big
        // tracks) and let the anchor drift so Shift-ranges could not extend backwards.
        if (_syncingUi || _suppressSelSync) return;
        var indices = SelectedIndices();
        // A single row picked directly in the grid is an unambiguous anchor for a later Shift-range
        // started from the map or the profile plot.
        if (indices.Count == 1) _gridAnchor = indices[0];
        ApplySelectionToViews(indices);
    }

    /// <summary>Pushes the current point selection to the map, profile plot and stats panel. Called once
    /// per selection change (native grid edit, or a programmatic map/plot click) — never per added row.</summary>
    private void ApplySelectionToViews(IReadOnlyList<int> indices)
    {
        _mapMgr.SetSelection(_active, indices);
        UpdatePlotMarkers(indices);
        if (indices.Count > 1) StatusInfo.Text = $"{indices.Count} points selected";
        RefreshSelectionStats();
    }

    /// <summary>Double-clicking a grid row recenters the map on that point (no zoom change).</summary>
    private void PointsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_active is null) return;
        if (PointsGrid.CurrentItem is PointRow row && row.Index >= 0 && row.Index < _active.Points.Count)
            _mapMgr.CenterOn(_active.Points[row.Index]);
    }

    /// <summary>Right-clicking a grid row selects it (unless it is already part of the selection),
    /// so the context-menu point operations act on what the user pointed at.</summary>
    private void PointsGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var dep = e.OriginalSource as DependencyObject;
        while (dep is not null and not DataGridRow) dep = VisualTreeHelper.GetParent(dep);
        if (dep is DataGridRow rc && rc.Item is PointRow pr && !PointsGrid.SelectedItems.Contains(pr))
        {
            PointsGrid.SelectedItems.Clear();
            PointsGrid.SelectedItems.Add(pr);
        }
    }

    /// <summary>Point-list context menu: bring the first selected point to the map centre.</summary>
    private void CtxCenterPoint_Click(object sender, RoutedEventArgs e)
    {
        if (_active is null) return;
        var idx = SelectedIndices();
        if (idx.Count > 0 && idx[0] < _active.Points.Count) _mapMgr.CenterOn(_active.Points[idx[0]]);
    }

    /// <summary>Names the single selected point, turning it into a waypoint/marker.</summary>
    private void CtxSetWaypoint_Click(object sender, RoutedEventArgs e)
    {
        if (_active is null) return;
        var idx = SelectedIndices();
        if (idx.Count != 1 || idx[0] >= _active.Points.Count)
        {
            StatusInfo.Text = "Waypoint: select exactly one point to name";
            return;
        }
        int i = idx[0];
        var p = _active.Points[i];
        string? name = InputDialog.Ask(this, "Waypoint", "Waypoint name:", p.Name ?? "");
        if (name is null || name == p.Name) return;
        _doc.Snapshot(ActiveIndex());
        p.Name = name;
        AfterWaypointChange(i);
        StatusInfo.Text = $"Waypoint “{name}” set at point {i}";
    }

    /// <summary>Clears the waypoint mark from any selected point(s).</summary>
    private void CtxRemoveWaypoint_Click(object sender, RoutedEventArgs e)
    {
        if (_active is null) return;
        var idx = SelectedIndices().Where(i => i < _active.Points.Count && _active.Points[i].IsWaypoint).ToList();
        if (idx.Count == 0)
        {
            StatusInfo.Text = "No waypoint on the selected point(s)";
            return;
        }
        _doc.Snapshot(ActiveIndex());
        foreach (int i in idx) _active.Points[i].Name = null;
        AfterWaypointChange(idx[0]);
        StatusInfo.Text = $"Removed {idx.Count} waypoint mark(s)";
    }

    /// <summary>Refresh the views affected by a waypoint edit and reselect the edited point.</summary>
    private void AfterWaypointChange(int reselect)
    {
        RefreshTracksList(); // updates the '*' modified marker
        RefreshPointsGrid();
        _mapMgr.RebuildTracks(_doc.Tracks, _active); // draw/remove the waypoint marker on the map
        RefreshPlots();
        SelectPointInGrid(reselect);
    }

    // ======================= map context-menu ops =======================

    private void MapFitAll_Click(object sender, RoutedEventArgs e)
    {
        if (_doc.Tracks.Count > 0) _mapMgr.ZoomToTracks(_doc.Tracks);
    }

    private void MapClearMeasure_Click(object sender, RoutedEventArgs e) => ResetMeasurement();

    /// <summary>Clicking the Measure button while already in Measure mode starts a fresh measurement.</summary>
    private void ModeMeasure_Click(object sender, RoutedEventArgs e)
    {
        if (_mode == EditMode.Measure) ResetMeasurement();
    }

    /// <summary>Drops all measurement points and clears the overlay.</summary>
    private void ResetMeasurement()
    {
        _measurePts.Clear();
        _mapMgr?.ClearMeasure();
        if (MeasureText is not null) MeasureText.Text = "Click points on the map to measure";
    }

    // Map context-menu mode switches drive the toolbar radios (Mode_Checked does the real work).
    private void MapModeView_Click(object sender, RoutedEventArgs e) => ModeView.IsChecked = true;
    private void MapModeEdit_Click(object sender, RoutedEventArgs e) => ModeEdit.IsChecked = true;
    private void MapModeMeasure_Click(object sender, RoutedEventArgs e) => ModeMeasure.IsChecked = true;

    /// <summary>Selects a point in the grid programmatically (map click, plot click).</summary>
    /// <summary>
    /// The point a Shift-range extends from. Held explicitly because SelectedItems is in insertion
    /// order — using its first entry anchored every range to the lowest selected index, so a range
    /// could only ever be extended forwards.
    /// </summary>
    private int _gridAnchor = -1;

    /// <summary>Set while SelectPointInGrid rewrites the grid selection, so PointsGrid_SelectionChanged
    /// stays quiet until the whole change is in place (one sync, stable anchor).</summary>
    private bool _suppressSelSync;

    private void SelectPointInGrid(int index, bool ctrl = false, bool shift = false)
    {
        if (PointsGrid.ItemsSource is not List<PointRow> rows || index < 0 || index >= rows.Count) return;

        _suppressSelSync = true;
        try
        {
            if (shift && _gridAnchor >= 0 && _gridAnchor < rows.Count)
            {
                // Range in either direction; the anchor stays put so it can be re-extended both ways.
                PointsGrid.SelectedItems.Clear();
                for (int i = Math.Min(_gridAnchor, index); i <= Math.Max(_gridAnchor, index); i++)
                    PointsGrid.SelectedItems.Add(rows[i]);
            }
            else if (ctrl)
            {
                if (PointsGrid.SelectedItems.Contains(rows[index]))
                    PointsGrid.SelectedItems.Remove(rows[index]);
                else
                    PointsGrid.SelectedItems.Add(rows[index]);
                _gridAnchor = index;
            }
            else
            {
                PointsGrid.SelectedItems.Clear();
                PointsGrid.SelectedItems.Add(rows[index]);
                _gridAnchor = index;
            }
        }
        finally { _suppressSelSync = false; }

        PointsGrid.ScrollIntoView(rows[index]);
        ApplySelectionToViews(SelectedIndices()); // one sync for the whole selection change
    }

    // ======================= editing commands =======================

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (!_doc.CanUndo) return;
        int active = _doc.Undo(ActiveIndex());
        _active = active >= 0 && active < _doc.Tracks.Count ? _doc.Tracks[active] : _doc.Tracks.FirstOrDefault();
        RefreshAll();
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        if (!_doc.CanRedo) return;
        int active = _doc.Redo(ActiveIndex());
        _active = active >= 0 && active < _doc.Tracks.Count ? _doc.Tracks[active] : _doc.Tracks.FirstOrDefault();
        RefreshAll();
    }

    private void NewTrack_Click(object sender, RoutedEventArgs e)
    {
        _doc.Snapshot(ActiveIndex());
        var track = new Track
        {
            Name = $"New track {_doc.Tracks.Count + 1}",
            ColorHex = Palette[_paletteCursor++ % Palette.Length].Hex,
        };
        _doc.Tracks.Add(track);
        _active = track;
        RefreshAll();
        ModeEdit.IsChecked = true;
        StatusInfo.Text = "Edit mode: click on the map to add points, right-click removes the last one";
    }

    private void RemoveTrack_Click(object sender, RoutedEventArgs e)
    {
        if (_active is not null) RemoveTrack(_active);
    }

    private void RemoveTrack(Track track)
    {
        if (!_doc.Tracks.Contains(track)) return;
        _doc.Snapshot(ActiveIndex());
        _doc.Tracks.Remove(track);
        if (ReferenceEquals(_active, track)) _active = _doc.Tracks.FirstOrDefault();
        RefreshAll();
    }

    // Tracks-list context menu / double-click. The clicked row is the MenuItem's DataContext.
    private void CtxRemoveTrack_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TrackRow row) RemoveTrack(row.T);
    }

    private void CtxZoomTrack_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TrackRow row) _mapMgr.ZoomToTracks(new[] { row.T });
    }

    private void CtxRenameTrack_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TrackRow row) return;
        string? name = InputDialog.Ask(this, "Rename track", "Track name:", row.T.Name);
        if (name is null || name == row.T.Name) return;
        row.T.Name = name; // rename counts as a modification (IsModified is content-based)
        RefreshTracksList();
        StatusInfo.Text = $"Renamed to “{name}”";
    }

    private void CtxTrackInfo_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TrackRow row)
            new TrackInfoWindow(row.T, _settings.PaceMode) { Owner = this }.ShowDialog();
    }

    /// <summary>Makes the right-clicked track active so a track-wide op runs on it; false if none.</summary>
    private bool SelectCtxTrack(object sender)
    {
        if ((sender as FrameworkElement)?.DataContext is not TrackRow row) return false;
        MakeActive(row.T);
        return true;
    }

    // Track-list context menu ops: activate the clicked track, then reuse the existing command.
    private void CtxSaveTrack_Click(object sender, RoutedEventArgs e) { if (SelectCtxTrack(sender)) SaveActive_Click(sender, e); }
    private void CtxReverse_Click(object sender, RoutedEventArgs e) { if (SelectCtxTrack(sender)) Reverse_Click(sender, e); }
    private void CtxSimplify_Click(object sender, RoutedEventArgs e) { if (SelectCtxTrack(sender)) Simplify_Click(sender, e); }
    private void CtxEvalSurface_Click(object sender, RoutedEventArgs e) { if (SelectCtxTrack(sender)) EvalSurface_Click(sender, e); }
    private void CtxMerge_Click(object sender, RoutedEventArgs e) { if (SelectCtxTrack(sender)) Merge_Click(sender, e); }
    private void CtxApplyRaceModel_Click(object sender, RoutedEventArgs e) { if (SelectCtxTrack(sender)) ApplyRaceModel_Click(sender, e); }

    private void TracksList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (TracksList.SelectedItem is TrackRow row) _mapMgr.ZoomToTracks(new[] { row.T });
    }

    private void DeleteLast_Click(object sender, RoutedEventArgs e)
    {
        if (_active is null || _active.Points.Count == 0) return;
        _doc.Snapshot(ActiveIndex());
        _active.Points.RemoveAt(_active.Points.Count - 1);
        RefreshAll();
    }

    private void DeletePoints_Click(object sender, RoutedEventArgs e)
    {
        if (_active is null) return;
        var indices = SelectedIndices();
        if (indices.Count == 0) return;
        _doc.Snapshot(ActiveIndex());
        for (int i = indices.Count - 1; i >= 0; i--)
            _active.Points.RemoveAt(indices[i]);
        RefreshAll();
        int reselect = Math.Min(indices[0], _active.Points.Count - 1);
        if (reselect >= 0) SelectPointInGrid(reselect);
        StatusInfo.Text = $"Deleted {indices.Count} point(s)";
    }

    private void Crop_Click(object sender, RoutedEventArgs e)
    {
        if (_active is null) return;
        var indices = SelectedIndices();
        if (indices.Count < 2)
        {
            StatusInfo.Text = "Crop: select at least 2 points (kept range = min…max of selection)";
            return;
        }
        _doc.Snapshot(ActiveIndex());
        int from = indices[0], to = indices[^1];
        _active.Points = _active.Points.GetRange(from, to - from + 1);
        RefreshAll();
        StatusInfo.Text = $"Cropped to points {from}…{to}";
    }

    private void Split_Click(object sender, RoutedEventArgs e)
    {
        if (_active is null) return;
        var indices = SelectedIndices();
        if (indices.Count != 1 || indices[0] <= 0 || indices[0] >= _active.Points.Count - 1)
        {
            StatusInfo.Text = "Split: select exactly one interior point";
            return;
        }
        _doc.Snapshot(ActiveIndex());
        int at = indices[0];
        var second = new Track
        {
            Name = _active.Name + " [2]",
            ColorHex = Palette[_paletteCursor++ % Palette.Length].Hex,
            Width = _active.Width,
            Points = _active.Points.Skip(at).Select(p => p.Clone()).ToList(),
        };
        _active.Points = _active.Points.Take(at + 1).ToList();
        _active.Name += " [1]";
        _doc.Tracks.Insert(_doc.Tracks.IndexOf(_active) + 1, second);
        RefreshAll();
        StatusInfo.Text = $"Split at point {at}: {_active.Points.Count} + {second.Points.Count} points";
    }

    private void Simplify_Click(object sender, RoutedEventArgs e)
    {
        if (_active is null || _active.Points.Count < 3)
        {
            StatusInfo.Text = "Simplify: need an active track with 3+ points";
            return;
        }
        string? input = InputDialog.Ask(this, "Simplify Track", "Tolerance (meters):", "10");
        if (input is null) return;
        if (!double.TryParse(input.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double tol) || tol <= 0)
        {
            StatusInfo.Text = "Simplify: enter a positive tolerance in meters";
            return;
        }
        _doc.Snapshot(ActiveIndex());
        int before = _active.Points.Count;
        var keep = GeoMath.DouglasPeucker(_active.Points, tol);
        _active.Points = keep.Select(i => _active.Points[i]).ToList();
        RefreshAll();
        StatusInfo.Text = $"Simplified: {before} → {_active.Points.Count} points (tolerance {tol} m)";
    }

    private void Reverse_Click(object sender, RoutedEventArgs e)
    {
        if (_active is null || _active.Points.Count < 2) return;
        _doc.Snapshot(ActiveIndex());
        _active.Points.Reverse();
        RefreshAll();
        StatusInfo.Text = "Track reversed (note: timestamps are now in reverse order)";
    }

    // ======================= join tracks =======================

    /// <summary>The first track picked for a Join, or null when no join is in progress.</summary>
    private Track? _joinFrom;

    /// <summary>Starts (or cancels) a join: remembers the active track, then waits for a second pick.</summary>
    private void Join_Click(object sender, RoutedEventArgs e)
    {
        if (_joinFrom is not null) { CancelJoin(); return; }
        if (_active is null || _doc.Tracks.Count < 2) return;
        ArmJoin(_active);
    }

    /// <summary>Arms a join from <paramref name="t"/> and prompts for the second track.</summary>
    private void ArmJoin(Track t)
    {
        _joinFrom = t;
        StatusInfo.Text = $"Join: select another track in the list to append to “{t.Name}” " +
                          "(Esc or Track ▸ Cancel Join to cancel).";
        UpdateJoinUi();
    }

    /// <summary>Drops a pending join (Esc, or picking the Join command again). No-op when not joining.</summary>
    private void CancelJoin()
    {
        if (_joinFrom is null) return;
        _joinFrom = null;
        StatusInfo.Text = "Join cancelled.";
        UpdateJoinUi();
    }

    /// <summary>
    /// Reflects the join state in the menus: while a join is armed every top-level menu except Track
    /// goes inert, and the Join command becomes "Cancel Join" so the flow can always be escaped.
    /// Only the top-level items are toggled — several submenu items bind IsEnabled, and setting a
    /// local value there would overwrite the binding permanently.
    /// </summary>
    private void UpdateJoinUi()
    {
        bool armed = _joinFrom is not null;
        MenuJoin.Header = armed ? "Cancel _Join" : "_Join Tracks…";
        foreach (var obj in MainMenu.Items)
            if (obj is MenuItem top) top.IsEnabled = !armed || ReferenceEquals(top, MenuTrackTop);
    }

    /// <summary>Greys out every item of a just-opened menu except the Join command while joining.</summary>
    private void ApplyJoinGuard(object sender)
    {
        if (_joinFrom is null || sender is not MenuItem menu) return;
        foreach (var obj in menu.Items)
            if (obj is MenuItem mi && !ReferenceEquals(mi, MenuJoin)) mi.IsEnabled = false;
    }

    /// <summary>
    /// The track-list context menu is declared per row in a template, so its items are reached here
    /// rather than by name: while a join is armed everything but "Join" is inert, and that item is
    /// relabelled so the pending join can be completed or cancelled from the same place.
    /// </summary>
    private void TrackCtxMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        bool armed = _joinFrom is not null;
        foreach (var obj in menu.Items)
        {
            if (obj is not MenuItem mi) continue; // separators
            bool isJoin = (mi.Tag as string) == "join";
            mi.IsEnabled = !armed || isJoin;
            if (isJoin) mi.Header = armed ? "Cancel Join" : "Join Tracks…";
        }
    }

    /// <summary>
    /// Track-list context menu "Join Tracks": with no join pending this arms one from the clicked
    /// track; with a join pending it completes the join against the clicked track. Deliberately does
    /// not go through SelectCtxTrack, whose MakeActive call would complete the join first.
    /// </summary>
    private void CtxJoin_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TrackRow row) return;
        if (_joinFrom is not null) { JoinWith(row.T); return; }
        if (_doc.Tracks.Count < 2) return;
        MakeActive(row.T); // no join pending, so this only switches the active track
        ArmJoin(row.T);
    }

    /// <summary>
    /// Completes a join: adds a new track whose points are the first track's followed by the second's.
    /// Both source tracks are left untouched.
    /// </summary>
    private void JoinWith(Track second)
    {
        var first = _joinFrom;
        _joinFrom = null;
        UpdateJoinUi(); // the join is over either way — bring the menus back
        if (first is null || ReferenceEquals(first, second) || !_doc.Tracks.Contains(first))
        {
            StatusInfo.Text = "Join cancelled — pick a different second track.";
            return;
        }

        _doc.Snapshot(ActiveIndex());
        var joined = new Track
        {
            Name = $"{first.Name} + {second.Name}",
            ColorHex = first.ColorHex,
            Width = first.Width,
            Visible = true,
            Points = first.Points.Select(p => p.Clone()).Concat(second.Points.Select(p => p.Clone())).ToList(),
        };
        _doc.Tracks.Add(joined);
        SetActive(joined);
        RefreshAll();
        StatusInfo.Text = $"Joined “{first.Name}” + “{second.Name}” → “{joined.Name}” ({joined.Points.Count} pts).";
    }

    private void CopyPoints_Click(object sender, RoutedEventArgs e)
    {
        if (_active is null) return;
        var indices = SelectedIndices();
        if (indices.Count == 0) return;
        _doc.Clipboard.Clear();
        _doc.Clipboard.AddRange(indices.Select(i => _active.Points[i].Clone()));
        StatusInfo.Text = $"Copied {indices.Count} point(s)";
    }

    private void PastePoints_Click(object sender, RoutedEventArgs e)
    {
        if (_active is null || _doc.Clipboard.Count == 0) return;
        _doc.Snapshot(ActiveIndex());
        var indices = SelectedIndices();
        int insertAt = indices.Count > 0 ? indices[^1] + 1 : _active.Points.Count;
        _active.Points.InsertRange(insertAt, _doc.Clipboard.Select(p => p.Clone()));
        RefreshAll();
        SelectPointInGrid(Math.Min(insertAt + _doc.Clipboard.Count - 1, _active.Points.Count - 1));
        StatusInfo.Text = $"Pasted {_doc.Clipboard.Count} point(s) at index {insertAt}";
    }

    /// <summary>Track menu: re-evaluate elevation for the active track (overwrites from the sources).</summary>
    private async void ApplyElevation_Click(object sender, RoutedEventArgs e)
    {
        if (_active is not null) await ReevaluateElevationAsync(_active);
    }

    /// <summary>Context menu: re-evaluate elevation for the right-clicked track.</summary>
    private async void CtxReevalElevation_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TrackRow row)
            await ReevaluateElevationAsync(row.T);
    }

    /// <summary>Manual re-evaluation: recompute all heights (SRTM overwrites, online fills the rest).</summary>
    private async Task ReevaluateElevationAsync(Track track)
    {
        if (_elevBusy) return;
        if (!SrtmActive && !_settings.OnlineEnabled)
        {
            MessageBox.Show(this,
                "No elevation source is enabled. Open Tools → Settings… and enable SRTM " +
                "(with a folder of .hgt tiles) and/or the online elevation service.",
                "Elevation", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _doc.Snapshot(ActiveIndex());
        _elevBusy = true;
        BeginBusy("Fetching elevation…");
        (int Srtm, int Online) r = (0, 0);
        try { r = await FillElevationAsync(track, overwrite: true); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Elevation lookup failed:\n{ex.Message}",
                "Elevation", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _elevBusy = false; EndBusy(); }

        if (_doc.Tracks.Contains(track)) RefreshAll();
        StatusInfo.Text =
            $"Elevation — SRTM: {r.Srtm}, online: {r.Online}, of {track.Points.Count} points";
    }

    /// <summary>
    /// Background pass after loading: fill tracks still missing elevation (gaps only, never overwrite).
    /// Runs after the tracks are shown, then refreshes.
    /// </summary>
    private async void FillElevationAfterLoad(IReadOnlyList<Track> tracks, bool isInitialLoad = false)
    {
        if (_elevBusy || (!SrtmActive && !_settings.OnlineEnabled)) return;
        var pending = tracks.Where(t => t.Points.Any(p => p.Ele is null)).ToList();
        if (pending.Count == 0) return;

        _elevBusy = true;
        BeginBusy("Fetching elevation…");
        int total = 0;
        try
        {
            foreach (var t in pending)
            {
                var r = await FillElevationAsync(t, overwrite: false);
                total += r.Srtm + r.Online;
                // Auto-enrichment at load time is part of "as loaded", not a user edit.
                if (isInitialLoad) t.ResetBaseline();
            }
        }
        catch (Exception ex)
        {
            StatusInfo.Text = $"Elevation lookup failed: {ex.Message}";
        }
        finally { _elevBusy = false; EndBusy(); }

        if (total > 0) RefreshAll();
    }

    /// <summary>
    /// Fills a track's elevation from the enabled sources: SRTM (auto-downloading tiles if allowed),
    /// then the online service for any points still missing. When <paramref name="overwrite"/> is true,
    /// SRTM replaces existing heights; otherwise only points without a value are touched.
    /// Caller owns the _elevBusy guard and exception handling. Returns per-source counts applied.
    /// </summary>
    private async Task<(int Srtm, int Online)> FillElevationAsync(Track track, bool overwrite)
    {
        int srtmApplied = 0;
        if (SrtmActive)
        {
            if (_srtm.AutoDownload)
            {
                var need = track.Points.Where(p => overwrite || p.Ele is null).Select(p => (p.Lat, p.Lon));
                await _srtm.EnsureTilesAsync(need, BusyProgress());
            }
            foreach (var p in track.Points)
                if ((overwrite || p.Ele is null) && _srtm.GetElevation(p.Lat, p.Lon) is double ele)
                { p.Ele = ele; srtmApplied++; }
            if (srtmApplied > 0) track.ElevationEstimated = true;
        }

        int onlineApplied = 0;
        if (_settings.OnlineEnabled)
        {
            var missing = new List<int>();
            for (int i = 0; i < track.Points.Count; i++)
                if (track.Points[i].Ele is null) missing.Add(i);

            if (missing.Count > 0)
            {
                var coords = missing.Select(i => (track.Points[i].Lat, track.Points[i].Lon)).ToList();
                var progress = BusyProgress<(int Done, int Total)>(
                    pr => $"Fetching elevation online… {pr.Done}/{pr.Total}");
                var elevs = await _online.GetElevationsAsync(coords, progress);
                for (int k = 0; k < missing.Count; k++)
                    if (elevs[k] is double ele) { track.Points[missing[k]].Ele = ele; onlineApplied++; }
                if (onlineApplied > 0) track.ElevationEstimated = true;
            }
        }

        return (srtmApplied, onlineApplied);
    }

    private void ZoomToTrack_Click(object sender, RoutedEventArgs e)
    {
        if (_active is not null) _mapMgr.ZoomToTracks(new[] { _active });
    }

    // ======================= modes / keyboard =======================

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (ModeEdit is null || ModeMeasure is null) return; // during InitializeComponent
        _mode = ModeEdit.IsChecked == true ? EditMode.Edit
              : ModeMeasure.IsChecked == true ? EditMode.Measure
              : EditMode.View;
        if (StatusMode is not null)
            StatusMode.Text = $"Mode: {_mode}";

        // The measurement panel is only meaningful in Measure mode.
        if (MeasurePanel is not null)
            MeasurePanel.Visibility = _mode == EditMode.Measure ? Visibility.Visible : Visibility.Collapsed;

        if (_mode != EditMode.Measure) ResetMeasurement();
        else if (MeasureText is not null) MeasureText.Text = "Click points on the map to measure";
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;

        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (e.Key == Key.Escape && _joinFrom is not null) { CancelJoin(); e.Handled = true; }
        else if (ctrl && e.Key == Key.Z) { Undo_Click(sender, e); e.Handled = true; }
        else if (ctrl && e.Key == Key.Y) { Redo_Click(sender, e); e.Handled = true; }
        else if (ctrl && e.Key == Key.C) { CopyPoints_Click(sender, e); e.Handled = true; }
        else if (ctrl && e.Key == Key.V) { PastePoints_Click(sender, e); e.Handled = true; }
        else if (ctrl && e.Key == Key.O) { OpenFile_Click(sender, e); e.Handled = true; }
        else if (ctrl && e.Key == Key.S) { SaveActive_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.Delete) { DeletePoints_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.F1) { Help_Click(sender, e); e.Handled = true; }
    }

    // ======================= background-activity indicator =======================

    private int _busyDepth;
    private int _busyGen; // bumped when the indicator clears, so late Progress reports can't re-show it

    /// <summary>Show the status-bar busy indicator for a background task. Pair with EndBusy in a finally.</summary>
    private void BeginBusy(string message)
    {
        _busyDepth++;
        UpdateBusy(message);
    }

    /// <summary>Update the busy indicator's text while a task is running (e.g. progress).</summary>
    private void UpdateBusy(string message)
    {
        StatusBusy.Text = message;
        StatusBusyItem.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// A progress reporter for the busy indicator that ignores reports delivered after the task ended.
    /// <see cref="Progress{T}"/> posts asynchronously, so its last report can arrive after EndBusy has
    /// cleared the text — which would otherwise leave a stale "…N/N" frozen on the status bar.
    /// </summary>
    private IProgress<T> BusyProgress<T>(Func<T, string> format)
    {
        int gen = _busyGen;
        return new Progress<T>(v => { if (gen == _busyGen) UpdateBusy(format(v)); });
    }

    private IProgress<string> BusyProgress() => BusyProgress<string>(s => s);

    private void EndBusy()
    {
        if (--_busyDepth > 0) return;
        _busyDepth = 0;
        _busyGen++;
        StatusBusy.Text = "";
        StatusBusyItem.Visibility = Visibility.Collapsed;
    }

    // ======================= statistics =======================

    // ======================= command enable states =======================

    private bool HasActive => _active is not null;
    private bool HasTracks => _doc.Tracks.Count > 0;
    private bool HasEle => _active is not null && _active.Points.Any(p => p.Ele is not null);
    private bool HasTime => _active is not null && _active.Points.Any(p => p.Time is not null);
    private bool CanFlag => _active is not null && _active.Points.Count >= 2;
    private bool HasClipboard => _doc.Clipboard.Count > 0;
    private bool ElevationSourceOn => SrtmActive || _settings.OnlineEnabled;

    /// <summary>Enables/disables the always-visible controls (toolbar Save, Flags, profile toggles).</summary>
    private void UpdateCommandStates()
    {
        if (BtnSave is not null) BtnSave.IsEnabled = HasActive;
        if (FlagsCheck is not null) FlagsCheck.IsEnabled = CanFlag;
        if (ChkAlt is not null) ChkAlt.IsEnabled = HasEle;   // no elevation -> nothing to plot
        if (ChkSpeed is not null) ChkSpeed.IsEnabled = HasTime; // speed needs timestamps
        if (ChkHr is not null) ChkHr.IsEnabled = HasChannel(p => p.Hr is not null);
        if (ChkCad is not null) ChkCad.IsEnabled = HasChannel(p => p.Cad is not null);
        if (ChkTemp is not null) ChkTemp.IsEnabled = HasChannel(p => p.Temp is not null);
    }

    private bool HasChannel(Func<TrackPoint, bool> has) => _active is not null && _active.Points.Any(has);

    // Menus and context menus compute their item states just before opening.

    private void FileMenu_Opened(object sender, RoutedEventArgs e)
    {
        MenuSaveActive.IsEnabled = HasActive;
        MenuSaveAll.IsEnabled = HasTracks;
    }

    private void EditMenu_Opened(object sender, RoutedEventArgs e)
    {
        bool sel = SelectedIndices().Count > 0;
        MenuUndo.IsEnabled = _doc.CanUndo;
        MenuRedo.IsEnabled = _doc.CanRedo;
        MenuCopy.IsEnabled = sel;
        MenuPaste.IsEnabled = HasClipboard && HasActive;
        MenuDelete.IsEnabled = sel;
    }

    private void TrackMenu_Opened(object sender, RoutedEventArgs e)
    {
        var idx = SelectedIndices();
        int n = _active?.Points.Count ?? 0;
        MenuDeleteLast.IsEnabled = HasActive && n > 0;
        MenuSplit.IsEnabled = idx.Count == 1;
        MenuCrop.IsEnabled = idx.Count >= 2;
        MenuDeleteRange.IsEnabled = idx.Count > 0;
        MenuSimplify.IsEnabled = HasActive && n >= 3;
        MenuReverse.IsEnabled = HasActive && n >= 2;
        MenuApplyEle.IsEnabled = HasActive && ElevationSourceOn;
        MenuZoomTrack.IsEnabled = HasActive;
        MenuRemoveTrack.IsEnabled = HasActive;
        MenuJoin.IsEnabled = _joinFrom is not null || (HasActive && _doc.Tracks.Count >= 2);
        ApplyJoinGuard(sender); // while joining, only Join/Cancel Join stays live
    }

    private void PointsCtx_Opened(object sender, RoutedEventArgs e)
    {
        var idx = SelectedIndices();
        bool one = idx.Count == 1;
        bool selectedIsWaypoint = one && _active is not null
            && idx[0] < _active.Points.Count && _active.Points[idx[0]].IsWaypoint;
        CtxCopy.IsEnabled = idx.Count > 0;
        CtxPaste.IsEnabled = HasClipboard && HasActive;
        CtxDeletePts.IsEnabled = idx.Count > 0;
        CtxSplit.IsEnabled = one;
        CtxCrop.IsEnabled = idx.Count >= 2;
        CtxSetWp.IsEnabled = one;
        CtxRemoveWp.IsEnabled = selectedIsWaypoint;
        CtxCenter.IsEnabled = idx.Count > 0;
    }

    private void MapCtx_Opened(object sender, RoutedEventArgs e)
    {
        MapFitAll.IsEnabled = HasTracks;
        MapZoomActive.IsEnabled = HasActive;
        MapDeletePts.IsEnabled = SelectedIndices().Count > 0;
        MapResetMeasure.IsEnabled = _mode == EditMode.Measure && _measurePts.Count > 0;
    }

    // ======================= statistics =======================

    private void RefreshStats()
    {
        UpdateCommandStates();
        StatsText.Text = _active is null || _active.Points.Count < 2
            ? "—"
            : TrackStatistics.Compute(_active.Points).ToDisplayString(paceMode: _settings.PaceMode);
        RefreshSelectionStats();
    }

    /// <summary>Statistics for the selected span (first→last selected index, inclusive).</summary>
    private void RefreshSelectionStats()
    {
        // The panel only appears once a span of 2+ points is selected.
        var idx = _active is null ? new List<int>() : SelectedIndices();
        if (SelStatsPanel is not null)
            SelStatsPanel.Visibility = idx.Count >= 2 ? Visibility.Visible : Visibility.Collapsed;
        if (_active is null || idx.Count < 2) { SelStatsText.Text = "Select 2+ points"; return; }

        int lo = idx[0], hi = idx[^1];
        var span = _active.Points.GetRange(lo, hi - lo + 1);
        string header = $"Points {lo}–{hi} ({span.Count})\n";
        SelStatsText.Text = header + TrackStatistics.Compute(span).ToDisplayString(includeIncline: true, paceMode: _settings.PaceMode);
    }

    // ======================= map measurement =======================

    /// <summary>Path length + elevation stats along all measurement points (elevation sampled like a track).</summary>
    private async Task ComputeMeasurementAsync(List<(double Lat, double Lon)> pts)
    {
        if (pts.Count < 2) return;
        var a = pts[0];
        var b = pts[^1];

        // Total travelled along every leg, plus the straight line from the first to the last point.
        double dist = 0;
        for (int i = 0; i + 1 < pts.Count; i++)
            dist += GeoMath.HaversineM(pts[i].Lat, pts[i].Lon, pts[i + 1].Lat, pts[i + 1].Lon);
        double direct = GeoMath.HaversineM(a.Lat, a.Lon, b.Lat, b.Lon);
        double bearing = GeoMath.BearingDeg(a.Lat, a.Lon, b.Lat, b.Lon);

        // Sample every leg and pull elevation for the samples the same way tracks are filled.
        var sampled = new List<TrackPoint>();
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            var leg = SampleLine(pts[i], pts[i + 1]);
            if (i > 0 && leg.Count > 0) leg.RemoveAt(0); // avoid duplicating the shared vertex
            sampled.AddRange(leg);
        }
        var temp = new Track { Points = sampled };
        if (!_elevBusy && (SrtmActive || _settings.OnlineEnabled))
        {
            _elevBusy = true;
            BeginBusy("Measuring…");
            try { await FillElevationAsync(temp, overwrite: true); }
            catch { /* offline / lookup failure → distance only */ }
            finally { _elevBusy = false; EndBusy(); }
        }

        var s = TrackStatistics.Compute(temp.Points);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{pts.Count} points, {pts.Count - 1} leg(s)");
        sb.AppendLine($"Path length:     {dist / 1000:F2} km");
        if (pts.Count > 2)
            sb.AppendLine($"Straight line:   {direct / 1000:F2} km");
        sb.AppendLine($"Bearing:         {bearing:F0}°");
        if (s.MinEleM is not null)
        {
            sb.AppendLine($"Elevation:       {s.MinEleM:F0} … {s.MaxEleM:F0} m");
            sb.AppendLine($"Ascent:          {s.AscentM:F0} m");
            sb.AppendLine($"Descent:         {s.DescentM:F0} m");
            if (s.NetInclineDeg is not null)
                sb.AppendLine($"Avg incline:     {s.NetInclineDeg:+0.0;-0.0;0.0}°  ({Math.Tan(s.NetInclineDeg.Value * Math.PI / 180) * 100:+0;-0;0} %)");
        }
        else sb.AppendLine("(no elevation source — distance only)");

        MeasureText.Text = sb.ToString().TrimEnd();
        StatusInfo.Text = $"Measured {dist / 1000:F2} km";
    }

    /// <summary>Evenly spaced points along the A→B line (~90 m spacing, capped) for elevation sampling.</summary>
    private static List<TrackPoint> SampleLine((double Lat, double Lon) a, (double Lat, double Lon) b)
    {
        double dist = GeoMath.HaversineM(a.Lat, a.Lon, b.Lat, b.Lon);
        int segs = Math.Clamp((int)(dist / 90), 1, 400);
        var pts = new List<TrackPoint>(segs + 1);
        for (int i = 0; i <= segs; i++)
        {
            double t = (double)i / segs;
            pts.Add(new TrackPoint { Lat = a.Lat + (b.Lat - a.Lat) * t, Lon = a.Lon + (b.Lon - a.Lon) * t });
        }
        return pts;
    }
}

// ======================= binding rows =======================

public class TrackRow
{
    public Track T { get; }
    public TrackRow(Track t) => T = t;

    public string Title => $"{T.Name}{(T.IsModified ? " *" : "")}  ({T.Points.Count} pts)";
    /// <summary>Terrain / load / effort classification, built lazily when the row's tooltip opens.</summary>
    public string ClassTip => TrackClassifier.Classify(T.Points).Tooltip();
    public bool Visible { get => T.Visible; set => T.Visible = value; }
    public System.Windows.Media.Brush Swatch =>
        new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(T.ColorHex));
}

public class PointRow
{
    public int Index { get; set; }
    public string LatStr { get; set; } = "";
    public string LonStr { get; set; } = "";
    public string EleStr { get; set; } = "";
    public string TimeStr { get; set; } = "";
    public string DistStr { get; set; } = "";
    public string NameStr { get; set; } = "";
    public string HrStr { get; set; } = "";
    public string CadStr { get; set; } = "";
    public string TempStr { get; set; } = "";
    public string SurfaceStr { get; set; } = "";
    public bool IsWaypoint { get; set; }
}
