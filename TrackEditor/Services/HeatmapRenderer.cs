using Mapsui;
using Mapsui.Nts;
using Mapsui.Projections;
using Mapsui.Styles;

using NetTopologySuite.Geometries;

namespace TrackEditor.Services;

/// <summary>
/// Converts a list of heatmap segments into Mapsui <see cref="IFeature"/> line features.
/// Density (number of unique named tracks visiting a grid cell) drives line opacity so
/// frequently-used paths appear brighter. Grid cells are ~50 m at mid-latitudes.
/// </summary>
public static class HeatmapRenderer
{
    // Grid cell size in degrees: ~50 m at 55° latitude.
    private const double GridDeg = 0.0005;

    /// <summary>
    /// Builds a list of georeferenced line features for <paramref name="segments"/>.
    /// Density (unique named tracks per ~50 m grid cell) drives line WIDTH so high-traffic
    /// paths appear thicker. All segments render at full opacity — layer-level transparency
    /// (set by the caller on the MemoryLayer) composites the whole heatmap over the basemap
    /// once, avoiding per-segment alpha accumulation that would make overlapping segments opaque.
    /// </summary>
    public static List<IFeature> BuildFeatures(
        IReadOnlyList<HeatmapIndex.Segment> segments,
        string colorHex,
        double maxLineWidth = 4.0)
    {
        if (segments.Count == 0) return [];

        // Step 1: compute per-grid-cell density (unique track names).
        var cellNames = new Dictionary<(int, int), HashSet<string>>();
        foreach (var seg in segments)
        {
            float midLat = (seg.Lat1 + seg.Lat2) * 0.5f;
            float midLon = (seg.Lon1 + seg.Lon2) * 0.5f;
            var key = LatLonToCell(midLat, midLon);
            if (!cellNames.TryGetValue(key, out var names))
                cellNames[key] = names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            names.Add(seg.TrackName);
        }

        int maxCount = cellNames.Values.Max(s => s.Count);
        if (maxCount < 1) maxCount = 1;

        // Step 2: parse base color (full opacity — layer Opacity controls transparency).
        var (r, g, b) = ParseHex(colorHex);
        var color = new Color(r, g, b);

        // Step 3: emit one LineString feature per segment, width ∝ density.
        var features = new List<IFeature>(segments.Count);
        foreach (var seg in segments)
        {
            float midLat = (seg.Lat1 + seg.Lat2) * 0.5f;
            float midLon = (seg.Lon1 + seg.Lon2) * 0.5f;
            var key = LatLonToCell(midLat, midLon);
            int count = cellNames.TryGetValue(key, out var ns) ? ns.Count : 1;

            // Logarithmic scaling: even single-pass paths show clearly, high-count stand out.
            double t = Math.Log(count + 1) / Math.Log(maxCount + 1);
            double width = 1.5 + t * (maxLineWidth - 1.5);

            var (x1, y1) = SphericalMercator.FromLonLat(seg.Lon1, seg.Lat1);
            var (x2, y2) = SphericalMercator.FromLonLat(seg.Lon2, seg.Lat2);

            var lf = new GeometryFeature(new LineString(
                [new Coordinate(x1, y1), new Coordinate(x2, y2)]));
            lf.Styles.Add(new VectorStyle
            {
                Line = new Pen(color, width)
                {
                    PenStrokeCap = PenStrokeCap.Round,
                    StrokeJoin = StrokeJoin.Round,
                },
            });
            features.Add(lf);
        }
        return features;
    }

    private static (int col, int row) LatLonToCell(double lat, double lon)
        => ((int)Math.Floor(lon / GridDeg), (int)Math.Floor(lat / GridDeg));

    private static (byte R, byte G, byte B) ParseHex(string hex)
    {
        try
        {
            hex = hex.TrimStart('#');
            return (Convert.ToByte(hex[..2], 16),
                    Convert.ToByte(hex[2..4], 16),
                    Convert.ToByte(hex[4..6], 16));
        }
        catch { return (255, 85, 0); } // fallback: orange
    }
}
