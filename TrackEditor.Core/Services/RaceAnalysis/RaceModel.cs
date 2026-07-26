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
    public TurnSpec Turn { get; set; } = new();
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
    /// <summary>Observed HR drift (bpm per driver-unit) from the fit — an aerobic-decoupling measure. Reported for
    /// insight and used to steepen fatigue for harder-than-fitted efforts (see <see cref="RacePredictor"/>).</summary>
    public double? HrDriftPerUnit { get; set; }

    public double Mult(double effort) => MultWith(effort, K);

    /// <summary>Same decay as <see cref="Mult"/> but with an explicit coefficient, so the predictor can apply an
    /// effort-adjusted <paramref name="k"/> (decoupling: pushing harder than the fitted intensity fades faster).</summary>
    public double MultWith(double effort, double k)
    {
        double m = Shape == FatigueShape.Exp ? Math.Exp(-k * effort) : 1.0 - k * effort;
        return Math.Clamp(m, Floor, 1.0);
    }
}

/// <summary>
/// Speed multiplier for course sinuosity (turn density, deg/m). Neutral (1.0) at <see cref="RefDegPerM"/> — the
/// average twistiness already baked into the base curve — so only relative twistiness moves it: tighter sections
/// derate, straighter give a mild boost. <see cref="Coeff"/> is fitted &lt;= 0 (turns never speed a runner up).
/// </summary>
public class TurnSpec
{
    /// <summary>Reference turn density (deg/m): the fit's mean, where the multiplier is exactly 1.0.</summary>
    public double RefDegPerM { get; set; }
    /// <summary>Change in multiplier per unit turn density above the reference; fitted &lt;= 0.</summary>
    public double Coeff { get; set; }
    public double Floor { get; set; } = 0.75;
    public double Ceil { get; set; } = 1.05;

    public double Mult(double turnDegPerM) =>
        Math.Clamp(1.0 + Coeff * (turnDegPerM - RefDegPerM), Floor, Ceil);
}

/// <summary>Physiological derate above a reference elevation. A fit leaves <see cref="DeratePerKm"/> at 0 (the
/// fitted speeds already bake in whatever altitude the recording saw); the predictor enables it with
/// <see cref="DefaultDeratePerKm"/> when the user asks to model a course higher than the fit.</summary>
public class AltitudeSpec
{
    /// <summary>Enabled-by-default derate (fraction of speed lost per 1000 m above <see cref="RefM"/>): ~4%/km,
    /// a moderate endurance-running value for altitudes to ~3000 m. Applied when the user opts into altitude and
    /// the model carries no fitted derate.</summary>
    public const double DefaultDeratePerKm = 0.04;

    public double RefM { get; set; } = 1500;
    public double DeratePerKm { get; set; }   // 0 = disabled
    public double Floor { get; set; } = 0.7;

    public double Mult(double? eleM) => MultWith(eleM, RefM, DeratePerKm, Floor);

    /// <summary>Altitude speed multiplier with explicit parameters, so the predictor can apply a default derate
    /// without mutating the stored model. Neutral (1.0) below <paramref name="refM"/> or when derate ≤ 0.</summary>
    public static double MultWith(double? eleM, double refM, double deratePerKm, double floor)
    {
        if (deratePerKm <= 0 || eleM is not double e || e <= refM) return 1.0;
        return Math.Clamp(1.0 - (e - refM) / 1000.0 * deratePerKm, floor, 1.0);
    }
}

/// <summary>The intensity anchor each recorded track was normalized to, and its reference HR.</summary>
public class AthleteBaseline
{
    public double FlatSpeedMps { get; set; }
    public double? RefHr { get; set; }
}
