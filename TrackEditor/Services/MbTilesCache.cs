using BruTile;
using BruTile.Cache;
using Microsoft.Data.Sqlite;
using System.IO;

namespace TrackEditor.Services;

/// <summary>
/// A single-file MBTiles (SQLite) tile cache implementing BruTile's IPersistentCache — one
/// .mbtiles file per basemap instead of thousands of loose .png files. Rows are stored TMS-flipped
/// and a metadata table is written, so the file is a standard MBTiles readable by QGIS and friends.
/// Enforces an optional size cap by evicting least-recently-used tiles. Thread-safe: all access is
/// serialized, and it becomes an inert no-op once disposed so in-flight background fetches are safe.
/// </summary>
public sealed class MbTilesCache : IPersistentCache<byte[]>, IDisposable
{
    private const long TouchThrottleSec = 3600; // update a tile's LRU timestamp at most hourly on read

    private readonly SqliteConnection _conn;
    private readonly object _gate = new();
    private bool _disposed;
    private long _maxBytes;          // 0 or less = unlimited
    private long _bytes;             // running total of tile_data lengths

    public MbTilesCache(string path, string name, long maxBytes = 0)
    {
        _maxBytes = maxBytes;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _conn = new SqliteConnection($"Data Source={path}");
        _conn.Open();
        // Rollback-journal (not WAL): all access here is already serialized by _gate, so we gain
        // nothing from WAL and it would leave -wal/-shm sidecars. DELETE keeps it one file at rest.
        Exec("PRAGMA journal_mode=DELETE; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;");
        Exec("CREATE TABLE IF NOT EXISTS tiles (zoom_level INTEGER, tile_column INTEGER, tile_row INTEGER, " +
             "tile_data BLOB, accessed INTEGER, PRIMARY KEY (zoom_level, tile_column, tile_row));");
        Exec("CREATE TABLE IF NOT EXISTS metadata (name TEXT PRIMARY KEY, value TEXT);");
        if (!ColumnExists("tiles", "accessed")) // migrate caches written by an earlier version
            Exec("ALTER TABLE tiles ADD COLUMN accessed INTEGER;");
        Exec("CREATE INDEX IF NOT EXISTS idx_tiles_accessed ON tiles(accessed);");
        SetMeta("name", name);
        SetMeta("format", "png");
        _bytes = QueryLong("SELECT COALESCE(SUM(LENGTH(tile_data)), 0) FROM tiles;");
        EnforceLimit();
    }

    public void Add(TileIndex index, byte[] tile)
    {
        lock (_gate)
        {
            if (_disposed) return;
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "INSERT OR REPLACE INTO tiles (zoom_level, tile_column, tile_row, tile_data, accessed) " +
                                  "VALUES ($z, $x, $y, $d, $a);";
                Bind(cmd, index);
                cmd.Parameters.AddWithValue("$d", tile);
                cmd.Parameters.AddWithValue("$a", Now());
                cmd.ExecuteNonQuery();
            }
            _bytes += tile.Length; // replaces are rare; EnforceLimit reconciles the exact total
            EnforceLimit();
        }
    }

    // Interface annotates the return as non-null, but (like BruTile's own FileCache) a miss returns null.
    public byte[] Find(TileIndex index)
    {
        lock (_gate)
        {
            if (_disposed) return null!;
            byte[]? data;
            long accessed;
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "SELECT tile_data, accessed FROM tiles WHERE zoom_level=$z AND tile_column=$x AND tile_row=$y;";
                Bind(cmd, index);
                using var r = cmd.ExecuteReader();
                if (!r.Read()) return null!;
                data = r.IsDBNull(0) ? null : (byte[])r.GetValue(0);
                accessed = r.IsDBNull(1) ? 0 : r.GetInt64(1);
            }
            long now = Now();
            if (now - accessed >= TouchThrottleSec) // mark as recently used (throttled to limit writes)
            {
                using var upd = _conn.CreateCommand();
                upd.CommandText = "UPDATE tiles SET accessed=$a WHERE zoom_level=$z AND tile_column=$x AND tile_row=$y;";
                Bind(upd, index);
                upd.Parameters.AddWithValue("$a", now);
                upd.ExecuteNonQuery();
            }
            return data!;
        }
    }

    /// <summary>Empties the cache and reclaims disk (VACUUM).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            if (_disposed) return;
            Exec("DELETE FROM tiles;");
            Exec("VACUUM;");
            _bytes = 0;
        }
    }

    /// <summary>Updates the size cap (0 or less = unlimited) and evicts down to it if needed.</summary>
    public void SetSizeLimit(long maxBytes)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _maxBytes = maxBytes;
            EnforceLimit();
        }
    }

    /// <summary>Evicts least-recently-used tiles until the total is at/under 90% of the cap.</summary>
    private void EnforceLimit()
    {
        if (_maxBytes <= 0 || _bytes <= _maxBytes) return;
        long needed = _bytes - (long)(_maxBytes * 0.9);

        var toDelete = new List<long>();
        long freed = 0;
        using (var sel = _conn.CreateCommand())
        {
            sel.CommandText = "SELECT rowid, LENGTH(tile_data) FROM tiles ORDER BY accessed ASC, rowid ASC;";
            using var r = sel.ExecuteReader();
            while (freed < needed && r.Read())
            {
                toDelete.Add(r.GetInt64(0));
                freed += r.GetInt64(1);
            }
        }
        if (toDelete.Count == 0) return;

        using (var tx = _conn.BeginTransaction())
        using (var del = _conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM tiles WHERE rowid=$r;";
            var p = del.CreateParameter();
            p.ParameterName = "$r";
            del.Parameters.Add(p);
            foreach (long rowid in toDelete) { p.Value = rowid; del.ExecuteNonQuery(); }
            tx.Commit();
        }
        _bytes -= freed;
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private bool ColumnExists(string table, string column)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private long QueryLong(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    public void Remove(TileIndex index)
    {
        lock (_gate)
        {
            if (_disposed) return;
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM tiles WHERE zoom_level=$z AND tile_column=$x AND tile_row=$y;";
            Bind(cmd, index);
            cmd.ExecuteNonQuery();
        }
    }

    // BruTile uses OSM row order (origin top-left); MBTiles stores TMS (origin bottom-left).
    private static void Bind(SqliteCommand cmd, TileIndex index)
    {
        long tmsRow = ((1L << index.Level) - 1) - index.Row;
        cmd.Parameters.AddWithValue("$z", index.Level);
        cmd.Parameters.AddWithValue("$x", index.Col);
        cmd.Parameters.AddWithValue("$y", tmsRow);
    }

    private void SetMeta(string name, string value)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO metadata (name, value) VALUES ($n, $v);";
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

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
