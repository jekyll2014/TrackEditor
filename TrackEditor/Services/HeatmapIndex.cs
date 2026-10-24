using Microsoft.Data.Sqlite;

using System.IO;

using TrackEditor.Core.Models;
using TrackEditor.Core.Services;

namespace TrackEditor.Services;

/// <summary>
/// SQLite-backed spatial index of activity tracks (.fit/.gpx/.tcx).
/// Stores Douglas-Peucker-simplified segments in an R-tree for fast viewport bbox queries.
/// Thread-safe: all DB access serialized on <c>_gate</c>; becomes a no-op after Dispose.
/// </summary>
public sealed class HeatmapIndex : IDisposable
{
    // DP tolerance: 10 m keeps path shape at any zoom while reducing point count ~10x on typical GPS tracks.
    private const double DpToleranceM = 10.0;
    // Hard cap on segments returned per query to bound rendering time.
    private const int QueryLimit = 800_000;
    private const string Exts = ".fit|.gpx|.tcx";

    private readonly SqliteConnection _conn;
    private readonly object _gate = new();
    private bool _disposed;

    public record Segment(float Lat1, float Lon1, float Lat2, float Lon2, string TrackName);
    public record TrackEntry(string Name, string FilePath);

    // ── Construction ─────────────────────────────────────────────────────────────

    public HeatmapIndex()
    {
        string path = DbPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _conn = new SqliteConnection($"Data Source={path}");
        _conn.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;");
        Exec("""
            CREATE TABLE IF NOT EXISTS heatmap_files (
                id    INTEGER PRIMARY KEY,
                path  TEXT    NOT NULL UNIQUE,
                name  TEXT    NOT NULL,
                mtime INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS heatmap_segs (
                id      INTEGER PRIMARY KEY,
                file_id INTEGER NOT NULL REFERENCES heatmap_files(id) ON DELETE CASCADE,
                lat1 REAL NOT NULL, lon1 REAL NOT NULL,
                lat2 REAL NOT NULL, lon2 REAL NOT NULL
            );
            CREATE VIRTUAL TABLE IF NOT EXISTS heatmap_seg_rtree USING rtree(
                id,
                min_lat, max_lat,
                min_lon, max_lon
            );
            CREATE TRIGGER IF NOT EXISTS del_seg_rtree
            AFTER DELETE ON heatmap_segs BEGIN
                DELETE FROM heatmap_seg_rtree WHERE id = OLD.id;
            END;
            """);
    }

    // ── Public API ────────────────────────────────────────────────────────────────

    /// <summary>Walks <paramref name="folder"/> for activity files, indexes new/changed, removes deleted.
    /// <paramref name="progress"/> is called with (indexed, total) as files are processed.</summary>
    public async Task ScanAsync(string folder, IProgress<(int Done, int Total)>? progress = null,
                                CancellationToken ct = default)
    {
        if (!Directory.Exists(folder)) return;
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                             .Where(f => Exts.Contains(Path.GetExtension(f).ToLowerInvariant()))
                             .ToList();

        // Build set of known paths to detect deleted files.
        HashSet<string> known;
        lock (_gate)
        {
            if (_disposed) return;
            known = QueryPaths();
        }

        try
        {
            int done = 0;
            foreach (string file in files)
            {
                ct.ThrowIfCancellationRequested();
                long mtime = new FileInfo(file).LastWriteTimeUtc.Ticks;
                lock (_gate)
                {
                    if (_disposed) return;
                    long storedMtime = QueryMtime(file);
                    if (storedMtime != mtime)
                        IndexFileCore(file, mtime);
                    known.Remove(file);
                }
                progress?.Report((++done, files.Count));
                await Task.Yield(); // keep UI responsive between files
            }
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            throw;
        }

        // Remove entries for deleted files.
        if (known.Count > 0)
            lock (_gate) { if (!_disposed) DeleteFiles(known); }
    }

    /// <summary>Indexes a single file immediately (blocks caller). Used for one-off additions.</summary>
    public void IndexFile(string path)
    {
        long mtime = new FileInfo(path).LastWriteTimeUtc.Ticks;
        lock (_gate) { if (!_disposed) IndexFileCore(path, mtime); }
    }

    /// <summary>Returns all track segments that intersect the given lat/lon bounding box.</summary>
    public List<Segment> QuerySegments(double minLat, double maxLat, double minLon, double maxLon)
    {
        lock (_gate)
        {
            if (_disposed) return [];
            return QuerySegmentsCore(minLat, maxLat, minLon, maxLon);
        }
    }

    /// <summary>Returns distinct tracks whose segments intersect the bbox — for the Heatmap tab list.</summary>
    public List<TrackEntry> QueryVisibleTracks(double minLat, double maxLat, double minLon, double maxLon)
    {
        lock (_gate)
        {
            if (_disposed) return [];
            return QueryTrackEntries(minLat, maxLat, minLon, maxLon);
        }
    }

    /// <summary>Returns distinct tracks whose segments pass within <paramref name="radiusDeg"/> of a point.</summary>
    public List<TrackEntry> QueryTracksNear(double lat, double lon, double radiusDeg = 0.001)
    {
        lock (_gate)
        {
            if (_disposed) return [];
            return QueryTrackEntries(lat - radiusDeg, lat + radiusDeg, lon - radiusDeg, lon + radiusDeg);
        }
    }

    private List<TrackEntry> QueryTrackEntries(double minLat, double maxLat, double minLon, double maxLon)
    {
        var result = new List<TrackEntry>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT f.name, f.path
            FROM heatmap_segs s
            JOIN heatmap_seg_rtree r ON r.id = s.id
            JOIN heatmap_files f ON f.id = s.file_id
            WHERE r.max_lat >= $la AND r.min_lat <= $lb
              AND r.max_lon >= $ca AND r.min_lon <= $cb
            ORDER BY f.name;
            """;
        cmd.Parameters.AddWithValue("$la", minLat);
        cmd.Parameters.AddWithValue("$lb", maxLat);
        cmd.Parameters.AddWithValue("$ca", minLon);
        cmd.Parameters.AddWithValue("$cb", maxLon);
        using var r = cmd.ExecuteReader();
        while (r.Read()) result.Add(new TrackEntry(r.GetString(0), r.GetString(1)));
        return result;
    }

    /// <summary>Returns total indexed file count.</summary>
    public int FileCount()
    {
        lock (_gate) { return _disposed ? 0 : (int)QueryLong("SELECT COUNT(*) FROM heatmap_files;"); }
    }

    /// <summary>Removes all indexed data (does not delete the DB file).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            if (_disposed) return;
            Exec("DELETE FROM heatmap_files;"); // cascades to segs; trigger cleans rtree
        }
    }

    // ── Core (must be called under _gate) ────────────────────────────────────────

    private void IndexFileCore(string path, long mtime)
    {
        List<Track> tracks;
        try
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            tracks = ext switch
            {
                ".fit" => FitIo.Load(path),
                ".gpx" => GpxIo.Load(path),
                ".tcx" => TcxIo.Load(path),
                _ => [],
            };
        }
        catch { return; } // corrupt / unsupported variant — skip silently

        // Merge all segments from this file under the first track's name (or file stem).
        string name = tracks.FirstOrDefault()?.Name ?? Path.GetFileNameWithoutExtension(path);

        using var tx = _conn.BeginTransaction();

        // Remove previous entry (ON DELETE CASCADE clears segs + rtree rows).
        using (var del = _conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM heatmap_files WHERE path = $p;";
            del.Parameters.AddWithValue("$p", path);
            del.ExecuteNonQuery();
        }

        long fileId;
        using (var ins = _conn.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = "INSERT INTO heatmap_files (path, name, mtime) VALUES ($p, $n, $m); SELECT last_insert_rowid();";
            ins.Parameters.AddWithValue("$p", path);
            ins.Parameters.AddWithValue("$n", name);
            ins.Parameters.AddWithValue("$m", mtime);
            fileId = (long)ins.ExecuteScalar()!;
        }

        // Insert simplified segments for all tracks in this file.
        using var segIns = _conn.CreateCommand();
        segIns.Transaction = tx;
        segIns.CommandText = "INSERT INTO heatmap_segs (file_id, lat1, lon1, lat2, lon2) VALUES ($f, $a, $b, $c, $d); SELECT last_insert_rowid();";
        var pFile = segIns.Parameters.Add("$f", SqliteType.Integer); pFile.Value = fileId;
        var pLat1 = segIns.Parameters.Add("$a", SqliteType.Real);
        var pLon1 = segIns.Parameters.Add("$b", SqliteType.Real);
        var pLat2 = segIns.Parameters.Add("$c", SqliteType.Real);
        var pLon2 = segIns.Parameters.Add("$d", SqliteType.Real);

        using var rtIns = _conn.CreateCommand();
        rtIns.Transaction = tx;
        rtIns.CommandText = "INSERT INTO heatmap_seg_rtree (id, min_lat, max_lat, min_lon, max_lon) VALUES ($id, $la, $lb, $ca, $cb);";
        var pId = rtIns.Parameters.Add("$id", SqliteType.Integer);
        var pLa = rtIns.Parameters.Add("$la", SqliteType.Real);
        var pLb = rtIns.Parameters.Add("$lb", SqliteType.Real);
        var pCa = rtIns.Parameters.Add("$ca", SqliteType.Real);
        var pCb = rtIns.Parameters.Add("$cb", SqliteType.Real);
        rtIns.Transaction = tx;

        foreach (var track in tracks)
        {
            if (track.Points.Count < 2) continue;
            var kept = GeoMath.DouglasPeucker(track.Points, DpToleranceM);
            for (int i = 1; i < kept.Count; i++)
            {
                var p1 = track.Points[kept[i - 1]];
                var p2 = track.Points[kept[i]];

                pLat1.Value = p1.Lat; pLon1.Value = p1.Lon;
                pLat2.Value = p2.Lat; pLon2.Value = p2.Lon;
                long segId = (long)segIns.ExecuteScalar()!;

                pId.Value = segId;
                pLa.Value = Math.Min(p1.Lat, p2.Lat); pLb.Value = Math.Max(p1.Lat, p2.Lat);
                pCa.Value = Math.Min(p1.Lon, p2.Lon); pCb.Value = Math.Max(p1.Lon, p2.Lon);
                rtIns.ExecuteNonQuery();
            }
        }

        tx.Commit();
    }

    private List<Segment> QuerySegmentsCore(double minLat, double maxLat, double minLon, double maxLon)
    {
        var result = new List<Segment>(512);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.lat1, s.lon1, s.lat2, s.lon2, f.name
            FROM heatmap_segs s
            JOIN heatmap_seg_rtree r ON r.id = s.id
            JOIN heatmap_files f ON f.id = s.file_id
            WHERE r.max_lat >= $la AND r.min_lat <= $lb
              AND r.max_lon >= $ca AND r.min_lon <= $cb
            LIMIT $lim;
            """;
        cmd.Parameters.AddWithValue("$la", minLat);
        cmd.Parameters.AddWithValue("$lb", maxLat);
        cmd.Parameters.AddWithValue("$ca", minLon);
        cmd.Parameters.AddWithValue("$cb", maxLon);
        cmd.Parameters.AddWithValue("$lim", QueryLimit);

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            result.Add(new Segment(
                (float)r.GetDouble(0), (float)r.GetDouble(1),
                (float)r.GetDouble(2), (float)r.GetDouble(3),
                r.GetString(4)));
        }
        return result;
    }

    private long QueryMtime(string path)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT mtime FROM heatmap_files WHERE path = $p;";
        cmd.Parameters.AddWithValue("$p", path);
        var v = cmd.ExecuteScalar();
        return v is null ? -1 : (long)v;
    }

    private HashSet<string> QueryPaths()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT path FROM heatmap_files;";
        using var r = cmd.ExecuteReader();
        while (r.Read()) set.Add(r.GetString(0));
        return set;
    }

    private void DeleteFiles(IEnumerable<string> paths)
    {
        using var tx = _conn.BeginTransaction();
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM heatmap_files WHERE path = $p;";
        var p = cmd.Parameters.Add("$p", SqliteType.Text);
        foreach (string path in paths) { p.Value = path; cmd.ExecuteNonQuery(); }
        tx.Commit();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private long QueryLong(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    private static string DbPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TrackEditor", "heatmap.db");

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _conn.Dispose();
        }
    }
}
