using System.Text;

using TrackEditor.Core.Models;
using TrackEditor.Core.Services;
using TrackEditor.Localization;

namespace TrackEditor.Services;

public static class TrackFormatter
{
    public static string FormatStats(TrackStats s, bool includeIncline = false, bool paceMode = false)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{Loc.Get("StatsPoints")} {s.PointCount}");
        sb.AppendLine($"{Loc.Get("StatsDistance")} {s.DistanceM / 1000:F2} km");
        if (s.AscentM is not null) sb.AppendLine($"{Loc.Get("StatsAscent")} {s.AscentM:F0} m");
        if (s.DescentM is not null) sb.AppendLine($"{Loc.Get("StatsDescent")} {s.DescentM:F0} m");
        if (s.MinEleM is not null) sb.AppendLine($"{Loc.Get("StatsElevation")} {s.MinEleM:F0} … {s.MaxEleM:F0} m");
        if (includeIncline && s.NetInclineDeg is not null)
            sb.AppendLine($"{Loc.Get("StatsAvgIncline")} {s.NetInclineDeg:+0.0;-0.0;0.0}°  ({Math.Tan(s.NetInclineDeg.Value * Math.PI / 180) * 100:+0;-0;0} %)");
        if (s.RoughnessMPerKm is not null) sb.AppendLine($"{Loc.Get("StatsClimbPerKm")} {s.RoughnessMPerKm:F0} m/km");
        if (s.MaxGrade100mPct is not null) sb.AppendLine($"{Loc.Get("StatsMaxGrade")} {s.MaxGrade100mPct:F0} %");
        if (s.SteepDistanceM is not null) sb.AppendLine($"{Loc.Get("StatsSteep")} {s.SteepDistanceM / 1000:F2} km");
        if (s.StartTime is not null) sb.AppendLine($"{Loc.Get("StatsStart")} {s.StartTime:yyyy-MM-dd HH:mm:ss}");
        if (s.Duration is not null) sb.AppendLine($"{Loc.Get("StatsDuration")} {FmtTime(s.Duration.Value)}");
        if (s.MovingTime is not null) sb.AppendLine($"{Loc.Get("StatsMovingTime")} {FmtTime(s.MovingTime.Value)}");
        if (s.AvgSpeedMps is not null) sb.AppendLine(SpeedLine("StatsAvgSpeed", "StatsAvgPace", s.AvgSpeedMps.Value, paceMode));
        if (s.MovingAvgSpeedMps is not null) sb.AppendLine(SpeedLine("StatsMovingAvg", "StatsMovingPace", s.MovingAvgSpeedMps.Value, paceMode));
        if (s.MaxSpeedMps is not null) sb.AppendLine(SpeedLine("StatsMaxSpeed", "StatsBestPace", s.MaxSpeedMps.Value, paceMode));
        return sb.ToString().TrimEnd();
    }

    public static (string Caption, string HighLabel, string LowLabel) FormatGradientLegend(TrackGradientResult g)
    {
        string caption = g.Metric switch
        {
            GradientMetric.Speed => Loc.Get(g.PaceMode ? "GradPace" : "GradSpeed"),
            GradientMetric.Inclination => Loc.Get("GradGrade"),
            GradientMetric.Pavement => Loc.Get("GradSurface"),
            _ => "",
        };
        string high, low;
        switch (g.Metric)
        {
            case GradientMetric.Speed:
                high = FmtSpeed(g.HighValue, g.PaceMode);
                low = FmtSpeed(g.LowValue, g.PaceMode);
                break;
            case GradientMetric.Inclination:
                high = FmtGrade(g.HighValue, g.GradeUnit);
                low = FmtGrade(g.LowValue, g.GradeUnit);
                break;
            case GradientMetric.Pavement:
                high = Loc.Get("GradEasy");
                low = Loc.Get("GradHard");
                break;
            default:
                high = low = "";
                break;
        }
        return (caption, high, low);
    }

    private static string SpeedLine(string kmhKey, string paceKey, double mps, bool paceMode)
        => paceMode
            ? $"{Loc.Get(paceKey)} {PaceFormat.MinPerKm(mps)} /km"
            : $"{Loc.Get(kmhKey)} {mps * 3.6:F1} km/h";

    private static string FmtTime(TimeSpan t) => $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}";

    private static string FmtSpeed(double mps, bool paceMode) =>
        double.IsNaN(mps) ? "—"
        : paceMode ? $"{PaceFormat.MinPerKm(mps)} /km"
        : $"{mps * 3.6:F1} km/h";

    private static string FmtGrade(double pct, GradeUnit unit)
    {
        if (double.IsNaN(pct)) return "—";
        return unit == GradeUnit.Degree
            ? $"{Math.Atan(pct / 100.0) * (180.0 / Math.PI):+0.#;-0.#;0}°"
            : $"{pct:+0.#;-0.#;0}%";
    }
}
