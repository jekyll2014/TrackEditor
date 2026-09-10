using System.Text;

using TrackEditor.Core.Services.RaceAnalysis;
using TrackEditor.Localization;

namespace TrackEditor.Services;

public static class RaceFormatter
{
    public static string FormatAnalysisReport(AnalysisResult r, RaceAnalysisOptions? opt = null)
    {
        var m = r.Model;
        if (r.SegmentsUsed == 0)
            return Loc.Get("RaNoSegments");

        var sb = new StringBuilder();
        sb.AppendLine($"{Loc.Get("RaTracksFitted")} {m.Meta.SourceTracks.Count} ({string.Join(", ", m.Meta.SourceTracks)})");
        sb.AppendLine($"{Loc.Get("RaSegmentsUsed")} {m.Meta.SegmentsUsed}");
        sb.AppendLine($"{Loc.Get("RaSignals")} {string.Join(", ", m.Meta.SignalsUsed)}");
        sb.AppendLine($"{Loc.Get("RaFlatSpeed")} {m.AthleteBaseline.FlatSpeedMps * 3.6:F1} km/h");
        sb.AppendLine(Loc.Get("RaSpeedByGrade"));
        foreach (int g in new[] { -20, -10, -5, 0, 5, 10, 15, 20 })
            sb.AppendLine($"   {g,4}°: {m.BaseCurve.SpeedAt(g) * 3.6,5:F1} km/h");
        string unit = m.Fatigue.Driver switch
        {
            FatigueDriver.Elapsed => "s",
            FatigueDriver.Distance => "m",
            _ => "m climb",
        };
        sb.AppendLine($"{Loc.Get("RaFatigue")} k={m.Fatigue.K:G3} /{unit}; {Loc.Get("RaEndEffortSpeed")} x{m.Fatigue.Mult(r.MaxEffort):F2}");
        if (m.Fatigue.HrDriftPerUnit is double d)
            sb.AppendLine($"{Loc.Get("RaHrDrift")} {d * 1000:F1} bpm / 1000 {unit}");
        if (m.Turn.Coeff < 0)
        {
            double twisty = m.Turn.RefDegPerM * 3;
            sb.AppendLine($"{Loc.Get("RaTurnPenalty")} ref {m.Turn.RefDegPerM:F2}°/m; {Loc.Get("RaTwistySection")} x{m.Turn.Mult(twisty):F2}");
        }
        return sb.ToString().TrimEnd();
    }

    public static string FormatPredictionReport(PredictResult r, PredictOptions opt, Core.Services.RaceAnalysis.RaceModel model)
    {
        var sb = new StringBuilder();
        var finish = opt.StartTime.Add(r.TotalTime);
        double distM = r.DistanceKm * 1000;
        sb.AppendLine($"{Loc.Get("RpStart")} {opt.StartTime:HH:mm}");
        sb.AppendLine($"{Loc.Get("RpPredictedFinish")} {finish:HH:mm}  ({r.TotalTime:hh\\:mm\\:ss})");
        sb.AppendLine($"{Loc.Get("RpDistance")} {r.DistanceKm:F1} km");
        sb.AppendLine($"{Loc.Get("RpAvgMoving")} {distM / r.TotalTime.TotalSeconds * 3.6:F1} km/h");
        if (opt.Effort != RaceEffort.Race)
            sb.AppendLine($"{Loc.Get("RpEffort")} {opt.Effort} (×{opt.EffortScale:F2})");
        var recent = opt.Profile?.RecentRace;
        if (recent is { IsValid: true } && (opt.CalibrateToRecentRace || opt.CapToSustainable))
        {
            double scale = EnduranceCalibration.CalibrationScale(model, recent, r.DistanceKm);
            if (opt.CalibrateToRecentRace)
                sb.AppendLine($"{Loc.Get("RpCalibrated")} ×{scale:F2} {Loc.Get("RpToYourRace")} {recent.DistanceKm:F0} km / {recent.Time:hh\\:mm\\:ss}");
            else
                sb.AppendLine($"{Loc.Get("RpSustainableCap")} ×{scale:F2} {Loc.Get("RpCeilingFromRace")}");
            sb.AppendLine($"{Loc.Get("RpRiegel")} {EnduranceCalibration.RiegelTime(recent, r.DistanceKm):hh\\:mm\\:ss}");
        }
        if (opt.UseLoadModel && opt.Profile?.TotalMassKg is double tmass && opt.Profile.PackKg is double pack && pack > 0)
            sb.AppendLine($"{Loc.Get("RpLoad")} +{pack:F1} kg / {tmass:F0} kg{(opt.Profile.UsePoles ? $", {Loc.Get("RpPolesOnClimbs")}" : "")}");
        else if (opt.UseLoadModel && opt.Profile?.UsePoles == true)
            sb.AppendLine($"{Loc.Get("RpLoad")} {Loc.Get("RpPolesOnClimbs")}");
        if (opt.UseAltitude)
            sb.AppendLine(Loc.Get("RpAltitudeDerate"));
        var wpts = r.PredictedTrack.Points.Where(p => p.IsWaypoint && p.Time is not null).ToList();
        if (wpts.Count > 0)
        {
            sb.AppendLine(Loc.Get("RpWaypointETAs"));
            foreach (var w in wpts)
                sb.AppendLine($"   {w.Time:HH:mm}  {w.Name}");
        }
        return sb.ToString().TrimEnd();
    }
}
