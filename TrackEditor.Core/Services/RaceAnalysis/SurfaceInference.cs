using System.Text;

using TrackEditor.Core.Models;

namespace TrackEditor.Core.Services.RaceAnalysis;

/// <summary>Maps OSM way tags (surface / tracktype / highway) to a running-speed multiplier.</summary>
public static class SurfaceCatalog
{
    /// <summary>
    /// Speed multiplier for a BRouter WayTags string (e.g. <c>"highway=path surface=gravel tracktype=grade3"</c>).
    /// Returns null when the tags carry no usable surface hint, so the caller can treat that stretch as unknown
    /// rather than guessing. Priority: explicit <c>surface</c> &gt; <c>tracktype</c> &gt; <c>highway</c>.
    /// </summary>
    public static double? MultForTags(string? wayTags)
    {
        if (string.IsNullOrWhiteSpace(wayTags)) return null;
        string? surface = null, tracktype = null, highway = null;
        foreach (var tok in wayTags.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = tok.IndexOf('=');
            if (eq <= 0) continue;
            string key = tok[..eq], val = tok[(eq + 1)..].ToLowerInvariant();
            switch (key)
            {
                case "surface": surface = val; break;
                case "tracktype": tracktype = val; break;
                case "highway": highway = val; break;
            }
        }

        if (surface is not null) return SurfaceMult(surface);
        if (tracktype is not null) return TrackTypeMult(tracktype);
        if (highway is not null) return HighwayMult(highway);
        return null;
    }

    /// <summary>Human-readable surface label from a WayTags string (the chosen tag's value, same priority as
    /// <see cref="MultForTags"/>): e.g. "gravel", "grade3", "path". Null when no usable tag is present.</summary>
    public static string? TypeForTags(string? wayTags)
    {
        if (string.IsNullOrWhiteSpace(wayTags)) return null;
        string? surface = null, tracktype = null, highway = null;
        foreach (var tok in wayTags.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = tok.IndexOf('=');
            if (eq <= 0) continue;
            string key = tok[..eq], val = tok[(eq + 1)..].ToLowerInvariant();
            switch (key)
            {
                case "surface": surface = val; break;
                case "tracktype": tracktype = val; break;
                case "highway": highway = val; break;
            }
        }
        return surface ?? tracktype ?? highway;
    }

    /// <summary>Passability multiplier for a bare surface/tracktype/highway token (as stored on
    /// <see cref="TrackPoint.Surface"/>), e.g. "gravel", "grade3", "path". Higher = easier/faster.
    /// A null/blank/unknown token falls back to the "unpaved" value, so stretches with no surface
    /// information are treated as unpaved rather than dropped.</summary>
    public static double PassabilityForToken(string? token)
    {
        double unpaved = SurfaceMult("unpaved")!.Value;
        if (string.IsNullOrWhiteSpace(token)) return unpaved;
        string v = token.Trim().ToLowerInvariant();
        return SurfaceMult(v) ?? TrackTypeMult(v) ?? HighwayMult(v) ?? unpaved;
    }

    /// <summary>The span of <see cref="PassabilityForToken"/> values, for normalising a pavement gradient.</summary>
    public const double PassabilityMin = 0.60;   // steps — hardest
    public const double PassabilityMax = 1.05;   // asphalt / paved road — easiest

    private static double? SurfaceMult(string s) => s switch
    {
        "asphalt" or "paved" or "concrete" or "concrete:plates" or "paving_stones" or "metal" or "wood" => 1.05,
        "compacted" or "fine_gravel" or "gravel" or "pebblestone" or "cobblestone" or "sett" or "unhewn_cobblestone" => 1.00,
        "unpaved" or "ground" or "dirt" or "earth" or "woodchips" => 0.95,
        "grass" or "grass_paver" or "meadow" => 0.90,
        "sand" or "mud" or "clay" or "snow" or "ice" => 0.80,
        "rock" or "stepping_stones" or "bare_rock" or "scree" => 0.78,
        _ => null,   // unrecognised surface -> unknown, don't guess
    };

    private static double? TrackTypeMult(string t) => t switch
    {
        "grade1" => 1.02,
        "grade2" => 1.00,
        "grade3" => 0.95,
        "grade4" => 0.88,
        "grade5" => 0.80,
        _ => null,
    };

    private static double? HighwayMult(string h) => h switch
    {
        "steps" => 0.60,
        "path" or "bridleway" => 0.95,
        "footway" or "pedestrian" or "track" => 0.98,
        "cycleway" => 1.03,
        "residential" or "living_street" or "service" or "unclassified" or "tertiary" or "secondary" or "primary" => 1.05,
        _ => null,
    };
}

/// <summary>Options for surface inference.</summary>
public class SurfaceInferOptions
{
    /// <summary>Spacing (m) at which the target is decimated into BRouter via-points. Kept dense so the routed
    /// path hugs the real track (coarse spacing lets the router wander off, and the gate then rejects everything).</summary>
    public double WaypointSpacingM { get; set; } = 150;
    /// <summary>Overall cap on via-points. High because long tracks are routed in chunks now, so this no longer
    /// has to keep a single request small — it only bounds pathological point counts. At 150 m spacing this
    /// covers ~600 km before the spacing is forced coarser.</summary>
    public int MaxWaypoints { get; set; } = 4000;
    /// <summary>A target point adopts a routed way's surface only if the route passes within this many metres.</summary>
    public double GateM { get; set; } = 25;
}

public class SurfaceInferResult
{
    /// <summary>Per-target-point surface multiplier, aligned to <c>target.Points</c>; 1.0 where uncovered.</summary>
    public double[] PerPointMult { get; set; } = System.Array.Empty<double>();
    /// <summary>Per-target-point surface type label (e.g. "gravel"); null where uncovered/unknown.</summary>
    public string?[] PerPointType { get; set; } = System.Array.Empty<string?>();
    public double Coverage { get; set; }   // fraction of points with a confident surface
    public double MeanMult { get; set; }   // mean multiplier over covered points
    public int Matched { get; set; }
    public int Total { get; set; }
    public bool Routed { get; set; }       // false when routing was unavailable
    public string Report { get; set; } = "";
}

/// <summary>
/// Infers a per-point surface multiplier for a target track by auto-routing along it (BRouter) and adopting the
/// OSM way surface — but only where the route actually hugs the track. Each target point takes the surface of the
/// nearest routed vertex when that vertex is within <see cref="SurfaceInferOptions.GateM"/>; otherwise it stays
/// neutral (1.0). This keeps off-trail legs, where the router picked a different path, from corrupting the guess.
/// </summary>
public static class SurfaceInference
{
    public static async Task<SurfaceInferResult> InferAsync(
        Track target, RoutingService routing, SurfaceInferOptions? options = null,
        System.Threading.CancellationToken ct = default)
    {
        var opt = options ?? new SurfaceInferOptions();
        var pts = target.Points;
        var result = new SurfaceInferResult
        {
            Total = pts.Count,
            PerPointMult = Enumerable.Repeat(1.0, pts.Count).ToArray(),
            PerPointType = new string?[pts.Count],
        };
        if (pts.Count < 2) { result.Report = "Track too short to infer surface."; return result; }

        var waypoints = Decimate(pts, opt.WaypointSpacingM, opt.MaxWaypoints);
        var routed = await routing.RouteWithTagsAsync(waypoints, ct);
        if (routed is null || routed.Points.Count == 0)
        {
            result.Report = "Routing unavailable (offline, rate-limited, or no route) — surface left neutral.";
            return result;
        }
        result.Routed = true;

        // Per routed vertex: its surface multiplier (null where the way carries no usable surface tag).
        var vtxMult = routed.WayTags.Select(SurfaceCatalog.MultForTags).ToArray();

        double sumCovered = 0;
        for (int i = 0; i < pts.Count; i++)
        {
            int j = NearestRoutedVertex(routed.Points, pts[i]);
            if (j < 0) continue;
            double gate = GeoMath.HaversineM(pts[i].Lat, pts[i].Lon, routed.Points[j].Lat, routed.Points[j].Lon);
            if (gate > opt.GateM) continue;                 // route strays here -> keep neutral
            result.PerPointType[i] = SurfaceCatalog.TypeForTags(routed.WayTags[j]);   // label even if no multiplier
            if (vtxMult[j] is not double m) continue;       // on-route but surface unknown -> mult stays neutral
            result.PerPointMult[i] = m;
            sumCovered += m;
            result.Matched++;
        }

        result.Coverage = pts.Count > 0 ? (double)result.Matched / pts.Count : 0;
        result.MeanMult = result.Matched > 0 ? sumCovered / result.Matched : 1.0;
        result.Report = BuildReport(result, routed.Points.Count);
        return result;
    }

    private static List<(double Lat, double Lon)> Decimate(IReadOnlyList<TrackPoint> pts, double spacingM, int maxCount)
    {
        var cum = GeoMath.CumulativeDistancesM(pts);
        double total = cum[^1];
        double step = Math.Max(spacingM, total / Math.Max(1, maxCount - 1));   // never exceed the cap
        var outp = new List<(double, double)> { (pts[0].Lat, pts[0].Lon) };
        double next = step;
        for (int i = 1; i < pts.Count; i++)
        {
            if (cum[i] >= next) { outp.Add((pts[i].Lat, pts[i].Lon)); next += step; }
        }
        var last = pts[^1];
        if (outp[^1] != (last.Lat, last.Lon)) outp.Add((last.Lat, last.Lon));
        return outp;
    }

    private static int NearestRoutedVertex(List<TrackPoint> routed, TrackPoint p)
    {
        int best = -1; double bestD = double.MaxValue;
        for (int i = 0; i < routed.Count; i++)
        {
            double dLat = routed[i].Lat - p.Lat, dLon = routed[i].Lon - p.Lon;
            double d = dLat * dLat + dLon * dLon;   // squared degrees for ranking
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    private static string BuildReport(SurfaceInferResult r, int routedVerts)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Routed vertices: {routedVerts}");
        sb.AppendLine($"Surface coverage:{r.Coverage * 100,5:F0}%  ({r.Matched}/{r.Total} points matched within gate)");
        if (r.Matched > 0)
            sb.AppendLine($"Mean surface:    x{r.MeanMult:F2}  (1.00 = as modelled; <1 slower, >1 faster)");
        else
            sb.AppendLine("No confident surface matches — the route did not hug the track. Surface left neutral.");
        return sb.ToString().TrimEnd();
    }
}
