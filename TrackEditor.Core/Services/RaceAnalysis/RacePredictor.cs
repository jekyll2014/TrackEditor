using System.Text;

using TrackEditor.Core.Models;

namespace TrackEditor.Core.Services.RaceAnalysis;

/// <summary>Race-day intent, mapped to a pace scale on the fitted model. "Race" = the intensity the model was
/// fitted at (typically a race recording); Easy/Steady ease off, AllOut pushes slightly past it.</summary>
public enum RaceEffort { Easy, Steady, Race, AllOut }

/// <summary>Inputs for a prediction run.</summary>
public class PredictOptions
{
    /// <summary>Clock time the race starts; predicted point times are start + accumulated seconds.</summary>
    public DateTime StartTime { get; set; } = DateTime.Today.AddHours(8);
    /// <summary>Global surface multiplier (1.0 = the model's own terrain). &lt;1 slows, e.g. mud/technical.</summary>
    public double SurfaceMult { get; set; } = 1.0;
    /// <summary>Optional per-point surface multiplier aligned to <c>target.Points</c> (e.g. from routing-inferred
    /// OSM surface). Applied on top of <see cref="SurfaceMult"/>; null or mismatched length is ignored.</summary>
    public IReadOnlyList<double>? PerPointSurfaceMult { get; set; }
    /// <summary>Apply an altitude derate above the model's reference elevation. Off by default.</summary>
    public bool UseAltitude { get; set; } = false;
    /// <summary>Fraction of speed lost per 1000 m above the model's altitude reference, used when
    /// <see cref="UseAltitude"/> is on and the model carries no fitted derate.</summary>
    public double AltitudeDeratePerKm { get; set; } = AltitudeSpec.DefaultDeratePerKm;
    /// <summary>Intended race intensity relative to the fitted model. Scales the whole speed curve and, for
    /// harder-than-fitted efforts, steepens fatigue (aerobic decoupling worsens when you push).</summary>
    public RaceEffort Effort { get; set; } = RaceEffort.Race;
    /// <summary>Athlete physiology; optional. Drives HR-reserve normalization, the load model, and the endurance
    /// calibration / sustainable cap (via <see cref="AthleteProfile.RecentRace"/>).</summary>
    public AthleteProfile? Profile { get; set; }
    /// <summary>Rescale the model's overall pace to what the athlete can hold over this route's distance, using
    /// their recent race (Riegel). Needs <see cref="Profile"/>.RecentRace; ignored otherwise.</summary>
    public bool CalibrateToRecentRace { get; set; } = false;
    /// <summary>Clamp the effort-scaled pace to the endurance-sustainable ceiling from the recent race, so an
    /// aggressive effort can't predict an unsustainable average. Needs <see cref="Profile"/>.RecentRace.</summary>
    public bool CapToSustainable { get; set; } = false;
    /// <summary>Apply the mass/pack/poles climb-cost model on positive grades. Needs body mass in the profile.</summary>
    public bool UseLoadModel { get; set; } = false;
    public double SpacingM { get; set; } = TrackResampler.DefaultSpacingM;
    public double EleWindowM { get; set; } = TrackResampler.DefaultEleWindowM;
    /// <summary>Speed can never fall below this (m/s) — guards against divide-by-tiny on extreme grades.</summary>
    public double MinSpeedMps { get; set; } = 0.3;

    /// <summary>Speed multiplier for <see cref="Effort"/> (1.0 = the model's fitted intensity).</summary>
    public double EffortScale => ScaleFor(Effort);

    /// <summary>Heuristic pace scale per intent, relative to the fitted intensity. Documented approximations, not
    /// fitted: real intensity is whatever the source tracks were run at, so "Race" is the neutral anchor.</summary>
    public static double ScaleFor(RaceEffort e) => e switch
    {
        RaceEffort.Easy => 0.88,
        RaceEffort.Steady => 0.94,
        RaceEffort.Race => 1.00,
        RaceEffort.AllOut => 1.04,
        _ => 1.00,
    };
}

public class PredictResult
{
    public Track PredictedTrack { get; set; } = new();
    public TimeSpan TotalTime { get; set; }
    public double DistanceKm { get; set; }
    public string Report { get; set; } = "";
}

/// <summary>
/// Applies a fitted <see cref="RaceModel"/> to a planned track to predict pace. It resamples the target to the
/// same fixed grid the model was fitted on, then integrates forward segment by segment: predicted segment
/// speed = baseCurve(grade) x fatigue(accumulated effort so far) x altitude x surface, and segment time =
/// distance / speed. Fatigue is fed forward so the runner tires realistically across the route. The resulting
/// times are interpolated back onto the original points, producing a timestamped copy of the track.
/// </summary>
public static class RacePredictor
{
    public static PredictResult Predict(Track target, RaceModel model, PredictOptions? options = null)
    {
        var opt = options ?? new PredictOptions();
        var pts = target.Points;
        if (pts.Count < 2) throw new InvalidOperationException("Target track needs at least two points.");

        var rs = TrackResampler.Resample(pts, opt.SpacingM, opt.EleWindowM);
        var cumRs = GeoMath.CumulativeDistancesM(rs);
        var turn = TurnMetrics.PerSegmentDegPerM(rs, cumRs);
        var surfAtRs = BuildGridSurface(opt.PerPointSurfaceMult, pts, cumRs);

        double targetKm = cumRs[^1] / 1000.0;
        var recent = opt.Profile?.RecentRace;

        // Overall pace level: effort intent × optional Riegel calibration to the athlete's recent race, then
        // optionally clamped to the endurance-sustainable ceiling for this distance (so a hard effort can't
        // predict an unholdable average). Calibration already lands on the ceiling, so the cap only bites AllOut.
        double effortScale = opt.EffortScale;
        double levelScale = effortScale;
        if (opt.CalibrateToRecentRace)
            levelScale = effortScale * EnduranceCalibration.CalibrationScale(model, recent, targetKm);
        if (opt.CapToSustainable && recent is { IsValid: true })
        {
            double capRatio = EnduranceCalibration.CalibrationScale(model, recent, targetKm);
            levelScale = Math.Min(levelScale, capRatio);
        }

        // Aerobic decoupling: efforts harder than the fitted intensity fade faster, so steepen the fatigue decay.
        double kEff = model.Fatigue.K * (effortScale > 1.0 ? effortScale : 1.0);
        // Altitude derate: fitted value if present, else the requested default — only when opted in.
        double deratePerKm = opt.UseAltitude
            ? (model.Altitude.DeratePerKm > 0 ? model.Altitude.DeratePerKm : opt.AltitudeDeratePerKm)
            : 0;
        // Load model: fraction of body+pack mass that is "extra" pack, and whether poles help on climbs.
        bool useLoad = opt.UseLoadModel && opt.Profile is not null;
        double packFraction = useLoad && opt.Profile!.TotalMassKg is double tm && tm > 0 && opt.Profile.PackKg is double pk
            ? Math.Clamp(pk / tm, 0, 0.5) : 0;
        bool poles = useLoad && opt.Profile!.UsePoles;

        // Integrate time along the resampled grid, feeding fatigue effort forward.
        var timeAtRs = new double[rs.Count];   // seconds from start
        double cumAsc = 0, elapsed = 0;
        for (int i = 1; i < rs.Count; i++)
        {
            double dDist = cumRs[i] - cumRs[i - 1];
            double dEle = (rs[i].Ele ?? 0) - (rs[i - 1].Ele ?? 0);
            if (dEle > 0) cumAsc += dEle;
            double gradeDeg = dDist > 0.01 ? Math.Atan2(dEle, dDist) * 180 / Math.PI : 0;

            double effort = model.Fatigue.Driver switch
            {
                FatigueDriver.Elapsed => elapsed,
                FatigueDriver.Distance => cumRs[i - 1],
                _ => cumAsc,
            };

            double altMult = deratePerKm > 0
                ? AltitudeSpec.MultWith(rs[i].Ele, model.Altitude.RefM, deratePerKm, model.Altitude.Floor)
                : 1.0;

            double speed = model.BaseCurve.SpeedAt(gradeDeg)
                         * model.Fatigue.MultWith(effort, kEff)
                         * model.Turn.Mult(turn[i])
                         * altMult
                         * opt.SurfaceMult * surfAtRs[i]
                         * LoadMult(gradeDeg, packFraction, poles)
                         * levelScale;
            speed = Math.Max(opt.MinSpeedMps, speed);

            elapsed += dDist / speed;
            timeAtRs[i] = elapsed;
        }

        // Map the integrated times back onto the original points by along-track distance.
        var cumOrig = GeoMath.CumulativeDistancesM(pts);
        var copy = target.Clone();
        copy.Name = target.Name + " (predicted)";
        int j = 1;
        for (int i = 0; i < copy.Points.Count; i++)
        {
            double d = Math.Min(cumOrig[i], cumRs[^1]);
            while (j < rs.Count - 1 && cumRs[j] < d) j++;
            double d0 = cumRs[j - 1], d1 = cumRs[j];
            double f = d1 > d0 ? (d - d0) / (d1 - d0) : 0;
            double sec = timeAtRs[j - 1] + (timeAtRs[j] - timeAtRs[j - 1]) * f;
            copy.Points[i].Time = opt.StartTime.AddSeconds(sec);
        }
        copy.ResetBaseline();

        return new PredictResult
        {
            PredictedTrack = copy,
            TotalTime = TimeSpan.FromSeconds(elapsed),
            DistanceKm = cumOrig[^1] / 1000.0,
            Report = BuildReport(copy, model, opt, elapsed, cumOrig[^1]),
        };
    }

    /// <summary>Projects a per-original-point surface multiplier onto the resampled grid by along-track distance
    /// (nearest original point). Returns all-1.0 when no per-point surface was supplied.</summary>
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

    /// <summary>Climb-cost multiplier for carried load and poles on positive grades. The share of effort that is
    /// vertical work grows with steepness (≈full by ~20°), so added pack mass bites most on steep climbs and
    /// nothing on the flat/descents; poles return a small uphill economy gain over the same share.</summary>
    private static double LoadMult(double gradeDeg, double packFraction, bool poles)
    {
        if (gradeDeg <= 0 || (packFraction <= 0 && !poles)) return 1.0;
        double share = Math.Clamp(gradeDeg / 20.0, 0, 1);
        double m = 1.0 - packFraction * share;
        if (poles) m *= 1.0 + 0.03 * share;
        return m;
    }

    private static string BuildReport(Track copy, RaceModel model, PredictOptions opt, double totalSec, double distM)
    {
        var sb = new StringBuilder();
        var finish = opt.StartTime.AddSeconds(totalSec);
        sb.AppendLine($"Start:           {opt.StartTime:HH:mm}");
        sb.AppendLine($"Predicted finish:{finish:HH:mm}  ({TimeSpan.FromSeconds(totalSec):hh\\:mm\\:ss})");
        sb.AppendLine($"Distance:        {distM / 1000:F1} km");
        sb.AppendLine($"Avg moving:      {distM / totalSec * 3.6:F1} km/h");
        if (opt.Effort != RaceEffort.Race)
            sb.AppendLine($"Effort:          {opt.Effort} (×{opt.EffortScale:F2})");
        var recent = opt.Profile?.RecentRace;
        if (recent is { IsValid: true } && (opt.CalibrateToRecentRace || opt.CapToSustainable))
        {
            double km = distM / 1000.0;
            double scale = EnduranceCalibration.CalibrationScale(model, recent, km);
            if (opt.CalibrateToRecentRace)
                sb.AppendLine($"Calibrated:      ×{scale:F2} to your {recent.DistanceKm:F0} km / {recent.Time:hh\\:mm\\:ss} race");
            else
                sb.AppendLine($"Sustainable cap: ×{scale:F2} ceiling from your recent race");
            sb.AppendLine($"Riegel (flat) ⇒  {EnduranceCalibration.RiegelTime(recent, km):hh\\:mm\\:ss}  (terrain-blind cross-check)");
        }
        if (opt.UseLoadModel && opt.Profile?.TotalMassKg is double tmass && opt.Profile.PackKg is double pack && pack > 0)
            sb.AppendLine($"Load:            +{pack:F1} kg pack of {tmass:F0} kg{(opt.Profile.UsePoles ? ", poles on climbs" : "")}");
        else if (opt.UseLoadModel && opt.Profile?.UsePoles == true)
            sb.AppendLine("Load:            poles on climbs");
        if (opt.UseAltitude)
            sb.AppendLine("Altitude:        derate applied above reference elevation");
        // Waypoint ETAs, if the track carries named points.
        var wpts = copy.Points.Where(p => p.IsWaypoint && p.Time is not null).ToList();
        if (wpts.Count > 0)
        {
            sb.AppendLine("Waypoint ETAs:");
            foreach (var w in wpts)
                sb.AppendLine($"   {w.Time:HH:mm}  {w.Name}");
        }
        return sb.ToString().TrimEnd();
    }
}
