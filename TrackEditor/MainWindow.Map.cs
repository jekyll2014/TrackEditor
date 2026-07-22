using System.Text;
using System.Windows;
using System.Windows.Input;

using TrackEditor.Core.Models;
using TrackEditor.Services;

namespace TrackEditor;

/// <summary>Map mouse interaction, hover popup, mileage flags and the bottom plots.</summary>
public partial class MainWindow
{
    private System.Windows.Point _mouseDownPos;
    private bool _leftDown;
    private int _hoverIdx = -1;
    private (double Cx, double Cy, double Res) _lastViewport;

    // Point dragging (Edit mode): index of the vertex being dragged, whether it has actually moved,
    // and a throttle so the live map rebuild stays smooth on large tracks.
    private int _dragIdx = -1;
    private bool _dragging;
    private int _lastDragTick;
    private bool _draggingViewer; // dragging the 3D viewpoint marker

    private ScottPlot.Plottables.VerticalLine? _marker; // selection marker on the profile plot
    private ScottPlot.Plottables.VerticalLine? _hover;  // hover marker on the profile plot

    // ======================= hover (map <-> plots, both directions) =======================

    /// <summary>Central hover state: highlights the point on the map AND on both plots. -1 clears.</summary>
    private void SetHoverPoint(int idx)
    {
        if (idx == _hoverIdx) return;
        _hoverIdx = idx;
        bool valid = idx >= 0 && _active is not null && idx < _active.Points.Count;
        _mapMgr.SetHover(valid ? _active!.Points[idx] : null);
        double x = valid && idx < _cumDist.Length ? _cumDist[idx] / 1000 : double.NaN;
        _hover = UpdateHoverLine(ProfilePlot, _hover, x);
    }

    private static ScottPlot.Plottables.VerticalLine? UpdateHoverLine(
        ScottPlot.WPF.WpfPlot plot, ScottPlot.Plottables.VerticalLine? line, double x)
    {
        if (double.IsNaN(x))
        {
            if (line is not null)
            {
                plot.Plot.Remove(line);
                plot.Refresh();
            }
            return null;
        }
        if (line is null)
        {
            line = plot.Plot.Add.VerticalLine(x);
            line.Color = ScottPlot.Colors.SteelBlue;
            line.LineWidth = 1;
        }
        else line.X = x;
        plot.Refresh();
        return line;
    }

    private void ShowHoverPopup(UIElement target, System.Windows.Point pos, int idx)
    {
        HoverText.Text = BuildPointInfo(idx);
        HoverPopup.PlacementTarget = target;
        HoverPopup.HorizontalOffset = pos.X + 16;
        HoverPopup.VerticalOffset = pos.Y + 16;
        HoverPopup.IsOpen = true;
    }

    // ======================= mouse on map =======================

    private void MapCtrl_MouseMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(MapCtrl);
        var (lon, lat) = MapManager.ScreenToLonLat(MapCtrl, pos.X, pos.Y);
        StatusCoords.Text = $"{lat:F5}, {lon:F5}";

        // Dragging the 3D viewpoint marker moves the 3D camera (the 3D window echoes back the marker).
        if (_draggingViewer && e.LeftButton == MouseButtonState.Pressed)
        {
            _map3D?.SetViewpoint(lat, lon);
            return;
        }

        // Dragging a vertex (Edit mode): move the point and live-refresh the map (throttled for big tracks).
        if (_dragIdx >= 0 && e.LeftButton == MouseButtonState.Pressed
            && _active is not null && _dragIdx < _active.Points.Count)
        {
            double moved = Math.Abs(pos.X - _mouseDownPos.X) + Math.Abs(pos.Y - _mouseDownPos.Y);
            if (!_dragging && moved > 3)
            {
                _dragging = true;
                _doc.Snapshot(ActiveIndex()); // a single undo step covers the whole drag
            }
            if (_dragging)
            {
                _active.Points[_dragIdx].Lat = lat;
                _active.Points[_dragIdx].Lon = lon;
                int now = Environment.TickCount;
                if (now - _lastDragTick >= 25)
                {
                    _lastDragTick = now;
                    _mapMgr.RebuildTracks(_doc.Tracks, _active);
                    _mapMgr.SetSelection(_active, new[] { _dragIdx });
                }
            }
            return; // suppress hover while dragging
        }

        if (_active is null || _active.Points.Count == 0)
        {
            HideHover();
            return;
        }

        int idx = _mapMgr.FindNearestPointIndex(_active, pos.X, pos.Y, 10);
        SetHoverPoint(idx);

        if (idx >= 0)
            ShowHoverPopup(MapCtrl, pos, idx);
        else
            HoverPopup.IsOpen = false;
    }

    private void MapCtrl_MouseLeave(object sender, MouseEventArgs e) => HideHover();

    private void HideHover()
    {
        HoverPopup.IsOpen = false;
        SetHoverPoint(-1);
    }

    private string BuildPointInfo(int idx)
    {
        var p = _active!.Points[idx];
        var sb = new StringBuilder();
        sb.AppendLine($"#{idx} of {_active.Points.Count - 1}");
        if (p.Time is DateTime t)
        {
            sb.Append($"Time: {t.ToLocalTime():HH:mm:ss}");
            if (_active.Points[0].Time is DateTime t0 && t >= t0)
                sb.Append($"  (+{FmtSpan(t - t0)})");
            sb.AppendLine();
        }
        double toEnd = _cumDist[^1] - _cumDist[idx];
        sb.AppendLine($"From start: {_cumDist[idx] / 1000:F2} km   To end: {toEnd / 1000:F2} km");
        if (p.Ele is double ele) sb.AppendLine($"Ele (track): {ele:F0} m");
        if (SrtmActive && _srtm.GetElevation(p.Lat, p.Lon) is double srtmEle) sb.AppendLine($"Ele (SRTM): {srtmEle:F0} m");
        if (idx < _speeds.Length && _speeds[idx] is double v) sb.AppendLine($"Speed: {v * 3.6:F1} km/h");
        return sb.ToString().TrimEnd();
    }

    private static string FmtSpan(TimeSpan t) => $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}";

    private void MapCtrl_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(MapCtrl);

        if (e.ClickCount == 2)
        {
            // In Edit mode a double-click inserts a point on the nearest segment; otherwise it
            // centres the nearest point in the viewport (same as double-click in the list).
            // Either way we suppress Mapsui's default double-click zoom.
            if (_mode == EditMode.Edit) InsertPointAtScreen(pos);
            else CenterNearestPoint(pos);
            _leftDown = false;
            _dragIdx = -1;
            e.Handled = true;
            return;
        }

        _leftDown = true;
        _dragging = false;
        _mouseDownPos = pos;

        // The 3D viewpoint marker can be dragged in any mode, and takes precedence over vertices.
        double vd = _mapMgr.ViewerScreenDistance(pos.X, pos.Y);
        if (_map3D is not null && vd >= 0 && vd <= 14)
        {
            _draggingViewer = true;
            Mouse.Capture(MapCtrl);
            e.Handled = true; // don't let Mapsui pan while dragging the marker
            return;
        }

        // In Edit mode, pressing on an existing vertex begins a drag (and suppresses Mapsui's pan).
        _dragIdx = -1;
        if (_mode == EditMode.Edit && _active is not null && _active.Points.Count > 0)
        {
            int idx = _mapMgr.FindNearestPointIndex(_active, pos.X, pos.Y, 10);
            if (idx >= 0)
            {
                _dragIdx = idx;
                Mouse.Capture(MapCtrl);
                e.Handled = true; // stop Mapsui from panning while we drag the point
            }
        }
    }

    /// <summary>Centres the active track's nearest point (within a pixel threshold) in the map
    /// viewport without changing zoom. Returns false if no point is close enough.</summary>
    private bool CenterNearestPoint(System.Windows.Point pos)
    {
        if (_active is null || _active.Points.Count == 0) return false;
        int idx = _mapMgr.FindNearestPointIndex(_active, pos.X, pos.Y, 20);
        if (idx < 0) return false;
        _mapMgr.CenterOn(_active.Points[idx]);
        return true;
    }

    private void MapCtrl_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(MapCtrl);

        if (_draggingViewer)
        {
            _draggingViewer = false;
            Mouse.Capture(null);
            _leftDown = false;
            e.Handled = true;
            return;
        }

        // Finish a vertex drag, or select the vertex if it was pressed without moving (Edit mode).
        if (_dragIdx >= 0)
        {
            Mouse.Capture(null);
            int idx = _dragIdx;
            _dragIdx = -1;
            _leftDown = false;
            e.Handled = true;
            if (_dragging)
            {
                _dragging = false;
                RefreshAll();               // recompute distances / plots / stats after the move
                SelectPointInGrid(idx);
                StatusInfo.Text = $"Moved point {idx} to {_active!.Points[idx].Lat:F5}, {_active.Points[idx].Lon:F5}";
            }
            else
            {
                bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
                bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
                SelectPointInGrid(idx, ctrl, shift);
            }
            return;
        }

        if (!_leftDown) return;
        _leftDown = false;
        if (Math.Abs(pos.X - _mouseDownPos.X) + Math.Abs(pos.Y - _mouseDownPos.Y) > 5) return; // it was a pan
        HandleMapClick(pos);
    }

    /// <summary>Inserts a new point directly after the currently selected one (Edit-mode double-click);
    /// with no selection it appends. Elevation is filled from SRTM when available.</summary>
    private void InsertPointAtScreen(System.Windows.Point pos)
    {
        if (_active is null)
        {
            StatusInfo.Text = "Insert: no active track (draw or open one first)";
            return;
        }
        var (lon, lat) = MapManager.ScreenToLonLat(MapCtrl, pos.X, pos.Y);
        var p = new TrackPoint { Lat = lat, Lon = lon };
        if (SrtmActive && _srtm.GetElevation(lat, lon) is double ele) { p.Ele = ele; _active.ElevationEstimated = true; }

        _doc.Snapshot(ActiveIndex());
        var sel = SelectedIndices();
        int at = sel.Count > 0 ? sel[^1] + 1 : _active.Points.Count;
        at = Math.Clamp(at, 0, _active.Points.Count);
        _active.Points.Insert(at, p);
        RefreshAll();
        SelectPointInGrid(at); // chain: the next insert goes after this one
        StatusInfo.Text = $"Inserted point at index {at}";
    }

    private void MapCtrl_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_mode == EditMode.Edit && _active is not null && _active.Points.Count > 0)
        {
            DeleteLast_Click(sender, e);
            e.Handled = true;
        }
    }

    private void HandleMapClick(System.Windows.Point pos)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        // Universal: a single click near an existing active-track point selects it (every mode but Measure,
        // which needs to place its endpoints freely).
        if (_mode != EditMode.Measure && _active is not null && _active.Points.Count > 0)
        {
            int nearIdx = _mapMgr.FindNearestPointIndex(_active, pos.X, pos.Y, 10);
            if (nearIdx >= 0)
            {
                SelectPointInGrid(nearIdx, ctrl, shift);
                return;
            }
        }

        switch (_mode)
        {
            case EditMode.Edit:
                {
                    // Appending only happens when the end of the track is the current insertion point,
                    // i.e. nothing is selected or the last point is selected. Otherwise the user is
                    // working mid-track and should double-click to insert after the selection.
                    if (_active is null)
                    {
                        NewTrack_Click(this, new RoutedEventArgs());
                        if (_active is null) return;
                    }
                    var sel = SelectedIndices();
                    bool atEnd = _active.Points.Count == 0 || sel.Count == 0 || sel[^1] == _active.Points.Count - 1;
                    if (!atEnd)
                    {
                        StatusInfo.Text = "Double-click to insert after the selected point (or select the last point to append)";
                        return;
                    }
                    var (lon, lat) = MapManager.ScreenToLonLat(MapCtrl, pos.X, pos.Y);

                    // Auto-route draws the leg along real paths instead of a straight line.
                    if (_settings.AutoRoute && _active.Points.Count > 0)
                    {
                        _ = AppendRoutedAsync(lat, lon);
                        break;
                    }

                    _doc.Snapshot(ActiveIndex());
                    var p = new TrackPoint { Lat = lat, Lon = lon };
                    if (SrtmActive && _srtm.GetElevation(lat, lon) is double ele) { p.Ele = ele; _active.ElevationEstimated = true; }
                    _active.Points.Add(p);
                    RefreshAll();
                    SelectPointInGrid(_active.Points.Count - 1); // keep appending on the next click
                    StatusInfo.Text = $"Added point {_active.Points.Count - 1} ({lat:F5}, {lon:F5})";
                    break;
                }
            case EditMode.Measure:
                {
                    var (lon, lat) = MapManager.ScreenToLonLat(MapCtrl, pos.X, pos.Y);
                    _measurePts.Add((lat, lon));
                    _mapMgr.SetMeasure(_measurePts);
                    if (_measurePts.Count < 2)
                    {
                        MeasureText.Text = "Click the next point (click Measure again to reset)";
                        StatusInfo.Text = "Measure: click the next point";
                    }
                    else _ = ComputeMeasurementAsync(_measurePts.ToList());
                    break;
                }
            default: // View: click on another track's line switches the active track
                {
                    var hit = _mapMgr.FindNearestTrack(_doc.Tracks, pos.X, pos.Y, 8);
                    if (hit is not null && !ReferenceEquals(hit, _active))
                    {
                        SetActive(hit);
                        RefreshTracksList();
                        RefreshPointsGrid();
                        _mapMgr.RebuildTracks(_doc.Tracks, _active);
                        UpdateFlags();
                        RefreshPlots();
                        RefreshStats();
                        StatusInfo.Text = $"Active track: {hit.Name}";
                    }
                    break;
                }
        }
    }

    // ======================= mileage flags =======================

    private void ViewportTimer_Tick(object? sender, EventArgs e)
    {
        var vp = _mapMgr.ViewportState();
        if (vp != _lastViewport)
        {
            _lastViewport = vp;
            if (FlagsCheck.IsChecked == true) UpdateFlags();
        }
    }

    private void FlagsToggle_Click(object sender, RoutedEventArgs e)
    {
        bool on = ReferenceEquals(sender, MenuFlags) ? MenuFlags.IsChecked : FlagsCheck.IsChecked == true;
        _syncingUi = true;
        FlagsCheck.IsChecked = on;
        MenuFlags.IsChecked = on;
        _syncingUi = false;
        UpdateFlags();
    }

    /// <summary>View → Mileage Flag Content radio items: pick distance / time / both.</summary>
    private void FlagContentMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem mi) return;
        _flagContent = int.Parse((string)mi.Tag);
        FlagContentDist.IsChecked = _flagContent == 0;
        FlagContentTime.IsChecked = _flagContent == 1;
        FlagContentBoth.IsChecked = _flagContent == 2;
        UpdateFlags();
    }

    /// <summary>
    /// Rebuilds the flag layer: walks the active track in order and places a flag on every
    /// point whose label rectangle does not overlap any already-placed one (greedy skip).
    /// </summary>
    private void UpdateFlags()
    {
        if (_mapMgr is null) return; // during InitializeComponent
        if (FlagsCheck.IsChecked != true || _active is null || _active.Points.Count < 2)
        {
            _mapMgr.ClearFlags();
            return;
        }

        int contentMode = _flagContent; // 0 dist, 1 time, 2 both
        var pts = _active.Points;
        var placed = new List<Rect>();
        var flags = new List<(TrackPoint, string)>();
        double w = MapCtrl.ActualWidth, h = MapCtrl.ActualHeight;
        DateTime? t0 = pts[0].Time;

        for (int i = 0; i < pts.Count && flags.Count < 400; i++)
        {
            var s = _mapMgr.WorldToScreen(pts[i]);
            if (s is null) return; // viewport not ready
            if (s.X < -100 || s.Y < -100 || s.X > w + 100 || s.Y > h + 100) continue;

            string text = BuildFlagText(i, contentMode, t0);
            if (text.Length == 0) continue;

            double rw = 7.5 * text.Length + 16;
            double rh = 26;
            var rect = new Rect(s.X - rw / 2, s.Y - 14 - rh, rw, rh);
            if (placed.Any(r => r.IntersectsWith(rect))) continue;

            placed.Add(rect);
            flags.Add((pts[i], text));
        }

        _mapMgr.SetFlags(flags);
    }

    private string BuildFlagText(int idx, int contentMode, DateTime? t0)
    {
        string dist = $"{_cumDist[idx] / 1000:F1} km";
        string time = "";
        if (_active!.Points[idx].Time is DateTime t && t0 is DateTime start && t >= start)
            time = FmtSpan(t - start);

        return contentMode switch
        {
            0 => dist,
            1 => time,
            _ => time.Length > 0 ? $"{dist} | {time}" : dist,
        };
    }

    // ======================= plots =======================

    /// <summary>Altitude (left axis) and speed (right axis) on one plot; checkboxes pick which show.</summary>
    private void RefreshPlots()
    {
        var plt = ProfilePlot.Plot;
        plt.Clear();
        plt.Axes.Remove(ScottPlot.Edge.Right); // drop any right axis added on the previous refresh
        _marker = null;
        _hover = null;
        _hoverIdx = -1;

        bool showAlt = ChkAlt?.IsChecked == true;
        bool showSpeed = ChkSpeed?.IsChecked == true;
        // Elevation is stored on the track (filled from SRTM/online when applicable), so the plot
        // just reads Points[i].Ele — no per-refresh DEM lookups.
        bool eleEstimated = _active?.ElevationEstimated == true;
        var altColor = _active is not null ? ScottPlot.Color.FromHex(_active.ColorHex) : ScottPlot.Colors.SteelBlue;
        var spdColor = ScottPlot.Color.FromHex("#E67E22"); // distinct from the track color

        bool hasAlt = false, hasSpeed = false;
        if (_active is not null && _active.Points.Count > 1)
        {
            var xsE = new List<double>();
            var ysE = new List<double>();
            var xsS = new List<double>();
            var ysS = new List<double>();
            for (int i = 0; i < _active.Points.Count; i++)
            {
                double km = _cumDist[i] / 1000;
                if (_active.Points[i].Ele is double ele) { xsE.Add(km); ysE.Add(ele); }
                if (_speeds[i] is double v) { xsS.Add(km); ysS.Add(v * 3.6); }
            }

            if (showAlt && xsE.Count > 1)
            {
                var sc = plt.Add.Scatter(xsE.ToArray(), ysE.ToArray());
                sc.MarkerSize = 0;
                sc.LineWidth = 2;
                sc.Color = altColor;
                sc.LegendText = "Altitude";
                sc.Axes.YAxis = plt.Axes.Left;
                // Dashed line signals the heights are estimated (DEM/online), not recorded.
                if (eleEstimated) sc.LinePattern = ScottPlot.LinePattern.Dashed;
                StyleYAxis(plt.Axes.Left, eleEstimated ? "Altitude, m (est.)" : "Altitude, m", altColor);
                hasAlt = true;
            }

            if (showSpeed && xsS.Count > 1)
            {
                var sc = plt.Add.Scatter(xsS.ToArray(), ysS.ToArray());
                sc.MarkerSize = 0;
                sc.LineWidth = 2;
                sc.Color = spdColor;
                sc.LegendText = "Speed";
                // Altitude keeps the left axis; speed goes on a right axis, or on the left if alone.
                ScottPlot.IYAxis yax = hasAlt ? plt.Axes.AddRightAxis() : plt.Axes.Left;
                sc.Axes.YAxis = yax;
                StyleYAxis(yax, "Speed, km/h", spdColor);
                hasSpeed = true;
            }
        }

        plt.Axes.Bottom.Label.Text = "km";
        if (!hasAlt && !hasSpeed) plt.Axes.Left.Label.Text = "";
        if (hasAlt && hasSpeed) plt.ShowLegend(ScottPlot.Alignment.UpperLeft);
        else plt.HideLegend();

        plt.Axes.AutoScale();
        AddWaypointMarkers(plt);
        plt.Benchmark.IsVisible = false; // no "Rendered in … ms" debug overlay
        ProfilePlot.Refresh();
    }

    /// <summary>Draws a labelled vertical line on the profile at each named waypoint of the active track.</summary>
    private void AddWaypointMarkers(ScottPlot.Plot plt)
    {
        if (_active is null) return;
        // Colours come from settings; the label tag's background defaults to the line colour.
        var wpBack = ScottPlot.Color.FromHex(_settings.WaypointLabelBackHex);
        var wpText = ScottPlot.Color.FromHex(_settings.WaypointLabelTextHex);
        for (int i = 0; i < _active.Points.Count && i < _cumDist.Length; i++)
        {
            if (!_active.Points[i].IsWaypoint) continue;
            var vl = plt.Add.VerticalLine(_cumDist[i] / 1000);
            vl.Color = wpBack;
            vl.LineWidth = 1;
            vl.LinePattern = ScottPlot.LinePattern.Dashed;
            vl.Text = _active.Points[i].Name!;
            vl.LabelOppositeAxis = true; // label rides along the top edge, clear of the x-axis ticks
            vl.LabelStyle.FontSize = 11;
            vl.LabelStyle.Bold = true;
            vl.LabelStyle.ForeColor = wpText;
            vl.LabelStyle.OffsetY = 9; // nudge down so the top of the tag isn't clipped by the frame edge
        }
    }

    private static void StyleYAxis(ScottPlot.IYAxis axis, string label, ScottPlot.Color color)
    {
        axis.Label.Text = label;
        axis.Label.ForeColor = color;
        axis.TickLabelStyle.ForeColor = color;
    }

    private void UpdatePlotMarkers(int idx)
    {
        if (_active is null || idx < 0 || idx >= _cumDist.Length) return;
        double x = _cumDist[idx] / 1000;

        if (_marker is null)
        {
            _marker = ProfilePlot.Plot.Add.VerticalLine(x);
            _marker.Color = ScottPlot.Colors.Gray;
            _marker.LineWidth = 1;
        }
        else _marker.X = x;
        ProfilePlot.Refresh();
    }

    private void ProfilePlot_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Double-click centres the corresponding point in the map viewport (same as the list).
        if (e.ClickCount == 2)
        {
            int idx = PlotHitIndex(ProfilePlot, e);
            if (idx >= 0 && _active is not null) _mapMgr.CenterOn(_active.Points[idx]);
            e.Handled = true;
            return;
        }
        PlotClickSelect(ProfilePlot, e);
    }
    private void ProfilePlot_MouseMove(object sender, MouseEventArgs e) => PlotHover(ProfilePlot, e);
    private void Plot_MouseLeave(object sender, MouseEventArgs e) => HideHover();

    private void PlotSeriesToggle(object sender, RoutedEventArgs e)
    {
        if (ProfilePlot is null) return; // fires during InitializeComponent before the plot exists
        RefreshPlots();
    }

    /// <summary>Track point index under the plot cursor (nearest by distance-from-start), or -1.</summary>
    private int PlotHitIndex(ScottPlot.WPF.WpfPlot plot, MouseEventArgs e)
    {
        if (_active is null || _cumDist.Length == 0) return -1;
        var pos = e.GetPosition(plot);
        var px = new ScottPlot.Pixel((float)(pos.X * plot.DisplayScale), (float)(pos.Y * plot.DisplayScale));
        var coord = plot.Plot.GetCoordinates(px);
        return NearestIndexByDistance(coord.X * 1000);
    }

    /// <summary>Binary search for the point nearest to a cumulative distance (meters).</summary>
    private int NearestIndexByDistance(double targetM)
    {
        if (_cumDist.Length == 0) return -1;
        if (_cumDist.Length == 1) return 0;
        int lo = 0, hi = _cumDist.Length - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (_cumDist[mid] < targetM) lo = mid; else hi = mid;
        }
        return Math.Abs(_cumDist[lo] - targetM) <= Math.Abs(_cumDist[hi] - targetM) ? lo : hi;
    }

    /// <summary>
    /// Click on a plot selects the nearest track point by distance-from-start.
    /// Shift extends the range from the anchor; Ctrl toggles the point (same as the map/grid).
    /// </summary>
    private void PlotClickSelect(ScottPlot.WPF.WpfPlot plot, MouseButtonEventArgs e)
    {
        int idx = PlotHitIndex(plot, e);
        if (idx < 0) return;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        SelectPointInGrid(idx, ctrl, shift);
    }

    /// <summary>Hover over a plot highlights the point on the map and shows its info popup.</summary>
    private void PlotHover(ScottPlot.WPF.WpfPlot plot, MouseEventArgs e)
    {
        int idx = PlotHitIndex(plot, e);
        SetHoverPoint(idx);
        if (idx >= 0)
            ShowHoverPopup(plot, e.GetPosition(plot), idx);
        else
            HoverPopup.IsOpen = false;
    }

}
