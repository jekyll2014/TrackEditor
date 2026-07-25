using TrackEditor.Core.Models;

namespace TrackEditor.Core.Services.RaceAnalysis;

/// <summary>
/// Cleans noisy recorded sensor channels before they feed the race model.
/// Real GPX files carry dropouts (e.g. an HR of 3 bpm from a lost chest-strap contact) and single-sample
/// spikes; left in, they poison the fatigue fit. Cleaning is: plausibility clamp -> median de-spike.
/// </summary>
public static class SignalCleaning
{
    /// <summary>Physiologically plausible human heart-rate window (bpm); outside = sensor error, dropped.</summary>
    public const int HrMin = 30;
    public const int HrMax = 230;

    /// <summary>A value this far (bpm) from its local median is treated as a spike and replaced by the median.</summary>
    public const int HrSpikeBpm = 20;

    /// <summary>
    /// Cleans the Hr channel of a point list, aligned 1:1 with the input.
    /// Implausible readings become null; isolated spikes are pulled to the local median.
    /// </summary>
    public static int?[] CleanHr(IReadOnlyList<TrackPoint> pts, int medianWindow = 5)
    {
        var hr = new int?[pts.Count];
        for (int i = 0; i < pts.Count; i++)
        {
            int? v = pts[i].Hr;
            hr[i] = (v is int h && h >= HrMin && h <= HrMax) ? h : null;   // plausibility clamp
        }
        return MedianDespike(hr, medianWindow, HrSpikeBpm);
    }

    /// <summary>
    /// Replaces any value differing from its local median by more than <paramref name="threshold"/> with that
    /// median. Nulls are ignored (neither smoothed nor filled). Window is forced odd and centered.
    /// </summary>
    public static int?[] MedianDespike(IReadOnlyList<int?> values, int window, int threshold)
    {
        int n = values.Count;
        var outp = new int?[n];
        int half = Math.Max(1, window) / 2;
        var buf = new List<int>(window | 1);
        for (int i = 0; i < n; i++)
        {
            if (values[i] is not int v) { outp[i] = null; continue; }
            buf.Clear();
            for (int j = Math.Max(0, i - half); j <= Math.Min(n - 1, i + half); j++)
                if (values[j] is int w) buf.Add(w);
            int med = Median(buf);
            outp[i] = Math.Abs(v - med) > threshold ? med : v;
        }
        return outp;
    }

    /// <summary>Median of a non-empty list (mutates order via sort). Even count -> lower-middle element.</summary>
    public static int Median(List<int> xs)
    {
        xs.Sort();
        return xs[(xs.Count - 1) / 2];
    }
}
