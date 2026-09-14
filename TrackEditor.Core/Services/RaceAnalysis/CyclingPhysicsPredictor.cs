using TrackEditor.Core.Models;

namespace TrackEditor.Core.Services.RaceAnalysis;

/// <summary>
/// Physics-based cycling race predictor (Martin et al. 1998, J Appl Biomech).
///
/// Power balance per segment:
///   P_drive / η = P_aero + P_rolling + P_gravity
///   P_aero    = 0.5 · CdA · ρ(alt) · v³
///   P_rolling = Crr · m · g · cos(θ) · v
///   P_gravity = m · g · sin(θ) · v
///
/// Speed is solved from the resulting cubic in v using Newton–Raphson.
/// Descents where gravity exceeds drag at all speeds yield the terminal coast velocity.
///
/// Fatigue follows the Critical Power / W' model (Monod &amp; Scherrer 1965, Skiba 2012):
///   P > CP  →  W' depletes at (P − CP) W per second
///   P ≤ CP  →  W' recovers at ~50% the depletion rate
///   W' = 0  →  power falls back to CP until recovery
///
/// Requires <see cref="AthleteProfile.EffectiveCpW"/> (FTP or CP in the athlete profile).
/// </summary>
public static class CyclingPhysicsPredictor
{
    private const double G = 9.80665;

    public static PredictResult Predict(Track target, RaceModel model, PredictOptions opt)
    {
        var spec = opt.PhysicsSpec
            ?? model.Cycling
            ?? (model.Sport == SportType.CyclingXC ? CyclingSpec.XCDefault() : CyclingSpec.RoadDefault());

        var profile = opt.Profile!;
        double cp = profile.EffectiveCpW!.Value;
        double wPrime = profile.WPrimeJ ?? 20_000;
        double riderKg = profile.MassKg ?? 75.0;
        double bikeKg = profile.BikeKg ?? (model.Sport == SportType.CyclingXC ? 12.0 : 9.0);
        double totalKg = riderKg + bikeKg;

        // Scale CP by effort intent: Race=1.00, AllOut=1.04 burns W'; Easy/Steady stay below CP.
        double targetPower = cp * opt.EffortScale;

        var pts = target.Points;
        var rs = TrackResampler.Resample(pts, opt.SpacingM, opt.EleWindowM);
        var cumRs = GeoMath.CumulativeDistancesM(rs);
        var surfAtRs = BuildGridSurface(opt.PerPointSurfaceMult, pts, cumRs);

        var timeAtRs = new double[rs.Count];
        double wCurrent = wPrime;
        double elapsed = 0;

        for (int i = 1; i < rs.Count; i++)
        {
            double dDist = cumRs[i] - cumRs[i - 1];
            double dEle = (rs[i].Ele ?? 0) - (rs[i - 1].Ele ?? 0);

            double slope = dDist > 0.01 ? dEle / dDist : 0;       // tan(θ)
            double hypot = Math.Sqrt(1 + slope * slope);
            double sinTheta = slope / hypot;
            double cosTheta = 1.0 / hypot;

            // Barometric air density correction (~1% per 80 m, standard atmosphere).
            double ele = rs[i].Ele ?? 0;
            double rho = spec.AirDensityKgM3 * Math.Exp(-Math.Max(ele, 0) / 8500.0);

            // W' / CP model: hold target if W' available, else fall back to CP.
            double avail = wCurrent > 0 ? targetPower : cp;

            // Cubic coefficients: a·v³ + b·v = netPower
            double a = 0.5 * spec.CdA * rho;
            double b = (spec.Crr * cosTheta + sinTheta) * totalKg * G;
            double netPower = avail / spec.DrivetrainEff;

            // Surface condition multiplier applied as a post-solve speed scale.
            double surfMult = opt.SurfaceMult * surfAtRs[i];

            double v = Math.Max(SolveSpeed(netPower, a, b, opt.MinSpeedMps) * surfMult, opt.MinSpeedMps);

            // W' dynamics: deplete above CP, recover below at half rate (Skiba 2012 approximation).
            double dt = dDist / v;
            if (avail > cp)
                wCurrent -= (avail - cp) * dt;
            else
                wCurrent += (cp - avail) * 0.5 * dt;
            wCurrent = Math.Clamp(wCurrent, 0, wPrime);

            elapsed += dt;
            timeAtRs[i] = elapsed;
        }

        // Map integrated times back onto original points by along-track distance (same logic as RacePredictor).
        double tailPace = cumRs[^1] > 1 ? timeAtRs[^1] / cumRs[^1] : 0;
        var cumOrig = GeoMath.CumulativeDistancesM(pts);
        var copy = target.Clone();
        copy.Name = string.IsNullOrWhiteSpace(model.Meta.AthleteName)
            ? target.Name + " (predicted)"
            : $"{target.Name} (predicted for {model.Meta.AthleteName.Trim()})";
        foreach (var pt in copy.Points) { pt.Hr = null; pt.Cad = null; pt.Temp = null; pt.Surface = null; }

        int j = 1;
        for (int i = 0; i < copy.Points.Count; i++)
        {
            double d = cumOrig[i];
            double sec;
            if (d >= cumRs[^1])
                sec = timeAtRs[^1] + (d - cumRs[^1]) * tailPace;
            else
            {
                while (j < rs.Count - 1 && cumRs[j] < d) j++;
                double d0 = cumRs[j - 1], d1 = cumRs[j];
                double f = d1 > d0 ? (d - d0) / (d1 - d0) : 0;
                sec = timeAtRs[j - 1] + (timeAtRs[j] - timeAtRs[j - 1]) * f;
            }
            copy.Points[i].Time = opt.StartTime.AddSeconds(sec);
        }
        copy.ResetBaseline();

        double totalSec = (copy.Points[^1].Time!.Value - opt.StartTime).TotalSeconds;
        return new PredictResult
        {
            PredictedTrack = copy,
            TotalTime = TimeSpan.FromSeconds(totalSec),
            DistanceKm = cumOrig[^1] / 1000.0,
        };
    }

    /// <summary>
    /// Solve a·v³ + b·v = P for the unique positive real root (Newton–Raphson, 20 iterations).
    /// When P ≤ 0 (net gravity exceeds all resistance): return the terminal coast speed sqrt(−b/a),
    /// or <paramref name="minSpeed"/> when the grade is too flat for coasting to be meaningful.
    /// </summary>
    private static double SolveSpeed(double P, double a, double b, double minSpeed)
    {
        if (P <= 0)
        {
            // Steep enough descent: terminal velocity from gravity vs. aero drag.
            if (b < 0 && a > 0)
                return Math.Min(Math.Sqrt(-b / a), 22.0);   // cap at ~80 km/h
            return minSpeed;
        }

        // Initial guess: ignore one force to start the Newton–Raphson iteration.
        double v = b > 0.001
            ? Math.Min(P / b, Math.Pow(P / a, 1.0 / 3))
            : Math.Pow(P / a, 1.0 / 3);
        v = Math.Max(v, 0.5);

        for (int iter = 0; iter < 20; iter++)
        {
            double fv = a * v * v * v + b * v - P;
            double dfv = 3 * a * v * v + b;
            if (Math.Abs(dfv) < 1e-12) break;
            double step = fv / dfv;
            v -= step;
            if (v < 0.1) v = 0.1;
            if (Math.Abs(step) < 1e-7) break;
        }
        return Math.Max(v, minSpeed);
    }

    private static double[] BuildGridSurface(IReadOnlyList<double>? perPoint, IReadOnlyList<TrackPoint> pts, double[] cumRs)
    {
        var s = new double[cumRs.Length];
        Array.Fill(s, 1.0);
        if (perPoint is null || perPoint.Count != pts.Count) return s;
        var cumOrig = GeoMath.CumulativeDistancesM(pts);
        int k = 0;
        for (int i = 0; i < cumRs.Length; i++)
        {
            double d = cumRs[i];
            while (k < cumOrig.Length - 1 && cumOrig[k + 1] < d) k++;
            int idx = k;
            if (k + 1 < cumOrig.Length && Math.Abs(cumOrig[k + 1] - d) < Math.Abs(cumOrig[k] - d)) idx = k + 1;
            s[i] = perPoint[idx];
        }
        return s;
    }
}
