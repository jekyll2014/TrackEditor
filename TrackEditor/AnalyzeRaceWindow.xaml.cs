using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

using Microsoft.Win32;

using TrackEditor.Core.Models;
using TrackEditor.Core.Services;
using TrackEditor.Core.Services.RaceAnalysis;

namespace TrackEditor;

/// <summary>
/// Fits a <see cref="RaceModel"/> from one or more recorded (timestamped) tracks and lets the user export it
/// as <c>*.racemodel.json</c>. Presents every loaded track with an include checkbox — only timestamped tracks
/// can be used — plus the signals to use and the fatigue driver.
/// </summary>
public partial class AnalyzeRaceWindow : Window
{
    /// <summary>One row in the track picker: the track, whether it can be used, and its include state.</summary>
    private sealed class TrackPick
    {
        public required Track T { get; init; }
        public required string Label { get; init; }
        public bool CanUse { get; init; }
        public bool Include { get; set; }
    }

    private readonly List<TrackPick> _picks;
    private RaceModel? _model;

    /// <summary>The fitted model, or null until Analyze succeeds. Lets a caller (e.g. Apply Race Model's
    /// "Create profile") adopt the result directly without a file round-trip.</summary>
    public RaceModel? Model => _model;

    /// <param name="target">Optional prediction target. When given, recorded tracks that are a good analog of
    /// it (terrain / load / distance) are marked ★ and pre-ticked, so the fit uses like-for-like efforts.</param>
    public AnalyzeRaceWindow(IEnumerable<Track> tracks, Track? target = null)
    {
        InitializeComponent();

        TrackClass? tc = target is { Points.Count: >= 2 } ? TrackClassifier.Classify(target.Points) : null;

        _picks = tracks.Select(t =>
        {
            bool timed = t.Points.Any(p => p.Time is not null);
            bool analog = tc is not null && !ReferenceEquals(t, target)
                          && TrackClassifier.IsAnalog(tc, TrackClassifier.Classify(t.Points));
            string label = DescribeTrack(t, timed) + (analog ? "   ★ similar to target" : "");
            // With a target, pre-tick only its analogs; without one, keep the old "all timestamped" default.
            return new TrackPick { T = t, CanUse = timed, Include = timed && (tc is null || analog), Label = label };
        }).ToList();
        TracksList.ItemsSource = _picks;

        if (tc is not null)
            IntroText.Text = $"Fitting a profile to predict “{target!.Name}”. ★ marks recorded tracks similar to " +
                             "it (terrain, load, distance); those are pre-ticked — adjust as you like.";
    }

    private static string DescribeTrack(Track t, bool timed)
    {
        var sig = new List<string>();
        if (t.Points.Any(p => p.Ele is not null)) sig.Add("ele");
        if (t.Points.Any(p => p.Hr is not null)) sig.Add("hr");
        if (t.Points.Any(p => p.Cad is not null)) sig.Add("cad");
        if (t.Points.Any(p => p.Temp is not null)) sig.Add("temp");
        var cum = GeoMath.CumulativeDistancesM(t.Points);
        double km = cum.Length > 0 ? cum[^1] / 1000.0 : 0;
        string signals = sig.Count > 0 ? string.Join(", ", sig) : "none";
        return $"{t.Name} — {km:F1} km, {t.Points.Count} pts · signals: {signals}"
             + (timed ? "" : "  ·  no timestamps (cannot use)");
    }

    private void Run_Click(object sender, RoutedEventArgs e)
    {
        var chosen = _picks.Where(p => p.Include && p.CanUse).Select(p => p.T).ToList();
        if (chosen.Count == 0)
        {
            HintText.Text = "Tick at least one timestamped track.";
            return;
        }

        var options = new RaceAnalysisOptions
        {
            UseHr = ChkUseHr.IsChecked == true,
            NormalizePerTrack = ChkNormalize.IsChecked == true,
            Driver = SelectedDriver(),
        };

        try
        {
            var result = RaceAnalyzer.Analyze(chosen, options);
            _model = result.Model;
            ReportText.Text = result.Report;
            ExportButton.IsEnabled = true;
            HintText.Text = $"Fitted from {result.TracksUsed} track(s), {result.SegmentsUsed} segments.";
        }
        catch (System.Exception ex)
        {
            _model = null;
            ExportButton.IsEnabled = false;
            ReportText.Text = "Analysis failed: " + ex.Message;
            HintText.Text = "Analysis failed.";
        }
    }

    private FatigueDriver SelectedDriver() =>
        ((CmbDriver.SelectedItem as ComboBoxItem)?.Tag as string) switch
        {
            "Elapsed" => FatigueDriver.Elapsed,
            "Distance" => FatigueDriver.Distance,
            _ => FatigueDriver.CumAscent,
        };

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_model is null) return;
        var dlg = new SaveFileDialog
        {
            Title = "Export race model",
            Filter = "Race model (*.racemodel.json)|*.racemodel.json|JSON|*.json|All files|*.*",
            FileName = "athlete.racemodel.json",
            AddExtension = true,
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            _model.Save(dlg.FileName);
            HintText.Text = "Model exported.";
        }
        catch (System.Exception ex)
        {
            MessageBox.Show(this, "Could not save the model:\n" + ex.Message, "Export failed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
