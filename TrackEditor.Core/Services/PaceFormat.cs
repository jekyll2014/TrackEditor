namespace TrackEditor.Core.Services;

/// <summary>
/// Shared speed⇄pace formatting so the statistics panel, hover hints and plots all render pace the same way.
/// Pace is the inverse of speed (minutes per km); below a walking crawl it explodes, so it is clamped.
/// </summary>
public static class PaceFormat
{
    /// <summary>Speeds at or below this (m/s) are treated as "stopped" — pace would otherwise blow up.</summary>
    private const double MinMps = 0.05;

    /// <summary>Pace as "m:ss" minutes per km for a speed in m/s, or "—" when effectively stopped.</summary>
    public static string MinPerKm(double mps)
    {
        if (mps <= MinMps) return "—";
        double secPerKm = 1000.0 / mps;
        int m = (int)(secPerKm / 60);
        int s = (int)System.Math.Round(secPerKm - m * 60);
        if (s == 60) { m++; s = 0; }
        return $"{m}:{s:D2}";
    }

    /// <summary>Pace in minutes per km as a number (for plotting), clamped so a near-stop doesn't spike the axis.</summary>
    public static double MinPerKmValue(double mps) => 1000.0 / System.Math.Max(mps, MinMps) / 60.0;
}
