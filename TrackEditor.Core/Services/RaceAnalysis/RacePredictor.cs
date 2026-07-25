using System.Text;

using TrackEditor.Core.Models;

namespace TrackEditor.Core.Services.RaceAnalysis;

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
    /// <summary>Apply the model's altitude derate (off by default / neutral in v1).</summary>
    public bool UseAltitude { get; set; } = false;
    public double SpacingM { get; set; } = TrackResampler.DefaultSpacingM;
    public double EleWindowM { get; set; } = TrackResampler.DefaultEleWindowM;
    /// <summary>Speed can never fall below this (m/s) — guards against divide-by-tiny on extreme grades.</summary>
    public double MinSpeedMps { get; set; } = 0.3;
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

            double speed = model.BaseCurve.SpeedAt(gradeDeg)
                         * model.Fatigue.Mult(effort)
                         * model.Turn.Mult(turn[i])
                         * (opt.UseAltitude ? model.Altitude.Mult(rs[i].Ele) : 1.0)
                         * opt.SurfaceMult * surfAtRs[i];
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

    private static string BuildReport(Track copy, RaceModel model, PredictOptions opt, double totalSec, double distM)
    {
        var sb = new StringBuilder();
        var finish = opt.StartTime.AddSeconds(totalSec);
        sb.AppendLine($"Start:           {opt.StartTime:HH:mm}");
        sb.AppendLine($"Predicted finish:{finish:HH:mm}  ({TimeSpan.FromSeconds(totalSec):hh\\:mm\\:ss})");
        sb.AppendLine($"Distance:        {distM / 1000:F1} km");
        sb.AppendLine($"Avg moving:      {distM / totalSec * 3.6:F1} km/h");
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
