using System.Text.Json.Serialization;

namespace TrackEditor.Core.Services.RaceAnalysis;

public enum Sex { Unspecified, Female, Male }

/// <summary>A recent race result used as an endurance anchor (Riegel). Distance + finish time is the single
/// best predictor of endurance pace, and lets an athlete with no fitted history still get a calibrated model.</summary>
public class RecentRace
{
    public double DistanceKm { get; set; }
    public TimeSpan Time { get; set; }

    [JsonIgnore]
    public bool IsValid => DistanceKm > 0.5 && Time > TimeSpan.FromMinutes(1);

    /// <summary>Average speed (m/s) of the recorded race.</summary>
    [JsonIgnore]
    public double AvgSpeedMps => IsValid ? DistanceKm * 1000.0 / Time.TotalSeconds : 0;

    public RecentRace Clone() => new() { DistanceKm = DistanceKm, Time = Time };
}

/// <summary>
/// Athlete-level physiology and biometrics, persisted once in settings and reused across predictions. Separate
/// from a fitted <see cref="RaceModel"/> (which describes speed-vs-terrain from recorded tracks): the profile is
/// the person, the model is their behaviour on a course. Every field is optional — the prediction degrades
/// gracefully to whatever the athlete has supplied. Consumed by Race Analysis: effort scaling + HR normalization
/// (this phase), Riegel endurance anchor, critical-speed cap, and load/pole climb-cost adjustments (later phases).
/// </summary>
public class AthleteProfile
{
    // --- biometrics ---
    /// <summary>Body mass (kg). Drives climbing cost (W/kg) and combines with pack mass.</summary>
    public double? MassKg { get; set; }
    public int? Age { get; set; }
    public Sex Sex { get; set; } = Sex.Unspecified;

    // --- heart-rate anchors ---
    public int? HrMaxBpm { get; set; }
    public int? RestingHrBpm { get; set; }
    /// <summary>Lactate-threshold HR — the sustainable-for-~1h ceiling; anchors the critical-speed cap.</summary>
    public int? LthrBpm { get; set; }

    // --- load carried (climb-cost phase) ---
    /// <summary>Pack / vest weight (kg) carried on the route; added to body mass for climbing cost.</summary>
    public double? PackKg { get; set; }
    /// <summary>Trekking poles in use — a measured uphill running-economy gain on steep grade.</summary>
    public bool UsePoles { get; set; }

    // --- endurance anchor (Riegel phase) ---
    public RecentRace? RecentRace { get; set; }

    // --- cycling ---
    /// <summary>Functional threshold power (W) — highest power sustainable for ~1 hour. Primary anchor for
    /// physics-based cycling predictions (Option B).</summary>
    public double? FtpW { get; set; }
    /// <summary>Critical power (W) from a CP test. If absent, the predictor uses 95% of FTP as a proxy.</summary>
    public double? CriticalPowerW { get; set; }
    /// <summary>Anaerobic work capacity W' (J). Depleted above CP, recovers below. Typical range 15 000–25 000 J.
    /// Defaults to 20 000 J when absent.</summary>
    public double? WPrimeJ { get; set; }
    /// <summary>Bike mass (kg). Added to body mass for physics predictions. Road bike ≈8–10 kg, XC ≈11–14 kg.</summary>
    public double? BikeKg { get; set; }

    [JsonIgnore]
    public bool IsEmpty =>
        MassKg is null && Age is null && Sex == Sex.Unspecified &&
        HrMaxBpm is null && RestingHrBpm is null && LthrBpm is null &&
        PackKg is null && !UsePoles && (RecentRace is null || !RecentRace.IsValid) &&
        FtpW is null && CriticalPowerW is null && WPrimeJ is null && BikeKg is null;

    /// <summary>Best available HRmax: the entered value, else Tanaka age estimate (208 − 0.7·age), else null.</summary>
    [JsonIgnore]
    public int? EffectiveHrMaxBpm =>
        HrMaxBpm ?? (Age is int a && a is > 0 and < 120 ? (int)Math.Round(208 - 0.7 * a) : null);

    /// <summary>Best available lactate-threshold HR: entered value, else ≈88% of HRmax (typical endurance runner).</summary>
    [JsonIgnore]
    public int? EffectiveLthrBpm =>
        LthrBpm ?? (EffectiveHrMaxBpm is int hm ? (int)Math.Round(0.88 * hm) : null);

    /// <summary>Karvonen HR-reserve fraction for a heart rate: (hr − rest)/(max − rest), clamped to [0,1].
    /// Null when HRmax or resting HR is unknown. Normalizes intensity across sessions and athletes.</summary>
    public double? HrReserveFraction(double hr)
    {
        if (EffectiveHrMaxBpm is not int max || RestingHrBpm is not int rest || max <= rest) return null;
        return Math.Clamp((hr - rest) / (double)(max - rest), 0.0, 1.0);
    }

    /// <summary>Total mass hauled uphill (body + pack), kg. Null when body mass is unknown.</summary>
    [JsonIgnore]
    public double? TotalMassKg => MassKg is double m ? m + (PackKg ?? 0) : null;

    /// <summary>Effective critical power: user-supplied CP, or 95% of FTP (standard physiological proxy).</summary>
    [JsonIgnore]
    public double? EffectiveCpW => CriticalPowerW ?? (FtpW is double f ? f * 0.95 : null);

    /// <summary>Total cycling system mass (rider + bike), kg. Falls back to a 9 kg bike estimate when not supplied.</summary>
    [JsonIgnore]
    public double? TotalCyclingMassKg => MassKg is double m ? m + (BikeKg ?? 9.0) : null;

    public AthleteProfile Clone() => new()
    {
        MassKg = MassKg, Age = Age, Sex = Sex,
        HrMaxBpm = HrMaxBpm, RestingHrBpm = RestingHrBpm, LthrBpm = LthrBpm,
        PackKg = PackKg, UsePoles = UsePoles,
        RecentRace = RecentRace?.Clone(),
        FtpW = FtpW, CriticalPowerW = CriticalPowerW, WPrimeJ = WPrimeJ, BikeKg = BikeKg,
    };
}
