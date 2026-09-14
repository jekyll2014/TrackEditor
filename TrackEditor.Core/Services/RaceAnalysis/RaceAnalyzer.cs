using TrackEditor.Core.Models;

namespace TrackEditor.Core.Services.RaceAnalysis;

/// <summary>Knobs for a fit run; sensible defaults match the locked plan (HR on, cumulative-ascent fatigue,
/// per-track intensity normalization).</summary>
public class RaceAnalysisOptions
{
    public bool UseHr { get; set; } = true;
    /// <summary>Optional athlete name recorded in the model's metadata to identify whose ability it describes.</summary>
    public string? AthleteName { get; set; }
    public SportType Sport { get; set; } = SportType.Running;
    public FatigueDriver Driver { get; set; } = FatigueDriver.CumAscent;
    public bool NormalizePerTrack { get; set; } = true;
    public double SpacingM { get; set; } = TrackResampler.DefaultSpacingM;
    public double EleWindowM { get; set; } = TrackResampler.DefaultEleWindowM;
    public double GradeMinDeg { get; set; } = -25;
    public double GradeMaxDeg { get; set; } = 25;
    public int MinBinSamples { get; set; } = 15;
    public double FatigueFloor { get; set; } = 0.5;
}

/// <summary>Outcome of a fit: the model and the raw counts behind it.</summary>
public class AnalysisResult
{
    public RaceModel Model { get; set; } = new();
    public int TracksUsed { get; set; }
    public int SegmentsUsed { get; set; }
    /// <summary>Maximum accumulated effort value across all fitted segments (used by WPF to format the fatigue line).</summary>
    public double MaxEffort { get; set; }
}

/// <summary>
/// Fits a <see cref="RaceModel"/> from one or more recorded tracks. Pipeline per track: clean HR -> resample
/// to a fixed grid -> derive per-segment (grade, speed, effort, HR) -> drop non-moving (aid-station) segments
/// -> normalize speed to the track's own flat-ground baseline. Pooled segments then yield a grade->speed base
/// curve (robust per-degree medians, gap-filled and smoothed) and a fatigue decay coefficient regressed from
/// the residual speed vs accumulated effort. Separating the two means each recorded track fills the whole
/// grade curve while its fatigue is read from how speed fades over the effort.
/// </summary>
public static class RaceAnalyzer
{
    private readonly struct Seg
    {
        public readonly double GradeDeg, SpeedRel, Effort, Turn;
        public readonly double? Hr;
        public Seg(double g, double s, double e, double turn, double? hr)
        { GradeDeg = g; SpeedRel = s; Effort = e; Turn = turn; Hr = hr; }
    }

    public static AnalysisResult Analyze(IEnumerable<Track> tracks, RaceAnalysisOptions? options = null)
    {
        var opt = options ?? new RaceAnalysisOptions();
        var segs = new List<Seg>();
        var flatRefs = new List<double>();
        var names = new List<string>();
        double totalKm = 0;
        bool anyHr = false, anyCad = false;

        foreach (var track in tracks)
        {
            var pts = track.Points;
            if (pts.Count < 2 || pts.All(p => p.Time is null)) continue;   // need time to measure speed

            // Clean HR onto a working copy, then resample so grade/speed/HR share one even grid.
            var work = pts.Select(p => p.Clone()).ToList();
            bool hasHr = opt.UseHr && pts.Any(p => p.Hr is not null);
            if (hasHr)
            {
                var clean = SignalCleaning.CleanHr(pts);
                for (int i = 0; i < work.Count; i++) work[i].Hr = clean[i];
                anyHr = true;
            }
            if (pts.Any(p => p.Cad is not null)) anyCad = true;

            var rs = TrackResampler.Resample(work, opt.SpacingM, opt.EleWindowM);
            if (rs.Count < 3) continue;

            var cum = GeoMath.CumulativeDistancesM(rs);
            totalKm += cum[^1] / 1000.0;
            names.Add(track.Name);
            var turn = TurnMetrics.PerSegmentDegPerM(rs, cum);

            // Per-segment features on the resampled grid.
            var raw = new List<(double Grade, double Speed, double CumAsc, double Elapsed, double Dist, double Turn, double? Hr)>();
            double cumAsc = 0;
            DateTime t0 = rs.First(p => p.Time is not null).Time!.Value;
            for (int i = 1; i < rs.Count; i++)
            {
                double dDist = cum[i] - cum[i - 1];
                if (dDist <= 0.01) continue;
                if (rs[i - 1].Ele is double e0 && rs[i].Ele is double e1 && e1 > e0) cumAsc += e1 - e0;
                if (rs[i - 1].Time is not DateTime ta || rs[i].Time is not DateTime tb) continue;
                double dt = (tb - ta).TotalSeconds;
                if (dt <= 0) continue;

                double v = dDist / dt;
                bool moving = v > TrackStatistics.MovingSpeedMps && (rs[i].Cad is null || rs[i].Cad > 0);
                if (!moving) continue;   // drop aid-station / standing segments

                double gradeDeg = Math.Atan2((rs[i].Ele ?? 0) - (rs[i - 1].Ele ?? 0), dDist) * 180 / Math.PI;
                double? hr = (rs[i - 1].Hr is int h0 && rs[i].Hr is int h1) ? (h0 + h1) / 2.0 : rs[i].Hr;
                raw.Add((gradeDeg, v, cumAsc, (tb - t0).TotalSeconds, cum[i], turn[i], hr));
            }
            if (raw.Count == 0) continue;

            // Intensity anchor = median moving speed near flat; normalize this track to it.
            var flat = raw.Where(r => Math.Abs(r.Grade) < 1.5).Select(r => r.Speed).ToList();
            double flatRef = Median(flat.Count > 0 ? flat : raw.Select(r => r.Speed).ToList());
            if (flatRef <= 0) continue;
            flatRefs.Add(flatRef);

            double norm = opt.NormalizePerTrack ? flatRef : 1.0;
            foreach (var r in raw)
            {
                double effort = opt.Driver switch
                {
                    FatigueDriver.Elapsed => r.Elapsed,
                    FatigueDriver.Distance => r.Dist,
                    _ => r.CumAsc,
                };
                segs.Add(new Seg(Math.Clamp(r.Grade, opt.GradeMinDeg, opt.GradeMaxDeg), r.Speed / norm, effort, r.Turn, r.Hr));
            }
        }

        if (segs.Count == 0)
            return new AnalysisResult();

        double athleteFlat = opt.NormalizePerTrack ? Mean(flatRefs) : Median(segs.Select(s => s.SpeedRel).ToList());

        // --- base curve: per-degree robust central speed, gap-filled + smoothed (in normalized space) ---
        int bins = (int)Math.Round((opt.GradeMaxDeg - opt.GradeMinDeg) / 1.0) + 1;
        var relCurve = FitBaseCurve(segs, opt.GradeMinDeg, bins, opt.MinBinSamples, opt.NormalizePerTrack ? 1.0 : athleteFlat);
        var relLookup = new BaseCurve { GradeMinDeg = opt.GradeMinDeg, StepDeg = 1, SpeedMps = relCurve };

        // --- fatigue: residual speed / base(grade) regressed against accumulated effort ---
        double k = FitFatigueK(segs, relLookup);
        double? hrDrift = null, refHr = null;
        if (anyHr)
        {
            var hrSegs = segs.Where(s => s.Hr is not null).Select(s => (s.Effort, s.Hr!.Value)).ToList();
            if (hrSegs.Count > 10)
            {
                hrDrift = Slope(hrSegs.Select(x => x.Effort).ToList(), hrSegs.Select(x => x.Item2).ToList());
                refHr = Median(hrSegs.Select(x => x.Item2).ToList());
            }
        }

        var fatigue = new FatigueSpec
        {
            Driver = opt.Driver,
            Shape = FatigueShape.Linear,
            K = k,
            Floor = opt.FatigueFloor,
            HrDriftPerUnit = hrDrift,
        };

        // --- turn penalty: residual after grade+fatigue regressed against turn density ---
        var turnSpec = FitTurn(segs, relLookup, fatigue);

        double scale = opt.NormalizePerTrack ? athleteFlat : 1.0;
        var model = new RaceModel
        {
            Sport = opt.Sport,
            // Cycling models carry physics defaults that the user can override per-prediction in the UI.
            Cycling = opt.Sport == SportType.CyclingXC ? CyclingSpec.XCDefault()
                    : opt.Sport == SportType.CyclingRoad ? CyclingSpec.RoadDefault()
                    : null,
            BaseCurve = new BaseCurve
            {
                GradeMinDeg = opt.GradeMinDeg,
                StepDeg = 1,
                SpeedMps = relCurve.Select(v => v * scale).ToArray(),
            },
            Fatigue = fatigue,
            Turn = turnSpec,
            Altitude = new AltitudeSpec { DeratePerKm = 0 },
            AthleteBaseline = new AthleteBaseline { FlatSpeedMps = athleteFlat, RefHr = refHr },
            Meta = new RaceModelMeta
            {
                AthleteName = string.IsNullOrWhiteSpace(opt.AthleteName) ? null : opt.AthleteName.Trim(),
                SourceTracks = names,
                TotalKm = totalKm,
                FitDateUtc = DateTime.UtcNow,
                SegmentsUsed = segs.Count,
                SignalsUsed = BuildSignalList(anyHr, anyCad),
            },
        };

        return new AnalysisResult
        {
            Model = model,
            TracksUsed = names.Count,
            SegmentsUsed = segs.Count,
            MaxEffort = segs.Max(s => s.Effort),
        };
    }

    private static double[] FitBaseCurve(List<Seg> segs, double gradeMin, int bins, int minSamples, double fallback)
    {
        var buckets = new List<double>[bins];
        for (int b = 0; b < bins; b++) buckets[b] = new List<double>();
        foreach (var s in segs)
        {
            int b = (int)Math.Round(s.GradeDeg - gradeMin);
            if (b >= 0 && b < bins) buckets[b].Add(s.SpeedRel);
        }

        var curve = new double?[bins];
        for (int b = 0; b < bins; b++)
            if (buckets[b].Count >= minSamples) curve[b] = Median(buckets[b]);

        // Fill gaps by linear interpolation between known bins; clamp the ends to the nearest known value.
        FillGaps(curve, fallback);

        // Light 3-wide smoothing.
        var outp = new double[bins];
        for (int b = 0; b < bins; b++)
        {
            double sum = 0; int c = 0;
            for (int j = Math.Max(0, b - 1); j <= Math.Min(bins - 1, b + 1); j++) { sum += curve[j]!.Value; c++; }
            outp[b] = sum / c;
        }
        return outp;
    }

    private static void FillGaps(double?[] curve, double fallback)
    {
        int n = curve.Length;
        int firstKnown = -1, lastKnown = -1;
        for (int i = 0; i < n; i++) if (curve[i] is not null) { if (firstKnown < 0) firstKnown = i; lastKnown = i; }
        if (firstKnown < 0) { for (int i = 0; i < n; i++) curve[i] = fallback; return; }
        for (int i = 0; i < firstKnown; i++) curve[i] = curve[firstKnown];
        for (int i = lastKnown + 1; i < n; i++) curve[i] = curve[lastKnown];
        int prev = firstKnown;
        for (int i = firstKnown + 1; i <= lastKnown; i++)
        {
            if (curve[i] is null) continue;
            if (i - prev > 1)
                for (int j = prev + 1; j < i; j++)
                    curve[j] = curve[prev]!.Value + (curve[i]!.Value - curve[prev]!.Value) * (j - prev) / (i - prev);
            prev = i;
        }
    }

    private static double FitFatigueK(List<Seg> segs, BaseCurve baseRel)
    {
        // r = speed/base(grade) ~ 1 - k*effort. Least-squares slope with the intercept fixed at 1.
        double num = 0, den = 0;
        foreach (var s in segs)
        {
            double b = baseRel.SpeedAt(s.GradeDeg);
            if (b <= 0 || s.Effort <= 0) continue;
            double r = s.SpeedRel / b;
            num += (1.0 - r) * s.Effort;
            den += s.Effort * s.Effort;
        }
        double k = den > 0 ? num / den : 0;
        return Math.Max(0, k);   // fitness doesn't improve mid-race
    }

    /// <summary>
    /// Fits the turn (sinuosity) penalty. Residual r = speed / (base(grade) x fatigue(effort)) ~ 1 at the mean
    /// turn density; regress (r - 1) on (turn - refTurn) through the reference. Coeff is clamped &lt;= 0 so twistier
    /// terrain can only slow the runner — a spurious positive (straighter = faster than base) is discarded.
    /// </summary>
    private static TurnSpec FitTurn(List<Seg> segs, BaseCurve baseRel, FatigueSpec fat)
    {
        var used = new List<(double R, double Turn)>();
        foreach (var s in segs)
        {
            double b = baseRel.SpeedAt(s.GradeDeg) * fat.Mult(s.Effort);
            if (b <= 0) continue;
            used.Add((s.SpeedRel / b, s.Turn));
        }
        if (used.Count < 30) return new TurnSpec();   // too little to fit; stay neutral

        double refTurn = used.Average(u => u.Turn);
        double num = 0, den = 0;
        foreach (var u in used)
        {
            double dx = u.Turn - refTurn;
            num += (u.R - 1.0) * dx;
            den += dx * dx;
        }
        double coeff = den > 0 ? num / den : 0;
        return new TurnSpec { RefDegPerM = refTurn, Coeff = Math.Min(0, coeff) };
    }

    private static List<string> BuildSignalList(bool hr, bool cad)
    {
        var l = new List<string> { "ele", "time" };
        if (hr) l.Add("hr");
        if (cad) l.Add("cad");
        return l;
    }

    // --- small numeric helpers ---
    private static double Median(List<double> xs)
    {
        if (xs.Count == 0) return 0;
        xs.Sort();
        int n = xs.Count;
        return n % 2 == 1 ? xs[n / 2] : (xs[n / 2 - 1] + xs[n / 2]) / 2.0;
    }

    private static double Mean(List<double> xs) => xs.Count == 0 ? 0 : xs.Average();

    /// <summary>OLS slope of y vs x through the data (used for HR drift bpm/effort-unit).</summary>
    private static double Slope(List<double> x, List<double> y)
    {
        int n = x.Count;
        double mx = x.Average(), my = y.Average(), num = 0, den = 0;
        for (int i = 0; i < n; i++) { num += (x[i] - mx) * (y[i] - my); den += (x[i] - mx) * (x[i] - mx); }
        return den > 0 ? num / den : 0;
    }
}
