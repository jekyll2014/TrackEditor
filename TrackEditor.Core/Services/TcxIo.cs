using System.Globalization;
using System.Xml;
using System.Xml.Linq;

using TrackEditor.Core.Models;

namespace TrackEditor.Core.Services;

/// <summary>Reads and writes Garmin TCX (Training Center Database) files. Import handles both &lt;Activity&gt;
/// and &lt;Course&gt; documents; export writes a single-lap &lt;Activity&gt;. Namespace-agnostic on read.</summary>
public static class TcxIo
{
    private static readonly XNamespace Tc = "http://www.garmin.com/xmlschemas/TrainingCenterDatabase/v2";
    private static readonly XNamespace Ax = "http://www.garmin.com/xmlschemas/ActivityExtension/v2";

    public static List<Track> Load(string path)
    {
        using var fs = File.OpenRead(path);
        return Load(fs, Path.GetFileNameWithoutExtension(path));
    }

    /// <summary>Stream-based load. Each &lt;Activity&gt;/&lt;Course&gt; becomes one track (its laps joined).</summary>
    public static List<Track> Load(Stream stream, string baseName = "Track")
    {
        var doc = XDocument.Load(stream);
        var tracks = new List<Track>();

        // A TCX Activity has no name field — only an <Id> timestamp — so the filename is the better label.
        // A Course carries a real <Name>. Number repeats when a file holds several of either.
        var activities = doc.Descendants().Where(e => e.Name.LocalName == "Activity").ToList();
        for (int i = 0; i < activities.Count; i++)
            AddContainer(activities[i], activities.Count > 1 ? $"{baseName} ({i + 1})" : baseName, tracks);
        foreach (var crs in doc.Descendants().Where(e => e.Name.LocalName == "Course"))
            AddContainer(crs, ChildValue(crs, "Name") ?? baseName + " (course)", tracks);

        return tracks;
    }

    private static void AddContainer(XElement container, string name, List<Track> tracks)
    {
        var track = new Track { Name = name };
        foreach (var tp in container.Descendants().Where(e => e.Name.LocalName == "Trackpoint"))
            if (ParsePoint(tp) is TrackPoint p) track.Points.Add(p);
        if (track.Points.Count > 0) tracks.Add(track);
    }

    private static string? ChildValue(XElement e, string localName) =>
        e.Elements().FirstOrDefault(c => c.Name.LocalName == localName)?.Value;

    private static string? DescendantValue(XElement e, string localName) =>
        e.Descendants().FirstOrDefault(c => c.Name.LocalName == localName)?.Value;

    private static TrackPoint? ParsePoint(XElement tp)
    {
        var pos = tp.Elements().FirstOrDefault(c => c.Name.LocalName == "Position");
        if (pos is null ||
            !double.TryParse(ChildValue(pos, "LatitudeDegrees"), NumberStyles.Float, CultureInfo.InvariantCulture, out double lat) ||
            !double.TryParse(ChildValue(pos, "LongitudeDegrees"), NumberStyles.Float, CultureInfo.InvariantCulture, out double lon))
            return null; // Trackpoints without a position (e.g. a paused sample) carry no geometry.

        var p = new TrackPoint { Lat = lat, Lon = lon };
        if (double.TryParse(ChildValue(tp, "AltitudeMeters"), NumberStyles.Float, CultureInfo.InvariantCulture, out double ele))
            p.Ele = ele;
        if (DateTime.TryParse(ChildValue(tp, "Time"), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime t))
            p.Time = DateTime.SpecifyKind(t, DateTimeKind.Utc);
        // HeartRateBpm wraps its number in <Value>; Cadence is a direct child. Temperature isn't standard TCX.
        if (int.TryParse(DescendantValue(tp, "HeartRateBpm"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int hr))
            p.Hr = hr;
        if (int.TryParse(ChildValue(tp, "Cadence"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int cad))
            p.Cad = cad;
        return p;
    }

    public static void Save(string path, IEnumerable<Track> tracks)
    {
        using var writer = XmlWriter.Create(path, new XmlWriterSettings { Indent = true });
        Write(writer, tracks);
    }

    public static void Save(Stream stream, IEnumerable<Track> tracks)
    {
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Indent = true });
        Write(writer, tracks);
    }

    private static void Write(XmlWriter writer, IEnumerable<Track> tracks)
    {
        var activities = new XElement(Tc + "Activities");
        foreach (var track in tracks)
            activities.Add(BuildActivity(track));

        var root = new XElement(Tc + "TrainingCenterDatabase",
            new XAttribute(XNamespace.Xmlns + "ns3", Ax.NamespaceName),
            activities);
        new XDocument(root).Save(writer);
    }

    private static XElement BuildActivity(Track track)
    {
        var pts = track.Points;
        var dist = GeoMath.CumulativeDistancesM(pts);
        // TCX requires a lap StartTime and the trackpoint <Time>; synthesize a clock for untimed (drawn) routes.
        var startTime = pts.Count > 0 ? pts[0].Time ?? new DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                                      : new DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        double totalSecs = pts.Count > 1 && pts[0].Time is DateTime s && pts[^1].Time is DateTime en
            ? (en - s).TotalSeconds : 0;

        var trackEl = new XElement(Tc + "Track");
        for (int i = 0; i < pts.Count; i++)
            trackEl.Add(BuildTrackpoint(pts[i], dist[i], startTime, i));

        var lap = new XElement(Tc + "Lap",
            new XAttribute("StartTime", Iso(startTime)),
            new XElement(Tc + "TotalTimeSeconds", totalSecs.ToString("F1", CultureInfo.InvariantCulture)),
            new XElement(Tc + "DistanceMeters", (dist.Length > 0 ? dist[^1] : 0).ToString("F1", CultureInfo.InvariantCulture)),
            new XElement(Tc + "Intensity", "Active"),
            new XElement(Tc + "TriggerMethod", "Manual"),
            trackEl);

        return new XElement(Tc + "Activity",
            new XAttribute("Sport", "Other"),
            new XElement(Tc + "Id", Iso(startTime)),
            lap);
    }

    private static XElement BuildTrackpoint(TrackPoint p, double distanceM, DateTime startTime, int index)
    {
        // Element order follows the TCX schema (Time, Position, AltitudeMeters, DistanceMeters, HeartRateBpm, Cadence).
        // Untimed points get a synthetic 1 Hz clock so the file stays schema-valid.
        var time = p.Time ?? startTime.AddSeconds(index);
        var tp = new XElement(Tc + "Trackpoint",
            new XElement(Tc + "Time", Iso(time)),
            new XElement(Tc + "Position",
                new XElement(Tc + "LatitudeDegrees", p.Lat.ToString("F7", CultureInfo.InvariantCulture)),
                new XElement(Tc + "LongitudeDegrees", p.Lon.ToString("F7", CultureInfo.InvariantCulture))));
        if (p.Ele is double ele)
            tp.Add(new XElement(Tc + "AltitudeMeters", ele.ToString("F1", CultureInfo.InvariantCulture)));
        tp.Add(new XElement(Tc + "DistanceMeters", distanceM.ToString("F1", CultureInfo.InvariantCulture)));
        if (p.Hr is int hr)
            tp.Add(new XElement(Tc + "HeartRateBpm", new XElement(Tc + "Value", hr)));
        if (p.Cad is int cad)
            tp.Add(new XElement(Tc + "Cadence", cad));
        return tp;
    }

    private static string Iso(DateTime t) =>
        t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
