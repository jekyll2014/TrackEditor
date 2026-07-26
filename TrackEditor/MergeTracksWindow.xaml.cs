using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;

using TrackEditor.Core.Models;
using TrackEditor.Core.Services;

namespace TrackEditor;

/// <summary>
/// Fuses a second recording of the same route into the base (active) track: combines sensor channels and,
/// optionally, averages the two lines to reduce GPS noise. Produces a new track; the caller reads
/// <see cref="MergedTrack"/> after a true dialog result and inserts it into the document.
/// </summary>
public partial class MergeTracksWindow : Window
{
    private readonly Track _base;
    private readonly List<Track> _others;   // selectable partners, index-aligned to CmbOther

    /// <summary>The merged copy the user chose to add; null until "Add Merged Track" is pressed.</summary>
    public Track? MergedTrack { get; private set; }

    public MergeTracksWindow(Track baseTrack, IReadOnlyList<Track> allTracks)
    {
        InitializeComponent();
        _base = baseTrack;
        _others = allTracks.Where(t => !ReferenceEquals(t, baseTrack)).ToList();

        BaseText.Text = $"Base track: “{baseTrack.Name}” ({baseTrack.Points.Count} pts). " +
                        "Pick a second recording of the same route to fuse its fields in.";
        foreach (var t in _others)
            CmbOther.Items.Add($"{t.Name} ({t.Points.Count} pts)");
        if (_others.Count > 0) CmbOther.SelectedIndex = 0;
    }

    private void Run_Click(object sender, RoutedEventArgs e)
    {
        int oi = CmbOther.SelectedIndex;
        if (oi < 0 || oi >= _others.Count) { HintText.Text = "Select a track to merge with."; return; }

        var opt = new MergeOptions
        {
            Align = (MergeAlign)CmbAlign.SelectedIndex,
            Geometry = (MergeGeometry)CmbGeometry.SelectedIndex,
            PreferBaseOnConflict = ChkPreferBase.IsChecked == true,
            MaxMatchDistM = double.TryParse(TxtGate.Text?.Trim(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out double g) && g > 0 ? g : 60,
        };

        try
        {
            var result = TrackMerger.Merge(_base, _others[oi], opt);
            MergedTrack = result.Merged;   // held; only committed if the user clicks Add
            ReportText.Text = result.Report;
            AddButton.IsEnabled = result.Matched > 0;
            HintText.Text = result.Matched > 0
                ? $"{result.Coverage * 100:F0}% overlap, {result.FieldsGained.Count} channel(s) gained."
                : "No overlap found — check the tracks or raise the match gate.";
        }
        catch (Exception ex)
        {
            MergedTrack = null;
            AddButton.IsEnabled = false;
            ReportText.Text = "Merge failed: " + ex.Message;
            HintText.Text = "Merge failed.";
        }
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (MergedTrack is null) return;
        DialogResult = true;
    }
}
