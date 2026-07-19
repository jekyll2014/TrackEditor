using System.Text.Json.Serialization;

namespace TrackEditor.Models;

public class TrackPoint
{
    public double Lat { get; set; }
    public double Lon { get; set; }
    public double? Ele { get; set; }
    public DateTime? Time { get; set; }

    /// <summary>Optional label. A named point is a waypoint/marker highlighting a key spot on the route.</summary>
    public string? Name { get; set; }

    /// <summary>True when this point is a named waypoint/marker.</summary>
    [JsonIgnore]
    public bool IsWaypoint => !string.IsNullOrWhiteSpace(Name);

    public TrackPoint Clone() => new() { Lat = Lat, Lon = Lon, Ele = Ele, Time = Time, Name = Name };
}

public class Track
{
    public string Name { get; set; } = "Track";
    public List<TrackPoint> Points { get; set; } = new();
    public string ColorHex { get; set; } = "#E53935";
    public double Width { get; set; } = 3;
    public bool Visible { get; set; } = true;

    /// <summary>True when some/all elevations were filled from a DEM/online service rather than recorded.</summary>
    public bool ElevationEstimated { get; set; }

    /// <summary>Path this track was loaded from / last saved to; null for a drawn (never-saved) track.</summary>
    public string? SourceFile { get; set; }

    /// <summary>Content hash captured at load/save; used to detect user modifications. Null = never baselined.</summary>
    public string? BaselineHash { get; set; }

    /// <summary>True when name/points/elevation differ from the load/save baseline (metadata like color/width is ignored).</summary>
    [JsonIgnore]
    public bool IsModified => Points.Count > 0 && ContentHash() != BaselineHash;

    /// <summary>Deterministic hash over the "content" that counts as a modification: name + each point's lat/lon/ele/time.</summary>
    public string ContentHash()
    {
        unchecked
        {
            ulong h = 1469598103934665603UL; // FNV-1a
            void Mix(long v) { for (int i = 0; i < 8; i++) { h ^= (byte)(v >> (i * 8)); h *= 1099511628211UL; } }
            foreach (char c in Name) { h ^= c; h *= 1099511628211UL; }
            foreach (var p in Points)
            {
                Mix(BitConverter.DoubleToInt64Bits(p.Lat));
                Mix(BitConverter.DoubleToInt64Bits(p.Lon));
                Mix(BitConverter.DoubleToInt64Bits(p.Ele ?? double.NaN));
                Mix(p.Time?.Ticks ?? 0);
                foreach (char c in p.Name ?? "") { h ^= c; h *= 1099511628211UL; }
            }
            return h.ToString("x");
        }
    }

    /// <summary>Mark the current content as the clean baseline (call after load-from-file or save-to-file).</summary>
    public void ResetBaseline() => BaselineHash = ContentHash();

    public Track Clone() => new()
    {
        Name = Name,
        ColorHex = ColorHex,
        Width = Width,
        Visible = Visible,
        ElevationEstimated = ElevationEstimated,
        SourceFile = SourceFile,
        BaselineHash = BaselineHash,
        Points = Points.Select(p => p.Clone()).ToList(),
    };
}

/// <summary>Document = all loaded tracks + clipboard + undo/redo history (whole-document snapshots).</summary>
public class TrackDocument
{
    private const int MaxUndo = 60;

    public List<Track> Tracks { get; private set; } = new();
    public List<TrackPoint> Clipboard { get; } = new();

    private readonly List<(List<Track> Tracks, int Active)> _undo = new();
    private readonly List<(List<Track> Tracks, int Active)> _redo = new();

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    private static List<Track> DeepClone(List<Track> tracks) => tracks.Select(t => t.Clone()).ToList();

    /// <summary>Call BEFORE every mutating operation.</summary>
    public void Snapshot(int activeIndex)
    {
        _undo.Add((DeepClone(Tracks), activeIndex));
        if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
        _redo.Clear();
    }

    /// <summary>Returns the active-track index to restore.</summary>
    public int Undo(int currentActive)
    {
        var s = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add((DeepClone(Tracks), currentActive));
        Tracks = s.Tracks;
        return s.Active;
    }

    public int Redo(int currentActive)
    {
        var s = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add((DeepClone(Tracks), currentActive));
        Tracks = s.Tracks;
        return s.Active;
    }
}
