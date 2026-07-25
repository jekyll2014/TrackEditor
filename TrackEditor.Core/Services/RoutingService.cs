using System.Globalization;
using System.Linq;
using System.Text.Json;

using TrackEditor.Core.Models;

namespace TrackEditor.Core.Services;

/// <summary>
/// Snaps a straight leg onto real paths using the public BRouter service (brouter.de).
/// No API key is required. Profiles are BRouter's own names, e.g. "trekking" or "hiking-beta".
/// Returns null when routing is unavailable so callers can fall back to a straight segment.
/// </summary>
public class RoutingService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(25) };

    /// <summary>BRouter profiles that make sense for track drawing.</summary>
    public static readonly string[] Profiles =
        { "trekking", "hiking-beta", "fastbike", "shortest", "car-fast" };

    public string Profile { get; set; } = "trekking";

    /// <summary>A routed polyline plus, per vertex, the OSM way tags BRouter travelled on
    /// (e.g. <c>"highway=path surface=gravel tracktype=grade3"</c>; empty string when unknown).</summary>
    public sealed class RoutedPath
    {
        public List<TrackPoint> Points { get; init; } = new();
        public List<string> WayTags { get; init; } = new();   // aligned 1:1 with Points
    }

    /// <summary>
    /// Routes from <paramref name="from"/> to <paramref name="to"/>. The first returned point
    /// coincides with <paramref name="from"/>, so callers appending to a track should skip it.
    /// </summary>
    public async Task<List<TrackPoint>?> RouteAsync(
        (double Lat, double Lon) from, (double Lat, double Lon) to, CancellationToken ct = default)
    {
        var geo = await FetchAsync(new[] { from, to }, ct);
        return geo is null ? null : ParseGeoJson(geo);
    }

    /// <summary>
    /// Routes through all <paramref name="waypoints"/> (BRouter passes each as a via-point, so the route hugs
    /// the input) and returns the geometry with the OSM way tags per vertex. Returns null when unavailable.
    /// </summary>
    public async Task<RoutedPath?> RouteWithTagsAsync(
        IReadOnlyList<(double Lat, double Lon)> waypoints, CancellationToken ct = default)
    {
        if (waypoints.Count < 2) return null;
        var json = await FetchAsync(waypoints, ct);
        return json is null ? null : ParseRoutedPath(json);
    }

    private async Task<string?> FetchAsync(IReadOnlyList<(double Lat, double Lon)> waypoints, CancellationToken ct)
    {
        static string N(double v) => v.ToString("F6", CultureInfo.InvariantCulture);
        string lonlats = string.Join("|", waypoints.Select(w => $"{N(w.Lon)},{N(w.Lat)}"));
        string url = "https://brouter.de/brouter" +
                     $"?lonlats={lonlats}&profile={Uri.EscapeDataString(Profile)}&alternativeidx=0&format=geojson";
        try
        {
            using var resp = await Http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch
        {
            return null; // offline / rate-limited / no route -> caller draws a straight segment
        }
    }

    /// <summary>Reads features[0].geometry.coordinates ([lon, lat, ele?]) from a BRouter GeoJSON response.</summary>
    private static List<TrackPoint>? ParseGeoJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("features", out var features) || features.GetArrayLength() == 0)
            return null;
        if (!features[0].TryGetProperty("geometry", out var geom) ||
            !geom.TryGetProperty("coordinates", out var coords))
            return null;

        var pts = new List<TrackPoint>(coords.GetArrayLength());
        foreach (var c in coords.EnumerateArray())
        {
            if (c.GetArrayLength() < 2) continue;
            var p = new TrackPoint { Lon = c[0].GetDouble(), Lat = c[1].GetDouble() };
            // BRouter includes an elevation as the third ordinate when it knows one.
            if (c.GetArrayLength() > 2 && c[2].ValueKind == JsonValueKind.Number)
                p.Ele = c[2].GetDouble();
            pts.Add(p);
        }
        return pts.Count > 0 ? pts : null;
    }

    /// <summary>
    /// Reads geometry AND per-vertex way tags. BRouter's <c>properties.messages</c> is a table (header row +
    /// one row per way segment) whose Longitude/Latitude (µdeg ×1e6) mark each segment's start node and whose
    /// WayTags column holds the OSM tags. Each geometry vertex inherits the tags of the message run it lies in.
    /// </summary>
    private static RoutedPath? ParseRoutedPath(string json)
    {
        var pts = ParseGeoJson(json);
        if (pts is null) return null;
        var tags = new string[pts.Count];   // default null -> "" below
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("features", out var features) && features.GetArrayLength() > 0 &&
                features[0].TryGetProperty("properties", out var props) &&
                props.TryGetProperty("messages", out var msgs) && msgs.ValueKind == JsonValueKind.Array &&
                msgs.GetArrayLength() > 1)
            {
                // Locate columns from the header row.
                var header = msgs[0];
                int lonCol = -1, latCol = -1, tagCol = -1;
                for (int c = 0; c < header.GetArrayLength(); c++)
                {
                    string name = header[c].GetString() ?? "";
                    if (name == "Longitude") lonCol = c;
                    else if (name == "Latitude") latCol = c;
                    else if (name == "WayTags") tagCol = c;
                }
                if (lonCol >= 0 && latCol >= 0 && tagCol >= 0)
                {
                    // For each message start node, find its nearest geometry vertex, then fill forward.
                    var starts = new List<(int Idx, string Tags)>();
                    for (int r = 1; r < msgs.GetArrayLength(); r++)
                    {
                        var row = msgs[r];
                        if (row.GetArrayLength() <= Math.Max(tagCol, Math.Max(lonCol, latCol))) continue;
                        if (!double.TryParse(row[lonCol].GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double lonU) ||
                            !double.TryParse(row[latCol].GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double latU))
                            continue;
                        double lon = lonU / 1e6, lat = latU / 1e6;
                        int j = NearestVertex(pts, lat, lon);
                        starts.Add((j, row[tagCol].GetString() ?? ""));
                    }
                    starts.Sort((a, b) => a.Idx.CompareTo(b.Idx));
                    for (int s = 0; s < starts.Count; s++)
                    {
                        int end = s + 1 < starts.Count ? starts[s + 1].Idx : pts.Count;
                        for (int j = Math.Max(0, starts[s].Idx); j < end; j++) tags[j] = starts[s].Tags;
                    }
                }
            }
        }
        catch { /* tags are best-effort; geometry still returned */ }

        return new RoutedPath
        {
            Points = pts,
            WayTags = tags.Select(t => t ?? "").ToList(),
        };
    }

    private static int NearestVertex(List<TrackPoint> pts, double lat, double lon)
    {
        int best = 0; double bestD = double.MaxValue;
        for (int i = 0; i < pts.Count; i++)
        {
            double dLat = pts[i].Lat - lat, dLon = pts[i].Lon - lon;
            double d = dLat * dLat + dLon * dLon;   // squared degrees: fine for nearest-vertex ranking
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }
}
