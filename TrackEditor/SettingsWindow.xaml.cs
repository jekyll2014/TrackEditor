using Microsoft.Win32;

using System.Globalization;
using System.Windows;

using TrackEditor.Core.Services;

namespace TrackEditor;

public partial class SettingsWindow : Window
{
    /// <summary>Populated with the edited settings when the dialog returns true.</summary>
    public AppSettings Result { get; private set; }

    /// <summary>Raised by the "Clear tile cache" button; the owner performs the clear (it owns the map).</summary>
    public event EventHandler? ClearTileCacheRequested;

    private void ClearCache_Click(object sender, RoutedEventArgs e) =>
        ClearTileCacheRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Public global datasets on api.opentopodata.org (see opentopodata.org/datasets).</summary>
    private static readonly string[] OpenTopoDatasets =
    {
        "srtm90m", "srtm30m", "aster30m", "nasadem", "mapzen",
        "eudem25m", "ned10m", "etopo1", "gebco2020", "emod2018", "bkg200m",
    };

    public SettingsWindow(AppSettings current)
    {
        InitializeComponent();
        Result = current.Clone();

        ChkSrtm.IsChecked = Result.SrtmEnabled;
        ChkSrtmAuto.IsChecked = Result.SrtmAutoDownload;
        TxtSrtmFolder.Text = Result.SrtmFolder ?? "";

        ChkOnline.IsChecked = Result.OnlineEnabled;
        CmbProvider.SelectedIndex = Result.OnlineProvider == OnlineElevationProvider.OpenTopoData ? 0 : 1;

        foreach (var d in OpenTopoDatasets) CmbDataset.Items.Add(d);
        CmbDataset.Text = Result.OpenTopoDataset; // editable: keeps a custom value if not in the list

        CmbBaseMap.SelectedIndex = (int)Result.BaseMap;
        TxtCacheLimit.Text = Result.TileCacheLimitMB.ToString();

        TxtWpBack.Text = Result.WaypointLabelBackHex; // fires WpColor_Changed → updates previews
        TxtWpText.Text = Result.WaypointLabelTextHex;

        ChkRouteSimplify.IsChecked = Result.AutoRouteSimplify;
        TxtRouteTolerance.Text = Result.AutoRouteToleranceM.ToString(CultureInfo.CurrentCulture);

        ChkColWaypoint.IsChecked = Result.ColWaypoint;
        ChkColLat.IsChecked = Result.ColLat;
        ChkColLon.IsChecked = Result.ColLon;
        ChkColEle.IsChecked = Result.ColEle;
        ChkColTime.IsChecked = Result.ColTime;
        ChkColDist.IsChecked = Result.ColDist;

        UpdateEnabledState();
    }

    private void WpColor_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var back = TryBrush(TxtWpBack?.Text);
        var text = TryBrush(TxtWpText?.Text);
        if (back is not null)
        {
            if (RectWpBack is not null) RectWpBack.Fill = back;
            if (WpPreviewBorder is not null) WpPreviewBorder.Background = back;
        }
        if (text is not null)
        {
            if (RectWpText is not null) RectWpText.Fill = text;
            if (WpPreviewText is not null) WpPreviewText.Foreground = text;
        }
    }

    private static System.Windows.Media.Brush? TryBrush(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        try
        {
            return new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex.Trim()));
        }
        catch { return null; }
    }

    /// <summary>Normalises a hex colour to #RRGGBB, or returns the fallback if it can't be parsed.</summary>
    private static string NormalizeHex(string? input, string fallback)
    {
        if (string.IsNullOrWhiteSpace(input)) return fallback;
        try
        {
            var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(input.Trim());
            return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        }
        catch { return fallback; }
    }

    private void RouteSimplify_Toggled(object sender, RoutedEventArgs e) => UpdateEnabledState();
    private void Srtm_Toggled(object sender, RoutedEventArgs e) => UpdateEnabledState();
    private void Online_Toggled(object sender, RoutedEventArgs e) => UpdateEnabledState();
    private void Provider_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateEnabledState();

    private void UpdateEnabledState()
    {
        if (SrtmPanel is not null) SrtmPanel.IsEnabled = ChkSrtm.IsChecked == true;
        if (OnlinePanel is not null) OnlinePanel.IsEnabled = ChkOnline.IsChecked == true;
        // The dataset field only applies to OpenTopoData.
        if (DatasetRow is not null) DatasetRow.IsEnabled = CmbProvider.SelectedIndex == 0;
        // The tolerance only matters when route simplification is on.
        if (RouteToleranceRow is not null) RouteToleranceRow.IsEnabled = ChkRouteSimplify.IsChecked == true;
    }

    private void BrowseSrtm_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Folder with SRTM .hgt tiles" };
        if (!string.IsNullOrWhiteSpace(TxtSrtmFolder.Text)) dlg.InitialDirectory = TxtSrtmFolder.Text;
        if (dlg.ShowDialog() == true) TxtSrtmFolder.Text = dlg.FolderName;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Result.SrtmEnabled = ChkSrtm.IsChecked == true;
        Result.SrtmAutoDownload = ChkSrtmAuto.IsChecked == true;
        Result.SrtmFolder = string.IsNullOrWhiteSpace(TxtSrtmFolder.Text) ? null : TxtSrtmFolder.Text.Trim();
        Result.OnlineEnabled = ChkOnline.IsChecked == true;
        Result.OnlineProvider = CmbProvider.SelectedIndex == 1
            ? OnlineElevationProvider.OpenElevation
            : OnlineElevationProvider.OpenTopoData;
        Result.OpenTopoDataset = string.IsNullOrWhiteSpace(CmbDataset.Text) ? "srtm90m" : CmbDataset.Text.Trim();
        Result.BaseMap = (BaseMapProvider)Math.Max(0, CmbBaseMap.SelectedIndex);
        Result.TileCacheLimitMB = int.TryParse(TxtCacheLimit.Text, out int mb) ? mb : Result.TileCacheLimitMB;
        Result.WaypointLabelBackHex = NormalizeHex(TxtWpBack.Text, Result.WaypointLabelBackHex);
        Result.WaypointLabelTextHex = NormalizeHex(TxtWpText.Text, Result.WaypointLabelTextHex);
        Result.AutoRouteSimplify = ChkRouteSimplify.IsChecked == true;
        Result.AutoRouteToleranceM =
            double.TryParse(TxtRouteTolerance.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double tol) && tol > 0
                ? tol : Result.AutoRouteToleranceM;
        Result.ColWaypoint = ChkColWaypoint.IsChecked == true;
        Result.ColLat = ChkColLat.IsChecked == true;
        Result.ColLon = ChkColLon.IsChecked == true;
        Result.ColEle = ChkColEle.IsChecked == true;
        Result.ColTime = ChkColTime.IsChecked == true;
        Result.ColDist = ChkColDist.IsChecked == true;
        DialogResult = true;
    }
}
