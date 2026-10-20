using System.Globalization;
using System.IO;
using TrackEditor.Core.Services;
using TrackEditor.Core.Services.RaceAnalysis;
using TrackEditor.Core.Models;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

string samplesRoot = args.Length > 0 ? args[0]
    : Path.Combine("..", "..", "..", "..", ".SAMPLES");

if (!Directory.Exists(samplesRoot))
{
    Console.Error.WriteLine($"Samples folder not found: {Path.GetFullPath(samplesRoot)}");
    return 1;
}

var configs = new (string Label, bool AutoCalib)[]
{
    ("no-calib",   false),
    ("auto-calib", true),
};

foreach (string racerDir in Directory.GetDirectories(samplesRoot).OrderBy(d => d))
{
    string racerName = Path.GetFileName(racerDir);
    string finishedDir = Path.Combine(racerDir, "races_finished");
    if (!Directory.Exists(finishedDir)) continue;

    Console.WriteLine($"\n{'=',0}{'=',0}{'=',0} {racerName.ToUpperInvariant()} {'=',0}{'=',0}{'=',0}");

    var finished = LoadTracks(finishedDir);
    if (finished.Count == 0) { Console.WriteLine("  No finished races found."); continue; }

    Console.WriteLine("\nFinished races:");
    foreach (var (file, track) in finished)
    {
        var s = TrackStatistics.Compute(track.Points);
        Console.WriteLine($"  {Path.GetFileName(file),-44} {s.DistanceM/1000,5:F1} km  {s.Duration:hh\\:mm\\:ss}  " +
                          $"flat {RaceAnalyzer.Analyze(new[]{track}).Model.AthleteBaseline.FlatSpeedMps*3.6:F1} km/h");
    }

    // For each prediction target, find its matched finished race (ground truth)
    var targets = LoadTracks(racerDir);
    if (targets.Count == 0) { Console.WriteLine("  No prediction targets found."); continue; }

    foreach (var (targetFile, targetTrack) in targets)
    {
        // Try to find a ground-truth finished race for this target
        var gtMatch = BestMatch(targetFile, finished);
        bool hasGt = gtMatch.HasValue;
        var trainPool = hasGt
            ? finished.Where(x => x.file != gtMatch!.Value.file).ToList()
            : finished;

        var targetStats = TrackStatistics.Compute(targetTrack.Points);
        Console.WriteLine($"\n--- Target: {Path.GetFileName(targetFile)}  ({targetStats.DistanceM/1000:F1} km) ---");

        if (hasGt)
        {
            var gtStats = TrackStatistics.Compute(gtMatch!.Value.track.Points);
            Console.WriteLine($"    Ground truth: {Path.GetFileName(gtMatch.Value.file)}  actual finish {gtStats.Duration:hh\\:mm\\:ss}");
        }
        else
        {
            Console.WriteLine("    (no ground truth found — prediction only)");
        }

        if (trainPool.Count == 0) { Console.WriteLine("    No training races available."); continue; }

        // Riegel reference times from each finished race (terrain-blind sanity check)
        if (hasGt)
        {
            Console.WriteLine("    Riegel (flat, terrain-blind) from each finished race:");
            foreach (var (ff, ft) in trainPool)
            {
                var fs = TrackStatistics.Compute(ft.Points);
                if (fs.Duration is null || fs.Duration == TimeSpan.Zero) continue;
                var rr = new RecentRace { DistanceKm = fs.DistanceM / 1000.0, Time = fs.Duration!.Value };
                var riegelTime = EnduranceCalibration.RiegelTime(rr, targetStats.DistanceM / 1000.0);
                Console.WriteLine($"      from {ShortName(ff),-20}  → {riegelTime:hh\\:mm\\:ss}");
            }
        }

        // Subset analysis
        var subsets = PowerSet(trainPool);
        var errors = configs.Select(_ => new List<double>()).ToArray();

        Console.WriteLine();
        Console.Write($"  {"Subset",-42}");
        foreach (var cfg in configs)
            Console.Write($"  {cfg.Label,12}");
        if (hasGt) Console.Write($"  {"err(auto)",10}");
        Console.WriteLine();
        Console.WriteLine(new string('-', 42 + 2 + configs.Length * 14 + (hasGt ? 12 : 0)));

        foreach (var subset in subsets)
        {
            // Skip huge subsets to keep output manageable; always include single-track and full pool
            if (subset.Count > 3 && subset.Count < trainPool.Count) continue;

            string label = subset.Count == trainPool.Count
                ? "[ALL training]"
                : string.Join("+", subset.Select(x => ShortName(x.file)));
            Console.Write($"  {label,-42}");

            var preds = new TimeSpan?[configs.Length];
            for (int ci = 0; ci < configs.Length; ci++)
            {
                try
                {
                    var res = RaceAnalyzer.Analyze(subset.Select(x => x.track));
                    if (res.SegmentsUsed == 0) { Console.Write($"  {"no segs",12}"); continue; }

                    var opt = new PredictOptions
                    {
                        AutoCalibrateByDistance = configs[ci].AutoCalib,
                        Effort = RaceEffort.Race,
                    };
                    var pred = RacePredictor.Predict(targetTrack, res.Model, opt);
                    preds[ci] = pred.TotalTime;
                    Console.Write($"  {pred.TotalTime,12:hh\\:mm\\:ss}");
                }
                catch (Exception ex)
                {
                    Console.Write($"  {"ERR:"+ex.Message[..Math.Min(8,ex.Message.Length)],12}");
                }
            }

            if (hasGt)
            {
                var gtDur = TrackStatistics.Compute(gtMatch!.Value.track.Points).Duration;
                var autoTime = preds[1] ?? preds[0];
                if (autoTime.HasValue && gtDur.HasValue)
                {
                    double err = (autoTime.Value.TotalSeconds - gtDur.Value.TotalSeconds)
                                 / gtDur.Value.TotalSeconds * 100.0;
                    Console.Write($"  {err,+9:F1}%");
                    errors[1].Add(Math.Abs(err));
                    if (preds[0].HasValue)
                        errors[0].Add(Math.Abs((preds[0]!.Value.TotalSeconds - gtDur.Value.TotalSeconds)
                                               / gtDur.Value.TotalSeconds * 100.0));
                }
            }
            Console.WriteLine();
        }

        if (hasGt && errors.Any(e => e.Count > 0))
        {
            Console.WriteLine();
            Console.WriteLine("  Summary (abs %, lower=better):");
            for (int ci = 0; ci < configs.Length; ci++)
            {
                if (errors[ci].Count == 0) continue;
                double mean = errors[ci].Average();
                double std = Math.Sqrt(errors[ci].Average(e => (e-mean)*(e-mean)));
                Console.WriteLine($"    {configs[ci].Label,-12}  mean={mean,5:F1}%  stddev={std,4:F1}%  best={errors[ci].Min(),4:F1}%");
            }
        }
    }
}

Console.WriteLine("\nDone.");
return 0;

// ---- helpers ----

static List<(string file, Track track)> LoadTracks(string dir)
{
    var result = new List<(string, Track)>();
    foreach (string file in Directory.GetFiles(dir).OrderBy(f => f))
    {
        string ext = Path.GetExtension(file).ToLowerInvariant();
        if (ext is not (".gpx" or ".fit" or ".tcx")) continue;
        try
        {
            var tracks = ext switch
            {
                ".gpx" => GpxIo.Load(file),
                ".fit" => FitIo.Load(file),
                _      => TcxIo.Load(file),
            };
            var best = tracks.OrderByDescending(t => t.Points.Count).FirstOrDefault();
            if (best is not null) result.Add((file, best));
        }
        catch { }
    }
    return result;
}

/// <summary>Token overlap match: find the finished race whose filename shares the most keywords with the target.</summary>
static (string file, Track track)? BestMatch(string targetFile, List<(string file, Track track)> pool)
{
    var targetTokens = Tokens(targetFile);
    int bestScore = 1;  // require at least 2 overlapping tokens
    (string file, Track track)? best = null;
    foreach (var candidate in pool)
    {
        var candTokens = Tokens(candidate.file);
        if (!HasMeaningfulOverlap(targetTokens, candTokens)) continue;
        int score = targetTokens.Intersect(candTokens).Count();
        if (score > bestScore) { bestScore = score; best = candidate; }
    }
    return best;
}

static HashSet<string> Tokens(string path)
{
    string name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
    var raw = System.Text.RegularExpressions.Regex.Split(name, @"[^a-z0-9]+")
        .Where(t => t.Length >= 2);
    // Normalize "21k"→"21", "65k"→"65" etc. so numeric distances compare across naming styles.
    var tokens = new HashSet<string>(raw.Select(t =>
        System.Text.RegularExpressions.Regex.IsMatch(t, @"^\d+[a-z]+$")
            ? System.Text.RegularExpressions.Regex.Match(t, @"^\d+").Value
            : t));
    return tokens;
}

static bool HasMeaningfulOverlap(HashSet<string> a, HashSet<string> b)
{
    // At least one shared token must start with a letter (race name, not just a year/distance).
    return a.Intersect(b).Any(t => char.IsLetter(t[0]));
}

static string ShortName(string path)
{
    string n = Path.GetFileNameWithoutExtension(path);
    int us = n.IndexOf('_');
    if (us >= 0 && us < 8) n = n[(us + 1)..];
    return n.Length > 18 ? n[..18] : n;
}

static List<List<(string file, Track track)>> PowerSet(List<(string file, Track track)> items)
{
    var result = new List<List<(string, Track)>>();
    int n = items.Count;
    for (int mask = 1; mask < (1 << n); mask++)
    {
        var s = new List<(string, Track)>();
        for (int i = 0; i < n; i++)
            if ((mask & (1 << i)) != 0) s.Add(items[i]);
        result.Add(s);
    }
    return result;
}
