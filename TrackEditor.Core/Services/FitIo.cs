using System.Text;

using Dynastream.Fit;

using TrackEditor.Core.Models;

using FitDateTime = Dynastream.Fit.DateTime;
using IoFile = System.IO.File;

namespace TrackEditor.Core.Services;

/// <summary>Reads and writes Garmin FIT files. Import accepts any FIT (activity or course); export writes a
/// FIT Course — the route form a device navigates — so drawn (untimed) tracks round-trip too.</summary>
public static class FitIo
{
    // A FIT position field is an int count of "semicircles": degrees = semicircles × 180 / 2^31.
    private const double SemicirclesPerDegree = 2147483648.0 / 180.0; // 2^31 / 180

    private static double ToDegrees(int semicircles) => semicircles / SemicirclesPerDegree;
    private static int ToSemicircles(double degrees) => (int)System.Math.Round(degrees * SemicirclesPerDegree);

    public static List<Track> Load(string path)
    {
        using var fs = IoFile.OpenRead(path);
        return Load(fs, Path.GetFileNameWithoutExtension(path));
    }

    /// <summary>Stream-based load. A FIT file is one recording, so all Record messages join into a single track;
    /// records without a GPS fix (paused/searching) are skipped.</summary>
    public static List<Track> Load(Stream stream, string baseName = "Track")
    {
        var track = new Track { Name = baseName };

        var decode = new Decode();
        decode.MesgEvent += (_, e) =>
        {
            switch (e.mesg.Num)
            {
                case MesgNum.Course:
                    string? name = NameString(new CourseMesg(e.mesg).GetName());
                    if (!string.IsNullOrWhiteSpace(name)) track.Name = name;
                    break;
                case MesgNum.Record:
                    if (ParseRecord(new RecordMesg(e.mesg)) is TrackPoint p) track.Points.Add(p);
                    break;
            }
        };

        decode.Read(stream);
        return track.Points.Count > 0 ? new List<Track> { track } : new List<Track>();
    }

    private static TrackPoint? ParseRecord(RecordMesg r)
    {
        int? latSc = r.GetPositionLat();
        int? lonSc = r.GetPositionLong();
        if (latSc is null || lonSc is null) return null; // no GPS fix on this record

        var p = new TrackPoint { Lat = ToDegrees(latSc.Value), Lon = ToDegrees(lonSc.Value) };
        if ((r.GetEnhancedAltitude() ?? r.GetAltitude()) is float ele) p.Ele = ele;
        if (r.GetTimestamp() is FitDateTime ts && ts.GetDateTime().Year > 1990)
            p.Time = System.DateTime.SpecifyKind(ts.GetDateTime(), DateTimeKind.Utc);
        if (r.GetHeartRate() is byte hr) p.Hr = hr;
        if (r.GetCadence() is byte cad) p.Cad = cad;
        if (r.GetTemperature() is sbyte temp) p.Temp = temp;
        return p;
    }

    private static string? NameString(byte[]? raw) =>
        raw is null ? null : Encoding.UTF8.GetString(raw).TrimEnd('\0').Trim();

    public static void Save(string path, IEnumerable<Track> tracks)
    {
        using var fs = IoFile.Create(path);
        Save(fs, tracks);
    }

    /// <summary>Writes the tracks as FIT Course files inside one FIT stream (FileId + Course + Lap + Records each).</summary>
    public static void Save(Stream stream, IEnumerable<Track> tracks)
    {
        var encoder = new Encode(ProtocolVersion.V20);
        encoder.Open(stream);
        uint serial = 0;
        foreach (var track in tracks)
        {
            if (track.Points.Count == 0) continue;
            WriteCourse(encoder, track, serial++);
        }
        encoder.Close();
    }

    private static void WriteCourse(Encode encoder, Track track, uint serial)
    {
        var pts = track.Points;
        var start = pts[0];
        var end = pts[^1];
        var startTime = start.Time ?? new System.DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var fileId = new FileIdMesg();
        fileId.SetType(Dynastream.Fit.File.Course);
        fileId.SetManufacturer(Manufacturer.Development);
        fileId.SetProduct(0);
        fileId.SetSerialNumber(serial);
        fileId.SetTimeCreated(new FitDateTime(startTime.ToUniversalTime()));
        encoder.Write(fileId);

        var course = new CourseMesg();
        course.SetName(track.Name);
        encoder.Write(course);

        // Cumulative distance drives course navigation; times are optional and only emitted when recorded.
        var dist = GeoMath.CumulativeDistancesM(pts);
        double totalDist = dist[^1];

        var lap = new LapMesg();
        lap.SetStartTime(new FitDateTime(startTime.ToUniversalTime()));
        lap.SetTimestamp(new FitDateTime((end.Time ?? startTime).ToUniversalTime()));
        lap.SetStartPositionLat(ToSemicircles(start.Lat));
        lap.SetStartPositionLong(ToSemicircles(start.Lon));
        lap.SetEndPositionLat(ToSemicircles(end.Lat));
        lap.SetEndPositionLong(ToSemicircles(end.Lon));
        lap.SetTotalDistance((float)totalDist);
        if (start.Time is System.DateTime s && end.Time is System.DateTime en)
        {
            float secs = (float)(en - s).TotalSeconds;
            lap.SetTotalTimerTime(secs);
            lap.SetTotalElapsedTime(secs);
        }
        encoder.Write(lap);

        for (int i = 0; i < pts.Count; i++)
        {
            var p = pts[i];
            var r = new RecordMesg();
            r.SetPositionLat(ToSemicircles(p.Lat));
            r.SetPositionLong(ToSemicircles(p.Lon));
            r.SetDistance((float)dist[i]);
            if (p.Ele is double ele) r.SetAltitude((float)ele);
            if (p.Time is System.DateTime t) r.SetTimestamp(new FitDateTime(t.ToUniversalTime()));
            if (p.Hr is int hr and >= 0 and <= 255) r.SetHeartRate((byte)hr);
            if (p.Cad is int cad and >= 0 and <= 255) r.SetCadence((byte)cad);
            if (p.Temp is double temp and >= -128 and <= 127) r.SetTemperature((sbyte)temp);
            encoder.Write(r);
        }
    }
}
