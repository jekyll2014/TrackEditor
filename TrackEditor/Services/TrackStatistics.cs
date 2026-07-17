using System.Text;
using TrackEditor.Models;

namespace TrackEditor.Services;

public class TrackStats
{
    public int PointCount;
    public double DistanceM;
    public double? AscentM, DescentM, MinEleM, MaxEleM;
    public DateTime? StartTime, EndTime;
    public TimeSpan? Duration, MovingTime;
    public double? AvgSpeedMps, MovingAvgSpeedMps, MaxSpeedMps;
    public double? MaxGrade100mPct;       // steepest average grade over any ~100 m window
    public double? SteepDistanceM;        // distance covered on grades > 15 %
    public double? RoughnessMPerKm;       // (ascent + descent) per km — trail "hilliness" index
    public double? NetInclineDeg;         // signed average gradient (first→last elevation over horizontal distance)

    public string ToDisplayString(bool includeIncline = false)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Points:          {PointCount}");
        sb.AppendLine($"Distance:        {DistanceM / 1000:F2} km");
        if (AscentM is not null) sb.AppendLine($"Ascent:          {AscentM:F0} m");
        if (DescentM is not null) sb.AppendLine($"Descent:         {DescentM:F0} m");
        if (MinEleM is not null) sb.AppendLine($"Elevation:       {MinEleM:F0} … {MaxEleM:F0} m");
        if (includeIncline && NetInclineDeg is not null)
            sb.AppendLine($"Avg incline:     {NetInclineDeg:+0.0;-0.0;0.0}°  ({Math.Tan(NetInclineDeg.Value * Math.PI / 180) * 100:+0;-0;0} %)");
        if (RoughnessMPerKm is not null) sb.AppendLine($"Climb per km:    {RoughnessMPerKm:F0} m/km");
        if (MaxGrade100mPct is not null) sb.AppendLine($"Max grade/100m:  {MaxGrade100mPct:F0} %");
        if (SteepDistanceM is not null) sb.AppendLine($"Steep (>15%):    {SteepDistanceM / 1000:F2} km");
        if (StartTime is not null) sb.AppendLine($"Start:           {StartTime:yyyy-MM-dd HH:mm:ss}");
        if (Duration is not null) sb.AppendLine($"Duration:        {Fmt(Duration.Value)}");
        if (MovingTime is not null) sb.AppendLine($"Moving time:     {Fmt(MovingTime.Value)}");
        if (AvgSpeedMps is not null) sb.AppendLine($"Avg speed:       {AvgSpeedMps * 3.6:F1} km/h");
        if (MovingAvgSpeedMps is not null) sb.AppendLine($"Moving avg:      {MovingAvgSpeedMps * 3.6:F1} km/h");
        if (MaxSpeedMps is not null) sb.AppendLine($"Max speed:       {MaxSpeedMps * 3.6:F1} km/h");
        return sb.ToString().TrimEnd();
    }

    private static string Fmt(TimeSpan t) => $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}";
}

public static class TrackStatistics
{
    /// <summary>Elevation hysteresis (m) — ignores GPS elevation jitter below this threshold.</summary>
    public const double EleThresholdM = 3.0;
    /// <summary>A segment counts as "moving" when speed exceeds this (m/s).</summary>
    public const double MovingSpeedMps = 0.5;

    public static TrackStats Compute(IReadOnlyList<TrackPoint> pts)
    {
        var s = new TrackStats { PointCount = pts.Count };
        if (pts.Count < 2) return s;

        var cum = GeoMath.CumulativeDistancesM(pts);
        s.DistanceM = cum[^1];

        // --- elevation ---
        var eles = new List<(double CumM, double Ele)>();
        for (int i = 0; i < pts.Count; i++)
            if (pts[i].Ele is double e) eles.Add((cum[i], e));

        if (eles.Count >= 2)
        {
            double ascent = 0, descent = 0, min = double.MaxValue, max = double.MinValue;
            double refEle = eles[0].Ele;
            foreach (var (_, e) in eles)
            {
                min = Math.Min(min, e);
                max = Math.Max(max, e);
                double diff = e - refEle;
                if (diff >= EleThresholdM) { ascent += diff; refEle = e; }
                else if (diff <= -EleThresholdM) { descent -= diff; refEle = e; }
            }
            s.AscentM = ascent;
            s.DescentM = descent;
            s.MinEleM = min;
            s.MaxEleM = max;
            if (s.DistanceM > 100)
                s.RoughnessMPerKm = (ascent + descent) / (s.DistanceM / 1000.0);

            // net average incline over the elevation span (signed: + uphill, − downhill)
            double spanDx = eles[^1].CumM - eles[0].CumM;
            if (spanDx > 1)
                s.NetInclineDeg = Math.Atan2(eles[^1].Ele - eles[0].Ele, spanDx) * 180 / Math.PI;

            // steepest 100 m window + distance on >15 % grade
            double maxGrade = 0, steepDist = 0;
            int j = 0;
            for (int i = 1; i < eles.Count; i++)
            {
                // per-segment grade for steep-distance accounting
                double segD = eles[i].CumM - eles[i - 1].CumM;
                double segE = Math.Abs(eles[i].Ele - eles[i - 1].Ele);
                if (segD > 1 && segE / segD > 0.15) steepDist += segD;

                // sliding ~100 m window
                while (eles[i].CumM - eles[j].CumM > 100 && j < i - 1) j++;
                double d = eles[i].CumM - eles[j].CumM;
                if (d > 30)
                    maxGrade = Math.Max(maxGrade, Math.Abs(eles[i].Ele - eles[j].Ele) / d);
            }
            s.MaxGrade100mPct = maxGrade * 100;
            s.SteepDistanceM = steepDist;
        }

        // --- time / speed ---
        if (pts[0].Time is DateTime t0 && pts[^1].Time is DateTime t1 && t1 > t0)
        {
            s.StartTime = t0;
            s.EndTime = t1;
            s.Duration = t1 - t0;
            s.AvgSpeedMps = s.DistanceM / (t1 - t0).TotalSeconds;

            double movingSec = 0, movingDist = 0, maxSpeed = 0;
            for (int i = 1; i < pts.Count; i++)
            {
                if (pts[i - 1].Time is DateTime ta && pts[i].Time is DateTime tb && tb > ta)
                {
                    double dt = (tb - ta).TotalSeconds;
                    double dd = cum[i] - cum[i - 1];
                    double v = dd / dt;
                    if (v > MovingSpeedMps) { movingSec += dt; movingDist += dd; }
                    if (dt >= 1) maxSpeed = Math.Max(maxSpeed, v);
                }
            }
            if (movingSec > 0)
            {
                s.MovingTime = TimeSpan.FromSeconds(movingSec);
                s.MovingAvgSpeedMps = movingDist / movingSec;
            }
            s.MaxSpeedMps = maxSpeed;
        }

        return s;
    }
}
