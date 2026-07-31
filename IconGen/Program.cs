using System.Drawing;
using System.Drawing.Imaging;
using CpuWidget;

// Writes app.ico from ThermostatIcon.Render — the same drawing code the tray icon uses.
//
//   dotnet run --project IconGen -c Release -- app.ico

string output = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "app.ico"));

int[] sizes = { 16, 20, 24, 32, 48, 64, 128, 256 };
var frames = sizes.Select(ThermostatIcon.Render).ToArray();

WriteIco(output, frames);
Console.WriteLine($"wrote {output} ({string.Join(", ", sizes)})");

foreach (var f in frames) f.Dispose();

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
