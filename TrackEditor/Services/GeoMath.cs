using System;
using System.Collections.Generic;
using TrackEditor.Models;

namespace TrackEditor.Services;

public static class GeoMath
{
    public const double EarthRadiusM = 6371000.0;

    public static double HaversineM(double lat1, double lon1, double lat2, double lon2)
    {
        double dLat = ToRad(lat2 - lat1);
        double dLon = ToRad(lon2 - lon1);
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2)) *
                   Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * EarthRadiusM * Math.Asin(Math.Min(1.0, Math.Sqrt(a)));
    }

    public static double ToRad(double deg) => deg * Math.PI / 180.0;

    /// <summary>Cumulative distance in meters from the first point; [0] = 0.</summary>
    public static double[] CumulativeDistancesM(IReadOnlyList<TrackPoint> pts)
    {
        var d = new double[pts.Count];
        for (int i = 1; i < pts.Count; i++)
            d[i] = d[i - 1] + HaversineM(pts[i - 1].Lat, pts[i - 1].Lon, pts[i].Lat, pts[i].Lon);
        return d;
    }

    /// <summary>
    /// Douglas-Peucker simplification with tolerance in meters (2D, ignores elevation).
    /// Returns the sorted indices of points to KEEP, so elevation/time data survive.
    /// </summary>
    public static List<int> DouglasPeucker(IReadOnlyList<TrackPoint> pts, double tolMeters)
    {
        var keep = new List<int>();
        if (pts.Count < 3)
        {
            for (int i = 0; i < pts.Count; i++) keep.Add(i);
            return keep;
        }

        // Local equirectangular projection (meters) — accurate enough for track extents.
        double lat0 = ToRad(pts[0].Lat);
        double cos0 = Math.Cos(lat0);
        var xs = new double[pts.Count];
        var ys = new double[pts.Count];
        for (int i = 0; i < pts.Count; i++)
        {
            xs[i] = ToRad(pts[i].Lon) * cos0 * EarthRadiusM;
            ys[i] = ToRad(pts[i].Lat) * EarthRadiusM;
        }

        var keepFlag = new bool[pts.Count];
        keepFlag[0] = keepFlag[pts.Count - 1] = true;

        var stack = new Stack<(int First, int Last)>();
        stack.Push((0, pts.Count - 1));
        while (stack.Count > 0)
        {
            var (first, last) = stack.Pop();
            double maxDist = -1;
            int maxIdx = -1;
            double ax = xs[first], ay = ys[first], bx = xs[last], by = ys[last];
            for (int i = first + 1; i < last; i++)
            {
                double dist = PointToSegmentDist(xs[i], ys[i], ax, ay, bx, by);
                if (dist > maxDist) { maxDist = dist; maxIdx = i; }
            }
            if (maxDist > tolMeters && maxIdx > 0)
            {
                keepFlag[maxIdx] = true;
                stack.Push((first, maxIdx));
                stack.Push((maxIdx, last));
            }
        }

        for (int i = 0; i < pts.Count; i++)
            if (keepFlag[i]) keep.Add(i);
        return keep;
    }

    public static double PointToSegmentDist(double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay;
        double len2 = dx * dx + dy * dy;
        if (len2 <= 0) return Math.Sqrt((px - ax) * (px - ax) + (py - ay) * (py - ay));
        double t = ((px - ax) * dx + (py - ay) * dy) / len2;
        t = Math.Clamp(t, 0, 1);
        double cx = ax + t * dx, cy = ay + t * dy;
        return Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
    }

    /// <summary>
    /// Per-point speed in m/s smoothed over a ±window neighborhood, null when no time data.
    /// </summary>
    public static double?[] SpeedsMps(IReadOnlyList<TrackPoint> pts, int window = 2)
    {
        var speeds = new double?[pts.Count];
        if (pts.Count < 2) return speeds;
        var cum = CumulativeDistancesM(pts);
        for (int i = 0; i < pts.Count; i++)
        {
            int a = Math.Max(0, i - window);
            int b = Math.Min(pts.Count - 1, i + window);
            if (pts[a].Time is DateTime ta && pts[b].Time is DateTime tb)
            {
                double dt = (tb - ta).TotalSeconds;
                if (dt > 0.1)
                    speeds[i] = (cum[b] - cum[a]) / dt;
            }
        }
        return speeds;
    }
}
