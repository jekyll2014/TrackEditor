using Microsoft.Win32;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using TrackEditor.Core.Models;
using TrackEditor.Core.Services;
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
    private double?[] _speeds = Array.Empty<double?>();
    private EditMode _mode = EditMode.View;
    private readonly List<(double Lat, double Lon)> _measurePts = new(); // multi-point map measurement
    private readonly RoutingService _router = new();
    private Map3DWindow? _map3D; // non-null while the 3D view is open
    private bool _syncingUi;
    private bool _syncingRoute; // guards the toolbar Route combo while it is set programmatically
    private int _paletteCursor;
    private int _flagContent; // 0 dist, 1 time, 2 both (chosen from View → Mileage Flag Content)

    public MainWindow()
    {
        InitializeComponent();
        _settings = AppSettings.Load();
        _mapMgr = new MapManager(MapCtrl, _settings.BaseMap, _settings.TileCacheLimitMB);
        Closed += (_, _) =>
        {
            _mapMgr.Dispose(); // checkpoint/close the MBTiles cache cleanly
            SessionStore.Save(new SessionStore.Session { Active = ActiveIndex(), Tracks = _doc.Tracks });
        };
        BuildColorCombo();
        BuildRouteCombo();
        ApplySettings();
        SetupPlotMenu();

        _viewportTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _viewportTimer.Tick += ViewportTimer_Tick;
        _viewportTimer.Start();

        RefreshPlots();

        // Restore the last session, then open any files passed on the command line.
        Loaded += (_, _) =>
        {
            RestoreSession();
            var args = Environment.GetCommandLineArgs().Skip(1).Where(File.Exists).ToArray();
            if (args.Length > 0) LoadFiles(args);
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
        _mapMgr.SetBaseMap(_settings.BaseMap);
        _mapMgr.SetTileCacheLimit(_settings.TileCacheLimitMB);
        _mapMgr.SetWaypointColors(_settings.WaypointLabelBackHex, _settings.WaypointLabelTextHex);
        _router.Profile = _settings.RoutingProfile;
        SyncRouteCombo();
        ApplyColumnVisibility();
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
            StatusInfo.Text = $"Auto-route on ({sel}) — new points follow paths";
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

    /// <summary>Shows/hides the optional points-list columns per settings (the index column always shows).</summary>
    private void ApplyColumnVisibility()
    {
        if (ColWaypoint is null) return; // during InitializeComponent
        static Visibility V(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
        ColWaypoint.Visibility = V(_settings.ColWaypoint);
        ColLat.Visibility = V(_settings.ColLat);
        ColLon.Visibility = V(_settings.ColLon);
        ColEle.Visibility = V(_settings.ColEle);
        ColTime.Visibility = V(_settings.ColTime);
        ColDist.Visibility = V(_settings.ColDist);
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

        var win = new Map3DWindow(
            _mapMgr.ViewportExtent(),
            _mapMgr.CurrentZoomLevel(),
            _mapMgr.BaseMaxZoom,
            _mapMgr.BaseTileSource,
            _doc.Tracks.ToList(),
            _srtm)
        { Owner = this };

        // Keep the 2D viewer marker in step with the 3D camera.
        win.ViewpointChanged += (lat, lon, heading) =>
            Dispatcher.Invoke(() => _mapMgr.SetViewer(lat, lon, heading));
        win.Closed += (_, _) => { _map3D = null; _mapMgr.ClearViewer(); };

        _map3D = win;
        win.Show();
        StatusInfo.Text = "3D view opened — drag the teal marker on the map to move the viewpoint";
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

    // ======================= file operations =======================

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Tracks (*.gpx;*.kml;*.kmz)|*.gpx;*.kml;*.kmz|GPX|*.gpx|KML/KMZ|*.kml;*.kmz|All files|*.*",
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
                var tracks = file.EndsWith(".gpx", StringComparison.OrdinalIgnoreCase)
                    ? GpxIo.Load(file)
                    : KmlIo.Load(file);
                foreach (var t in tracks) t.SourceFile = file;
                loaded.AddRange(tracks);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to load {file}:\n{ex.Message}", "Open",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
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
            Filter = "GPX|*.gpx",
            FileName = string.Join("_", suggestedName.Split(System.IO.Path.GetInvalidFileNameChars())) + ".gpx",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var saved = tracks.ToList();
            GpxIo.Save(dlg.FileName, saved);
            // Saving establishes a new clean baseline and source for the written tracks.
            foreach (var t in saved) { t.SourceFile = dlg.FileName; t.ResetBaseline(); }
            RefreshTracksList();
            StatusInfo.Text = $"Saved {dlg.FileName}";
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
                _doc.Tracks, save.FileName, new Progress<string>(UpdateBusy));
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
        _speeds = _active is not null ? GeoMath.SpeedsMps(_active.Points) : Array.Empty<double?>();
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
        RefreshPlots();
        RefreshStats();
        UpdateUndoButtons();
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

    private void TracksList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingUi) return;
        if (TracksList.SelectedItem is TrackRow row) MakeActive(row.T);
    }

    /// <summary>Switches the active track and refreshes every view. No-op if already active.</summary>
    private void MakeActive(Track t)
    {
        if (ReferenceEquals(_active, t)) return;
        SetActive(t);
        RefreshTracksList();
        RefreshPointsGrid();
        _mapMgr.RebuildTracks(_doc.Tracks, _active);
        _mapMgr.SetSelection(null, Array.Empty<int>());
        UpdateFlags();
        RefreshPlots();
        RefreshStats();
    }

    private void TrackVisible_Click(object sender, RoutedEventArgs e)
    {
        _mapMgr.RebuildTracks(_doc.Tracks, _active);
        UpdateFlags();
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
                        IsWaypoint = p.IsWaypoint,
                    });
                }
            }
            PointsGrid.ItemsSource = rows;
        }
        finally { _syncingUi = false; }
    }

    private void PointsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingUi) return;
        var indices = SelectedIndices();
        _mapMgr.SetSelection(_active, indices);
        if (indices.Count > 0) UpdatePlotMarkers(indices[^1]);
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
    private void SelectPointInGrid(int index, bool ctrl = false, bool shift = false)
    {
        if (PointsGrid.ItemsSource is not List<PointRow> rows || index < 0 || index >= rows.Count) return;

        if (shift && PointsGrid.SelectedItems.Count > 0)
        {
            int anchor = ((PointRow)PointsGrid.SelectedItems[0]!).Index;
            PointsGrid.SelectedItems.Clear();
            for (int i = Math.Min(anchor, index); i <= Math.Max(anchor, index); i++)
                PointsGrid.SelectedItems.Add(rows[i]);
        }
        else if (ctrl)
        {
            if (PointsGrid.SelectedItems.Contains(rows[index]))
                PointsGrid.SelectedItems.Remove(rows[index]);
            else
                PointsGrid.SelectedItems.Add(rows[index]);
        }
        else
        {
            PointsGrid.SelectedItems.Clear();
            PointsGrid.SelectedItems.Add(rows[index]);
        }
        PointsGrid.ScrollIntoView(rows[index]);
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
            new TrackInfoWindow(row.T) { Owner = this }.ShowDialog();
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
                await _srtm.EnsureTilesAsync(need, new Progress<string>(UpdateBusy));
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
                var progress = new Progress<(int Done, int Total)>(pr =>
                    UpdateBusy($"Fetching elevation online… {pr.Done}/{pr.Total}"));
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
        if (ctrl && e.Key == Key.Z) { Undo_Click(sender, e); e.Handled = true; }
        else if (ctrl && e.Key == Key.Y) { Redo_Click(sender, e); e.Handled = true; }
        else if (ctrl && e.Key == Key.C) { CopyPoints_Click(sender, e); e.Handled = true; }
        else if (ctrl && e.Key == Key.V) { PastePoints_Click(sender, e); e.Handled = true; }
        else if (ctrl && e.Key == Key.O) { OpenFile_Click(sender, e); e.Handled = true; }
        else if (ctrl && e.Key == Key.S) { SaveAll_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.Delete) { DeletePoints_Click(sender, e); e.Handled = true; }
    }

    // ======================= background-activity indicator =======================

    private int _busyDepth;

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

    private void EndBusy()
    {
        if (--_busyDepth > 0) return;
        _busyDepth = 0;
        StatusBusy.Text = "";
        StatusBusyItem.Visibility = Visibility.Collapsed;
    }

    // ======================= statistics =======================

    private void RefreshStats()
    {
        StatsText.Text = _active is null || _active.Points.Count < 2
            ? "—"
            : TrackStatistics.Compute(_active.Points).ToDisplayString();
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
        SelStatsText.Text = header + TrackStatistics.Compute(span).ToDisplayString(includeIncline: true);
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
    public bool IsWaypoint { get; set; }
}
