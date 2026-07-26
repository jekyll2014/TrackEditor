using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

using Microsoft.Win32;

using TrackEditor.Core.Models;
using TrackEditor.Core.Services;
using TrackEditor.Core.Services.RaceAnalysis;

namespace TrackEditor;

/// <summary>
/// Applies a saved <see cref="RaceModel"/> to one target track to predict its pace, producing a timestamped
/// copy (<c>"&lt;name&gt; (predicted)"</c>). The caller reads <see cref="PredictedTrack"/> after a true dialog
/// result and inserts it into the document.
/// </summary>
public partial class ApplyRaceModelWindow : Window
{
    private readonly Track _target;
    private readonly AppSettings _settings;
    private RaceModel? _model;
    private double[]? _surface;   // per-point surface multiplier from routing inference, or null

    /// <summary>The predicted copy the user chose to add; null until "Add Predicted Track" is pressed.</summary>
    public Track? PredictedTrack { get; private set; }

    public ApplyRaceModelWindow(Track target, AppSettings settings)
    {
        InitializeComponent();
        _target = target;
        _settings = settings;
        LoadProfileToUi(settings.Profile);
        TargetText.Text = $"Predict the race flow on “{target.Name}” ({target.Points.Count} pts) " +
                          "by applying a saved race model.";
        if (!target.Points.Any(p => p.Ele is not null))
            TargetText.Text += "\n⚠ This track has no elevation — every grade reads as flat, so the prediction " +
                               "will be poor. Run Track ▸ Apply Elevation first for a realistic result.";
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Import race model",
            Filter = "Race model (*.racemodel.json)|*.racemodel.json|JSON|*.json|All files|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            _model = RaceModel.Load(dlg.FileName);
            ModelText.Text = DescribeModel(_model);
            RunButton.IsEnabled = true;
            HintText.Text = "Model loaded — press Predict.";
        }
        catch (Exception ex)
        {
            _model = null;
            RunButton.IsEnabled = false;
            ModelText.Text = "Failed to load model: " + ex.Message;
        }
    }

    private static string DescribeModel(RaceModel m)
    {
        string src = m.Meta.SourceTracks.Count > 0 ? string.Join(", ", m.Meta.SourceTracks) : "unknown";
        return $"Flat pace {m.AthleteBaseline.FlatSpeedMps * 3.6:F1} km/h · fatigue by {m.Fatigue.Driver} · " +
               $"from: {src}";
    }

    private void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_model is null) return;
        if (!TryParseStart(TxtStart.Text, out DateTime start))
        {
            HintText.Text = "Start time must be HH:mm (e.g. 08:00).";
            return;
        }

        SaveUiToProfile();
        var options = new PredictOptions
        {
            StartTime = start,
            SurfaceMult = SelectedSurfaceMult(),
            PerPointSurfaceMult = _surface,
            UseAltitude = ChkAltitude.IsChecked == true,
            Effort = (RaceEffort)CmbEffort.SelectedIndex,
            Profile = _settings.Profile,
            CalibrateToRecentRace = ChkCalibrate.IsChecked == true,
            CapToSustainable = ChkCap.IsChecked == true,
            UseLoadModel = ChkLoad.IsChecked == true,
        };

        try
        {
            var result = RacePredictor.Predict(_target, _model, options);
            PredictedTrack = result.PredictedTrack;   // held; only committed if the user clicks Add
            ReportText.Text = result.Report;
            AddButton.IsEnabled = true;
            HintText.Text = $"Predicted {result.DistanceKm:F1} km in {result.TotalTime:hh\\:mm\\:ss}.";
        }
        catch (Exception ex)
        {
            PredictedTrack = null;
            AddButton.IsEnabled = false;
            ReportText.Text = "Prediction failed: " + ex.Message;
            HintText.Text = "Prediction failed.";
        }
    }

    private static bool TryParseStart(string text, out DateTime start)
    {
        start = default;
        if (!TimeSpan.TryParseExact(text?.Trim(), new[] { "h\\:mm", "hh\\:mm" }, CultureInfo.InvariantCulture, out var tod))
            return false;
        start = DateTime.Today.Add(tod);
        return true;
    }

    private async void InferSurface_Click(object sender, RoutedEventArgs e)
    {
        InferSurfaceButton.IsEnabled = false;
        SurfaceStatus.Text = "Routing along the track…";
        try
        {
            var res = await SurfaceInference.InferAsync(_target, new RoutingService());
            if (res.Routed && res.Matched > 0)
            {
                _surface = res.PerPointMult;
                SurfaceStatus.Text = $"Surface: {res.Coverage * 100:F0}% covered, mean ×{res.MeanMult:F2}. " +
                                     "Re-run Predict to apply.";
            }
            else
            {
                _surface = null;
                SurfaceStatus.Text = res.Routed
                    ? "No confident surface matches — the route didn't hug the track. Left neutral."
                    : "Routing unavailable (offline / rate-limited). Surface left neutral.";
            }
        }
        catch (Exception ex)
        {
            _surface = null;
            SurfaceStatus.Text = "Surface inference failed: " + ex.Message;
        }
        finally
        {
            InferSurfaceButton.IsEnabled = true;
        }
    }

    private double SelectedSurfaceMult() =>
        double.TryParse((CmbSurface.SelectedItem as ComboBoxItem)?.Tag as string,
            NumberStyles.Float, CultureInfo.InvariantCulture, out double m) ? m : 1.0;

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (PredictedTrack is null) return;
        DialogResult = true;
    }

    // --- athlete profile <-> UI (persisted in AppSettings so it carries across predictions) ---

    private void LoadProfileToUi(AthleteProfile p)
    {
        TxtMass.Text = p.MassKg?.ToString(CultureInfo.InvariantCulture) ?? "";
        TxtAge.Text = p.Age?.ToString(CultureInfo.InvariantCulture) ?? "";
        CmbSex.SelectedIndex = (int)p.Sex;
        TxtHrMax.Text = p.HrMaxBpm?.ToString(CultureInfo.InvariantCulture) ?? "";
        TxtRestHr.Text = p.RestingHrBpm?.ToString(CultureInfo.InvariantCulture) ?? "";
        TxtLthr.Text = p.LthrBpm?.ToString(CultureInfo.InvariantCulture) ?? "";
        TxtPack.Text = p.PackKg?.ToString(CultureInfo.InvariantCulture) ?? "";
        ChkPoles.IsChecked = p.UsePoles;
        TxtRaceKm.Text = p.RecentRace is { IsValid: true } r ? r.DistanceKm.ToString(CultureInfo.InvariantCulture) : "";
        TxtRaceTime.Text = p.RecentRace is { IsValid: true } rr ? rr.Time.ToString(@"h\:mm\:ss") : "";
    }

    private void SaveUiToProfile()
    {
        var p = _settings.Profile;
        p.MassKg = ParseNullableDouble(TxtMass.Text);
        p.Age = ParseNullableInt(TxtAge.Text);
        p.Sex = (Sex)Math.Max(0, CmbSex.SelectedIndex);
        p.HrMaxBpm = ParseNullableInt(TxtHrMax.Text);
        p.RestingHrBpm = ParseNullableInt(TxtRestHr.Text);
        p.LthrBpm = ParseNullableInt(TxtLthr.Text);
        p.PackKg = ParseNullableDouble(TxtPack.Text);
        p.UsePoles = ChkPoles.IsChecked == true;

        double? km = ParseNullableDouble(TxtRaceKm.Text);
        TimeSpan? time = ParseNullableTime(TxtRaceTime.Text);
        p.RecentRace = km is double d && d > 0 && time is TimeSpan t && t > TimeSpan.Zero
            ? new RecentRace { DistanceKm = d, Time = t }
            : null;

        _settings.Save();
    }

    private static double? ParseNullableDouble(string? s) =>
        double.TryParse(s?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v > 0 ? v : null;

    private static int? ParseNullableInt(string? s) =>
        int.TryParse(s?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v > 0 ? v : null;

    private static TimeSpan? ParseNullableTime(string? s) =>
        TimeSpan.TryParse(s?.Trim(), CultureInfo.InvariantCulture, out TimeSpan t) ? t : null;
}
