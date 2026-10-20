namespace TrackEditor.Core.Services.RaceAnalysis;

/// <summary>Running surface / distance profile that controls how much pace changes with race distance (Riegel b).
/// Road runners show the strongest distance effect; ultra-trail specialists show almost none because terrain,
/// not distance, drives fatigue.</summary>
public enum RunningProfile
{
    /// <summary>Road / track running. Classic Riegel b=1.06 — pace improves ~4% per halving of distance.</summary>
    Road,
    /// <summary>Trail running. b=1.04 and distance ratio capped at 4:1 — pace-distance curve is flatter.</summary>
    Trail,
    /// <summary>Ultra trail. b=1.02, cap 3:1 — flat curve; terrain dominates fatigue, not total distance.</summary>
    UltraTrail,
}

/// <summary>
/// Converts a single recent-race result into a duration-appropriate sustainable pace, using Riegel's endurance
/// law (t = a·dᵇ, b≈1.06 — the well-validated "how a runner's pace fades with distance" relation). Two uses that
/// share the same math so they never double-count:
///  • <b>Calibration</b> — scale a fitted model's overall level so its flat pace matches what the athlete can
///    actually hold over <i>this</i> route's distance (the model supplies terrain shape, the race supplies level).
///  • <b>Sustainable cap</b> — the same endurance-adjusted pace as an upper ceiling, so an aggressive effort
///    setting (or a short-race model applied to a long route) can't predict an unsustainable average pace.
/// When calibration is on, expected pace already equals the ceiling, so the cap only bites on top-end efforts.
/// </summary>
public static class EnduranceCalibration
{
    /// <summary>Riegel fatigue exponent. 1.06 is the classic distance-running value (pace slows ~4% per doubling).</summary>
    public const double RiegelExponent = 1.06;

    /// <summary>Riegel exponent for the given profile.</summary>
    public static double ExponentFor(RunningProfile p) => p switch
    {
        RunningProfile.Trail     => 1.04,
        RunningProfile.UltraTrail => 1.02,
        _ => RiegelExponent,
    };

    /// <summary>Maximum training/target distance ratio applied before Riegel scaling. Prevents the correction from
    /// running away when a very-long-race model is applied to a short target. Road has no cap (∞).</summary>
    public static double MaxRatioFor(RunningProfile p) => p switch
    {
        RunningProfile.Trail     => 4.0,
        RunningProfile.UltraTrail => 3.0,
        _ => double.PositiveInfinity,
    };

    /// <summary>Endurance-adjusted flat speed (m/s) the athlete can sustain over <paramref name="targetKm"/>,
    /// projected from their recent race by Riegel: v₂ = v₁·(d₂/d₁)^(1−b).</summary>
    public static double AdjustedFlatSpeed(RecentRace r, double targetKm, double exponent = RiegelExponent)
    {
        if (!r.IsValid || targetKm <= 0) return 0;
        return r.AvgSpeedMps * Math.Pow(targetKm / r.DistanceKm, 1.0 - exponent);
    }

    /// <summary>Pure distance-only Riegel finish time for <paramref name="targetKm"/> — a terrain-blind cross-check
    /// against the terrain-aware model prediction, not a substitute for it.</summary>
    public static TimeSpan RiegelTime(RecentRace r, double targetKm, double exponent = RiegelExponent)
    {
        if (!r.IsValid || targetKm <= 0) return TimeSpan.Zero;
        return TimeSpan.FromSeconds(r.Time.TotalSeconds * Math.Pow(targetKm / r.DistanceKm, exponent));
    }

    /// <summary>Multiplier that rescales a fitted model's flat pace to the endurance-adjusted pace for the target
    /// distance. Clamped to [0.5, 1.8] so a road-race anchor applied to a rough trail model can't run away.
    /// Returns 1.0 (no change) when the race is missing/invalid or the model has no flat baseline.</summary>
    public static double CalibrationScale(RaceModel model, RecentRace? race, double targetKm)
    {
        if (race is null || !race.IsValid) return 1.0;
        double flat = model.AthleteBaseline.FlatSpeedMps;
        double target = AdjustedFlatSpeed(race, targetKm);
        if (flat <= 0 || target <= 0) return 1.0;
        return Math.Clamp(target / flat, 0.5, 1.8);
    }

    /// <summary>Pure distance-scaling factor when a model fitted at <paramref name="trainKm"/> is applied to
    /// <paramref name="targetKm"/>. Uses Riegel: <c>(d_target/d_train)^(1-b)</c>. No race anchor needed —
    /// the training distance serves as the reference. Returns 1.0 when distances are equal or either is zero.
    /// Clamped to [0.5, 2.0] to prevent extreme extrapolations.
    /// <paramref name="maxRatio"/> caps how far apart the two distances can be (train/target); use
    /// <see cref="MaxRatioFor"/> for the right value per profile. Default = no cap.</summary>
    public static double DistanceScale(double trainKm, double targetKm,
        double exponent = RiegelExponent, double maxRatio = double.PositiveInfinity)
    {
        if (trainKm <= 0 || targetKm <= 0) return 1.0;
        // Cap the effective training distance so a very long training race doesn't over-correct a short target.
        double effectiveTrain = double.IsFinite(maxRatio) ? Math.Min(trainKm, targetKm * maxRatio) : trainKm;
        double ratio = targetKm / effectiveTrain;
        if (Math.Abs(ratio - 1.0) < 0.01) return 1.0;
        return Math.Clamp(Math.Pow(ratio, 1.0 - exponent), 0.5, 2.0);
    }

    /// <summary>Convenience overload that derives exponent and maxRatio from a <see cref="RunningProfile"/>.</summary>
    public static double DistanceScale(double trainKm, double targetKm, RunningProfile profile)
        => DistanceScale(trainKm, targetKm, ExponentFor(profile), MaxRatioFor(profile));
}
