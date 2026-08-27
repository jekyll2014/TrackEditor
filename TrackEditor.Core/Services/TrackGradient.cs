using TrackEditor.Core.Models;
using TrackEditor.Core.Services.RaceAnalysis;

namespace TrackEditor.Core.Services;

/// <summary>Per-segment gradient colouring of a track, computed once and consumed by the map renderer
/// (features) and the on-map legend (labels).</summary>
public class TrackGradientResult
{
    /// <summary>Per-segment "goodness" in [0,1] aligned to the gaps between points (length = points-1):
    /// 1 = fast/easy (red end), 0 = slow/hard (blue end). <see cref="double.NaN"/> where the metric is
    /// undefined for that segment (e.g. a gap with no timestamps); the renderer draws those neutrally.</summary>
    public double[] Goodness { get; init; } = System.Array.Empty<double>();

    /// <summary>Short metric name for the legend caption (e.g. "Speed", "Grade", "Surface").</summary>
    public string Caption { get; init; } = "";

    /// <summary>Legend label for the red end (fast / easy / descending).</summary>
    public string HighLabel { get; init; } = "";

    /// <summary>Legend label for the blue end (slow / hard / climbing).</summary>
    public string LowLabel { get; init; } = "";
}

/// <summary>
/// Turns a track into a red→blue gradient bound to speed, inclination or pavement. Values are normalised
/// robustly (5th–95th percentile) so a single outlier segment doesn't wash out the whole ramp. The colour
/// ramp is a ColorBrewer RdYlBu diverging scheme (red = fast/easy, blue = slow/hard).
/// </summary>
public static class TrackGradient
{
    /// <summary>
    /// Computes the per-segment gradient for a track, or returns null when the chosen metric can't be
    /// represented (Speed without timestamps, Inclination without elevations, or metric None). Pavement is
    /// always representable — segments with no surface information default to "unpaved".
    /// </summary>
    public static TrackGradientResult? Compute(IReadOnlyList<TrackPoint> pts, GradientMetric metric,
        bool paceMode = false, GradeUnit gradeUnit = GradeUnit.Percent)
    {
        if (metric == GradientMetric.None || pts.Count < 2) return null;
        return metric switch
        {
            GradientMetric.Speed => Speed(pts, paceMode),
            GradientMetric.Inclination => Inclination(pts, gradeUnit),
            GradientMetric.Pavement => Pavement(pts),
            _ => null,
        };
    }

    private static TrackGradientResult? Speed(IReadOnlyList<TrackPoint> pts, bool paceMode)
    {
        var sp = GeoMath.SpeedsMps(pts);            // per-point m/s, null where no time data
        int n = pts.Count - 1;
        var raw = new double[n];
        bool any = false;
        for (int i = 0; i < n; i++)
        {
            double? a = sp[i], b = sp[i + 1];
            double v = a is double va && b is double vb ? (va + vb) / 2 : (a ?? b) ?? double.NaN;
            raw[i] = v;
            if (!double.IsNaN(v)) any = true;
        }
        if (!any) return null;

        var (lo, hi) = Percentiles(raw);
        var g = Normalize(raw, lo, hi, invert: false);   // faster -> higher -> red
        return new TrackGradientResult
        {
            Goodness = g,
            Caption = paceMode ? "Pace" : "Speed",
            HighLabel = FormatSpeed(hi, paceMode),        // red end = fastest
            LowLabel = FormatSpeed(lo, paceMode),         // blue end = slowest
        };
    }

    private static TrackGradientResult? Inclination(IReadOnlyList<TrackPoint> pts, GradeUnit gradeUnit)
    {
        var cum = GeoMath.CumulativeDistancesM(pts);
        int n = pts.Count - 1;
        var raw = new double[n];
        bool any = false;
        for (int i = 0; i < n; i++)
        {
            double dist = cum[i + 1] - cum[i];
            if (pts[i].Ele is double e0 && pts[i + 1].Ele is double e1 && dist > 0.5)
            {
                raw[i] = (e1 - e0) / dist * 100.0;        // signed grade %
                any = true;
            }
            else raw[i] = double.NaN;
        }
        if (!any) return null;

        var (lo, hi) = Percentiles(raw);                 // lo = steepest descent, hi = steepest climb
        // Easier where descending: invert so a low (negative) grade lands at the red end.
        var g = Normalize(raw, lo, hi, invert: true);
        return new TrackGradientResult
        {
            Goodness = g,
            Caption = "Grade",
            HighLabel = FormatGrade(lo, gradeUnit),       // red end = descending
            LowLabel = FormatGrade(hi, gradeUnit),        // blue end = climbing
        };
    }

    private static TrackGradientResult Pavement(IReadOnlyList<TrackPoint> pts)
    {
        int n = pts.Count - 1;
        var raw = new double[n];
        for (int i = 0; i < n; i++)
        {
            // A segment adopts the surface at its start (or its end when the start is unlabelled);
            // unlabelled stretches default to "unpaved" inside PassabilityForToken.
            string? token = pts[i].Surface ?? pts[i + 1].Surface;
            raw[i] = SurfaceCatalog.PassabilityForToken(token);
        }
        var g = Normalize(raw, SurfaceCatalog.PassabilityMin, SurfaceCatalog.PassabilityMax, invert: false);
        return new TrackGradientResult
        {
            Goodness = g,
            Caption = "Surface",
            HighLabel = "easy",     // red end = most passable (paved)
            LowLabel = "hard",      // blue end = least passable
        };
    }

    /// <summary>Number of colour bins consecutive segments are quantised into before merging into runs.</summary>
    public const int ColorBinCount = 32;

    /// <summary>
    /// Merges the per-segment goodness into contiguous colour runs so a smoothly varying metric renders as a
    /// handful of polylines instead of one per segment. Each run is the inclusive point range [Start..End]
    /// (runs share their boundary point, so the line stays continuous) plus the run's RGB colour. Segments
    /// whose goodness is NaN form neutral-grey runs.
    /// </summary>
    public static IEnumerable<(int Start, int End, byte R, byte G, byte B)> ColorRuns(double[] goodness)
    {
        int segs = goodness.Length;                 // = points - 1; last point index = segs
        if (segs == 0) yield break;

        static int Bin(double g) =>
            double.IsNaN(g) ? -1 : System.Math.Clamp((int)(g * ColorBinCount), 0, ColorBinCount - 1);
        (byte R, byte G, byte B) RunColor(int bin) => Color(bin < 0 ? double.NaN : (bin + 0.5) / ColorBinCount);

        int runStart = 0;
        int cur = Bin(goodness[0]);
        for (int seg = 1; seg < segs; seg++)
        {
            int b = Bin(goodness[seg]);
            if (b == cur) continue;
            var (r, g, bl) = RunColor(cur);
            yield return (runStart, seg, r, g, bl);
            runStart = seg;
            cur = b;
        }
        var (fr, fg, fb) = RunColor(cur);
        yield return (runStart, segs, fr, fg, fb);
    }

    /// <summary>Maps a normalised goodness in [0,1] to an RGB colour: 0 = blue (slow/hard),
    /// 0.5 = pale yellow, 1 = red (fast/easy). NaN yields a neutral grey.</summary>
    public static (byte R, byte G, byte B) Color(double g)
    {
        if (double.IsNaN(g)) return (170, 170, 170);
        g = System.Math.Clamp(g, 0, 1);
        // ColorBrewer RdYlBu, ordered blue(0) -> red(1).
        (double Pos, byte R, byte G, byte B)[] stops =
        {
            (0.00,  44, 123, 182),
            (0.25, 171, 217, 233),
            (0.50, 255, 255, 191),
            (0.75, 253, 174,  97),
            (1.00, 215,  25,  28),
        };
        for (int i = 1; i < stops.Length; i++)
        {
            if (g <= stops[i].Pos)
            {
                var a = stops[i - 1];
                var b = stops[i];
                double t = (g - a.Pos) / (b.Pos - a.Pos);
                return (Lerp(a.R, b.R, t), Lerp(a.G, b.G, t), Lerp(a.B, b.B, t));
            }
        }
        var last = stops[^1];
        return (last.R, last.G, last.B);
    }

    private static byte Lerp(byte a, byte b, double t) =>
        (byte)System.Math.Round(a + (b - a) * System.Math.Clamp(t, 0, 1));

    /// <summary>Normalises raw values into [0,1] against [lo,hi], optionally inverted. NaN stays NaN.
    /// A degenerate range (hi ≤ lo) maps every defined value to the mid-point.</summary>
    private static double[] Normalize(double[] raw, double lo, double hi, bool invert)
    {
        var g = new double[raw.Length];
        double span = hi - lo;
        for (int i = 0; i < raw.Length; i++)
        {
            if (double.IsNaN(raw[i])) { g[i] = double.NaN; continue; }
            double t = span > 1e-9 ? System.Math.Clamp((raw[i] - lo) / span, 0, 1) : 0.5;
            g[i] = invert ? 1 - t : t;
        }
        return g;
    }

    /// <summary>Robust 5th/95th percentiles over the defined (non-NaN) values, used as the ramp endpoints.</summary>
    private static (double Lo, double Hi) Percentiles(double[] raw)
    {
        var sorted = raw.Where(v => !double.IsNaN(v)).OrderBy(v => v).ToArray();
        if (sorted.Length == 0) return (0, 1);
        if (sorted.Length == 1) return (sorted[0], sorted[0]);
        double Pick(double q)
        {
            double idx = q * (sorted.Length - 1);
            int lo = (int)System.Math.Floor(idx);
            int hi = (int)System.Math.Ceiling(idx);
            return sorted[lo] + (sorted[hi] - sorted[lo]) * (idx - lo);
        }
        return (Pick(0.05), Pick(0.95));
    }

    private static string FormatSpeed(double mps, bool paceMode) =>
        double.IsNaN(mps) ? "—"
        : paceMode ? $"{PaceFormat.MinPerKm(mps)} /km"
        : $"{mps * 3.6:F1} km/h";

    /// <summary>Formats a rise/run grade percentage in the chosen unit: the percentage itself, or the
    /// equivalent slope angle (arctan) in degrees.</summary>
    private static string FormatGrade(double pct, GradeUnit unit)
    {
        if (double.IsNaN(pct)) return "—";
        return unit == GradeUnit.Degree
            ? $"{System.Math.Atan(pct / 100.0) * (180.0 / System.Math.PI):+0.#;-0.#;0}°"
            : $"{pct:+0.#;-0.#;0}%";
    }
}
