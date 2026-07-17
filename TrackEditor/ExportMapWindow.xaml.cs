using System;
using System.Windows;
using System.Windows.Controls;
using TrackEditor.Services;

namespace TrackEditor;

public partial class ExportMapWindow : Window
{
    private const int MaxDimension = 12000; // guard against absurdly large exports

    private readonly (double MinX, double MinY, double MaxX, double MaxY) _extent;

    public int Zoom { get; private set; }
    public double Scale { get; private set; } = 1;

    public ExportMapWindow((double MinX, double MinY, double MaxX, double MaxY) extent, int currentZoom, int maxZoom)
    {
        InitializeComponent();
        _extent = extent;

        int lo = Math.Max(1, currentZoom - 2);
        int hi = Math.Min(maxZoom, currentZoom + 5);
        for (int z = lo; z <= hi; z++)
        {
            var item = new ComboBoxItem { Content = $"Zoom {z}" + (z == currentZoom ? "  (current)" : ""), Tag = z };
            CmbZoom.Items.Add(item);
            if (z == currentZoom) item.IsSelected = true;
        }
        if (CmbZoom.SelectedItem is null && CmbZoom.Items.Count > 0) CmbZoom.SelectedIndex = CmbZoom.Items.Count - 1;
        CmbScale.SelectedIndex = 0;
        UpdateEstimate();
    }

    private void Selection_Changed(object sender, SelectionChangedEventArgs e) => UpdateEstimate();

    private void UpdateEstimate()
    {
        if (EstimateText is null || CmbZoom?.SelectedItem is not ComboBoxItem zi) return;
        int zoom = (int)zi.Tag;
        double scale = CmbScale?.SelectedItem is ComboBoxItem si ? double.Parse((string)si.Tag) : 1;

        var (w, h, tiles) = MapExporter.EstimateSize(_extent, zoom, scale);
        bool tooBig = w > MaxDimension || h > MaxDimension;
        EstimateText.Text = $"Output: {w:N0} × {h:N0} px · {tiles:N0} tile(s) to fetch"
            + (tooBig ? $"\nToo large (max {MaxDimension:N0}px per side) — lower the zoom or scale." : "");
        EstimateText.Foreground = tooBig
            ? System.Windows.Media.Brushes.Firebrick
            : System.Windows.Media.Brushes.Gray;
        OkButton.IsEnabled = !tooBig;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (CmbZoom.SelectedItem is ComboBoxItem zi) Zoom = (int)zi.Tag;
        if (CmbScale.SelectedItem is ComboBoxItem si) Scale = double.Parse((string)si.Tag);
        DialogResult = true;
    }
}
