using System.Globalization;
using System.Xml;
using System.Xml.Linq;

using TrackEditor.Core.Models;

namespace TrackEditor.Core.Services;

public static class GpxIo
{
    /// <summary>Loads all &lt;trk&gt; (segments joined) and &lt;rte&gt; elements. Namespace-agnostic (GPX 1.0/1.1).</summary>
    public static List<Track> Load(string path)
    {
        using var fs = File.OpenRead(path);
        return Load(fs, Path.GetFileNameWithoutExtension(path));
    }

    /// <summary>Stream-based load (browser upload / any source). <paramref name="baseName"/> names tracks that lack a &lt;name&gt;.</summary>
    public static List<Track> Load(Stream stream, string baseName = "Track")
    {
        var doc = XDocument.Load(stream);
        var tracks = new List<Track>();

        foreach (var trk in doc.Descendants().Where(e => e.Name.LocalName == "trk"))
        {
            var track = new Track { Name = ChildValue(trk, "name") ?? baseName };
            foreach (var seg in trk.Elements().Where(e => e.Name.LocalName == "trkseg"))
                foreach (var pt in seg.Elements().Where(e => e.Name.LocalName == "trkpt"))
                    if (ParsePoint(pt) is TrackPoint p) track.Points.Add(p);
            if (track.Points.Count > 0) tracks.Add(track);
        }

        foreach (var rte in doc.Descendants().Where(e => e.Name.LocalName == "rte"))
        {
            var track = new Track { Name = ChildValue(rte, "name") ?? baseName + " (route)" };
            foreach (var pt in rte.Elements().Where(e => e.Name.LocalName == "rtept"))
                if (ParsePoint(pt) is TrackPoint p) track.Points.Add(p);
            if (track.Points.Count > 0) tracks.Add(track);
        }

        return tracks;
    }

    private static string? ChildValue(XElement e, string localName) =>
        e.Elements().FirstOrDefault(c => c.Name.LocalName == localName)?.Value;

    /// <summary>First descendant (any depth) with the given local name — for nested &lt;extensions&gt;
    /// sensor tags. Namespace-agnostic: matches Strava (gpxtpx:) and Garmin (ns3:) alike.</summary>
    private static string? DescendantValue(XElement e, string localName) =>
        e.Descendants().FirstOrDefault(c => c.Name.LocalName == localName)?.Value;

    private static TrackPoint? ParsePoint(XElement pt)
    {
        if (!double.TryParse(pt.Attribute("lat")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double lat) ||
            !double.TryParse(pt.Attribute("lon")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double lon))
            return null;

        var p = new TrackPoint { Lat = lat, Lon = lon };
        if (double.TryParse(ChildValue(pt, "ele"), NumberStyles.Float, CultureInfo.InvariantCulture, out double ele))
            p.Ele = ele;
        if (DateTime.TryParse(ChildValue(pt, "time"), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime t))
            p.Time = DateTime.SpecifyKind(t, DateTimeKind.Utc);
        // Optional sensor channels from <extensions> (Garmin ns3: / Strava gpxtpx:), matched by local name.
        if (int.TryParse(DescendantValue(pt, "hr"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int hr))
            p.Hr = hr;
        if (int.TryParse(DescendantValue(pt, "cad"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int cad))
            p.Cad = cad;
        if (double.TryParse(DescendantValue(pt, "atemp"), NumberStyles.Float, CultureInfo.InvariantCulture, out double atemp))
            p.Temp = atemp;
        string? surface = DescendantValue(pt, "surface");
        if (!string.IsNullOrWhiteSpace(surface)) p.Surface = surface;
        // A <name> on a trkpt/rtept marks it as a named waypoint (valid GPX wptType member).
        string? name = ChildValue(pt, "name");
        if (!string.IsNullOrWhiteSpace(name)) p.Name = name.Trim();
        return p;
    }

    public static void Save(string path, IEnumerable<Track> tracks)
    {
        using var writer = XmlWriter.Create(path, new XmlWriterSettings { Indent = true });
        Write(writer, tracks);
    }

    /// <summary>Stream-based save (browser download / any sink).</summary>
    public static void Save(Stream stream, IEnumerable<Track> tracks)
    {
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Indent = true });
        Write(writer, tracks);
    }

    private static void Write(XmlWriter writer, IEnumerable<Track> tracks)
    {
        XNamespace ns = "http://www.topografix.com/GPX/1/1";
        XNamespace tpx = "http://www.garmin.com/xmlschemas/TrackPointExtension/v1";
        var gpx = new XElement(ns + "gpx",
            new XAttribute("version", "1.1"),
            new XAttribute("creator", "TrackEditor"),
            new XAttribute(XNamespace.Xmlns + "gpxtpx", tpx.NamespaceName));

        foreach (var track in tracks)
        {
            var seg = new XElement(ns + "trkseg");
            foreach (var p in track.Points)
            {
                var pt = new XElement(ns + "trkpt",
                    new XAttribute("lat", p.Lat.ToString("F7", CultureInfo.InvariantCulture)),
                    new XAttribute("lon", p.Lon.ToString("F7", CultureInfo.InvariantCulture)));
                // GPX wptType element order: ele, time, then name — keep it so strict parsers accept it.
                if (p.Ele is double ele)
                    pt.Add(new XElement(ns + "ele", ele.ToString("F1", CultureInfo.InvariantCulture)));
                if (p.Time is DateTime t)
                {
                    // Emit sub-second precision when present. Predicted times are fractional; rounding them to
                    // whole seconds makes per-point Δt uneven, which shows up as a spiky derived-speed graph.
                    var u = t.ToUniversalTime();
                    string fmt = u.Millisecond == 0 ? "yyyy-MM-dd'T'HH:mm:ss'Z'" : "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
                    pt.Add(new XElement(ns + "time", u.ToString(fmt, CultureInfo.InvariantCulture)));
                }
                if (p.IsWaypoint)
                {
                    pt.Add(new XElement(ns + "name", p.Name));
                    pt.Add(new XElement(ns + "sym", "Flag"));
                }
                // <extensions> is the last wptType member; emit recorded sensor channels + surface if any.
                if (p.Hr is not null || p.Cad is not null || p.Temp is not null || p.Surface is not null)
                {
                    var ext = new XElement(ns + "extensions");
                    if (p.Hr is not null || p.Cad is not null || p.Temp is not null)
                    {
                        var tpxExt = new XElement(tpx + "TrackPointExtension");
                        if (p.Hr is int hr) tpxExt.Add(new XElement(tpx + "hr", hr));
                        if (p.Cad is int cad) tpxExt.Add(new XElement(tpx + "cad", cad));
                        if (p.Temp is double temp)
                            tpxExt.Add(new XElement(tpx + "atemp", temp.ToString("0.#", CultureInfo.InvariantCulture)));
                        ext.Add(tpxExt);
                    }
                    if (!string.IsNullOrWhiteSpace(p.Surface)) ext.Add(new XElement(ns + "surface", p.Surface));
                    pt.Add(ext);
                }
                seg.Add(pt);
            }
            gpx.Add(new XElement(ns + "trk", new XElement(ns + "name", track.Name), seg));
        }

        new XDocument(gpx).Save(writer);
    }
}
