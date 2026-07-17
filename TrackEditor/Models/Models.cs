using System;
using System.Collections.Generic;
using System.Linq;

namespace TrackEditor.Models;

public class TrackPoint
{
    public double Lat { get; set; }
    public double Lon { get; set; }
    public double? Ele { get; set; }
    public DateTime? Time { get; set; }

    public TrackPoint Clone() => new() { Lat = Lat, Lon = Lon, Ele = Ele, Time = Time };
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

    public Track Clone() => new()
    {
        Name = Name,
        ColorHex = ColorHex,
        Width = Width,
        Visible = Visible,
        ElevationEstimated = ElevationEstimated,
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
