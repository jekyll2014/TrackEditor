using System.Globalization;
using System.IO;
using TrackEditor.Services;

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
            Console.WriteLine($"    '{t.Name}': {t.Points.Count} pts, {stats.DistanceM / 1000:F2} km, " +
                              $"ele={(hasEle ? $"{stats.AscentM:F0}m up/{stats.DescentM:F0}m down" : "no")}, " +
                              $"time={(hasTime ? stats.Duration?.ToString() ?? "partial" : "no")}");

            // exercise simplify + speeds on the fly
            var keep = GeoMath.DouglasPeucker(t.Points, 10);
            var speeds = GeoMath.SpeedsMps(t.Points);
            Console.WriteLine($"    simplify(10m): {t.Points.Count} -> {keep.Count}; " +
                              $"speed pts: {speeds.Count(s => s is not null)}");
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
