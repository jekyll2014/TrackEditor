using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SharpKml.Dom;
using SharpKml.Engine;
using TrackEditor.Models;

namespace TrackEditor.Services;

public static class KmlIo
{
    /// <summary>Loads LineStrings, gx:Tracks and gx:MultiTracks from a .kml or .kmz file.</summary>
    public static List<Track> Load(string path)
    {
        using var fs = File.OpenRead(path);
        KmlFile kml;
        if (path.EndsWith(".kmz", StringComparison.OrdinalIgnoreCase))
        {
            using var kmz = KmzFile.Open(fs);
            kml = kmz.GetDefaultKmlFile()
                  ?? throw new InvalidDataException("KMZ contains no KML document.");
        }
        else
        {
            kml = KmlFile.Load(fs);
        }

        var tracks = new List<Track>();
        string baseName = Path.GetFileNameWithoutExtension(path);
        if (kml.Root is null) return tracks;

        foreach (var pm in kml.Root.Flatten().OfType<Placemark>())
            Collect(pm.Geometry, pm.Name ?? baseName, tracks);

        return tracks;
    }

    private static void Collect(Geometry? geometry, string name, List<Track> tracks)
    {
        switch (geometry)
        {
            case LineString ls when ls.Coordinates is not null:
            {
                var track = new Track { Name = name };
                foreach (var v in ls.Coordinates)
                    track.Points.Add(new TrackPoint { Lat = v.Latitude, Lon = v.Longitude, Ele = v.Altitude });
                if (track.Points.Count > 1) tracks.Add(track);
                break;
            }
            case SharpKml.Dom.GX.Track gxTrack:
            {
                var track = new Track { Name = name };
                var whens = gxTrack.When.ToList();
                var coords = gxTrack.Coordinates.ToList();
                for (int i = 0; i < coords.Count; i++)
                {
                    var p = new TrackPoint { Lat = coords[i].Latitude, Lon = coords[i].Longitude, Ele = coords[i].Altitude };
                    if (i < whens.Count) p.Time = DateTime.SpecifyKind(whens[i], DateTimeKind.Utc);
                    track.Points.Add(p);
                }
                if (track.Points.Count > 1) tracks.Add(track);
                break;
            }
            case SharpKml.Dom.GX.MultipleTrack multi:
                foreach (var t in multi.Tracks) Collect(t, name, tracks);
                break;
            case MultipleGeometry mg:
                foreach (var g in mg.Geometry) Collect(g, name, tracks);
                break;
        }
    }
}
