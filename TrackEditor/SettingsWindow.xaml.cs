using Microsoft.Win32;
using System.Windows;
using TrackEditor.Services;

namespace TrackEditor;

public partial class SettingsWindow : Window
{
    /// <summary>Populated with the edited settings when the dialog returns true.</summary>
    public AppSettings Result { get; private set; }

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

        UpdateEnabledState();
    }

    private void Srtm_Toggled(object sender, RoutedEventArgs e) => UpdateEnabledState();
    private void Online_Toggled(object sender, RoutedEventArgs e) => UpdateEnabledState();
    private void Provider_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateEnabledState();

    private void UpdateEnabledState()
    {
        if (SrtmPanel is not null) SrtmPanel.IsEnabled = ChkSrtm.IsChecked == true;
        if (OnlinePanel is not null) OnlinePanel.IsEnabled = ChkOnline.IsChecked == true;
        // The dataset field only applies to OpenTopoData.
        if (DatasetRow is not null) DatasetRow.IsEnabled = CmbProvider.SelectedIndex == 0;
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
        DialogResult = true;
    }
}
