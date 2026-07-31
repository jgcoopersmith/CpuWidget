using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace CpuWidget;

/// <summary>
/// Draws the app's thermostat dial: a round old-style face with the needle swung to the
/// top of the scale, into a red "maxed out" band.
/// Shared by the tray icon and by IconGen, which writes app.ico from the same code.
/// </summary>
public static class ThermostatIcon
{
    public static Bitmap Render(int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        float s = size;
        float cx = s / 2f, cy = s / 2f;
        float r = s * 0.47f;                 // outer bezel radius
        bool detailed = size >= 32;          // fine detail disappears below this, so skip it

        RectangleF Box(float radius) => new(cx - radius, cy - radius, radius * 2, radius * 2);

        // --- brushed metal bezel ---
        using (var bezel = new LinearGradientBrush(Box(r), Color.FromArgb(0xC9, 0xCF, 0xD8),
                                                   Color.FromArgb(0x4A, 0x51, 0x5C), 55f))
        {
            g.FillEllipse(bezel, Box(r));
        }

        // thin dark separation ring so the bezel reads against light wallpapers
        using (var edge = new Pen(Color.FromArgb(0x20, 0x24, 0x2B), Math.Max(1f, s * 0.02f)))
        {
            g.DrawEllipse(edge, Box(r - s * 0.01f));
        }

        // --- cream dial face ---
        // At small sizes the bezel has to give up room or the icon reads as a dark blob.
        float faceR = r * (detailed ? 0.80f : 0.90f);
        using (var face = new LinearGradientBrush(Box(faceR), Color.FromArgb(0xFA, 0xF4, 0xE6),
                                                  Color.FromArgb(0xDA, 0xCE, 0xB4), 90f))
        {
            g.FillEllipse(face, Box(faceR));
        }

        // --- temperature scale ---
        // GDI+ angles run clockwise from 3 o'clock, so 270° is straight up. The scale sweeps
        // 240° from lower-left (150°) over the top and round to lower-right (30°).
        float scaleR = faceR * 0.78f;
        float bandWidth = s * (detailed ? 0.085f : 0.15f);

        void Band(Color color, float start, float sweep)
        {
            using var pen = new Pen(color, bandWidth) { StartCap = LineCap.Flat, EndCap = LineCap.Flat };
            g.DrawArc(pen, Box(scaleR), start, sweep);
        }

        // The warm bands sit at the clockwise (right-hand) end of the scale, red running out
        // to the very end of the sweep.
        var cool = Color.FromArgb(0x9A, 0x93, 0x82);
        Band(cool, 150f, 120f);                                 // cool end, up to the top
        Band(Color.FromArgb(0xE2, 0xA3, 0x3A), 270f, 40f);      // warming
        Band(Color.FromArgb(0xE0, 0x2B, 0x1A), 310f, 80f);      // maxed out, to the end of the scale

        if (detailed)
        {
            // tick marks every 24° across the scale
            using var tick = new Pen(Color.FromArgb(0x3A, 0x35, 0x2C), Math.Max(1f, s * 0.016f));
            for (int i = 0; i <= 10; i++)
            {
                double deg = 150 + i * 24.0;
                double rad = deg * Math.PI / 180.0;
                float inner = scaleR - bandWidth * 0.75f;
                float outer = scaleR - bandWidth * 1.45f;
                g.DrawLine(tick,
                    cx + (float)(Math.Cos(rad) * outer), cy + (float)(Math.Sin(rad) * outer),
                    cx + (float)(Math.Cos(rad) * inner), cy + (float)(Math.Sin(rad) * inner));
            }
        }

        // --- needle, pinned at the top of the scale ---
        float needleLen = faceR * 0.90f;
        float halfBase = s * (detailed ? 0.07f : 0.10f);
        var needle = new[]
        {
            new PointF(cx, cy - needleLen),                 // tip, straight up
            new PointF(cx - halfBase, cy + s * 0.045f),
            new PointF(cx + halfBase, cy + s * 0.045f),
        };
        using (var brush = new SolidBrush(Color.FromArgb(0xD9, 0x2B, 0x1C)))
        {
            g.FillPolygon(brush, needle);
        }
        if (detailed)
        {
            using var outline = new Pen(Color.FromArgb(0x7A, 0x14, 0x0C), Math.Max(1f, s * 0.012f));
            g.DrawPolygon(outline, needle);
        }

        // --- centre hub ---
        float hubR = s * (detailed ? 0.10f : 0.12f);
        using (var hub = new LinearGradientBrush(Box(hubR), Color.FromArgb(0x50, 0x57, 0x62),
                                                 Color.FromArgb(0x1C, 0x20, 0x27), 90f))
        {
            g.FillEllipse(hub, Box(hubR));
        }

        return bmp;
    }
}
