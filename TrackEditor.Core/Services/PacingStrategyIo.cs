using System.Globalization;
using System.Text;

using TrackEditor.Core.Models;

namespace TrackEditor.Core.Services;

/// <summary>
/// Exports a track's per-kilometre pacing strategy as CSV (<c>km,pace</c>), where each row's pace
/// is the m:ss it took to cover that kilometre. Only tracks with recorded timestamps can produce one.
/// </summary>
public static class PacingStrategyIo
{
    /// <summary>One CSV row: the cumulative kilometre marker, this split's length (km), its average
    /// pace (seconds per km), and the cumulative elapsed time (seconds from the first timestamp).</summary>
    public readonly record struct PaceRow(double Km, double SplitKm, double PaceSecPerKm, double CumulativeSec);

    /// <summary>True when the points carry enough timing (a first and last timestamp, elapsed &gt; 0) to pace.</summary>
    public static bool HasTiming(IReadOnlyList<TrackPoint> pts)
    {
        DateTime? first = null, last = null;
        foreach (var p in pts)
            if (p.Time is DateTime t) { first ??= t; last = t; }
        return first is DateTime f && last is DateTime l && l > f;
    }

    /// <summary>
    /// Rows for the pacing CSV: one row per full kilometre whose pace is that kilometre's split, plus a
    /// trailing row for the final partial kilometre (its pace normalised to seconds per km). Empty when
    /// the track lacks distance or usable timestamps.
    /// </summary>
    public static IReadOnlyList<PaceRow> Compute(IReadOnlyList<TrackPoint> pts)
    {
        var rows = new List<PaceRow>();
        if (pts.Count < 2 || !HasTiming(pts)) return rows;

        var cum = GeoMath.CumulativeDistancesM(pts);
        double totalM = cum[^1];
        if (totalM <= 0) return rows;

        // Points that carry both a distance and a timestamp, as (metres, elapsed seconds from the
        // first timestamp). Elapsed time at any distance is interpolated linearly over these.
        var ds = new List<double>();
        var secs = new List<double>();
        DateTime? t0 = null;
        for (int i = 0; i < pts.Count; i++)
        {
            if (pts[i].Time is not DateTime t) continue;
            t0 ??= t;
            ds.Add(cum[i]);
            secs.Add((t - t0.Value).TotalSeconds);
        }
        if (ds.Count < 2) return rows;

        double ElapsedAt(double d)
        {
            if (d <= ds[0]) return secs[0];
            if (d >= ds[^1]) return secs[^1];
            // ds is non-decreasing; find the segment straddling d.
            int hi = 1;
            while (hi < ds.Count && ds[hi] < d) hi++;
            int lo = hi - 1;
            double span = ds[hi] - ds[lo];
            double f = span > 0 ? (d - ds[lo]) / span : 0;
            return secs[lo] + f * (secs[hi] - secs[lo]);
        }

        // Each split runs from the previous marker to this one; pace normalises its duration to per-km.
        void AddSplit(double markerM, double prevMarkerM, double prevSec)
        {
            double cumSec = ElapsedAt(markerM);
            double splitKm = (markerM - prevMarkerM) / 1000.0;
            double pace = splitKm > 0 ? (cumSec - prevSec) / splitKm : 0;
            rows.Add(new PaceRow(markerM / 1000.0, splitKm, pace, cumSec));
        }

        double prevM = 0, prevElapsed = ElapsedAt(0);
        int fullKm = (int)Math.Floor(totalM / 1000.0 + 1e-6);
        for (int k = 1; k <= fullKm; k++)
        {
            double markerM = k * 1000.0;
            AddSplit(markerM, prevM, prevElapsed);
            prevM = markerM;
            prevElapsed = ElapsedAt(markerM);
        }

        // Trailing partial kilometre (skip a sliver caused by float rounding).
        if (totalM - fullKm * 1000.0 > 1.0)
            AddSplit(totalM, prevM, prevElapsed);

        return rows;
    }

    /// <summary>The CSV text (header <c>km,Split Distance,Split Pace,Cumulative Time</c>).</summary>
    public static string ToCsv(IReadOnlyList<TrackPoint> pts)
    {
        var sb = new StringBuilder();
        sb.AppendLine("km,Split Distance,Split Pace,Cumulative Time");
        foreach (var r in Compute(pts))
            sb.AppendLine($"{Num(r.Km)},{Num(r.SplitKm)},{FormatPace(r.PaceSecPerKm)},{FormatDuration(r.CumulativeSec)}");
        return sb.ToString();
    }

    /// <summary>Writes the pacing CSV to <paramref name="path"/>.</summary>
    public static void Save(string path, Track track) => File.WriteAllText(path, ToCsv(track.Points));

    private static string Num(double km) => km.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Pace as <c>m:ss</c> per km; "0" for a non-positive/degenerate value.</summary>
    private static string FormatPace(double secPerKm)
    {
        if (secPerKm <= 0 || double.IsNaN(secPerKm) || double.IsInfinity(secPerKm)) return "0";
        int total = (int)Math.Round(secPerKm);
        int m = total / 60, s = total % 60;
        return $"{m}:{s:D2}";
    }

    /// <summary>Cumulative elapsed time as <c>h:mm:ss</c>.</summary>
    private static string FormatDuration(double sec)
    {
        if (sec < 0 || double.IsNaN(sec) || double.IsInfinity(sec)) sec = 0;
        var t = TimeSpan.FromSeconds(Math.Round(sec));
        return $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}";
    }
}
