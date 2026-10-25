using Mapsui;
using Mapsui.Nts;
using Mapsui.Projections;
using Mapsui.Styles;

using NetTopologySuite.Geometries;

namespace TrackEditor.Services;

/// <summary>
/// Converts pre-computed <see cref="HeatmapIndex.HeatEdge"/> records into Mapsui line features.
/// Edge counts are baked in at index time — no per-render density calculation needed.
/// Line WIDTH encodes density logarithmically; layer-level Opacity composites over the basemap.
/// </summary>
public static class HeatmapRenderer
{
    public static List<IFeature> BuildFeatures(
        IReadOnlyList<HeatmapIndex.HeatEdge> edges,
        string colorHex,
        double maxLineWidth = 4.0)
    {
        if (edges.Count == 0) return [];

        int maxCount = 1;
        foreach (var e in edges)
            if (e.Count > maxCount) maxCount = e.Count;

        var (r, g, b) = ParseHex(colorHex);
        var color = new Color(r, g, b);

        var features = new List<IFeature>(edges.Count);
        foreach (var edge in edges)
        {
            double t = Math.Log(edge.Count + 1) / Math.Log(maxCount + 1);
            double width = 1.5 + t * (maxLineWidth - 1.5);

            var (x1, y1) = SphericalMercator.FromLonLat(edge.Lon1, edge.Lat1);
            var (x2, y2) = SphericalMercator.FromLonLat(edge.Lon2, edge.Lat2);

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

    private static (byte R, byte G, byte B) ParseHex(string hex)
    {
        try
        {
            hex = hex.TrimStart('#');
            return (Convert.ToByte(hex[..2], 16),
                    Convert.ToByte(hex[2..4], 16),
                    Convert.ToByte(hex[4..6], 16));
        }
        catch { return (255, 85, 0); }
    }
}
