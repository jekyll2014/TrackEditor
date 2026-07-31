using System.Text;

using TrackEditor.Core.Models;

namespace TrackEditor.Core.Services;

public enum TerrainClass { Unknown, Flat, Rolling, Hilly, Mountainous }
public enum LoadClass { Unknown, Speed, Endurance, Climbing, Mixed }
public enum IntensityClass { Unknown, Easy, Moderate, Hard }
public enum SurfaceClass { Unknown, Road, Trail, Mixed }

/// <summary>
/// An athlete-independent characterisation of one track: what terrain it covers, what kind of load it
/// demands, and roughly how much energy it costs. Computed from the track's own geometry/time/surface so
/// two tracks can be compared on the same footing (energy uses a fixed reference mass, not the user's).
/// Intended for a hover summary in the track list and, later, for matching recorded tracks to a target.
/// </summary>
public class TrackClass
{
    public TerrainClass Terrain { get; set; }
    public SurfaceClass Surface { get; set; }
    public LoadClass Load { get; set; }
    public IntensityClass Intensity { get; set; }

    public double DistanceKm { get; set; }
    public double AscentM { get; set; }
    public double DescentM { get; set; }
    /// <summary>Hilliness index: (ascent + descent) per km. Drives the terrain band.</summary>
    public double ClimbPerKm { get; set; }
    public double? AvgSpeedKmh { get; set; }
    public TimeSpan? Duration { get; set; }
    /// <summary>Reference-mass energy estimate (kcal) — flat work plus vertical climb work.</summary>
    public int Kcal { get; set; }

    public bool HasEle { get; set; }
    public bool Timed { get; set; }

    /// <summary>Compact one-liner, e.g. "Hilly · Endurance · ~1240 kcal".</summary>
    public string Summary()
    {
        var parts = new List<string>();
        if (Terrain != TerrainClass.Unknown) parts.Add(Terrain.ToString());
        if (Load != LoadClass.Unknown) parts.Add(Load.ToString());
        if (Kcal > 0) parts.Add($"~{Kcal} kcal");
        return parts.Count > 0 ? string.Join(" · ", parts) : "No classification (needs ≥ 2 points)";
    }

    /// <summary>Multi-line breakdown for a hover tooltip.</summary>
    public string Tooltip()
    {
        var sb = new StringBuilder();
        string terrain = Terrain == TerrainClass.Unknown ? "Unknown (no elevation)" : Terrain.ToString();
        if (Surface != SurfaceClass.Unknown) terrain += $" · {Surface}";
        sb.AppendLine($"Terrain: {terrain}");
        if (Load != LoadClass.Unknown) sb.AppendLine($"Load: {Load}");
        if (Kcal > 0)
            sb.AppendLine(Intensity == IntensityClass.Unknown
                ? $"Effort: ~{Kcal} kcal"
                : $"Effort: ~{Kcal} kcal · {Intensity}");

        var facts = new List<string> { $"{DistanceKm:0.0} km" };
        if (HasEle) facts.Add($"+{AscentM:0}/-{DescentM:0} m");
        if (Duration is TimeSpan d) facts.Add($"{(int)d.TotalHours}:{d.Minutes:D2}");
        if (AvgSpeedKmh is double v) facts.Add($"{v:0.0} km/h");
        sb.Append(string.Join(" · ", facts));
        return sb.ToString();
    }
}

/// <summary>
/// Derives a <see cref="TrackClass"/> from a track's points. All thresholds are deliberate, documented
/// heuristics rather than a fitted classifier — the goal is a quick, comparable label, not a precise
/// physiological readout. Energy uses a fixed reference mass so tracks stay comparable across athletes.
/// </summary>
public static class TrackClassifier
{
    /// <summary>Body mass (kg) the energy estimate assumes, so a track's kcal is a property of the route,
    /// not of whoever is viewing it. Scale linearly for a different mass.</summary>
    public const double ReferenceMassKg = 70.0;

    // Net metabolic cost of level running/hiking, ~1 kcal per kg per km — a common textbook approximation.
    private const double KcalPerKgKm = 1.0;
    // Fraction of metabolic energy that becomes mechanical work climbing; the rest is heat. ~25 % is typical.
    private const double ClimbEfficiency = 0.25;
    private const double G = 9.80665;
    private const double JoulesPerKcal = 4184.0;

    public static TrackClass Classify(IReadOnlyList<TrackPoint> pts)
    {
        var c = new TrackClass();
        if (pts.Count < 2) return c;

        var s = TrackStatistics.Compute(pts);
        c.DistanceKm = s.DistanceM / 1000.0;
        c.HasEle = s.AscentM is not null;
        c.AscentM = s.AscentM ?? 0;
        c.DescentM = s.DescentM ?? 0;
        c.ClimbPerKm = s.RoughnessMPerKm ?? 0;
        c.Duration = s.Duration;
        c.Timed = s.AvgSpeedMps is not null;
        // Prefer moving average (excludes stops) for a fairer "how fast" read; fall back to overall.
        if ((s.MovingAvgSpeedMps ?? s.AvgSpeedMps) is double mps) c.AvgSpeedKmh = mps * 3.6;

        c.Terrain = TerrainOf(c);
        c.Surface = SurfaceOf(pts);
        c.Kcal = (int)Math.Round(Energy(c.DistanceKm, c.AscentM));
        c.Load = LoadOf(c);
        c.Intensity = IntensityOf(c);
        return c;
    }

    // Terrain from the hilliness index (ascent + descent per km). No elevation ⇒ Unknown.
    private static TerrainClass TerrainOf(TrackClass c)
    {
        if (!c.HasEle) return TerrainClass.Unknown;
        double r = c.ClimbPerKm;
        if (r < 20) return TerrainClass.Flat;
        if (r < 50) return TerrainClass.Rolling;
        if (r < 100) return TerrainClass.Hilly;
        return TerrainClass.Mountainous;
    }

    // OSM surface tokens split into paved vs unpaved; the paved fraction picks Road / Mixed / Trail.
    // Unknown when too few points carry a surface to judge.
    private static SurfaceClass SurfaceOf(IReadOnlyList<TrackPoint> pts)
    {
        int paved = 0, unpaved = 0;
        foreach (var p in pts)
        {
            if (string.IsNullOrEmpty(p.Surface)) continue;
            if (IsPaved(p.Surface)) paved++; else unpaved++;
        }
        int tagged = paved + unpaved;
        if (tagged < Math.Max(3, pts.Count / 10)) return SurfaceClass.Unknown;  // not enough coverage to trust
        double pavedFrac = (double)paved / tagged;
        if (pavedFrac >= 0.7) return SurfaceClass.Road;
        if (pavedFrac <= 0.3) return SurfaceClass.Trail;
        return SurfaceClass.Mixed;
    }

    private static bool IsPaved(string surface) => surface.ToLowerInvariant() switch
    {
        "asphalt" or "paved" or "concrete" or "concrete:plates" or "paving_stones"
            or "sett" or "chipseal" or "cobblestone" or "metal" or "wood" => true,
        _ => false,
    };

    // Reference-mass energy: level component (kcal/kg/km) plus the metabolic cost of raising the body mass
    // through the total ascent (mechanical work / efficiency).
    private static double Energy(double distanceKm, double ascentM)
    {
        double flat = ReferenceMassKg * distanceKm * KcalPerKgKm;
        double climb = ReferenceMassKg * G * ascentM / ClimbEfficiency / JoulesPerKcal;
        return flat + climb;
    }

    // Dominant demand. Order matters: a very hilly route reads as Climbing even if it's also long; a short,
    // quick, flat effort reads as Speed; otherwise long ⇒ Endurance, else Mixed.
    private static LoadClass LoadOf(TrackClass c)
    {
        if (c.DistanceKm < 0.5) return LoadClass.Unknown;
        if (c.HasEle && c.ClimbPerKm >= 80) return LoadClass.Climbing;
        if (c.Timed && c.AvgSpeedKmh is double v && v >= 11 && c.DistanceKm <= 12) return LoadClass.Speed;
        if (c.DistanceKm >= 20 || (c.Duration?.TotalHours ?? 0) >= 2.5) return LoadClass.Endurance;
        return LoadClass.Mixed;
    }

    // Rough intensity from energy burned per hour (needs time). Bands are running-ish approximations.
    private static IntensityClass IntensityOf(TrackClass c)
    {
        if (!c.Timed || c.Duration is not TimeSpan d || d.TotalHours <= 0 || c.Kcal <= 0)
            return IntensityClass.Unknown;
        double kcalPerHour = c.Kcal / d.TotalHours;
        if (kcalPerHour < 500) return IntensityClass.Easy;
        if (kcalPerHour < 800) return IntensityClass.Moderate;
        return IntensityClass.Hard;
    }

    /// <summary>Score in 0..1 of how good an analog one track is for another — i.e. how well a recorded track
    /// would model a prediction target. Compares terrain band, load, surface and distance; a dimension that is
    /// Unknown on either side is skipped and the rest are re-weighted. Returns a neutral 0.5 when nothing is
    /// comparable (e.g. neither track has elevation, time or surface).</summary>
    public static double Similarity(TrackClass a, TrackClass b)
    {
        double wSum = 0, sSum = 0;
        void Add(double w, double s) { wSum += w; sSum += w * s; }

        if (a.Terrain != TerrainClass.Unknown && b.Terrain != TerrainClass.Unknown)
        {
            int d = Math.Abs((int)a.Terrain - (int)b.Terrain);
            Add(0.35, d == 0 ? 1.0 : d == 1 ? 0.5 : 0.0);   // adjacent bands are partly comparable
        }
        if (a.Load != LoadClass.Unknown && b.Load != LoadClass.Unknown)
            Add(0.30, a.Load == b.Load ? 1.0 : 0.0);
        if (a.Surface != SurfaceClass.Unknown && b.Surface != SurfaceClass.Unknown)
            Add(0.15, a.Surface == b.Surface ? 1.0 : 0.5);  // Road vs Trail still share some traits
        if (a.DistanceKm > 0 && b.DistanceKm > 0)
            Add(0.20, Math.Min(a.DistanceKm, b.DistanceKm) / Math.Max(a.DistanceKm, b.DistanceKm));

        return wSum > 0 ? sSum / wSum : 0.5;
    }

    /// <summary>Whether <paramref name="candidate"/> is a good enough analog of <paramref name="target"/> to
    /// highlight it as a fitting input (<see cref="Similarity"/> ≥ 0.6).</summary>
    public static bool IsAnalog(TrackClass target, TrackClass candidate) => Similarity(target, candidate) >= 0.6;
}
