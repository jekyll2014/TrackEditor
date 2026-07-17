using System.IO;
using System.Text;
using System.Windows;
using TrackEditor.Models;
using TrackEditor.Services;

namespace TrackEditor;

public partial class TrackInfoWindow : Window
{
    public TrackInfoWindow(Track track)
    {
        InitializeComponent();
        InfoText.Text = BuildInfo(track);
    }

    private static string BuildInfo(Track track)
    {
        var sb = new StringBuilder();

        sb.AppendLine("— File —");
        sb.AppendLine($"Name:            {track.Name}{(track.IsModified ? "  (modified)" : "")}");
        if (!string.IsNullOrEmpty(track.SourceFile))
        {
            sb.AppendLine($"Source:          {track.SourceFile}");
            try
            {
                var fi = new FileInfo(track.SourceFile);
                if (fi.Exists)
                {
                    sb.AppendLine($"File size:       {fi.Length / 1024.0:F1} KB");
                    sb.AppendLine($"File modified:   {fi.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
                }
                else sb.AppendLine("File:            (no longer on disk)");
            }
            catch { /* path not accessible */ }
        }
        else sb.AppendLine("Source:          (not saved to a file)");

        bool hasTime = track.Points.Any(p => p.Time is not null);
        bool hasEle = track.Points.Any(p => p.Ele is not null);
        sb.AppendLine();
        sb.AppendLine("— Content —");
        sb.AppendLine($"Points:          {track.Points.Count}");
        sb.AppendLine($"Elevation:       {(hasEle ? (track.ElevationEstimated ? "yes (estimated)" : "yes (recorded)") : "none")}");
        sb.AppendLine($"Timestamps:      {(hasTime ? "yes" : "none")}");
        sb.AppendLine($"Color / width:   {track.ColorHex} / {track.Width:0.#}");

        sb.AppendLine();
        sb.AppendLine("— Statistics —");
        if (track.Points.Count >= 2)
            sb.AppendLine(TrackStatistics.Compute(track.Points).ToDisplayString());
        else
            sb.AppendLine("(need at least 2 points)");

        return sb.ToString().TrimEnd();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
