using Microsoft.Data.Sqlite;

using System.IO;

using TrackEditor.Core.Models;
using TrackEditor.Core.Services;

namespace TrackEditor.Services;

/// <summary>
/// SQLite-backed spatial index of activity tracks (.fit/.gpx/.tcx).
///
/// Pre-computes a heat-edge network at index time: GPS segments are snapped to a ~11 m grid,
/// direction-normalised, and merged into unique edges. Each edge stores the count of distinct
/// files that pass through it, so rendering only needs to draw the merged network instead of
/// every raw segment — dramatically fewer draw calls for large collections.
///
/// Thread-safe: all DB access serialised on <c>_gate</c>; becomes a no-op after Dispose.
/// </summary>
public sealed class HeatmapIndex : IDisposable
{
    // DP tolerance: 10 m keeps path shape while reducing point count ~10× on typical GPS tracks.
    private const double DpToleranceM = 10.0;
    // Snapping grid: ~11 m at the equator — merges GPS noise and near-identical parallel paths.
    private const double SnapDeg = 0.0001;
    // Hard cap on heat edges returned per viewport query.
    private const int QueryLimit = 200_000;
    private const string Exts = ".fit|.gpx|.tcx";

    private readonly SqliteConnection _conn;
    private readonly object _gate = new();
    private bool _disposed;

    /// <summary>A pre-computed, snapped heat edge with its accumulated track count.</summary>
    public record HeatEdge(float Lat1, float Lon1, float Lat2, float Lon2, int Count);

    public record TrackEntry(string Name, string FilePath, DateTime? TrackDate, double? DistanceKm, string? ActivityType)
    {
        public string TrackDateDisplay
        {
            get
            {
                var parts = new System.Collections.Generic.List<string>();
                if (!string.IsNullOrWhiteSpace(ActivityType)) parts.Add(ActivityType!);
                if (TrackDate is DateTime d) parts.Add(d.ToLocalTime().ToString("yyyy-MM-dd  HH:mm"));
                if (DistanceKm is double km) parts.Add($"{km:F1} km");
                return string.Join("  ·  ", parts);
            }
        }
    }

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
                id            INTEGER PRIMARY KEY,
                path          TEXT    NOT NULL UNIQUE,
                name          TEXT    NOT NULL,
                mtime         INTEGER NOT NULL,
                start_time    INTEGER,
                distance_m    REAL,
                activity_type TEXT
            );

            -- Snapped, direction-normalised edges shared across all files that pass through them.
            CREATE TABLE IF NOT EXISTS heat_edges (
                id    INTEGER PRIMARY KEY,
                lat1  REAL    NOT NULL,
                lon1  REAL    NOT NULL,
                lat2  REAL    NOT NULL,
                lon2  REAL    NOT NULL,
                count INTEGER NOT NULL DEFAULT 0,
                UNIQUE(lat1, lon1, lat2, lon2)
            );

            -- Which files contributed to each edge (one row per file/edge pair).
            CREATE TABLE IF NOT EXISTS heat_edge_tracks (
                edge_id INTEGER NOT NULL REFERENCES heat_edges(id)      ON DELETE CASCADE,
                file_id INTEGER NOT NULL REFERENCES heatmap_files(id)   ON DELETE CASCADE,
                PRIMARY KEY (edge_id, file_id)
            );

            CREATE VIRTUAL TABLE IF NOT EXISTS heat_edge_rtree USING rtree(
                id,
                min_lat, max_lat,
                min_lon, max_lon
            );

            -- When an edge loses all files, remove it from the R-tree.
            CREATE TRIGGER IF NOT EXISTS del_heat_edge_rtree
            AFTER DELETE ON heat_edges BEGIN
                DELETE FROM heat_edge_rtree WHERE id = OLD.id;
            END;

            -- When a file/edge association is removed, decrement the edge count;
            -- delete the edge when it reaches zero (triggers del_heat_edge_rtree above).
            CREATE TRIGGER IF NOT EXISTS dec_heat_edge_count
            AFTER DELETE ON heat_edge_tracks BEGIN
                UPDATE heat_edges SET count = count - 1 WHERE id = OLD.edge_id;
                DELETE FROM heat_edges WHERE id = OLD.edge_id AND count <= 0;
            END;
            """);
    }

    // ── Public API ────────────────────────────────────────────────────────────────

    /// <summary>Walks <paramref name="folder"/> for activity files, indexes new/changed, removes deleted.</summary>
    public async Task ScanAsync(string folder, IProgress<(int Done, int Total)>? progress = null,
                                CancellationToken ct = default)
    {
        if (!Directory.Exists(folder)) return;
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                             .Where(f => Exts.Contains(Path.GetExtension(f).ToLowerInvariant()))
                             .ToList();

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
                await Task.Yield();
            }
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            throw;
        }

        if (known.Count > 0)
            lock (_gate) { if (!_disposed) DeleteFiles(known); }
    }

    /// <summary>Indexes a single file immediately (blocks caller).</summary>
    public void IndexFile(string path)
    {
        long mtime = new FileInfo(path).LastWriteTimeUtc.Ticks;
        lock (_gate) { if (!_disposed) IndexFileCore(path, mtime); }
    }

    /// <summary>Returns merged heat edges that intersect the given lat/lon bounding box.</summary>
    public List<HeatEdge> QueryHeatEdges(double minLat, double maxLat, double minLon, double maxLon)
    {
        lock (_gate)
        {
            if (_disposed) return [];
            return QueryHeatEdgesCore(minLat, maxLat, minLon, maxLon);
        }
    }

    /// <summary>Returns distinct tracks whose edges intersect the bbox — for the Heatmap tab list.</summary>
    public List<TrackEntry> QueryVisibleTracks(double minLat, double maxLat, double minLon, double maxLon)
    {
        lock (_gate)
        {
            if (_disposed) return [];
            return QueryTrackEntries(minLat, maxLat, minLon, maxLon);
        }
    }

    /// <summary>Returns distinct tracks whose edges pass within <paramref name="radiusDeg"/> of a point.</summary>
    public List<TrackEntry> QueryTracksNear(double lat, double lon, double radiusDeg = 0.001)
    {
        lock (_gate)
        {
            if (_disposed) return [];
            return QueryTrackEntries(lat - radiusDeg, lat + radiusDeg, lon - radiusDeg, lon + radiusDeg);
        }
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
            Exec("DELETE FROM heatmap_files;"); // cascades to heat_edge_tracks → dec trigger → deletes edges → rtree
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
        catch { return; }

        string name = tracks.FirstOrDefault()?.Name ?? Path.GetFileNameWithoutExtension(path);
        DateTime? startTime = tracks
            .SelectMany(t => t.Points)
            .Where(p => p.Time is { Year: > 1990 })
            .Select(p => p.Time)
            .FirstOrDefault();
        double distM = tracks
            .Where(t => t.Points.Count >= 2)
            .Sum(t => GeoMath.CumulativeDistancesM(t.Points)[^1]);
        string? activityType = tracks.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.ActivityType))?.ActivityType;

        using var tx = _conn.BeginTransaction();

        // Delete previous entry — CASCADE removes heat_edge_tracks rows,
        // triggers dec_heat_edge_count which decrements counts and prunes zero-count edges.
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
            ins.CommandText = "INSERT INTO heatmap_files (path, name, mtime, start_time, distance_m, activity_type) VALUES ($p, $n, $m, $st, $dm, $at); SELECT last_insert_rowid();";
            ins.Parameters.AddWithValue("$p", path);
            ins.Parameters.AddWithValue("$n", name);
            ins.Parameters.AddWithValue("$m", mtime);
            ins.Parameters.AddWithValue("$st", (object?)startTime?.ToUniversalTime().Ticks ?? DBNull.Value);
            ins.Parameters.AddWithValue("$dm", distM > 0 ? (object)distM : DBNull.Value);
            ins.Parameters.AddWithValue("$at", (object?)activityType ?? DBNull.Value);
            fileId = (long)ins.ExecuteScalar()!;
        }

        // Prepared commands reused for every edge in this file.
        using var edgeIns = _conn.CreateCommand();
        edgeIns.Transaction = tx;
        edgeIns.CommandText = "INSERT OR IGNORE INTO heat_edges (lat1, lon1, lat2, lon2, count) VALUES ($a, $b, $c, $d, 0);";
        var eA = edgeIns.Parameters.Add("$a", SqliteType.Real);
        var eB = edgeIns.Parameters.Add("$b", SqliteType.Real);
        var eC = edgeIns.Parameters.Add("$c", SqliteType.Real);
        var eD = edgeIns.Parameters.Add("$d", SqliteType.Real);

        using var changesCmd = _conn.CreateCommand();
        changesCmd.Transaction = tx;
        changesCmd.CommandText = "SELECT changes();";

        using var edgeSel = _conn.CreateCommand();
        edgeSel.Transaction = tx;
        edgeSel.CommandText = "SELECT id FROM heat_edges WHERE lat1=$a AND lon1=$b AND lat2=$c AND lon2=$d;";
        var sA = edgeSel.Parameters.Add("$a", SqliteType.Real);
        var sB = edgeSel.Parameters.Add("$b", SqliteType.Real);
        var sC = edgeSel.Parameters.Add("$c", SqliteType.Real);
        var sD = edgeSel.Parameters.Add("$d", SqliteType.Real);

        using var rtreeIns = _conn.CreateCommand();
        rtreeIns.Transaction = tx;
        rtreeIns.CommandText = "INSERT INTO heat_edge_rtree (id, min_lat, max_lat, min_lon, max_lon) VALUES ($id, $la, $lb, $ca, $cb);";
        var rId  = rtreeIns.Parameters.Add("$id", SqliteType.Integer);
        var rLa  = rtreeIns.Parameters.Add("$la", SqliteType.Real);
        var rLb  = rtreeIns.Parameters.Add("$lb", SqliteType.Real);
        var rCa  = rtreeIns.Parameters.Add("$ca", SqliteType.Real);
        var rCb  = rtreeIns.Parameters.Add("$cb", SqliteType.Real);

        // INSERT OR IGNORE prevents double-counting when the same file visits the same edge twice
        // (e.g. out-and-back routes). changes() after INSERT returns 1 if new, 0 if duplicate.
        using var assocCmd = _conn.CreateCommand();
        assocCmd.Transaction = tx;
        assocCmd.CommandText = """
            INSERT OR IGNORE INTO heat_edge_tracks (edge_id, file_id) VALUES ($e, $f);
            UPDATE heat_edges SET count = count + changes() WHERE id = $e;
            """;
        var aEdge = assocCmd.Parameters.Add("$e", SqliteType.Integer);
        var aFile = assocCmd.Parameters.Add("$f", SqliteType.Integer);
        aFile.Value = fileId;

        foreach (var track in tracks)
        {
            if (track.Points.Count < 2) continue;
            var kept = GeoMath.DouglasPeucker(track.Points, DpToleranceM);
            for (int i = 1; i < kept.Count; i++)
            {
                var p1 = track.Points[kept[i - 1]];
                var p2 = track.Points[kept[i]];

                var (s1, s2) = NormalizeEdge(Snap(p1.Lat, p1.Lon), Snap(p2.Lat, p2.Lon));
                if (s1 == s2) continue; // zero-length after snapping — skip

                // Upsert edge.
                eA.Value = s1.Lat; eB.Value = s1.Lon; eC.Value = s2.Lat; eD.Value = s2.Lon;
                edgeIns.ExecuteNonQuery();
                bool isNew = (long)changesCmd.ExecuteScalar()! > 0;

                sA.Value = s1.Lat; sB.Value = s1.Lon; sC.Value = s2.Lat; sD.Value = s2.Lon;
                long edgeId = (long)edgeSel.ExecuteScalar()!;

                if (isNew)
                {
                    rId.Value = edgeId;
                    rLa.Value = Math.Min(s1.Lat, s2.Lat); rLb.Value = Math.Max(s1.Lat, s2.Lat);
                    rCa.Value = Math.Min(s1.Lon, s2.Lon); rCb.Value = Math.Max(s1.Lon, s2.Lon);
                    rtreeIns.ExecuteNonQuery();
                }

                aEdge.Value = edgeId;
                assocCmd.ExecuteNonQuery();
            }
        }

        tx.Commit();
    }

    private List<HeatEdge> QueryHeatEdgesCore(double minLat, double maxLat, double minLon, double maxLon)
    {
        var result = new List<HeatEdge>(512);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT e.lat1, e.lon1, e.lat2, e.lon2, e.count
            FROM heat_edges e
            JOIN heat_edge_rtree r ON r.id = e.id
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
            result.Add(new HeatEdge(
                (float)r.GetDouble(0), (float)r.GetDouble(1),
                (float)r.GetDouble(2), (float)r.GetDouble(3),
                r.GetInt32(4)));
        }
        return result;
    }

    private List<TrackEntry> QueryTrackEntries(double minLat, double maxLat, double minLon, double maxLon)
    {
        var result = new List<TrackEntry>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT f.name, f.path, f.start_time, f.distance_m, f.activity_type
            FROM heat_edge_rtree r
            JOIN heat_edges e ON e.id = r.id
            JOIN heat_edge_tracks et ON et.edge_id = e.id
            JOIN heatmap_files f ON f.id = et.file_id
            WHERE r.max_lat >= $la AND r.min_lat <= $lb
              AND r.max_lon >= $ca AND r.min_lon <= $cb
            ORDER BY COALESCE(f.start_time, f.mtime) DESC;
            """;
        cmd.Parameters.AddWithValue("$la", minLat);
        cmd.Parameters.AddWithValue("$lb", maxLat);
        cmd.Parameters.AddWithValue("$ca", minLon);
        cmd.Parameters.AddWithValue("$cb", maxLon);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            DateTime? trackDate = r.IsDBNull(2) ? null : new DateTime(r.GetInt64(2), DateTimeKind.Utc);
            double? distKm = r.IsDBNull(3) ? null : r.GetDouble(3) / 1000.0;
            string? actType = r.IsDBNull(4) ? null : r.GetString(4);
            result.Add(new TrackEntry(r.GetString(0), r.GetString(1), trackDate, distKm, actType));
        }
        return result;
    }

    // ── Snapping helpers ─────────────────────────────────────────────────────────

    private static (double Lat, double Lon) Snap(double lat, double lon) =>
        (Math.Round(lat / SnapDeg) * SnapDeg, Math.Round(lon / SnapDeg) * SnapDeg);

    /// <summary>Returns the edge in canonical order (smaller endpoint first) so A→B and B→A are the same edge.</summary>
    private static ((double Lat, double Lon) A, (double Lat, double Lon) B) NormalizeEdge(
        (double Lat, double Lon) a, (double Lat, double Lon) b)
    {
        if (a.Lat < b.Lat || (a.Lat == b.Lat && a.Lon <= b.Lon)) return (a, b);
        return (b, a);
    }

    // ── DB helpers ───────────────────────────────────────────────────────────────

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

    // v2 DB: separate file from v1 so old index is not corrupted during investigation.
    private static string DbPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TrackEditor", "heatmap_v2.db");

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
