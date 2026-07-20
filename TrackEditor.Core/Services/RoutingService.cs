using System.Globalization;
using System.Net.Http;
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

    /// <summary>
    /// Routes from <paramref name="from"/> to <paramref name="to"/>. The first returned point
    /// coincides with <paramref name="from"/>, so callers appending to a track should skip it.
    /// </summary>
    public async Task<List<TrackPoint>?> RouteAsync(
        (double Lat, double Lon) from, (double Lat, double Lon) to, CancellationToken ct = default)
    {
        static string N(double v) => v.ToString("F6", CultureInfo.InvariantCulture);
        string url = "https://brouter.de/brouter" +
                     $"?lonlats={N(from.Lon)},{N(from.Lat)}|{N(to.Lon)},{N(to.Lat)}" +
                     $"&profile={Uri.EscapeDataString(Profile)}&alternativeidx=0&format=geojson";
        try
        {
            using var resp = await Http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            string json = await resp.Content.ReadAsStringAsync(ct);
            return ParseGeoJson(json);
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
}
