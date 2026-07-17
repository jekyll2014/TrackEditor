using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace TrackEditor.Services;

/// <summary>
/// Reads SRTM .hgt tiles (SRTM1 3601x3601 or SRTM3 1201x1201, big-endian int16)
/// from a local folder and returns bilinearly interpolated elevations.
/// Tiles are named like N54E025.hgt; missing ones can be auto-downloaded (see EnsureTilesAsync).
/// </summary>
public class SrtmService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public string? Folder { get; set; }

    /// <summary>When true, EnsureTilesAsync may fetch missing tiles from the AWS open dataset.</summary>
    public bool AutoDownload { get; set; }

    private readonly Dictionary<(int Lat, int Lon), (short[] Data, int Size)?> _cache = new();

    public bool IsAvailable => !string.IsNullOrEmpty(Folder) && Directory.Exists(Folder);

    private static string TileName(int lat, int lon) =>
        $"{(lat >= 0 ? 'N' : 'S')}{Math.Abs(lat):D2}{(lon >= 0 ? 'E' : 'W')}{Math.Abs(lon):D3}.hgt";

    /// <summary>
    /// Ensures .hgt tiles covering the given coordinates exist locally, downloading any that are
    /// missing (gzipped SRTM1 from the elevation-tiles-prod open dataset). Returns the count fetched.
    /// Ocean/void tiles simply return 404 and are skipped. No-op unless AutoDownload and a folder are set.
    /// </summary>
    public async Task<int> EnsureTilesAsync(
        IEnumerable<(double Lat, double Lon)> coords,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (!AutoDownload || string.IsNullOrEmpty(Folder)) return 0;
        Directory.CreateDirectory(Folder);

        var needed = coords
            .Select(c => ((int)Math.Floor(c.Lat), (int)Math.Floor(c.Lon)))
            .Distinct()
            .Where(t => FindFile(TileName(t.Item1, t.Item2)) is null) // not already on disk
            .ToList();

        int downloaded = 0;
        foreach (var (lat, lon) in needed)
        {
            ct.ThrowIfCancellationRequested();
            string name = TileName(lat, lon);
            progress?.Report($"Downloading SRTM tile {name}…");
            if (await TryDownloadTileAsync(lat, lon, Path.Combine(Folder, name), ct))
            {
                _cache.Remove((lat, lon)); // invalidate any cached "missing" result
                downloaded++;
            }
        }
        return downloaded;
    }

    private static async Task<bool> TryDownloadTileAsync(int lat, int lon, string dest, CancellationToken ct)
    {
        string latDir = $"{(lat >= 0 ? 'N' : 'S')}{Math.Abs(lat):D2}";
        string gz = $"{TileName(lat, lon)}.gz";
        string url = $"https://s3.amazonaws.com/elevation-tiles-prod/skadi/{latDir}/{gz}";
        try
        {
            using var resp = await Http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return false; // 404 = no tile there (e.g. open sea)
            using var src = new GZipStream(await resp.Content.ReadAsStreamAsync(ct), CompressionMode.Decompress);
            using (var outFs = File.Create(dest))
                await src.CopyToAsync(outFs, ct);
            return true;
        }
        catch
        {
            try { if (File.Exists(dest)) File.Delete(dest); } catch { }
            return false;
        }
    }

    public double? GetElevation(double lat, double lon)
    {
        if (!IsAvailable) return null;

        int tileLat = (int)Math.Floor(lat);
        int tileLon = (int)Math.Floor(lon);
        var tile = GetTile(tileLat, tileLon);
        if (tile is null) return null;

        var (data, size) = tile.Value;
        // row 0 = north edge (tileLat + 1), col 0 = west edge
        double x = (lon - tileLon) * (size - 1);
        double y = (tileLat + 1 - lat) * (size - 1);
        int x0 = Math.Clamp((int)x, 0, size - 2);
        int y0 = Math.Clamp((int)y, 0, size - 2);
        double fx = x - x0, fy = y - y0;

        double? e00 = Sample(data, size, y0, x0);
        double? e01 = Sample(data, size, y0, x0 + 1);
        double? e10 = Sample(data, size, y0 + 1, x0);
        double? e11 = Sample(data, size, y0 + 1, x0 + 1);
        if (e00 is null || e01 is null || e10 is null || e11 is null) return null;

        return e00 * (1 - fx) * (1 - fy) + e01 * fx * (1 - fy) +
               e10 * (1 - fx) * fy + e11 * fx * fy;
    }

    private static double? Sample(short[] data, int size, int row, int col)
    {
        short v = data[row * size + col];
        return v == short.MinValue ? null : v; // -32768 = void
    }

    private (short[] Data, int Size)? GetTile(int lat, int lon)
    {
        var key = (lat, lon);
        if (_cache.TryGetValue(key, out var cached)) return cached;

        string name = TileName(lat, lon);
        (short[], int)? result = null;
        try
        {
            string? file = FindFile(name);
            if (file is not null)
            {
                byte[] bytes = File.ReadAllBytes(file);
                int size = (int)Math.Sqrt(bytes.Length / 2.0);
                if (size * size * 2 == bytes.Length && (size == 1201 || size == 3601))
                {
                    var data = new short[size * size];
                    for (int i = 0; i < data.Length; i++)
                        data[i] = (short)((bytes[i * 2] << 8) | bytes[i * 2 + 1]);
                    result = (data, size);
                }
            }
        }
        catch { /* unreadable tile -> treat as missing */ }

        _cache[key] = result;
        return result;
    }

    private string? FindFile(string name)
    {
        string direct = Path.Combine(Folder!, name);
        if (File.Exists(direct)) return direct;
        // also look one level deep (common layout: unzipped per-continent subfolders)
        foreach (var dir in Directory.GetDirectories(Folder!))
        {
            string sub = Path.Combine(dir, name);
            if (File.Exists(sub)) return sub;
        }
        return null;
    }
}
