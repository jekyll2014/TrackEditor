using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TrackEditor.Core.Services;

/// <summary>Thrown when a provider returns HTTP 429. Carries the number of seconds to wait before retrying.</summary>
public class RateLimitedException(int retryAfterSeconds) : Exception($"HTTP 429 – retry after {retryAfterSeconds}s")
{
    public int RetryAfterSeconds { get; } = retryAfterSeconds;
}

/// <summary>
/// Thrown when <see cref="OnlineElevationService.GetElevationsAsync"/> fails mid-batch.
/// <see cref="Partial"/> contains whatever was successfully fetched before the failure;
/// entries for unfetched points are null.
/// </summary>
public class PartialElevationException(double?[] partial, Exception inner)
    : Exception("Elevation fetch stopped: " + inner.Message, inner)
{
    public double?[] Partial { get; } = partial;
}

/// <summary>
/// Fetches elevations from a public web API in batches. Intended as a fallback when SRTM tiles are
/// unavailable. Requires network access and is subject to each provider's rate limits.
/// <para>
/// Provider notes for browser (WASM) callers: only providers that return CORS headers work in a browser.
/// <b>OpenMeteo</b> and <b>OpenElevation</b> (via GET) do; <b>OpenTopoData</b> does not send CORS headers
/// and can only be used from the desktop app.
/// </para>
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
    /// Retries each batch up to 3 times on HTTP 429, honouring the Retry-After header.
    /// Throws <see cref="PartialElevationException"/> on unrecoverable failure so the caller
    /// can save whatever was fetched before the error.
    /// </summary>
    public async Task<double?[]> GetElevationsAsync(
        IReadOnlyList<(double Lat, double Lon)> points,
        IProgress<(int Done, int Total)>? progress = null,
        CancellationToken ct = default)
    {
        var result = new double?[points.Count];
        bool openTopo = Provider == OnlineElevationProvider.OpenTopoData;
        int batchSize = 100;
        const int maxRetries = 3;

        for (int start = 0; start < points.Count; start += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            int count = Math.Min(batchSize, points.Count - start);
            var batch = new List<(double Lat, double Lon)>(count);
            for (int i = 0; i < count; i++) batch.Add(points[start + i]);

            double?[] elevs;
            int attempt = 0;
            while (true)
            {
                try
                {
                    elevs = await DispatchBatchAsync(batch, ct);
                    break;
                }
                catch (RateLimitedException rle) when (attempt < maxRetries)
                {
                    attempt++;
                    await Task.Delay(TimeSpan.FromSeconds(rle.RetryAfterSeconds), ct);
                }
                catch (Exception ex)
                {
                    throw new PartialElevationException(result, ex);
                }
            }

            for (int i = 0; i < count && i < elevs.Length; i++) result[start + i] = elevs[i];
            progress?.Report((Math.Min(start + count, points.Count), points.Count));

            // Respect the public OpenTopoData rate limit (~1 request/second).
            if (openTopo && start + batchSize < points.Count)
                await Task.Delay(1100, ct);
        }
        return result;
    }

    private Task<double?[]> DispatchBatchAsync(List<(double Lat, double Lon)> batch, CancellationToken ct) =>
        Provider switch
        {
            OnlineElevationProvider.OpenMeteo => OpenMeteoAsync(batch, ct),
            OnlineElevationProvider.OpenElevation => OpenElevationAsync(batch, ct),
            _ => OpenTopoDataAsync(batch, ct),
        };

    /// <summary>Throws <see cref="RateLimitedException"/> if the response is HTTP 429, leaving other errors to the caller.</summary>
    private static void ThrowIfRateLimited(HttpResponseMessage resp)
    {
        if ((int)resp.StatusCode != 429) return;
        int delaySec = 60;
        var ra = resp.Headers.RetryAfter;
        if (ra?.Delta is TimeSpan d) delaySec = Math.Max(1, (int)d.TotalSeconds);
        else if (ra?.Date is DateTimeOffset dt) delaySec = Math.Max(1, (int)(dt - DateTimeOffset.UtcNow).TotalSeconds);
        throw new RateLimitedException(delaySec);
    }

    private async Task<double?[]> OpenTopoDataAsync(List<(double Lat, double Lon)> batch, CancellationToken ct)
    {
        string locs = string.Join("|", batch.Select(p =>
            $"{p.Lat.ToString(CultureInfo.InvariantCulture)},{p.Lon.ToString(CultureInfo.InvariantCulture)}"));
        string url = $"https://api.opentopodata.org/v1/{Uri.EscapeDataString(OpenTopoDataset)}";
        using var content = new StringContent(
            JsonSerializer.Serialize(new { locations = locs }), Encoding.UTF8, "application/json");
        using var resp = await Http.PostAsync(url, content, ct);
        ThrowIfRateLimited(resp);
        resp.EnsureSuccessStatusCode();
        string json = await resp.Content.ReadAsStringAsync(ct);
        return ParseElevations(json, batch.Count);
    }

    /// <summary>Open-Meteo elevation API — a simple CORS-enabled GET, so it works in the browser too.</summary>
    private static async Task<double?[]> OpenMeteoAsync(List<(double Lat, double Lon)> batch, CancellationToken ct)
    {
        string lats = string.Join(",", batch.Select(p => p.Lat.ToString(CultureInfo.InvariantCulture)));
        string lons = string.Join(",", batch.Select(p => p.Lon.ToString(CultureInfo.InvariantCulture)));
        string url = $"https://api.open-meteo.com/v1/elevation?latitude={lats}&longitude={lons}";
        using var resp = await Http.GetAsync(url, ct);
        ThrowIfRateLimited(resp);
        resp.EnsureSuccessStatusCode();
        string json = await resp.Content.ReadAsStringAsync(ct);

        var outv = new double?[batch.Count];
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("elevation", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            int i = 0;
            foreach (var e in arr.EnumerateArray())
            {
                if (i >= batch.Count) break;
                if (e.ValueKind == JsonValueKind.Number) outv[i] = e.GetDouble();
                i++;
            }
        }
        return outv;
    }

    /// <summary>Open-Elevation lookup via GET (its POST endpoint triggers a browser CORS preflight it rejects).</summary>
    private static async Task<double?[]> OpenElevationAsync(List<(double Lat, double Lon)> batch, CancellationToken ct)
    {
        string locs = string.Join("|", batch.Select(p =>
            $"{p.Lat.ToString(CultureInfo.InvariantCulture)},{p.Lon.ToString(CultureInfo.InvariantCulture)}"));
        string url = $"https://api.open-elevation.com/api/v1/lookup?locations={Uri.EscapeDataString(locs)}";
        using var resp = await Http.GetAsync(url, ct);
        ThrowIfRateLimited(resp);
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
