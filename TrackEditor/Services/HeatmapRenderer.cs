using Mapsui;
using Mapsui.Layers;
using Mapsui.Projections;
using Mapsui.Styles;

using SkiaSharp;

namespace TrackEditor.Services;

/// <summary>
/// Paints pre-computed <see cref="HeatmapIndex.HeatCell"/> density cells into a single raster
/// bitmap (Strava-style heatmap). One <see cref="RasterFeature"/> is returned regardless of how
/// many cells/tracks are in view, so the draw cost is constant. Colour intensity and opacity scale
/// logarithmically with the per-cell track count.
/// </summary>
public static class HeatmapRenderer
{
    // Cap the raster dimension so wide zoom levels don't allocate huge bitmaps. When the cell span
    // exceeds this, multiple cells fall onto one pixel and their counts are max-combined.
    private const int MaxDim = 1536;

    /// <summary>
    /// Builds a raster feature covering the given lon/lat viewport bbox. Returns an empty list when
    /// there are no cells so the layer clears. When <paramref name="alwaysVisible"/> is set the blobs
    /// are drawn thicker with a higher minimum opacity so the overlay survives low/mid zoom.
    /// </summary>
    public static List<IFeature> BuildFeatures(
        IReadOnlyList<HeatmapIndex.HeatCell> cells,
        double minLon, double minLat, double maxLon, double maxLat,
        string colorHex, bool alwaysVisible = true)
    {
        if (cells.Count == 0) return [];

        double cell = HeatmapIndex.CellSizeDeg;

        // Cell-index bounds of the viewport.
        long gxMin = (long)Math.Floor(minLon / cell);
        long gxMax = (long)Math.Ceiling(maxLon / cell);
        long gyMin = (long)Math.Floor(minLat / cell);
        long gyMax = (long)Math.Ceiling(maxLat / cell);

        long cellsW = Math.Max(1, gxMax - gxMin);
        long cellsH = Math.Max(1, gyMax - gyMin);

        int width = (int)Math.Min(cellsW, MaxDim);
        int height = (int)Math.Min(cellsH, MaxDim);

        int maxCount = 1;
        foreach (var c in cells)
            if (c.Count > maxCount) maxCount = c.Count;
        double logMax = Math.Log(maxCount + 1);

        var (cr, cg, cb) = ParseHex(colorHex);

        // Blob radius: a cell maps to width/cellsW pixels. A floor keeps thin roads on-screen even
        // when the raster is downsampled; the boost raises both the floor and the minimum alpha.
        float pxPerCell = (float)width / cellsW;
        float minRadius = alwaysVisible ? 2.6f : 1.0f;
        float radius = Math.Max(minRadius, pxPerCell * 0.75f);
        byte minAlpha = alwaysVisible ? (byte)130 : (byte)70;
        double alphaSpan = 255 - minAlpha;

        // World extent of the pixel grid, in spherical mercator. Mapsui draws the raster linearly in
        // mercator, so cells must be positioned by their mercator coordinates — NOT linearly in
        // degrees. Latitude→mercator is nonlinear; placing cells by lat-degrees drifts vertically at
        // low zoom (growing toward the edges).
        var (wx0, wy0) = SphericalMercator.FromLonLat(gxMin * cell, gyMin * cell);
        var (wx1, wy1) = SphericalMercator.FromLonLat(gxMax * cell, gyMax * cell);
        var rect = new MRect(wx0, wy0, wx1, wy1);
        double spanX = wx1 - wx0, spanY = wy1 - wy0;

        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        foreach (var c in cells)
        {
            var (mx, my) = SphericalMercator.FromLonLat(c.Gx * cell, c.Gy * cell);
            float px = (float)((mx - wx0) / spanX * width);
            float py = (float)((wy1 - my) / spanY * height); // flip Y: north at top
            double t = Math.Log(c.Count + 1) / logMax;        // 0..1
            byte a = (byte)(minAlpha + t * alphaSpan);
            paint.Color = new SKColor(cr, cg, cb, a);
            canvas.DrawCircle(px, py, radius, paint);
        }

        using var img = surface.Snapshot();
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        byte[] png = data.ToArray();

        var feature = new RasterFeature(new MRaster(png, rect));
        feature.Styles.Add(new RasterStyle());
        return [feature];
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
