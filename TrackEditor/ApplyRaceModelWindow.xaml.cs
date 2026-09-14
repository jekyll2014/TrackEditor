using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

using Microsoft.Win32;

using TrackEditor.Core.Models;
using TrackEditor.Core.Services;
using TrackEditor.Core.Services.RaceAnalysis;
using TrackEditor.Localization;
using TrackEditor.Services;

namespace TrackEditor;

/// <summary>
/// Applies a saved <see cref="RaceModel"/> to one target track to predict its pace, producing a timestamped
/// copy (<c>"&lt;name&gt; (predicted)"</c>). The caller reads <see cref="PredictedTrack"/> after a true dialog
/// result and inserts it into the document.
/// </summary>
public partial class ApplyRaceModelWindow : Window
{
    private readonly Track _target;
    private readonly IReadOnlyList<Track> _allTracks;
    private readonly AppSettings _settings;
    private RaceModel? _model;
    private double[]? _surface;   // per-point surface multiplier from routing inference, or null

    /// <summary>The predicted copy the user chose to add; null until "Add Predicted Track" is pressed.</summary>
    public Track? PredictedTrack { get; private set; }

    public ApplyRaceModelWindow(Track target, IReadOnlyList<Track> allTracks, AppSettings settings)
    {
        InitializeComponent();
        _target = target;
        _allTracks = allTracks;
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
            ApplyModelToUi(_model);
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

    /// <summary>Opens Analyze Race Ability seeded with this prediction target, so it can mark and pre-tick the
    /// recorded tracks most like the target. If a model is fitted there, adopt it here without a file round-trip.</summary>
    private void CreateProfile_Click(object sender, RoutedEventArgs e)
    {
        var an = new AnalyzeRaceWindow(_allTracks, _target) { Owner = this };
        an.ShowDialog();
        if (an.Model is not RaceModel m) return;
        _model = m;
        ModelText.Text = "Created: " + DescribeModel(m);
        ApplyModelToUi(m);
        RunButton.IsEnabled = true;
        HintText.Text = "Profile created — press Predict.";
    }

    /// <summary>Shows/hides cycling vs running controls and pre-populates CdA/Crr from model defaults.</summary>
    private void ApplyModelToUi(RaceModel m)
    {
        // Sync the sport combo to the loaded model and trigger the cycling UI update.
        SetSportCombo(m.Sport);

        var spec = m.Cycling ?? (m.Sport == SportType.CyclingXC ? CyclingSpec.XCDefault() : CyclingSpec.RoadDefault());
        if (m.Sport != SportType.Running)
        {
            TxtCdA.Text = spec.CdA.ToString(System.Globalization.CultureInfo.InvariantCulture);
            TxtCrr.Text = spec.Crr.ToString(System.Globalization.CultureInfo.InvariantCulture);
            SportHint.Visibility = Visibility.Visible;
        }
        else
        {
            SportHint.Visibility = Visibility.Collapsed;
        }
    }

    private void SetSportCombo(SportType sport)
    {
        string tag = sport switch
        {
            SportType.CyclingRoad => "CyclingRoad",
            SportType.CyclingXC => "CyclingXC",
            _ => "Running",
        };
        foreach (System.Windows.Controls.ComboBoxItem item in CmbSport.Items)
            if (item.Tag as string == tag) { CmbSport.SelectedItem = item; break; }
    }

    private SportType SelectedSport() =>
        ((CmbSport.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string) switch
        {
            "CyclingRoad" => SportType.CyclingRoad,
            "CyclingXC" => SportType.CyclingXC,
            _ => SportType.Running,
        };

    private void CmbSport_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // When user picks sport manually (no model), reset model so physics-only path is used.
        if (_model is not null && _model.Sport != SelectedSport())
        {
            _model = null;
            ModelText.Text = Loc.Get("LblNoModel");
            SportHint.Visibility = Visibility.Collapsed;
        }
        UpdateCyclingVisibility();
        UpdateRunButtonState();
    }

    private void UpdateCyclingVisibility()
    {
        bool isCycling = SelectedSport() != SportType.Running;
        CyclingExpander.Visibility = isCycling ? Visibility.Visible : Visibility.Collapsed;
        ChkUsePhysics.Visibility = isCycling ? Visibility.Visible : Visibility.Collapsed;
        ChkLoad.Visibility = isCycling ? Visibility.Collapsed : Visibility.Visible;
        if (isCycling && string.IsNullOrWhiteSpace(TxtCdA.Text))
        {
            var def = SelectedSport() == SportType.CyclingXC ? CyclingSpec.XCDefault() : CyclingSpec.RoadDefault();
            TxtCdA.Text = def.CdA.ToString(System.Globalization.CultureInfo.InvariantCulture);
            TxtCrr.Text = def.Crr.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private void UpdateRunButtonState()
    {
        if (RunButton is null) return;
        bool modelLoaded = _model is not null;
        bool physicsReady = SelectedSport() != SportType.Running
            && ChkUsePhysics?.IsChecked == true
            && ParseNullableDouble(TxtFtp?.Text) is not null;
        RunButton.IsEnabled = modelLoaded || physicsReady;
    }

    private void ChkUsePhysics_Changed(object sender, RoutedEventArgs e) => UpdateRunButtonState();

    private void TxtFtp_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdateRunButtonState();

    /// <summary>Stub model for physics-only predictions (no fitted rides required).</summary>
    private RaceModel BuildPhysicsModel()
    {
        var sport = SelectedSport();
        double cdA = ParseNullableDouble(TxtCdA.Text) ?? (sport == SportType.CyclingXC ? 0.45 : 0.32);
        double crr = ParseNullableDouble(TxtCrr.Text) ?? (sport == SportType.CyclingXC ? 0.012 : 0.004);
        return new RaceModel
        {
            Sport = sport,
            Cycling = new CyclingSpec { CdA = cdA, Crr = crr },
        };
    }

    private static string DescribeModel(RaceModel m)
    {
        string src = m.Meta.SourceTracks.Count > 0 ? string.Join(", ", m.Meta.SourceTracks) : "unknown";
        string who = string.IsNullOrWhiteSpace(m.Meta.AthleteName) ? "" : m.Meta.AthleteName.Trim() + " · ";
        string sport = m.Sport switch
        {
            SportType.CyclingRoad => "Road bike · ",
            SportType.CyclingXC => "XC/MTB · ",
            _ => "",
        };
        string physicsHint = m.Cycling is CyclingSpec s
            ? $"CdA {s.CdA:F2} Crr {s.Crr:F4} · "
            : "";
        return $"{who}{sport}{physicsHint}Flat {m.AthleteBaseline.FlatSpeedMps * 3.6:F1} km/h · " +
               $"fatigue/{m.Fatigue.Driver} · from: {src}";
    }

    private void Run_Click(object sender, RoutedEventArgs e)
    {
        bool physicsOnly = _model is null;
        var modelToUse = _model ?? BuildPhysicsModel();
        if (!TryParseStart(TxtStart.Text, out DateTime start))
        {
            HintText.Text = "Start time must be HH:mm (e.g. 08:00).";
            return;
        }

        SaveUiToProfile();
        bool isCycling = SelectedSport() != SportType.Running;
        bool usePhysics = isCycling && ChkUsePhysics.IsChecked == true;
        CyclingSpec? physicsSpec = null;
        if (isCycling)
        {
            double cdA = ParseNullableDouble(TxtCdA.Text) ?? (modelToUse.Cycling?.CdA ?? 0.32);
            double crr = ParseNullableDouble(TxtCrr.Text) ?? (modelToUse.Cycling?.Crr ?? 0.004);
            physicsSpec = new CyclingSpec { CdA = cdA, Crr = crr };
        }

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
            UseLoadModel = ChkLoad.IsChecked == true && !isCycling,
            UsePhysics = usePhysics,
            PhysicsSpec = physicsSpec,
        };

        try
        {
            var result = RacePredictor.Predict(_target, modelToUse, options);
            PredictedTrack = result.PredictedTrack;   // held; only committed if the user clicks Add
            ReportText.Text = RaceFormatter.FormatPredictionReport(result, options, modelToUse);
            AddButton.IsEnabled = true;
            HintText.Text = string.Format(Loc.Get("RpHintPredicted"), result.DistanceKm, result.TotalTime);
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
        TxtFtp.Text = p.FtpW?.ToString(CultureInfo.InvariantCulture) ?? "";
        TxtBikeKg.Text = p.BikeKg?.ToString(CultureInfo.InvariantCulture) ?? "";
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
        p.FtpW = ParseNullableDouble(TxtFtp.Text);
        p.BikeKg = ParseNullableDouble(TxtBikeKg.Text);

        double? km = ParseNullableDouble(TxtRaceKm.Text);
        TimeSpan? time = ParseNullableTime(TxtRaceTime.Text);
        p.RecentRace = km is double d && d > 0 && time is TimeSpan t && t > TimeSpan.Zero
            ? new RecentRace { DistanceKm = d, Time = t }
            : null;

        _settings.Save();
    }

    private void CmbCdAPreset_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (CmbCdAPreset.SelectedItem is System.Windows.Controls.ComboBoxItem item && item.Tag is string tag)
            TxtCdA.Text = tag;
    }

    private void CmbCrrPreset_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (CmbCrrPreset.SelectedItem is System.Windows.Controls.ComboBoxItem item && item.Tag is string tag)
            TxtCrr.Text = tag;
    }

    private static double? ParseNullableDouble(string? s) =>
        double.TryParse(s?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v > 0 ? v : null;

    private static int? ParseNullableInt(string? s) =>
        int.TryParse(s?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v > 0 ? v : null;

    private static TimeSpan? ParseNullableTime(string? s) =>
        TimeSpan.TryParse(s?.Trim(), CultureInfo.InvariantCulture, out TimeSpan t) ? t : null;
}
