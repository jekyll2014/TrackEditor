using BruTile;
using Mapsui.Projections;
using SkiaSharp;
using System.IO;
using TrackEditor.Models;

namespace TrackEditor.Services;

/// <summary>
/// Renders a map region to a PNG by fetching basemap tiles at a chosen zoom level and drawing the
/// track overlays on top with SkiaSharp. Independent of Mapsui's async view rendering, so the output
/// is deterministic. Tiles fetched here also populate the basemap's persistent cache.
/// </summary>
public static class MapExporter
{
    private const double Origin = 20037508.342789244; // Web Mercator half-extent (m)

    public static double ResolutionAtZoom(int zoom) => 2 * Origin / (256.0 * Math.Pow(2, zoom));

    /// <summary>Output pixel size and tile count for an extent at a zoom level and output scale.</summary>
    public static (int W, int H, long Tiles) EstimateSize(
        (double MinX, double MinY, double MaxX, double MaxY) e, int zoom, double scale)
    {
        double res = ResolutionAtZoom(zoom), span = 256 * res;
        int w = (int)Math.Round((e.MaxX - e.MinX) / res * scale);
        int h = (int)Math.Round((e.MaxY - e.MinY) / res * scale);
        long nx = (long)Math.Floor((e.MaxX + Origin) / span) - (long)Math.Floor((e.MinX + Origin) / span) + 1;
        long ny = (long)Math.Floor((Origin - e.MinY) / span) - (long)Math.Floor((Origin - e.MaxY) / span) + 1;
        return (Math.Max(1, w), Math.Max(1, h), Math.Max(1, nx * ny));
    }

    public static async Task ExportAsync(
        ITileSource source,
        (double MinX, double MinY, double MaxX, double MaxY) e,
        int zoom, double scale,
        IReadOnlyList<Track> tracks,
        string path,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        double res = ResolutionAtZoom(zoom), span = 256 * res;
        int outW = Math.Max(1, (int)Math.Round((e.MaxX - e.MinX) / res * scale));
        int outH = Math.Max(1, (int)Math.Round((e.MaxY - e.MinY) / res * scale));

        int tx0 = (int)Math.Floor((e.MinX + Origin) / span), tx1 = (int)Math.Floor((e.MaxX + Origin) / span);
        int ty0 = (int)Math.Floor((Origin - e.MaxY) / span), ty1 = (int)Math.Floor((Origin - e.MinY) / span);
        int worldTiles = 1 << zoom;

        // Output pixel position of a Web-Mercator coordinate.
        float PX(double x) => (float)((x - e.MinX) / res * scale);
        float PY(double y) => (float)((e.MaxY - y) / res * scale);

        using var bmp = new SKBitmap(outW, outH);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(new SKColor(0xEE, 0xEE, 0xEE));

        using var tilePaint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
        float tileSize = (float)(span / res * scale); // = 256 * scale
        long total = (long)(tx1 - tx0 + 1) * (ty1 - ty0 + 1), done = 0;

        for (int ty = ty0; ty <= ty1; ty++)
            for (int tx = tx0; tx <= tx1; tx++)
            {
                ct.ThrowIfCancellationRequested();
                done++;
                if (ty < 0 || ty >= worldTiles) continue;         // nothing above/below the world
                int wx = ((tx % worldTiles) + worldTiles) % worldTiles; // wrap across the antimeridian
                progress?.Report($"Rendering tiles… {done}/{total}");

                byte[]? bytes;
                try { bytes = await source.GetTileAsync(new TileInfo { Index = new TileIndex(wx, ty, zoom) }); }
                catch { bytes = null; }
                if (bytes is null) continue;
                using var tile = SKBitmap.Decode(bytes);
                if (tile is null) continue;

                float dx = PX(tx * span - Origin), dy = PY(Origin - ty * span);
                canvas.DrawBitmap(tile, new SKRect(dx, dy, dx + tileSize, dy + tileSize), tilePaint);
            }

        foreach (var t in tracks)
        {
            if (!t.Visible || t.Points.Count < 2) continue;
            using var paint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                Color = ParseHex(t.ColorHex),
                StrokeWidth = (float)Math.Max(1, t.Width * scale),
                IsAntialias = true,
                StrokeCap = SKStrokeCap.Round,
                StrokeJoin = SKStrokeJoin.Round,
            };
            using var sk = new SKPath();
            for (int i = 0; i < t.Points.Count; i++)
            {
                var (x, y) = SphericalMercator.FromLonLat(t.Points[i].Lon, t.Points[i].Lat);
                if (i == 0) sk.MoveTo(PX(x), PY(y)); else sk.LineTo(PX(x), PY(y));
            }
            canvas.DrawPath(sk, paint);
        }

        // Ground meters per output pixel at the view's centre latitude (Mercator stretches with latitude).
        var (_, latCenter) = SphericalMercator.ToLonLat((e.MinX + e.MaxX) / 2, (e.MinY + e.MaxY) / 2);
        double metersPerPixel = res / scale * Math.Cos(latCenter * Math.PI / 180.0);
        DrawScaleBar(canvas, outW, outH, metersPerPixel, scale);

        canvas.Flush();
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var fs = File.Create(path);
        data.SaveTo(fs);
    }

    /// <summary>Draws a bottom-right scale bar (matching the on-screen map) sized to a "nice" ground distance.</summary>
    private static void DrawScaleBar(SKCanvas canvas, int outW, int outH, double metersPerPixel, double scale)
    {
        if (metersPerPixel <= 0 || double.IsNaN(metersPerPixel)) return;
        float s = (float)scale;
        float margin = 12 * s;
        float maxBar = Math.Min(outW * 0.25f, 240 * s);

        double nice = NiceDistance(metersPerPixel * maxBar);
        float barPx = (float)(nice / metersPerPixel);
        if (barPx < 1) return;
        string label = nice >= 1000 ? $"{nice / 1000:0.#} km" : $"{nice:0} m";

        float textSize = 12 * s, tick = 5 * s, pad = 4 * s;
        using var textPaint = new SKPaint
        { Color = SKColors.Black, TextSize = textSize, IsAntialias = true, TextAlign = SKTextAlign.Center };
        using var barPaint = new SKPaint
        { Color = SKColors.Black, StrokeWidth = Math.Max(1, 2 * s), IsAntialias = true, Style = SKPaintStyle.Stroke };
        using var bgPaint = new SKPaint
        { Color = new SKColor(255, 255, 255, 210), IsAntialias = true, Style = SKPaintStyle.Fill };

        float right = outW - margin, left = right - barPx, barY = outH - margin;
        float cx = (left + right) / 2f;
        float labelBaseline = barY - tick - 4 * s;
        float textW = textPaint.MeasureText(label);

        float boxLeft = Math.Min(left, cx - textW / 2) - pad;
        float boxRight = Math.Max(right, cx + textW / 2) + pad;
        float boxTop = labelBaseline - textSize - pad;
        canvas.DrawRoundRect(boxLeft, boxTop, boxRight - boxLeft, barY + pad - boxTop, 3 * s, 3 * s, bgPaint);

        canvas.DrawLine(left, barY, right, barY, barPaint);       // bar
        canvas.DrawLine(left, barY, left, barY - tick, barPaint); // end ticks
        canvas.DrawLine(right, barY, right, barY - tick, barPaint);
        canvas.DrawText(label, cx, labelBaseline, textPaint);
    }

    /// <summary>Largest of {1,2,5}×10ⁿ metres not exceeding <paramref name="max"/>.</summary>
    private static double NiceDistance(double max)
    {
        if (max <= 1) return 1;
        double pow = Math.Pow(10, Math.Floor(Math.Log10(max)));
        foreach (double m in new[] { 5.0, 2.0, 1.0 })
            if (m * pow <= max) return m * pow;
        return pow;
    }

    private static SKColor ParseHex(string hex)
    {
        try
        {
            hex = hex.TrimStart('#');
            return new SKColor(
                Convert.ToByte(hex.Substring(0, 2), 16),
                Convert.ToByte(hex.Substring(2, 2), 16),
                Convert.ToByte(hex.Substring(4, 2), 16));
        }
        catch { return new SKColor(0xE5, 0x39, 0x35); }
    }
}
