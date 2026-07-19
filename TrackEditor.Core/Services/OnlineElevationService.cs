using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace TrackEditor.Core.Services;

/// <summary>
/// Fetches elevations from a public web API (OpenTopoData or Open-Elevation) in batches.
/// Intended as a fallback when SRTM tiles are unavailable. Requires network access and is
/// subject to the provider's rate limits — the public OpenTopoData instance allows up to
/// 100 locations per call and ~1 call per second.
/// </summary>
public class OnlineElevationService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public OnlineElevationProvider Provider { get; set; } = OnlineElevationProvider.OpenTopoData;
    public string OpenTopoDataset { get; set; } = "srtm90m";

    /// <summary>
    /// Resolves elevations for the given coordinates. Returns an array the same length as
    /// <paramref name="points"/>; entries are null where the service returned no value.
    /// <paramref name="progress"/> reports (done, total) after each batch.
    /// </summary>
    public async Task<double?[]> GetElevationsAsync(
        IReadOnlyList<(double Lat, double Lon)> points,
        IProgress<(int Done, int Total)>? progress = null,
        CancellationToken ct = default)
    {
        var result = new double?[points.Count];
        bool openTopo = Provider == OnlineElevationProvider.OpenTopoData;
        int batchSize = openTopo ? 100 : 200;

        for (int start = 0; start < points.Count; start += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            int count = Math.Min(batchSize, points.Count - start);
            var batch = new List<(double Lat, double Lon)>(count);
            for (int i = 0; i < count; i++) batch.Add(points[start + i]);

            double?[] elevs = openTopo
                ? await OpenTopoDataAsync(batch, ct)
                : await OpenElevationAsync(batch, ct);

            for (int i = 0; i < count && i < elevs.Length; i++) result[start + i] = elevs[i];
            progress?.Report((Math.Min(start + count, points.Count), points.Count));

            // Respect the public OpenTopoData rate limit (~1 request/second).
            if (openTopo && start + batchSize < points.Count)
                await Task.Delay(1100, ct);
        }
        return result;
    }

    private async Task<double?[]> OpenTopoDataAsync(List<(double Lat, double Lon)> batch, CancellationToken ct)
    {
        string locs = string.Join("|", batch.Select(p =>
            $"{p.Lat.ToString(CultureInfo.InvariantCulture)},{p.Lon.ToString(CultureInfo.InvariantCulture)}"));
        string url = $"https://api.opentopodata.org/v1/{Uri.EscapeDataString(OpenTopoDataset)}";
        using var content = new StringContent(
            JsonSerializer.Serialize(new { locations = locs }), Encoding.UTF8, "application/json");
        using var resp = await Http.PostAsync(url, content, ct);
        resp.EnsureSuccessStatusCode();
        string json = await resp.Content.ReadAsStringAsync(ct);
        return ParseElevations(json, batch.Count);
    }

    private static async Task<double?[]> OpenElevationAsync(List<(double Lat, double Lon)> batch, CancellationToken ct)
    {
        var body = new { locations = batch.Select(p => new { latitude = p.Lat, longitude = p.Lon }).ToArray() };
        using var content = new StringContent(
            JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await Http.PostAsync("https://api.open-elevation.com/api/v1/lookup", content, ct);
        resp.EnsureSuccessStatusCode();
        string json = await resp.Content.ReadAsStringAsync(ct);
        return ParseElevations(json, batch.Count);
    }

    /// <summary>Both APIs return {"results":[{"elevation":...}, ...]} in request order.</summary>
    private static double?[] ParseElevations(string json, int expected)
    {
        var outv = new double?[expected];
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results)) return outv;
        int i = 0;
        foreach (var r in results.EnumerateArray())
        {
            if (i >= expected) break;
            if (r.TryGetProperty("elevation", out var el) && el.ValueKind == JsonValueKind.Number)
                outv[i] = el.GetDouble();
            i++;
        }
        return outv;
    }
}
