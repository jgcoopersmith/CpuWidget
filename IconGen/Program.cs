using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

// Draws the app icon: a round, old-style thermostat dial with the needle swung all the
// way to the top, into a red "maxed out" band. Run it to regenerate app.ico.
//
//   dotnet run --project IconGen -- ..\app.ico

string output = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "app.ico"));

int[] sizes = { 16, 20, 24, 32, 48, 64, 128, 256 };
var frames = sizes.Select(Render).ToArray();

WriteIco(output, frames);
Console.WriteLine($"wrote {output} ({string.Join(", ", sizes)})");

foreach (var f in frames) f.Dispose();

static Bitmap Render(int size)
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

    var cool = Color.FromArgb(0x9A, 0x93, 0x82);
    Band(cool, 150f, 80f);                                  // cool end
    Band(Color.FromArgb(0xE2, 0xA3, 0x3A), 230f, 20f);      // warming
    Band(Color.FromArgb(0xE0, 0x2B, 0x1A), 250f, 40f);      // maxed out, centred on 270° (top)
    Band(cool, 290f, 100f);

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

// Minimal multi-size .ico writer. Every frame is stored as PNG, which Windows has
// supported for icon entries since Vista.
static void WriteIco(string path, Bitmap[] frames)
{
    var encoded = new List<byte[]>();
    foreach (var frame in frames)
    {
        using var ms = new MemoryStream();
        frame.Save(ms, ImageFormat.Png);
        encoded.Add(ms.ToArray());
    }

    using var fs = File.Create(path);
    using var w = new BinaryWriter(fs);

    w.Write((ushort)0);                     // reserved
    w.Write((ushort)1);                     // type: icon
    w.Write((ushort)frames.Length);

    int offset = 6 + 16 * frames.Length;    // header + directory entries
    for (int i = 0; i < frames.Length; i++)
    {
        int side = frames[i].Width;
        w.Write((byte)(side >= 256 ? 0 : side));   // 0 means 256
        w.Write((byte)(side >= 256 ? 0 : side));
        w.Write((byte)0);                   // palette size
        w.Write((byte)0);                   // reserved
        w.Write((ushort)1);                 // colour planes
        w.Write((ushort)32);                // bits per pixel
        w.Write(encoded[i].Length);
        w.Write(offset);
        offset += encoded[i].Length;
    }

    foreach (var bytes in encoded) w.Write(bytes);
}
