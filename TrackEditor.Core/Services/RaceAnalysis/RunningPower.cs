namespace TrackEditor.Core.Services.RaceAnalysis;

/// <summary>
/// Metabolic running power estimated from kinematics only (no power meter required). Uses the Minetti et al.
/// (2002) polynomial fitted to human treadmill data across grades −45% to +45%. Produces specific power (W/kg)
/// so results are mass-independent and directly comparable across athletes and sessions.
/// </summary>
public static class RunningPower
{
    /// <summary>Metabolic cost of running (J/kg/m) at grade <paramref name="gradeFraction"/> (rise/run).
    /// Minetti 2002: C(i) = 155.4i⁵ − 30.4i⁴ − 43.3i³ + 46.3i² + 19.5i + 3.6.
    /// Clamped to valid range ±0.45; floor at 0.5 J/kg/m (physiological minimum).</summary>
    public static double MetabolicCostJPerKgPerM(double gradeFraction)
    {
        double i = Math.Clamp(gradeFraction, -0.45, 0.45);
        double c = 155.4 * Pow5(i) - 30.4 * Pow4(i) - 43.3 * Pow3(i)
                 + 46.3 * i * i + 19.5 * i + 3.6;
        return Math.Max(0.5, c);
    }

    /// <summary>Specific running power (W/kg) from grade fraction (rise/run) and speed (m/s).</summary>
    public static double SpecificPowerWkg(double gradeFraction, double speedMps)
        => MetabolicCostJPerKgPerM(gradeFraction) * speedMps;

    /// <summary>90th-percentile value of <paramref name="values"/>. Sorts in place. Returns 0 for empty list.</summary>
    public static double Percentile90(List<double> values)
    {
        if (values.Count == 0) return 0;
        values.Sort();
        double idx = (values.Count - 1) * 0.9;
        int lo = (int)idx;
        int hi = Math.Min(lo + 1, values.Count - 1);
        return values[lo] + (values[hi] - values[lo]) * (idx - lo);
    }

    private static double Pow3(double x) => x * x * x;
    private static double Pow4(double x) => x * x * x * x;
    private static double Pow5(double x) => x * x * x * x * x;
}
