using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// The PDF4Free icon: a white sheet with a folded corner on a warm gradient tile, the letters "PDF",
// and a green "free" badge with a check mark. Small sizes drop the fine detail so they stay readable.
//
// Output (relative to the repository root, or the folder given as the first argument):
//   src/PDF4Free.App/Assets/PDF4Free.ico      16, 20, 24, 32, 40, 48, 64, 128 and 256 pixel images
//   src/PDF4Free.App/Assets/PDF4Free.png      256 pixel logo used on the welcome screen
//   src/PDF4Free.App/Assets/icon-preview.png  every size side by side, for review

var root = args.Length > 0 ? args[0] : FindRepositoryRoot();
var assets = Path.Combine(root, "src", "PDF4Free.App", "Assets");
Directory.CreateDirectory(assets);

int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
var images = sizes.Select(size => (Size: size, Bitmap: IconArt.Render(size))).ToList();

File.WriteAllBytes(Path.Combine(assets, "PDF4Free.ico"), IcoFile.Build(images));
File.WriteAllBytes(Path.Combine(assets, "PDF4Free.png"), IconArt.ToPng(IconArt.Render(256)));
File.WriteAllBytes(Path.Combine(assets, "icon-preview.png"), IconArt.ToPng(IconArt.ContactSheet(images.Select(i => i.Bitmap).ToList())));
Console.WriteLine($"Wrote PDF4Free.ico ({sizes.Length} sizes), PDF4Free.png and icon-preview.png to {assets}");
return 0;

static string FindRepositoryRoot()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "PDF4Free.slnx"))) return dir.FullName;
    }
    return Directory.GetCurrentDirectory();
}

internal static class IconArt
{
    private static readonly Color TileTop = Color.FromRgb(0xFF, 0x7A, 0x45);
    private static readonly Color TileBottom = Color.FromRgb(0xD9, 0x1E, 0x4F);
    private static readonly Color Ink = Color.FromRgb(0xC8, 0x1D, 0x4A);
    private static readonly Color Fold = Color.FromRgb(0xFF, 0xC9, 0xB5);
    private static readonly Color BadgeTop = Color.FromRgb(0x3D, 0xD6, 0x8C);
    private static readonly Color BadgeBottom = Color.FromRgb(0x12, 0x9E, 0x5E);

    /// <summary>Draws the icon on a 256-unit canvas and renders it at <paramref name="size"/> pixels.</summary>
    public static BitmapSource Render(int size)
    {
        var visual = new DrawingVisual();
        RenderOptions.SetEdgeMode(visual, EdgeMode.Unspecified);
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(size / 256.0, size / 256.0));
            DrawTile(dc);
            DrawSheet(dc, size);
            DrawBadge(dc, size);
            dc.Pop();
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static void DrawTile(DrawingContext dc)
    {
        var tile = new LinearGradientBrush(TileTop, TileBottom, new Point(0.1, 0), new Point(0.9, 1));
        dc.DrawRoundedRectangle(tile, null, new Rect(8, 8, 240, 240), 54, 54);
        // A soft highlight across the top half gives the tile some depth.
        var shine = new LinearGradientBrush(Color.FromArgb(70, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), new Point(0, 0), new Point(0, 1));
        dc.PushClip(new RectangleGeometry(new Rect(8, 8, 240, 240), 54, 54));
        dc.DrawEllipse(shine, null, new Point(128, -20), 220, 150);
        dc.Pop();
    }

    private static void DrawSheet(DrawingContext dc, int size)
    {
        const double left = 62, top = 40, right = 194, bottom = 216, fold = 46, radius = 12;
        // A soft shadow under the sheet.
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(55, 90, 0, 30)), null, new Rect(left + 4, top + 8, right - left, bottom - top), radius, radius);

        var sheet = new StreamGeometry();
        using (var g = sheet.Open())
        {
            g.BeginFigure(new Point(left + radius, top), true, true);
            g.LineTo(new Point(right - fold, top), true, true);
            g.LineTo(new Point(right, top + fold), true, true);
            g.LineTo(new Point(right, bottom - radius), true, true);
            g.ArcTo(new Point(right - radius, bottom), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, true);
            g.LineTo(new Point(left + radius, bottom), true, true);
            g.ArcTo(new Point(left, bottom - radius), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, true);
            g.LineTo(new Point(left, top + radius), true, true);
            g.ArcTo(new Point(left + radius, top), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, true);
        }
        dc.DrawGeometry(Brushes.White, null, sheet);

        var corner = new StreamGeometry();
        using (var g = corner.Open())
        {
            g.BeginFigure(new Point(right - fold, top), true, true);
            g.LineTo(new Point(right, top + fold), true, true);
            g.LineTo(new Point(right - fold + 8, top + fold), true, true);
            g.ArcTo(new Point(right - fold, top + fold - 8), new Size(8, 8), 0, false, SweepDirection.Clockwise, true, true);
        }
        dc.DrawGeometry(new SolidColorBrush(Fold), null, corner);

        if (size >= 40)
        {
            DrawPdfLetters(dc);
            // Two lines of "text" under the letters.
            var line = new SolidColorBrush(Color.FromRgb(0xE6, 0xD3, 0xD8));
            dc.DrawRoundedRectangle(line, null, new Rect(80, 160, 62, 9), 4.5, 4.5);
            dc.DrawRoundedRectangle(line, null, new Rect(80, 180, 44, 9), 4.5, 4.5);
        }
        else
        {
            // Too small for letters: a bold bar and two lines read as "a document".
            var ink = new SolidColorBrush(Ink);
            dc.DrawRoundedRectangle(ink, null, new Rect(80, 104, 96, 30), 8, 8);
            var line = new SolidColorBrush(Color.FromRgb(0xE0, 0xC4, 0xCC));
            dc.DrawRoundedRectangle(line, null, new Rect(80, 152, 62, 16), 7, 7);
        }
    }

    private static void DrawPdfLetters(DrawingContext dc)
    {
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Black, FontStretches.Normal);
        var text = new FormattedText("PDF", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 58, new SolidColorBrush(Ink), 1.0);
        text.SetFontWeight(FontWeights.Black);
        // Fit the word to the sheet's width whatever font ends up being used.
        const double available = 106;
        var scale = Math.Min(1, available / text.WidthIncludingTrailingWhitespace);
        dc.PushTransform(new ScaleTransform(scale, 1, 128, 0));
        dc.DrawText(text, new Point(128 - text.WidthIncludingTrailingWhitespace / 2, 82));
        dc.Pop();
    }

    private static void DrawBadge(DrawingContext dc, int size)
    {
        // Large sizes keep the badge clear of the letters; small sizes need it bigger to stay visible.
        var centre = size >= 40 ? new Point(197, 198) : new Point(190, 190);
        var radius = size >= 40 ? 40.0 : 50.0;
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(60, 60, 0, 20)), null, new Point(centre.X + 2, centre.Y + 5), radius, radius);
        dc.DrawEllipse(Brushes.White, null, centre, radius + 7, radius + 7);
        dc.DrawEllipse(new LinearGradientBrush(BadgeTop, BadgeBottom, 90), null, centre, radius, radius);
        var pen = new Pen(Brushes.White, size >= 40 ? 13 : 17) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        var check = new StreamGeometry();
        using (var g = check.Open())
        {
            var k = radius / 44;
            g.BeginFigure(new Point(centre.X - 20 * k, centre.Y + 1 * k), false, false);
            g.LineTo(new Point(centre.X - 6 * k, centre.Y + 15 * k), true, true);
            g.LineTo(new Point(centre.X + 21 * k, centre.Y - 14 * k), true, true);
        }
        dc.DrawGeometry(null, pen, check);
    }

    /// <summary>Every size next to each other on a light and a dark strip, to judge legibility.</summary>
    public static BitmapSource ContactSheet(IReadOnlyList<BitmapSource> images)
    {
        const int pad = 24;
        var width = images.Sum(i => i.PixelWidth) + pad * (images.Count + 1);
        var rowHeight = images.Max(i => i.PixelHeight) + 2 * pad;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3)), null, new Rect(0, 0, width, rowHeight));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)), null, new Rect(0, rowHeight, width, rowHeight));
            for (var row = 0; row < 2; row++)
            {
                double x = pad;
                foreach (var image in images)
                {
                    dc.DrawImage(image, new Rect(x, row * rowHeight + rowHeight - pad - image.PixelHeight, image.PixelWidth, image.PixelHeight));
                    x += image.PixelWidth + pad;
                }
            }
        }
        var bitmap = new RenderTargetBitmap(width, rowHeight * 2, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    public static byte[] ToPng(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }
}

/// <summary>Writes the Windows .ico container: classic 32-bit bitmaps for small sizes, PNG for 256 pixels.</summary>
internal static class IcoFile
{
    public static byte[] Build(IReadOnlyList<(int Size, BitmapSource Bitmap)> images)
    {
        var payloads = images.Select(i => i.Size >= 256 ? IconArt.ToPng(i.Bitmap) : ToDib(i.Bitmap)).ToList();
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)images.Count);
        var offset = 6 + 16 * images.Count;
        for (var i = 0; i < images.Count; i++)
        {
            var size = images[i].Size;
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(payloads[i].Length);
            writer.Write(offset);
            offset += payloads[i].Length;
        }
        foreach (var payload in payloads) writer.Write(payload);
        writer.Flush();
        return output.ToArray();
    }

    /// <summary>A device-independent bitmap as icons store it: header, bottom-up BGRA pixels, then a 1-bit mask.</summary>
    private static byte[] ToDib(BitmapSource source)
    {
        // Icons want straight (not premultiplied) alpha.
        var straight = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int width = straight.PixelWidth, height = straight.PixelHeight, stride = width * 4;
        var pixels = new byte[stride * height];
        straight.CopyPixels(pixels, stride, 0);
        var maskStride = (width + 31) / 32 * 4;
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write(40);
        writer.Write(width);
        writer.Write(height * 2);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(0);
        writer.Write(stride * height + maskStride * height);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        for (var y = height - 1; y >= 0; y--) writer.Write(pixels, y * stride, stride);
        for (var y = height - 1; y >= 0; y--)
        {
            var row = new byte[maskStride];
            for (var x = 0; x < width; x++)
            {
                if (pixels[y * stride + x * 4 + 3] == 0) row[x / 8] |= (byte)(0x80 >> (x % 8));
            }
            writer.Write(row);
        }
        writer.Flush();
        return output.ToArray();
    }
}
