using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

using Microsoft.Win32;

using TrackEditor.Core.Models;
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
    private RaceModel? _model;

    /// <summary>The predicted copy the user chose to add; null until "Add Predicted Track" is pressed.</summary>
    public Track? PredictedTrack { get; private set; }

    public ApplyRaceModelWindow(Track target)
    {
        InitializeComponent();
        _target = target;
        TargetText.Text = $"Predict the race flow on “{target.Name}” ({target.Points.Count} pts) " +
                          "by applying a saved race model.";
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

        var options = new PredictOptions
        {
            StartTime = start,
            SurfaceMult = SelectedSurfaceMult(),
            UseAltitude = ChkAltitude.IsChecked == true,
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

    private double SelectedSurfaceMult() =>
        double.TryParse((CmbSurface.SelectedItem as ComboBoxItem)?.Tag as string,
            NumberStyles.Float, CultureInfo.InvariantCulture, out double m) ? m : 1.0;

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (PredictedTrack is null) return;
        DialogResult = true;
    }
}
