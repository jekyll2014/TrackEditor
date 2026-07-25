using System.Text.Json;
using System.Text.Json.Serialization;

namespace TrackEditor.Core.Services.RaceAnalysis;

public enum FatigueDriver { CumAscent, Elapsed, Distance }
public enum FatigueShape { Linear, Exp }

/// <summary>
/// A portable, human-readable description of one athlete's speed-vs-terrain behaviour, fitted from recorded
/// tracks and applied to predict pace on any planned track. Deliberately separable and interpretable rather
/// than an opaque matrix: predicted speed = baseCurve(grade) x fatigue(effort) x altitude x surface x temp,
/// so each factor can be inspected, fitted, and toggled independently. Serialized as *.racemodel.json.
/// </summary>
public class RaceModel
{
    public int Version { get; set; } = 1;
    public RaceModelMeta Meta { get; set; } = new();
    public BaseCurve BaseCurve { get; set; } = new();
    public FatigueSpec Fatigue { get; set; } = new();
    public AltitudeSpec Altitude { get; set; } = new();
    public AthleteBaseline AthleteBaseline { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);
    public static RaceModel FromJson(string json) =>
        JsonSerializer.Deserialize<RaceModel>(json, JsonOpts) ?? throw new FormatException("Empty race model.");

    public void Save(string path) => File.WriteAllText(path, ToJson());
    public static RaceModel Load(string path) => FromJson(File.ReadAllText(path));
}

public class RaceModelMeta
{
    public List<string> SourceTracks { get; set; } = new();
    public double TotalKm { get; set; }
    public DateTime FitDateUtc { get; set; }
    public List<string> SignalsUsed { get; set; } = new();
    public int SegmentsUsed { get; set; }
    public string? Notes { get; set; }
}

/// <summary>
/// Absolute reference speed (m/s) as a function of signed grade, sampled on a uniform grade grid.
/// SpeedMps[i] is the speed at grade GradeMinDeg + i*StepDeg. This is the "fresh" curve; fatigue scales it down.
/// </summary>
public class BaseCurve
{
    public double GradeMinDeg { get; set; } = -25;
    public double StepDeg { get; set; } = 1;
    public double[] SpeedMps { get; set; } = Array.Empty<double>();

    /// <summary>Interpolated fresh speed (m/s) at a signed grade, clamped to the fitted grade range.</summary>
    public double SpeedAt(double gradeDeg)
    {
        if (SpeedMps.Length == 0) return 0;
        double f = (gradeDeg - GradeMinDeg) / StepDeg;
        if (f <= 0) return SpeedMps[0];
        if (f >= SpeedMps.Length - 1) return SpeedMps[^1];
        int i = (int)f;
        double frac = f - i;
        return SpeedMps[i] + (SpeedMps[i + 1] - SpeedMps[i]) * frac;
    }
}

/// <summary>
/// Speed multiplier (1.0 fresh, decaying with accumulated effort) capturing the runner tiring over the race.
/// Driver is the accumulated quantity (cumulative ascent / elapsed seconds / distance) that best tracks it.
/// </summary>
public class FatigueSpec
{
    public FatigueDriver Driver { get; set; } = FatigueDriver.CumAscent;
    public FatigueShape Shape { get; set; } = FatigueShape.Linear;
    /// <summary>Decay coefficient in units of 1/driver-unit (e.g. per metre of ascent).</summary>
    public double K { get; set; }
    /// <summary>Lower clamp so long efforts never predict an absurdly small speed.</summary>
    public double Floor { get; set; } = 0.5;
    /// <summary>Observed HR drift (bpm per driver-unit) — reported for insight; not applied directly in v1.</summary>
    public double? HrDriftPerUnit { get; set; }

    public double Mult(double effort)
    {
        double m = Shape == FatigueShape.Exp ? Math.Exp(-K * effort) : 1.0 - K * effort;
        return Math.Clamp(m, Floor, 1.0);
    }
}

/// <summary>Physiological derate above a reference elevation. Neutral (disabled) in v1 to avoid
/// double-counting altitude already baked into the fitted speeds; wired for v2.</summary>
public class AltitudeSpec
{
    public double RefM { get; set; } = 1500;
    public double DeratePerKm { get; set; }   // 0 = disabled
    public double Floor { get; set; } = 0.7;

    public double Mult(double? eleM)
    {
        if (DeratePerKm <= 0 || eleM is not double e || e <= RefM) return 1.0;
        return Math.Clamp(1.0 - (e - RefM) / 1000.0 * DeratePerKm, Floor, 1.0);
    }
}

/// <summary>The intensity anchor each recorded track was normalized to, and its reference HR.</summary>
public class AthleteBaseline
{
    public double FlatSpeedMps { get; set; }
    public double? RefHr { get; set; }
}
