using System.Drawing;
using System.Drawing.Imaging;
using Hourglass.Utilities;

// Renders Resources/app.ico from the shared hourglass glyph.
// Run with:  dotnet run --project tools/IconGen -- src/Hourglass/Resources/app.ico

var outputPath = args.Length > 0
    ? args[0]
    : Path.Combine("src", "Hourglass", "Resources", "app.ico");

int[] bmpSizes = { 16, 20, 24, 32, 40, 48, 64 };
int[] pngSizes = { 128, 256 };

var entries = new List<(int Size, byte[] Data, bool IsPng)>();

foreach (var size in bmpSizes)
{
    using var bitmap = HourglassGlyph.Render(size);
    entries.Add((size, EncodeDib(bitmap), false));
}

foreach (var size in pngSizes)
{
    using var bitmap = HourglassGlyph.Render(size);
    using var ms = new MemoryStream();
    bitmap.Save(ms, ImageFormat.Png);
    entries.Add((size, ms.ToArray(), true));
}

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
using (var file = File.Create(outputPath))
using (var writer = new BinaryWriter(file))
{
    writer.Write((ushort)0);              // reserved
    writer.Write((ushort)1);              // type: icon
    writer.Write((ushort)entries.Count);

    var offset = 6 + entries.Count * 16;
    foreach (var (size, data, _) in entries)
    {
        writer.Write((byte)(size >= 256 ? 0 : size));
        writer.Write((byte)(size >= 256 ? 0 : size));
        writer.Write((byte)0);            // palette entries
        writer.Write((byte)0);            // reserved
        writer.Write((ushort)1);          // colour planes
        writer.Write((ushort)32);         // bits per pixel
        writer.Write(data.Length);
        writer.Write(offset);
        offset += data.Length;
    }

    foreach (var (_, data, _) in entries)
        writer.Write(data);
}

Console.WriteLine($"Wrote {outputPath} ({entries.Count} sizes, {new FileInfo(outputPath).Length / 1024} KB)");

// Packs a bitmap as a bottom-up 32bpp DIB with the trailing AND mask an ICO expects.
static byte[] EncodeDib(Bitmap bitmap)
{
    var width = bitmap.Width;
    var height = bitmap.Height;
    var maskStride = (width + 31) / 32 * 4;

    using var ms = new MemoryStream();
    using var writer = new BinaryWriter(ms);

    writer.Write(40);                     // biSize
    writer.Write(width);                  // biWidth
    writer.Write(height * 2);             // biHeight: colour + mask
    writer.Write((ushort)1);              // biPlanes
    writer.Write((ushort)32);             // biBitCount
    writer.Write(0);                      // biCompression: BI_RGB
    writer.Write(width * height * 4 + maskStride * height);
    writer.Write(0);                      // biXPelsPerMeter
    writer.Write(0);                      // biYPelsPerMeter
    writer.Write(0);                      // biClrUsed
    writer.Write(0);                      // biClrImportant

    var data = bitmap.LockBits(new Rectangle(0, 0, width, height),
        ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    try
    {
        var row = new byte[width * 4];
        for (var y = height - 1; y >= 0; y--)
        {
            System.Runtime.InteropServices.Marshal.Copy(
                data.Scan0 + y * data.Stride, row, 0, row.Length);
            writer.Write(row);
        }
    }
    finally
    {
        bitmap.UnlockBits(data);
    }

    writer.Write(new byte[maskStride * height]);   // fully opaque AND mask
    writer.Flush();
    return ms.ToArray();
}
