using TrackEditor.Core.Models;

namespace TrackEditor.Core.Services.RaceAnalysis;

/// <summary>
/// Turn density (course sinuosity) along a resampled track: how much the heading changes per metre travelled.
/// Both the analyzer (fit) and the predictor (apply) must derive it identically, so it lives here. The value at
/// index <c>i</c> is the smoothed absolute heading change (degrees) per metre over the segment ending at point
/// <c>i</c>. Straight running ≈ 0; tight switchbacks are large. Elevation is ignored — this is a plan-view metric.
/// </summary>
public static class TurnMetrics
{
    /// <summary>Half-width of the smoothing window in metres; total window ≈ 2×this.</summary>
    public const double DefaultWindowM = 40.0;

    /// <summary>
    /// Per-segment turn density (deg/m), smoothed over a window. Returns an array the same length as
    /// <paramref name="rs"/>; entries [0] and [1] are 0 (a heading change needs two prior bearings).
    /// </summary>
    public static double[] PerSegmentDegPerM(IReadOnlyList<TrackPoint> rs, IReadOnlyList<double> cum,
                                             double windowM = DefaultWindowM)
    {
        int n = rs.Count;
        var density = new double[n];
        if (n < 3) return density;

        // Segment bearings: bearing[i] is the heading of the segment (i-1 -> i), for i in 1..n-1.
        var bearing = new double[n];
        for (int i = 1; i < n; i++)
            bearing[i] = GeoMath.BearingDeg(rs[i - 1].Lat, rs[i - 1].Lon, rs[i].Lat, rs[i].Lon);

        // Raw density at point i = |heading change between consecutive segments| / segment length (deg/m),
        // and dw[i] = that segment's length (weight for the moving average below).
        var raw = new double[n];
        var dw = new double[n];
        for (int i = 2; i < n; i++)
        {
            double dDist = cum[i] - cum[i - 1];
            dw[i] = dDist;
            raw[i] = dDist > 0.01 ? AngleDelta(bearing[i], bearing[i - 1]) / dDist : 0;
        }

        // Distance-weighted moving average over ±windowM. cum is monotonic, so slide two pointers: O(n).
        double sum = 0, wsum = 0;
        int lo = 2, hi = 1;   // (lo..hi] currently inside the window
        for (int i = 2; i < n; i++)
        {
            while (hi + 1 < n && cum[hi + 1] - cum[i] <= windowM) { hi++; sum += raw[hi] * dw[hi]; wsum += dw[hi]; }
            while (cum[i] - cum[lo] > windowM) { sum -= raw[lo] * dw[lo]; wsum -= dw[lo]; lo++; }
            density[i] = wsum > 0 ? sum / wsum : raw[i];
        }
        return density;
    }

    /// <summary>Smallest absolute difference between two bearings, in degrees (0..180).</summary>
    private static double AngleDelta(double a, double b)
    {
        double d = Math.Abs(a - b) % 360.0;
        return d > 180.0 ? 360.0 - d : d;
    }
}
