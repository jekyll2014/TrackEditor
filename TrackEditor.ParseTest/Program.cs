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

bool anyFailed = false;
foreach (string racerDir in Directory.GetDirectories(samplesRoot).OrderBy(d => d))
{
    string racerName = Path.GetFileName(racerDir);
    string finishedDir = Path.Combine(racerDir, "races_finished");
    if (!Directory.Exists(finishedDir)) continue;

    Console.WriteLine($"\n{'=',0}{'=',0}{'=',0} {racerName.ToUpperInvariant()} {'=',0}{'=',0}{'=',0}");

    // --- load finished races ---
    var finished = LoadTracks(finishedDir);
    if (finished.Count == 0) { Console.WriteLine("  No finished races found."); continue; }

    Console.WriteLine("\nFinished races:");
    foreach (var (file, track) in finished)
    {
        var s = TrackStatistics.Compute(track.Points);
        bool hasPom = IsPomerania(file);
        Console.WriteLine($"  {(hasPom ? "[GT] " : "     ")}{Path.GetFileName(file),-42} {s.DistanceM/1000,5:F1} km  {s.Duration:hh\\:mm\\:ss}");
    }

    // --- ground-truth pomerania ---
    var (pomFile, pomTrack) = finished.FirstOrDefault(x => IsPomerania(x.file));
    TimeSpan? pomActual = pomTrack is not null ? TrackStatistics.Compute(pomTrack.Points).Duration : null;

    // --- prediction targets (root of racer folder) ---
    var targets = LoadTracks(racerDir);
    if (targets.Count == 0) { Console.WriteLine("  No prediction targets found."); continue; }

    // Find pomerania prediction target (route GPX without time)
    var pomTarget = targets.FirstOrDefault(x => IsPomerania(x.file)).track
                 ?? targets.FirstOrDefault().track;

    Console.WriteLine($"\nPrediction target: {Path.GetFileName(targets.FirstOrDefault(x => IsPomerania(x.file)).file ?? targets[0].file)}");
    if (pomActual.HasValue)
        Console.WriteLine($"Ground truth finish: {pomActual:hh\\:mm\\:ss}");

    // --- training subsets = all finished races except pomerania ---
    var trainPool = finished.Where(x => !IsPomerania(x.file)).ToList();
    if (trainPool.Count == 0)
    {
        Console.WriteLine("  No training races (all are pomerania). Skipping.");
        continue;
    }

    // Power-set of training pool
    var subsets = PowerSet(trainPool);

    // --- configs to test ---
    var configs = new (string Label, bool AutoCalib, double BinPct)[]
    {
        ("median/no-calib",  false, 0.5),
        ("median/auto-calib",true,  0.5),
        ("P90/no-calib",     false, 0.9),
        ("P90/auto-calib",   true,  0.9),
    };

    // Header
    Console.WriteLine();
    Console.Write($"  {"Subset",-38}");
    foreach (var cfg in configs)
        Console.Write($"  {cfg.Label,16}");
    if (pomActual.HasValue)
        Console.Write($"   {"err%",16}");
    Console.WriteLine();
    Console.WriteLine(new string('-', 38 + 2 + configs.Length * 18 + 6));

    var errors = configs.Select(_ => new List<double>()).ToArray();

    foreach (var subset in subsets)
    {
        string label = string.Join("+", subset.Select(x => ShortName(x.file)));
        Console.Write($"  {label,-38}");

        var predictions = new TimeSpan?[configs.Length];
        for (int ci = 0; ci < configs.Length; ci++)
        {
            var cfg = configs[ci];
            try
            {
                var analyzeOpt = new RaceAnalysisOptions { BinPercentile = cfg.BinPct };
                var result = RaceAnalyzer.Analyze(subset.Select(x => x.track), analyzeOpt);
                if (result.SegmentsUsed == 0) { Console.Write($"  {"no segs",16}"); continue; }

                var predictOpt = new PredictOptions
                {
                    AutoCalibrateByDistance = cfg.AutoCalib,
                    Effort = RaceEffort.Race,
                };
                var pred = RacePredictor.Predict(pomTarget!, result.Model, predictOpt);
                predictions[ci] = pred.TotalTime;
                Console.Write($"  {pred.TotalTime,16:hh\\:mm\\:ss}");
            }
            catch (Exception ex)
            {
                Console.Write($"  {("ERR:"+ex.Message[..Math.Min(10,ex.Message.Length)]),16}");
            }
        }

        if (pomActual.HasValue)
        {
            // Show error column for best config (auto-calib P90)
            var best = predictions[3] ?? predictions[1];
            if (best.HasValue)
            {
                double errPct = (best.Value.TotalSeconds - pomActual.Value.TotalSeconds)
                                / pomActual.Value.TotalSeconds * 100.0;
                Console.Write($"   {errPct,+7:F1}%");
                for (int ci = 0; ci < configs.Length; ci++)
                    if (predictions[ci].HasValue && pomActual.HasValue)
                        errors[ci].Add(Math.Abs((predictions[ci]!.Value.TotalSeconds - pomActual.Value.TotalSeconds)
                                                / pomActual.Value.TotalSeconds * 100.0));
            }
        }
        Console.WriteLine();
    }

    // Also run with ALL finished races (including pomerania in training — labeled as cheating)
    if (pomTrack is not null)
    {
        Console.Write($"  {"[ALL incl. pomerania]",-38}");
        for (int ci = 0; ci < configs.Length; ci++)
        {
            var cfg = configs[ci];
            try
            {
                var analyzeOpt = new RaceAnalysisOptions { BinPercentile = cfg.BinPct };
                var result = RaceAnalyzer.Analyze(finished.Select(x => x.track), analyzeOpt);
                var predictOpt = new PredictOptions { AutoCalibrateByDistance = cfg.AutoCalib, Effort = RaceEffort.Race };
                var pred = RacePredictor.Predict(pomTarget!, result.Model, predictOpt);
                Console.Write($"  {pred.TotalTime,16:hh\\:mm\\:ss}");
            }
            catch { Console.Write($"  {"ERR",16}"); }
        }
        if (pomActual.HasValue) Console.Write($"   {"(cheating)",16}");
        Console.WriteLine();
    }

    // Summary stats
    if (pomActual.HasValue && errors.Any(e => e.Count > 0))
    {
        Console.WriteLine();
        Console.WriteLine("  Error summary (abs %, lower=better):");
        for (int ci = 0; ci < configs.Length; ci++)
        {
            if (errors[ci].Count == 0) continue;
            double mean = errors[ci].Average();
            double stdDev = Math.Sqrt(errors[ci].Average(e => (e - mean) * (e - mean)));
            double best = errors[ci].Min();
            Console.WriteLine($"    {configs[ci].Label,-18}  mean={mean,5:F1}%  stddev={stdDev,4:F1}%  best={best,4:F1}%  (n={errors[ci].Count})");
        }
    }
}

Console.WriteLine(anyFailed ? "\nSome tests FAILED." : "\nDone.");
return anyFailed ? 1 : 0;

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
            // Pick the track with the most points (main activity, not warmup)
            var best = tracks.OrderByDescending(t => t.Points.Count).FirstOrDefault();
            if (best is not null) result.Add((file, best));
        }
        catch { /* skip unreadable */ }
    }
    return result;
}

static bool IsPomerania(string? path) =>
    path is not null && Path.GetFileName(path).Contains("pomerania", StringComparison.OrdinalIgnoreCase);

static string ShortName(string path)
{
    string n = Path.GetFileNameWithoutExtension(path);
    // strip racer prefix "andrey_" / "olga_"
    int us = n.IndexOf('_');
    if (us >= 0 && us < 8) n = n[(us + 1)..];
    // truncate
    return n.Length > 14 ? n[..14] : n;
}

static List<List<(string file, Track track)>> PowerSet(List<(string file, Track track)> items)
{
    var result = new List<List<(string, Track)>>();
    int n = items.Count;
    // limit to 2^n subsets; n <= ~5 so max 31 non-empty subsets
    for (int mask = 1; mask < (1 << n); mask++)
    {
        var subset = new List<(string, Track)>();
        for (int i = 0; i < n; i++)
            if ((mask & (1 << i)) != 0) subset.Add(items[i]);
        result.Add(subset);
    }
    return result;
}
