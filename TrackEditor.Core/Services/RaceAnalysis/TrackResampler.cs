using TrackEditor.Core.Models;

namespace TrackEditor.Core.Services.RaceAnalysis;

/// <summary>
/// Prepares a raw track for race analysis by resampling it to a fixed along-track spacing and smoothing
/// elevation. Grade is the derivative of elevation, so it amplifies GPS/DEM noise; resampling to an even
/// grid and low-pass filtering the elevation first is what makes the later grade/speed extraction stable.
/// The same routine is applied to both the recorded (fit) and planned (predict) tracks so their grades are
/// measured on identical terms.
/// </summary>
public static class TrackResampler
{
    /// <summary>Default fixed spacing between resampled points (m).</summary>
    public const double DefaultSpacingM = 5.0;

    /// <summary>Default elevation smoothing window (m) — a moving average this wide is applied before grades.</summary>
    public const double DefaultEleWindowM = 40.0;

    /// <summary>
    /// Returns an evenly-spaced copy of <paramref name="pts"/> at <paramref name="spacingM"/> intervals,
    /// with lat/lon/ele/time and any Hr/Cad/Temp linearly interpolated and elevation moving-average smoothed.
    /// Tracks shorter than one spacing (or with &lt; 2 points) are returned as a plain clone.
    /// </summary>
    public static List<TrackPoint> Resample(
        IReadOnlyList<TrackPoint> pts,
        double spacingM = DefaultSpacingM,
        double eleWindowM = DefaultEleWindowM)
    {
        if (pts.Count < 2) return pts.Select(p => p.Clone()).ToList();

        var cum = GeoMath.CumulativeDistancesM(pts);
        double total = cum[^1];
        if (total < spacingM) return new List<TrackPoint> { pts[0].Clone(), pts[^1].Clone() };

        var outp = new List<TrackPoint>((int)(total / spacingM) + 2);
        int seg = 1;   // current source segment is (seg-1, seg); cum[seg-1] <= target <= cum[seg]
        for (double d = 0; d <= total + 1e-6; d += spacingM)
        {
            double target = Math.Min(d, total);
            while (seg < pts.Count - 1 && cum[seg] < target) seg++;
            double d0 = cum[seg - 1], d1 = cum[seg];
            double t = d1 > d0 ? (target - d0) / (d1 - d0) : 0;   // fraction along the source segment
            outp.Add(Lerp(pts[seg - 1], pts[seg], t));
        }

        SmoothElevation(outp, WindowSamples(eleWindowM, spacingM));
        return outp;
    }

    /// <summary>Linear interpolation of every channel between two source points at fraction <paramref name="t"/>.</summary>
    private static TrackPoint Lerp(TrackPoint a, TrackPoint b, double t) => new()
    {
        Lat = a.Lat + (b.Lat - a.Lat) * t,
        Lon = a.Lon + (b.Lon - a.Lon) * t,
        Ele = LerpN(a.Ele, b.Ele, t),
        Time = (a.Time is DateTime ta && b.Time is DateTime tb)
            ? ta + TimeSpan.FromTicks((long)((tb - ta).Ticks * t))
            : (a.Time ?? b.Time),
        Hr = LerpRound(a.Hr, b.Hr, t),
        Cad = LerpRound(a.Cad, b.Cad, t),
        Temp = LerpN(a.Temp, b.Temp, t),
    };

    private static double? LerpN(double? a, double? b, double t) =>
        (a is double x && b is double y) ? x + (y - x) * t : (a ?? b);

    private static int? LerpRound(int? a, int? b, double t) =>
        (a is int x && b is int y) ? (int)Math.Round(x + (y - x) * t) : (a ?? b);

    /// <summary>Converts a smoothing window in meters to an odd sample count for the given spacing.</summary>
    public static int WindowSamples(double windowM, double spacingM)
    {
        int w = (int)Math.Round(windowM / Math.Max(spacingM, 1e-6));
        return Math.Max(1, w | 1);   // force odd, >= 1
    }

    /// <summary>Centered moving-average low-pass over the Ele channel (in place). No-op if no elevations.</summary>
    public static void SmoothElevation(List<TrackPoint> pts, int window)
    {
        if (window <= 1 || pts.Count == 0) return;
        int half = window / 2;
        var src = pts.Select(p => p.Ele).ToArray();
        for (int i = 0; i < pts.Count; i++)
        {
            double sum = 0; int cnt = 0;
            for (int j = Math.Max(0, i - half); j <= Math.Min(pts.Count - 1, i + half); j++)
                if (src[j] is double e) { sum += e; cnt++; }
            if (cnt > 0) pts[i].Ele = sum / cnt;
        }
    }
}
