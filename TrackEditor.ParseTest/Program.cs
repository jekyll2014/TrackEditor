using System.Globalization;
using System.IO;

using TrackEditor.Core.Services;
using TrackEditor.Core.Services.RaceAnalysis;

// Headless sanity check: parse every sample file and print track/point counts + statistics.
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

string dir = args.Length > 0 ? args[0] : Path.Combine("..", "gpx_samples");
if (!Directory.Exists(dir))
{
    Console.Error.WriteLine($"Sample folder not found: {Path.GetFullPath(dir)}");
    return 1;
}

int failures = 0;
foreach (string file in Directory.GetFiles(dir).OrderBy(f => f))
{
    string ext = Path.GetExtension(file).ToLowerInvariant();
    if (ext is not (".gpx" or ".kml" or ".kmz")) continue;
    try
    {
        var tracks = ext == ".gpx" ? GpxIo.Load(file) : KmlIo.Load(file);
        Console.WriteLine($"=== {Path.GetFileName(file)}: {tracks.Count} track(s)");
        foreach (var t in tracks)
        {
            var stats = TrackStatistics.Compute(t.Points);
            bool hasTime = t.Points.Any(p => p.Time is not null);
            bool hasEle = t.Points.Any(p => p.Ele is not null);
            int hrN = t.Points.Count(p => p.Hr is not null);
            int cadN = t.Points.Count(p => p.Cad is not null);
            int tempN = t.Points.Count(p => p.Temp is not null);
            Console.WriteLine($"    '{t.Name}': {t.Points.Count} pts, {stats.DistanceM / 1000:F2} km, " +
                              $"ele={(hasEle ? $"{stats.AscentM:F0}m up/{stats.DescentM:F0}m down" : "no")}, " +
                              $"time={(hasTime ? stats.Duration?.ToString() ?? "partial" : "no")}");
            Console.WriteLine($"    sensors: hr={hrN}, cad={cadN}, temp={tempN}");

            // exercise simplify + speeds on the fly
            var keep = GeoMath.DouglasPeucker(t.Points, 10);
            var speeds = GeoMath.SpeedsMps(t.Points);
            Console.WriteLine($"    simplify(10m): {t.Points.Count} -> {keep.Count}; " +
                              $"speed pts: {speeds.Count(s => s is not null)}");

            // Race Analysis P1: resample to a fixed grid + clean HR
            var rs = TrackResampler.Resample(t.Points);
            var span = rs.Count > 1 ? GeoMath.CumulativeDistancesM(rs)[^1] / (rs.Count - 1) : 0;
            Console.WriteLine($"    resample(5m): {t.Points.Count} -> {rs.Count} pts, mean spacing {span:F2} m");
            if (hrN > 0)
            {
                var cleaned = SignalCleaning.CleanHr(t.Points);
                int dropped = Enumerable.Range(0, t.Points.Count).Count(i => t.Points[i].Hr is not null && cleaned[i] is null);
                Console.WriteLine($"    hr clean: {cleaned.Count(h => h is not null)} valid, {dropped} implausible dropped");
            }

            // Race Analysis P2: fit a model from this (timed) track and round-trip its JSON
            if (hasTime)
            {
                var res = RaceAnalyzer.Analyze(new[] { t });
                foreach (var line in res.Report.Split('\n'))
                    Console.WriteLine("    | " + line.TrimEnd());
                var round = RaceModel.FromJson(res.Model.ToJson());
                Console.WriteLine($"    | json round-trip: {round.BaseCurve.SpeedMps.Length} curve bins, " +
                                  $"flat {round.AthleteBaseline.FlatSpeedMps * 3.6:F1} km/h");
            }
        }
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine($"=== {Path.GetFileName(file)}: FAILED - {ex.Message}");
    }
}

Console.WriteLine(failures == 0 ? "ALL OK" : $"{failures} FAILURES");
return failures;
