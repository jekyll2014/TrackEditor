using System.Text;

using TrackEditor.Core.Models;

namespace TrackEditor.Core.Services;

/// <summary>How the two recordings are matched point-for-point.</summary>
public enum MergeAlign
{
    /// <summary>Time when both carry timestamps and their clocks overlap; distance otherwise.</summary>
    Auto,
    /// <summary>Match by timestamp — the right choice when both devices logged the same session in real (UTC) time.</summary>
    Time,
    /// <summary>Match by nearest point on the other track's line — for recordings without a shared clock.</summary>
    Distance,
}

/// <summary>What geometry the merged track carries.</summary>
public enum MergeGeometry
{
    /// <summary>Keep the base track's coordinates; only pull the other track's extra sensor channels in.</summary>
    KeepBase,
    /// <summary>Average the two lines where they overlap (midpoint of matched pairs) to reduce GPS noise;
    /// stretches with no partner keep the base coordinates so partial overlaps survive.</summary>
    Average,
}

public class MergeOptions
{
    public MergeAlign Align { get; set; } = MergeAlign.Auto;
    public MergeGeometry Geometry { get; set; } = MergeGeometry.KeepBase;
    /// <summary>When both tracks carry the same channel at a matched point: false = average the two values,
    /// true = keep the base track's value.</summary>
    public bool PreferBaseOnConflict { get; set; } = false;
    /// <summary>Spatial gate (m). A base point whose partner is farther than this is treated as unmatched:
    /// no field fusion and no geometry averaging there. Guards against fusing two genuinely different lines.</summary>
    public double MaxMatchDistM { get; set; } = 60;
    /// <summary>Spacing (m) the other track is resampled to before distance matching (perf + even field sampling).</summary>
    public double ResampleM { get; set; } = 5;
}

public class MergeResult
{
    public Track Merged { get; set; } = new();
    public int Matched { get; set; }
    public int Total { get; set; }
    public double Coverage { get; set; }
    public double MeanSepM { get; set; }
    public bool UsedTime { get; set; }
    /// <summary>Channels the base track lacked entirely that the other track supplied (e.g. "hr", "temp").</summary>
    public List<string> FieldsGained { get; set; } = new();
    public string Report { get; set; } = "";
}

/// <summary>
/// Fuses two recordings of (roughly) the same route into one track: it walks the base track and, for each
/// point, finds the matching moment on the other track — by timestamp when both share a real clock, else by the
/// nearest point on the other track's line within a distance gate. Sensor channels the base lacks (HR, cadence,
/// temp, elevation, surface) are filled from the other track; where both carry a channel the values are averaged
/// (or the base kept). Optionally the two lines are averaged where they overlap to smooth GPS noise. The base
/// track is never mutated — a new track is returned. Distinct from Join, which appends one track after another.
/// </summary>
public static class TrackMerger
{
    public static MergeResult Merge(Track baseTrack, Track other, MergeOptions? options = null)
    {
        var opt = options ?? new MergeOptions();
        var bp = baseTrack.Points;
        var op = other.Points;
        var result = new MergeResult { Total = bp.Count };
        if (bp.Count < 2 || op.Count < 2)
        {
            result.Merged = baseTrack.Clone();
            result.Report = "Both tracks need at least two points to merge.";
            return result;
        }

        // Which channels does the base lack but the other has? (Reported as "gained".)
        result.FieldsGained = GainedFields(bp, op);

        bool useTime = opt.Align switch
        {
            MergeAlign.Time => bp.Any(p => p.Time is not null) && op.Any(p => p.Time is not null),
            MergeAlign.Distance => false,
            _ => TimeRangesOverlap(bp, op),
        };
        result.UsedTime = useTime;

        var merged = baseTrack.Clone();
        merged.Name = baseTrack.Name + " + " + other.Name;
        merged.ElevationEstimated = baseTrack.ElevationEstimated && other.ElevationEstimated;

        double sepSum = 0;
        if (useTime)
        {
            // Base points matched to the other track interpolated at the same timestamp.
            var (times, pts) = TimedSamples(op);
            for (int i = 0; i < merged.Points.Count; i++)
            {
                var b = merged.Points[i];
                if (b.Time is not DateTime t || !InterpAtTime(times, pts, t, out TrackPoint partner))
                    continue;
                double sep = GeoMath.HaversineM(b.Lat, b.Lon, partner.Lat, partner.Lon);
                if (sep > opt.MaxMatchDistM) continue;   // same clock but far apart -> don't fuse
                Fuse(b, partner, opt);
                result.Matched++; sepSum += sep;
            }
        }
        else
        {
            // Even, field-preserving resample of the other track, then nearest-vertex match under the gate.
            var ow = RaceAnalysis.TrackResampler.Resample(op, opt.ResampleM, RaceAnalysis.TrackResampler.DefaultEleWindowM);
            double lat0 = GeoMath.ToRad(bp[0].Lat), cos0 = Math.Cos(lat0);
            var oxy = ProjectXY(ow, cos0);
            for (int i = 0; i < merged.Points.Count; i++)
            {
                var b = merged.Points[i];
                int j = NearestVertex(oxy, GeoMath.ToRad(b.Lon) * cos0 * GeoMath.EarthRadiusM,
                                            GeoMath.ToRad(b.Lat) * GeoMath.EarthRadiusM);
                double sep = GeoMath.HaversineM(b.Lat, b.Lon, ow[j].Lat, ow[j].Lon);
                if (sep > opt.MaxMatchDistM) continue;
                Fuse(b, ow[j], opt);
                result.Matched++; sepSum += sep;
            }
        }

        result.Coverage = bp.Count > 0 ? (double)result.Matched / bp.Count : 0;
        result.MeanSepM = result.Matched > 0 ? sepSum / result.Matched : 0;
        result.Merged = merged;
        merged.ResetBaseline();
        result.Report = BuildReport(result, opt);
        return result;
    }

    /// <summary>Fuses the other point's channels into the base point (mutated in place): missing channels are
    /// filled, shared channels averaged (or the base kept), and geometry averaged when requested.</summary>
    private static void Fuse(TrackPoint b, TrackPoint o, MergeOptions opt)
    {
        if (opt.Geometry == MergeGeometry.Average)
        {
            b.Lat = (b.Lat + o.Lat) / 2.0;
            b.Lon = (b.Lon + o.Lon) / 2.0;
        }
        b.Ele = FuseD(b.Ele, o.Ele, opt.PreferBaseOnConflict);
        b.Temp = FuseD(b.Temp, o.Temp, opt.PreferBaseOnConflict);
        b.Hr = FuseI(b.Hr, o.Hr, opt.PreferBaseOnConflict);
        b.Cad = FuseI(b.Cad, o.Cad, opt.PreferBaseOnConflict);
        b.Surface ??= o.Surface;
        b.Time ??= o.Time;
    }

    private static double? FuseD(double? a, double? b, bool preferA) =>
        a is double x && b is double y ? (preferA ? x : (x + y) / 2.0) : (a ?? b);

    private static int? FuseI(int? a, int? b, bool preferA) =>
        a is int x && b is int y ? (preferA ? x : (int)Math.Round((x + y) / 2.0)) : (a ?? b);

    private static List<string> GainedFields(IReadOnlyList<TrackPoint> b, IReadOnlyList<TrackPoint> o)
    {
        var gained = new List<string>();
        void Check(string name, bool baseHas, bool otherHas) { if (!baseHas && otherHas) gained.Add(name); }
        Check("ele", b.Any(p => p.Ele is not null), o.Any(p => p.Ele is not null));
        Check("hr", b.Any(p => p.Hr is not null), o.Any(p => p.Hr is not null));
        Check("cad", b.Any(p => p.Cad is not null), o.Any(p => p.Cad is not null));
        Check("temp", b.Any(p => p.Temp is not null), o.Any(p => p.Temp is not null));
        Check("surface", b.Any(p => p.Surface is not null), o.Any(p => p.Surface is not null));
        Check("time", b.Any(p => p.Time is not null), o.Any(p => p.Time is not null));
        return gained;
    }

    private static bool TimeRangesOverlap(IReadOnlyList<TrackPoint> a, IReadOnlyList<TrackPoint> b)
    {
        var at = a.Where(p => p.Time is not null).Select(p => p.Time!.Value).ToList();
        var bt = b.Where(p => p.Time is not null).Select(p => p.Time!.Value).ToList();
        if (at.Count == 0 || bt.Count == 0) return false;
        return at.Min() <= bt.Max() && bt.Min() <= at.Max();
    }

    /// <summary>The timed points of a track, in ascending time order, as parallel arrays for binary search.</summary>
    private static (DateTime[] Times, TrackPoint[] Pts) TimedSamples(IReadOnlyList<TrackPoint> pts)
    {
        var timed = pts.Where(p => p.Time is not null).OrderBy(p => p.Time!.Value).ToArray();
        return (timed.Select(p => p.Time!.Value).ToArray(), timed);
    }

    /// <summary>Interpolates the track at time <paramref name="t"/> (coords + all channels). False when t is
    /// outside the sampled time span.</summary>
    private static bool InterpAtTime(DateTime[] times, TrackPoint[] pts, DateTime t, out TrackPoint p)
    {
        p = new TrackPoint();
        if (times.Length == 0 || t < times[0] || t > times[^1]) return false;
        int lo = Array.BinarySearch(times, t);
        if (lo >= 0) { p = pts[lo].Clone(); return true; }
        int hi = ~lo;                 // first index with time > t
        if (hi <= 0 || hi >= times.Length) { p = pts[Math.Clamp(hi, 0, pts.Length - 1)].Clone(); return true; }
        var a = pts[hi - 1]; var b = pts[hi];
        double span = (times[hi] - times[hi - 1]).TotalSeconds;
        double f = span > 0 ? (t - times[hi - 1]).TotalSeconds / span : 0;
        p = new TrackPoint
        {
            Lat = a.Lat + (b.Lat - a.Lat) * f,
            Lon = a.Lon + (b.Lon - a.Lon) * f,
            Ele = Lerp(a.Ele, b.Ele, f),
            Temp = Lerp(a.Temp, b.Temp, f),
            Hr = LerpI(a.Hr, b.Hr, f),
            Cad = LerpI(a.Cad, b.Cad, f),
            Surface = a.Surface ?? b.Surface,
            Time = t,
        };
        return true;
    }

    private static double? Lerp(double? a, double? b, double f) =>
        a is double x && b is double y ? x + (y - x) * f : (a ?? b);
    private static int? LerpI(int? a, int? b, double f) =>
        a is int x && b is int y ? (int)Math.Round(x + (y - x) * f) : (a ?? b);

    private static (double X, double Y)[] ProjectXY(IReadOnlyList<TrackPoint> pts, double cos0)
    {
        var xy = new (double, double)[pts.Count];
        for (int i = 0; i < pts.Count; i++)
            xy[i] = (GeoMath.ToRad(pts[i].Lon) * cos0 * GeoMath.EarthRadiusM,
                     GeoMath.ToRad(pts[i].Lat) * GeoMath.EarthRadiusM);
        return xy;
    }

    private static int NearestVertex((double X, double Y)[] xy, double px, double py)
    {
        int best = 0; double bestD = double.MaxValue;
        for (int i = 0; i < xy.Length; i++)
        {
            double dx = xy[i].X - px, dy = xy[i].Y - py;
            double d = dx * dx + dy * dy;
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    private static string BuildReport(MergeResult r, MergeOptions opt)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Aligned by:      {(r.UsedTime ? "timestamp" : "distance")}");
        sb.AppendLine($"Geometry:        {(opt.Geometry == MergeGeometry.Average ? "averaged where overlapping" : "base track kept")}");
        sb.AppendLine($"Matched points:  {r.Matched}/{r.Total}  ({r.Coverage * 100:F0}% overlap)");
        if (r.Matched > 0)
            sb.AppendLine($"Mean separation: {r.MeanSepM:F1} m between the two lines");
        sb.AppendLine(r.FieldsGained.Count > 0
            ? $"Channels gained: {string.Join(", ", r.FieldsGained)}"
            : "Channels gained: none (base already had every channel the other track carries)");
        if (r.Matched == 0)
            sb.AppendLine("⚠ No points matched within the distance gate — the tracks may not be the same route.");
        return sb.ToString().TrimEnd();
    }
}
