using System.Drawing;
using System.Drawing.Drawing2D;

namespace Hourglass.Utilities;

/// <summary>
/// Draws the application's hourglass mark with System.Drawing so that the
/// executable icon, the window icon and the tray icon all share one shape.
/// Everything is authored on a 32x32 design grid and scaled to the target size.
/// </summary>
public static class HourglassGlyph
{
    public static readonly Color Frame = Color.FromArgb(0xFF, 0x76, 0xB5, 0xFF);
    public static readonly Color Glass = Color.FromArgb(0x2E, 0xC8, 0xE1, 0xFF);
    public static readonly Color Sand = Color.FromArgb(0xFF, 0xF0, 0xB4, 0x29);

    /// <summary>Renders the mark onto a transparent square bitmap of the given size.</summary>
    public static Bitmap Render(int size, Color? statusDot = null)
    {
        var bitmap = new Bitmap(size, size);
        bitmap.SetResolution(96f, 96f);

        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.Clear(Color.Transparent);

        var scale = size / 32f;
        g.ScaleTransform(scale, scale);

        // Below ~24px the fine details collapse into noise, so a heavier,
        // simplified silhouette is drawn instead.
        if (size >= 24)
            DrawDetailed(g);
        else
            DrawCompact(g);

        g.ResetTransform();

        if (statusDot is { } dot)
            DrawStatusDot(g, size, dot);

        return bitmap;
    }

    private static void DrawDetailed(Graphics g)
    {
        const float left = 7.5f;
        const float right = 24.5f;
        const float top = 4f;
        const float bottom = 28f;
        const float bar = 2.4f;
        const float waist = 16f;

        using var glassBrush = new SolidBrush(Glass);
        using var sandBrush = new SolidBrush(Sand);
        using var framePen = new Pen(Frame, 1.7f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };

        // Glass bulbs.
        using (var upper = new GraphicsPath())
        {
            upper.AddPolygon(new[]
            {
                new PointF(left + 0.6f, top + bar),
                new PointF(right - 0.6f, top + bar),
                new PointF(waist, waist - 0.6f)
            });
            g.FillPath(glassBrush, upper);
            g.DrawPath(framePen, upper);
        }

        using (var lower = new GraphicsPath())
        {
            lower.AddPolygon(new[]
            {
                new PointF(waist, waist + 0.6f),
                new PointF(right - 0.6f, bottom - bar),
                new PointF(left + 0.6f, bottom - bar)
            });
            g.FillPath(glassBrush, lower);
            g.DrawPath(framePen, lower);
        }

        // Sand that has already fallen: a mound in the lower bulb.
        using (var mound = new GraphicsPath())
        {
            mound.AddPolygon(new[]
            {
                new PointF(waist, 20.4f),
                new PointF(right - 2.1f, bottom - bar - 0.4f),
                new PointF(left + 2.1f, bottom - bar - 0.4f)
            });
            g.FillPath(sandBrush, mound);
        }

        // Sand still waiting in the upper bulb.
        using (var remaining = new GraphicsPath())
        {
            remaining.AddPolygon(new[]
            {
                new PointF(left + 1.9f, top + bar + 0.4f),
                new PointF(right - 1.9f, top + bar + 0.4f),
                new PointF(waist + 2.3f, 12.1f),
                new PointF(waist - 2.3f, 12.1f)
            });
            g.FillPath(sandBrush, remaining);
        }

        // The falling stream.
        g.FillRectangle(sandBrush, waist - 0.5f, 14.6f, 1f, 6.2f);

        // Cap and base.
        using var frameBrush = new SolidBrush(Frame);
        FillRoundedRect(g, frameBrush, left - 0.9f, top, (right - left) + 1.8f, bar, 1.1f);
        FillRoundedRect(g, frameBrush, left - 0.9f, bottom - bar, (right - left) + 1.8f, bar, 1.1f);
    }

    private static void DrawCompact(Graphics g)
    {
        const float left = 6.5f;
        const float right = 25.5f;
        const float top = 4f;
        const float bottom = 28f;
        const float bar = 3.2f;
        const float waist = 16f;

        using var frameBrush = new SolidBrush(Frame);
        using var sandBrush = new SolidBrush(Sand);

        using (var body = new GraphicsPath())
        {
            body.AddPolygon(new[]
            {
                new PointF(left + 1.4f, top + bar),
                new PointF(right - 1.4f, top + bar),
                new PointF(waist, waist)
            });
            body.CloseFigure();
            body.AddPolygon(new[]
            {
                new PointF(waist, waist),
                new PointF(right - 1.4f, bottom - bar),
                new PointF(left + 1.4f, bottom - bar)
            });
            g.FillPath(frameBrush, body);
        }

        using (var mound = new GraphicsPath())
        {
            mound.AddPolygon(new[]
            {
                new PointF(waist, 20.6f),
                new PointF(right - 3.1f, bottom - bar - 0.3f),
                new PointF(left + 3.1f, bottom - bar - 0.3f)
            });
            g.FillPath(sandBrush, mound);
        }

        FillRoundedRect(g, frameBrush, left, top, right - left, bar, 1.3f);
        FillRoundedRect(g, frameBrush, left, bottom - bar, right - left, bar, 1.3f);
    }

    private static void DrawStatusDot(Graphics g, int size, Color color)
    {
        var diameter = MathF.Max(5f, size * 0.36f);
        var x = size - diameter;
        var y = size - diameter;

        using var ring = new SolidBrush(Color.FromArgb(0xFF, 0x0D, 0x11, 0x17));
        using var fill = new SolidBrush(color);
        g.FillEllipse(ring, x - 1f, y - 1f, diameter + 2f, diameter + 2f);
        g.FillEllipse(fill, x, y, diameter, diameter);
    }

    private static void FillRoundedRect(Graphics g, Brush brush, float x, float y, float w, float h, float r)
    {
        r = MathF.Min(r, MathF.Min(w, h) / 2f);
        using var path = new GraphicsPath();
        path.AddArc(x, y, r * 2, r * 2, 180, 90);
        path.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
        path.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
        path.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
    }
}
