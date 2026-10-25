using Microsoft.Data.Sqlite;

using System.IO;

using TrackEditor.Core.Models;
using TrackEditor.Core.Services;

namespace TrackEditor.Services;

/// <summary>
/// SQLite-backed spatial index of activity tracks (.fit/.gpx/.tcx).
///
/// Pre-computes a density grid at index time: GPS polylines are rasterised onto a ~11 m grid and
/// each visited cell stores the count of distinct files passing through it. Rendering reads the
/// cells in the viewport and paints a single raster bitmap (Strava-style heatmap) — overlapping
/// tracks merge automatically and the draw cost is one image regardless of track count.
///
/// Thread-safe: all DB access serialised on <c>_gate</c>; becomes a no-op after Dispose.
/// </summary>
public sealed class HeatmapIndex : IDisposable
{
    // Snapping grid: ~11 m at the equator — merges GPS noise and near-identical parallel paths.
    private const double SnapDeg = 0.0001;
    // Hard cap on cells/buckets returned per viewport query.
    private const int QueryLimit = 200_000;
    // Target returned-grid dimension at low zoom; cells are bucketed so the grid stays ~this wide.
    // Matches HeatmapRenderer.MaxDim so one bucket ≈ one rendered pixel.
    private const int RenderGridMax = 1536;
    private const string Exts = ".fit|.gpx|.tcx";

    private readonly SqliteConnection _conn;
    private readonly object _gate = new();
    private bool _disposed;

    /// <summary>Grid cell size in degrees — exposed so the renderer can size the raster.</summary>
    public const double CellSizeDeg = SnapDeg;

    /// <summary>A density cell: integer grid coordinates plus the count of distinct files through it.</summary>
    public record HeatCell(int Gx, int Gy, int Count);

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

            -- Grid density cells shared across all files that pass through them.
            CREATE TABLE IF NOT EXISTS heat_cells (
                id    INTEGER PRIMARY KEY,
                gx    INTEGER NOT NULL,
                gy    INTEGER NOT NULL,
                count INTEGER NOT NULL DEFAULT 0,
                UNIQUE(gx, gy)
            );

            -- Which files contributed to each cell (one row per file/cell pair).
            CREATE TABLE IF NOT EXISTS heat_cell_tracks (
                cell_id INTEGER NOT NULL REFERENCES heat_cells(id)      ON DELETE CASCADE,
                file_id INTEGER NOT NULL REFERENCES heatmap_files(id)   ON DELETE CASCADE,
                PRIMARY KEY (cell_id, file_id)
            );

            CREATE VIRTUAL TABLE IF NOT EXISTS heat_cell_rtree USING rtree(
                id,
                min_gx, max_gx,
                min_gy, max_gy
            );

            -- When a cell loses all files, remove it from the R-tree.
            CREATE TRIGGER IF NOT EXISTS del_heat_cell_rtree
            AFTER DELETE ON heat_cells BEGIN
                DELETE FROM heat_cell_rtree WHERE id = OLD.id;
            END;

            -- When a file/cell association is removed, decrement the cell count;
            -- delete the cell when it reaches zero (triggers del_heat_cell_rtree above).
            CREATE TRIGGER IF NOT EXISTS dec_heat_cell_count
            AFTER DELETE ON heat_cell_tracks BEGIN
                UPDATE heat_cells SET count = count - 1 WHERE id = OLD.cell_id;
                DELETE FROM heat_cells WHERE id = OLD.cell_id AND count <= 0;
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

    /// <summary>Returns density cells that intersect the given lat/lon bounding box.</summary>
    public List<HeatCell> QueryHeatCells(double minLat, double maxLat, double minLon, double maxLon)
    {
        lock (_gate)
        {
            if (_disposed) return [];
            return QueryHeatCellsCore(minLat, maxLat, minLon, maxLon);
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
            Exec("DELETE FROM heatmap_files;"); // cascades to heat_cell_tracks → dec trigger → deletes cells → rtree
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

        // Prepared commands reused for every cell in this file.
        using var cellIns = _conn.CreateCommand();
        cellIns.Transaction = tx;
        cellIns.CommandText = "INSERT OR IGNORE INTO heat_cells (gx, gy, count) VALUES ($x, $y, 0); SELECT id FROM heat_cells WHERE gx=$x AND gy=$y;";
        var cX = cellIns.Parameters.Add("$x", SqliteType.Integer);
        var cY = cellIns.Parameters.Add("$y", SqliteType.Integer);

        using var changesCmd = _conn.CreateCommand();
        changesCmd.Transaction = tx;
        changesCmd.CommandText = "SELECT changes();";

        using var rtreeIns = _conn.CreateCommand();
        rtreeIns.Transaction = tx;
        rtreeIns.CommandText = "INSERT INTO heat_cell_rtree (id, min_gx, max_gx, min_gy, max_gy) VALUES ($id, $x, $x, $y, $y);";
        var rId = rtreeIns.Parameters.Add("$id", SqliteType.Integer);
        var rX  = rtreeIns.Parameters.Add("$x", SqliteType.Integer);
        var rY  = rtreeIns.Parameters.Add("$y", SqliteType.Integer);

        // INSERT OR IGNORE prevents double-counting when the same file visits the same cell twice.
        // changes() after INSERT returns 1 if newly associated, 0 if duplicate.
        using var assocCmd = _conn.CreateCommand();
        assocCmd.Transaction = tx;
        assocCmd.CommandText = """
            INSERT OR IGNORE INTO heat_cell_tracks (cell_id, file_id) VALUES ($c, $f);
            UPDATE heat_cells SET count = count + changes() WHERE id = $c;
            """;
        var aCell = assocCmd.Parameters.Add("$c", SqliteType.Integer);
        var aFile = assocCmd.Parameters.Add("$f", SqliteType.Integer);
        aFile.Value = fileId;

        // Per-file caches: cell-id lookup to skip repeated SELECTs, and the set of cells already
        // associated with this file so revisits don't re-run the assoc query.
        var cellIds = new Dictionary<(long, long), long>();
        var associated = new HashSet<long>();

        // Upserts one grid cell and associates it with this file.
        void EmitCell(long gx, long gy)
        {
            if (!cellIds.TryGetValue((gx, gy), out long cellId))
            {
                cX.Value = gx; cY.Value = gy;
                cellId = (long)cellIns.ExecuteScalar()!;      // INSERT OR IGNORE then SELECT id
                // changes() reflects the INSERT: >0 means the cell row is new → add to R-tree.
                if ((long)changesCmd.ExecuteScalar()! > 0)
                {
                    rId.Value = cellId; rX.Value = gx; rY.Value = gy;
                    rtreeIns.ExecuteNonQuery();
                }
                cellIds[(gx, gy)] = cellId;
            }

            if (associated.Add(cellId))
            {
                aCell.Value = cellId;
                assocCmd.ExecuteNonQuery();
            }
        }

        foreach (var track in tracks)
        {
            if (track.Points.Count < 2) continue;

            // Rasterize the polyline onto the snap grid: convert each point to an integer cell,
            // then walk a straight line of unit steps between consecutive cells so no cells are
            // skipped. All tracks down the same road touch the SAME cells, so density accumulates.
            long pgx = 0, pgy = 0;
            bool have = false;
            foreach (var p in track.Points)
            {
                long cgx = (long)Math.Round(p.Lon / SnapDeg);
                long cgy = (long)Math.Round(p.Lat / SnapDeg);
                if (!have) { pgx = cgx; pgy = cgy; have = true; EmitCell(cgx, cgy); continue; }
                if (cgx == pgx && cgy == pgy) continue;

                long dx = cgx - pgx, dy = cgy - pgy;
                long steps = Math.Max(Math.Abs(dx), Math.Abs(dy));
                for (long s = 1; s <= steps; s++)
                {
                    long nx = pgx + (long)Math.Round((double)dx * s / steps);
                    long ny = pgy + (long)Math.Round((double)dy * s / steps);
                    EmitCell(nx, ny);
                }
                pgx = cgx; pgy = cgy;
            }
        }

        tx.Commit();
    }

    private List<HeatCell> QueryHeatCellsCore(double minLat, double maxLat, double minLon, double maxLon)
    {
        // Convert the lat/lon bbox to inclusive integer cell bounds (pad by one cell).
        long gxMin = (long)Math.Floor(minLon / SnapDeg) - 1;
        long gxMax = (long)Math.Ceiling(maxLon / SnapDeg) + 1;
        long gyMin = (long)Math.Floor(minLat / SnapDeg) - 1;
        long gyMax = (long)Math.Ceiling(maxLat / SnapDeg) + 1;

        // At low zoom the viewport spans more cells than the renderer has pixels, so raw cells would
        // overflow QueryLimit and get truncated — dropping whole regions. Aggregate into buckets sized
        // so the returned grid stays ~RenderGridMax across, keeping full coverage with few rows.
        long span = Math.Max(gxMax - gxMin, gyMax - gyMin);
        long bf = Math.Max(1, (long)Math.Ceiling(span / (double)RenderGridMax));

        var result = new List<HeatCell>(4096);
        using var cmd = _conn.CreateCommand();
        if (bf == 1)
        {
            cmd.CommandText = """
                SELECT c.gx, c.gy, c.count
                FROM heat_cells c
                JOIN heat_cell_rtree r ON r.id = c.id
                WHERE r.max_gx >= $xa AND r.min_gx <= $xb
                  AND r.max_gy >= $ya AND r.min_gy <= $yb
                LIMIT $lim;
                """;
        }
        else
        {
            // Bucket by integer division; representative cell = bucket origin; brightest road wins.
            cmd.CommandText = """
                SELECT (c.gx / $bf) * $bf AS bx, (c.gy / $bf) * $bf AS by, MAX(c.count)
                FROM heat_cells c
                JOIN heat_cell_rtree r ON r.id = c.id
                WHERE r.max_gx >= $xa AND r.min_gx <= $xb
                  AND r.max_gy >= $ya AND r.min_gy <= $yb
                GROUP BY c.gx / $bf, c.gy / $bf
                LIMIT $lim;
                """;
            cmd.Parameters.AddWithValue("$bf", bf);
        }
        cmd.Parameters.AddWithValue("$xa", gxMin);
        cmd.Parameters.AddWithValue("$xb", gxMax);
        cmd.Parameters.AddWithValue("$ya", gyMin);
        cmd.Parameters.AddWithValue("$yb", gyMax);
        cmd.Parameters.AddWithValue("$lim", QueryLimit);

        using var r = cmd.ExecuteReader();
        while (r.Read())
            result.Add(new HeatCell(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2)));
        return result;
    }

    private List<TrackEntry> QueryTrackEntries(double minLat, double maxLat, double minLon, double maxLon)
    {
        long gxMin = (long)Math.Floor(minLon / SnapDeg) - 1;
        long gxMax = (long)Math.Ceiling(maxLon / SnapDeg) + 1;
        long gyMin = (long)Math.Floor(minLat / SnapDeg) - 1;
        long gyMax = (long)Math.Ceiling(maxLat / SnapDeg) + 1;

        var result = new List<TrackEntry>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT f.name, f.path, f.start_time, f.distance_m, f.activity_type
            FROM heat_cell_rtree r
            JOIN heat_cells c ON c.id = r.id
            JOIN heat_cell_tracks ct ON ct.cell_id = c.id
            JOIN heatmap_files f ON f.id = ct.file_id
            WHERE r.max_gx >= $xa AND r.min_gx <= $xb
              AND r.max_gy >= $ya AND r.min_gy <= $yb
            ORDER BY COALESCE(f.start_time, f.mtime) DESC;
            """;
        cmd.Parameters.AddWithValue("$xa", gxMin);
        cmd.Parameters.AddWithValue("$xb", gxMax);
        cmd.Parameters.AddWithValue("$ya", gyMin);
        cmd.Parameters.AddWithValue("$yb", gyMax);
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
        "TrackEditor", "heatmap_v4.db");

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
