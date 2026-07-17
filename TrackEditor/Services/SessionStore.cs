using System.IO;
using System.IO.Compression;
using System.Text.Json;
using TrackEditor.Models;

namespace TrackEditor.Services;

/// <summary>
/// Persists the open track list between runs in a standalone gzipped-JSON file
/// (%AppData%\TrackEditor\session.json.gz) — delete that one file to fully clear restored state.
/// </summary>
public static class SessionStore
{
    public class Session
    {
        public int Active { get; set; } = -1;
        public List<Track> Tracks { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = false };

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TrackEditor");

    public static string FilePath => Path.Combine(Dir, "session.json.gz");

    /// <summary>Uncompressed file written by earlier versions; still read (then replaced) for migration.</summary>
    private static string LegacyPath => Path.Combine(Dir, "session.json");

    public static Session? Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                using var fs = File.OpenRead(FilePath);
                using var gz = new GZipStream(fs, CompressionMode.Decompress);
                using var ms = new MemoryStream();
                gz.CopyTo(ms);
                return JsonSerializer.Deserialize<Session>(ms.ToArray(), Opts);
            }
            if (File.Exists(LegacyPath)) // older uncompressed session
                return JsonSerializer.Deserialize<Session>(File.ReadAllText(LegacyPath), Opts);
        }
        catch { /* corrupt/unreadable → start fresh */ }
        return null;
    }

    public static void Save(Session session)
    {
        try
        {
            if (session.Tracks.Count == 0) { Delete(); return; } // nothing open → no stale file
            Directory.CreateDirectory(Dir);
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(session, Opts);
            using (var fs = File.Create(FilePath))
            using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
                gz.Write(json, 0, json.Length);
            if (File.Exists(LegacyPath)) File.Delete(LegacyPath); // drop the old uncompressed file
        }
        catch { /* non-fatal: session just won't persist */ }
    }

    public static void Delete()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { /* ignore */ }
        try { if (File.Exists(LegacyPath)) File.Delete(LegacyPath); } catch { /* ignore */ }
    }
}
