using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfSharp.Pdf;

namespace PDF4Free.Core.Editing;

public sealed record OptimizeOptions(
    int JpegQuality = 72,
    int MaxImageDimension = 1800,
    bool ConvertLosslessImages = true,
    bool RemoveMetadata = false,
    bool RemoveThumbnails = true)
{
    public static OptimizeOptions HighQuality { get; } = new(88, 3000, false);
    public static OptimizeOptions Balanced { get; } = new(72, 1800, true);
    public static OptimizeOptions SmallestFile { get; } = new(50, 1100, true);
}

public sealed record OptimizeReport(long BytesBefore, long BytesAfter, int ImagesRecompressed, int ImagesSeen)
{
    public double SavedPercent => BytesBefore == 0 ? 0 : Math.Max(0, 100.0 * (BytesBefore - BytesAfter) / BytesBefore);
}

/// <summary>Shrinks a PDF: recompresses and downsamples images, compresses streams and drops unreferenced objects.</summary>
public static class OptimizeService
{
    public static byte[] Optimize(byte[] source, OptimizeOptions options, out OptimizeReport report)
    {
        var recompressed = 0;
        var seen = 0;
        var result = PdfIO.Modify(source, doc =>
        {
            doc.Options.CompressContentStreams = true;
            doc.Options.NoCompression = false;
            doc.Options.FlateEncodeMode = PdfFlateEncodeMode.BestCompression;
            foreach (var obj in doc.Internals.GetAllObjects())
            {
                if (obj is not PdfDictionary dict || dict.Stream is null) continue;
                if (dict.Name("/Subtype") == "/Image")
                {
                    seen++;
                    if (TryRecompress(dict, options)) recompressed++;
                }
                else if (!dict.Elements.ContainsKey("/Filter") && dict.Stream.Length > 256 && dict.Name("/Type") != "/Metadata")
                {
                    dict.Stream.Value = AnnotationService.Deflate(dict.Stream.Value);
                    dict.Elements.SetName("/Filter", "/FlateDecode");
                }
            }
            if (options.RemoveMetadata)
            {
                doc.Internals.Catalog.Elements.Remove("/Metadata");
                doc.Internals.Catalog.Elements.Remove("/PieceInfo");
            }
            foreach (var page in doc.Pages)
            {
                if (options.RemoveThumbnails) page.Elements.Remove("/Thumb");
                if (options.RemoveMetadata) page.Elements.Remove("/PieceInfo");
            }
        });
        // Never hand back a bigger file than we were given.
        if (result.Length >= source.Length)
        {
            report = new OptimizeReport(source.Length, source.Length, 0, seen);
            return source;
        }
        report = new OptimizeReport(source.Length, result.Length, recompressed, seen);
        return result;
    }

    private static bool TryRecompress(PdfDictionary image, OptimizeOptions options)
    {
        try
        {
            if (image.Elements.ContainsKey("/Decode") || image.Elements.ContainsKey("/Mask") || image.Elements.ContainsKey("/ImageMask")) return false;
            var width = image.Int("/Width");
            var height = image.Int("/Height");
            if (width < 32 || height < 32) return false;
            var original = image.Stream.Value;
            var decoded = SingleFilter(image) switch
            {
                "/DCTDecode" => DecodeJpeg(original),
                "/FlateDecode" when options.ConvertLosslessImages => DecodeRaw(image, width, height),
                _ => null,
            };
            if (decoded is null) return false;

            var largest = Math.Max(decoded.PixelWidth, decoded.PixelHeight);
            BitmapSource output = decoded;
            if (largest > options.MaxImageDimension)
            {
                var factor = (double)options.MaxImageDimension / largest;
                output = new TransformedBitmap(decoded, new ScaleTransform(factor, factor));
            }
            var encoded = EncodeJpeg(output, options.JpegQuality);
            if (encoded.Length > original.Length * 0.9) return false;
            if (!SameChannelCount(encoded, decoded.Format)) return false;

            image.Stream.Value = encoded;
            image.Elements.SetName("/Filter", "/DCTDecode");
            image.Elements.Remove("/DecodeParms");
            image.Elements.SetInteger("/Width", output.PixelWidth);
            image.Elements.SetInteger("/Height", output.PixelHeight);
            image.Elements.SetInteger("/BitsPerComponent", 8);
            return true;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException or OverflowException or InvalidDataException)
        {
            return false; // An image we cannot decode safely is left exactly as it was.
        }
    }

    private static string SingleFilter(PdfDictionary image) => PdfX.Resolve(image.Elements["/Filter"]) switch
    {
        PdfName name => name.Value,
        PdfArray { Elements.Count: 1 } array when PdfX.Resolve(array.Elements[0]) is PdfName only => only.Value,
        _ => "",
    };

    private static BitmapSource? DecodeJpeg(byte[] data)
    {
        var decoder = new JpegBitmapDecoder(new MemoryStream(data), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        // CMYK and other exotic JPEGs keep their original encoding: converting them would change colours.
        return frame.Format == PixelFormats.Bgr24 || frame.Format == PixelFormats.Bgr32 || frame.Format == PixelFormats.Gray8 || frame.Format == PixelFormats.Rgb24
            ? frame
            : null;
    }

    private static BitmapSource? DecodeRaw(PdfDictionary image, int width, int height)
    {
        if (image.Int("/BitsPerComponent") != 8 || image.Elements.ContainsKey("/SMask") && width * height < 256 * 256) return null;
        var colorSpace = image.Name("/ColorSpace");
        var channels = colorSpace switch
        {
            "/DeviceRGB" => 3,
            "/DeviceGray" => 1,
            _ => 0,
        };
        if (channels == 0 || (long)width * height < 128 * 128) return null;
        var raw = Inflate(image, width, channels);
        var expected = width * height * channels;
        // Some writers pad the stream; readers only consume width x height x channels bytes, and so do we.
        if (raw is null || raw.Length < expected) return null;
        if (raw.Length > expected) raw = raw[..expected];
        var format = channels == 3 ? PixelFormats.Rgb24 : PixelFormats.Gray8;
        var bitmap = BitmapSource.Create(width, height, 96, 96, format, null, raw, width * channels);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Decompresses a FlateDecode image and undoes the PNG row predictor when one is declared.</summary>
    private static byte[]? Inflate(PdfDictionary image, int width, int channels)
    {
        using var input = new MemoryStream(image.Stream.Value);
        using var zlib = new System.IO.Compression.ZLibStream(input, System.IO.Compression.CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        var data = output.ToArray();
        var predictor = image.Dict("/DecodeParms")?.Int("/Predictor", 1) ?? 1;
        if (predictor == 1) return data;
        if (predictor < 10) return null; // TIFF predictor: rare, leave the image alone.
        return UndoPngPredictor(data, width * channels, channels);
    }

    private static byte[]? UndoPngPredictor(byte[] data, int rowBytes, int pixelBytes)
    {
        var stride = rowBytes + 1;
        if (data.Length % stride != 0) return null;
        var rows = data.Length / stride;
        var result = new byte[rows * rowBytes];
        for (var y = 0; y < rows; y++)
        {
            var filter = data[y * stride];
            var source = y * stride + 1;
            var target = y * rowBytes;
            for (var x = 0; x < rowBytes; x++)
            {
                int left = x >= pixelBytes ? result[target + x - pixelBytes] : 0;
                int up = y > 0 ? result[target - rowBytes + x] : 0;
                int upLeft = y > 0 && x >= pixelBytes ? result[target - rowBytes + x - pixelBytes] : 0;
                var predicted = filter switch
                {
                    0 => 0,
                    1 => left,
                    2 => up,
                    3 => (left + up) / 2,
                    4 => Paeth(left, up, upLeft),
                    _ => -1,
                };
                if (predicted < 0) return null;
                result[target + x] = (byte)(data[source + x] + predicted);
            }
        }
        return result;
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static byte[] EncodeJpeg(BitmapSource bitmap, int quality)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 10, 100) };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    /// <summary>The PDF keeps its /ColorSpace entry, so the new JPEG must have the same number of channels as the old pixels.</summary>
    private static bool SameChannelCount(byte[] jpeg, PixelFormat before)
    {
        var after = new JpegBitmapDecoder(new MemoryStream(jpeg), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0].Format;
        var grayBefore = before == PixelFormats.Gray8;
        var grayAfter = after == PixelFormats.Gray8;
        return grayBefore == grayAfter;
    }
}
