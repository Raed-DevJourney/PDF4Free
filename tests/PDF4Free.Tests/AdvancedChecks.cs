using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PDF4Free.Core.Conversion;
using PDF4Free.Core.Editing;
using PDF4Free.Core.Model;
using PDF4Free.Core.Pdfium;

namespace PDF4Free.Tests;

public sealed partial class TestRunner
{
    private static BitmapSource Photo(int width, int height)
    {
        var pixels = new byte[width * height * 3];
        var random = new Random(7);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 3;
                pixels[i] = (byte)Math.Clamp(x * 255 / width + random.Next(-12, 12), 0, 255);
                pixels[i + 1] = (byte)Math.Clamp(y * 255 / height + random.Next(-12, 12), 0, 255);
                pixels[i + 2] = (byte)Math.Clamp((x + y) * 255 / (width + height) + random.Next(-12, 12), 0, 255);
            }
        }
        var bitmap = BitmapSource.Create(width, height, 200, 200, PixelFormats.Rgb24, null, pixels, width * 3);
        bitmap.Freeze();
        return bitmap;
    }

    [Check]
    public void Compression_shrinks_image_heavy_files_and_keeps_them_readable()
    {
        var photo = Photo(2400, 1800);
        var jpeg = new JpegBitmapEncoder { QualityLevel = 100 };
        jpeg.Frames.Add(BitmapFrame.Create(photo));
        using var jpegStream = new MemoryStream();
        jpeg.Save(jpegStream);
        var source = PageService.Merge([CreatePdf.FromBitmap(photo), CreatePdf.FromImages([jpegStream.ToArray()]), Samples.Basic(1)]);
        File.WriteAllBytes(PathFor("heavy.pdf"), source);

        var small = OptimizeService.Optimize(source, OptimizeOptions.Balanced, out var report);
        File.WriteAllBytes(PathFor("heavy-optimized.pdf"), small);
        Console.WriteLine($"      {report.BytesBefore / 1024} KB -> {report.BytesAfter / 1024} KB ({report.SavedPercent:0}% saved, {report.ImagesRecompressed}/{report.ImagesSeen} images)");
        AssertEqual(2, report.ImagesRecompressed, "both images recompressed");
        Assert(report.BytesAfter < report.BytesBefore / 3, "file is much smaller");
        using var doc = PdfiumDocument.Load(small);
        AssertEqual(3, doc.PageCount, "pages kept");
        Assert(doc.GetText(2).Text.Contains("quick brown fox"), "text kept");
        var before = PdfiumDocument.Load(source);
        var a = before.RenderBitmap(0, 30);
        var b = doc.RenderBitmap(0, 30);
        before.Dispose();
        AssertEqual(a.PixelWidth, b.PixelWidth, "page size unchanged");
        Assert(MeanDifference(a, b) < 12, $"picture still looks the same (mean difference {MeanDifference(a, b):0.0})");

        var light = OptimizeService.Optimize(Samples.Basic(1), OptimizeOptions.Balanced, out var none);
        Assert(light.Length <= Samples.Basic(1).Length, "never returns a bigger file");
        Assert(none.BytesAfter <= none.BytesBefore, "report is consistent");
    }

    private static double MeanDifference(BitmapSource a, BitmapSource b)
    {
        var stride = a.PixelWidth * 4;
        var pa = new byte[stride * a.PixelHeight];
        var pb = new byte[stride * b.PixelHeight];
        a.CopyPixels(pa, stride, 0);
        b.CopyPixels(pb, stride, 0);
        long total = 0;
        for (var i = 0; i < pa.Length; i++) total += Math.Abs(pa[i] - pb[i]);
        return (double)total / pa.Length;
    }

    [Check]
    public void Compare_reports_added_and_removed_words()
    {
        var (removed, added) = CompareService.Diff("a b c d e f".Split(' '), "a x c d f g".Split(' '));
        AssertEqual("b,e", string.Join(",", "a b c d e f".Split(' ').Where((_, i) => removed[i])), "removed words");
        AssertEqual("x,g", string.Join(",", "a x c d f g".Split(' ').Where((_, i) => added[i])), "added words");

        var oldBytes = Samples.Basic(2);
        var newBytes = ContentService.AddText(oldBytes, 1, new Point(72, 500), "A brand new sentence appears here.", new TextStyle());
        using (var heading = PdfiumDocument.Load(newBytes))
        {
            var target = heading.GetPageObjects(0).First(o => o.Text.Contains("quick brown"));
            newBytes = PdfiumOps.RemoveObjects(newBytes, 0, [target.Index]);
        }
        using var oldDoc = PdfiumDocument.Load(oldBytes);
        using var newDoc = PdfiumDocument.Load(newBytes);
        var result = CompareService.Compare(oldDoc, newDoc);
        AssertEqual(1, result.Removed.Count, "one removed run");
        Assert(result.Removed[0].Text.Contains("quick brown fox") && result.Removed[0].PageIndex == 0, "removed sentence located");
        AssertEqual(1, result.Added.Count, "one added run");
        Assert(result.Added[0].Text.StartsWith("A brand new") && result.Added[0].PageIndex == 1 && result.Added[0].Rects.Count == 1, "added sentence located");
        Assert(CompareService.Compare(oldDoc, oldDoc).Identical, "a document equals itself");
    }

    [Check]
    public void Digital_signatures_sign_and_verify()
    {
        var pfx = SignatureService.CreateSelfSignedPfx("Test Signer", "tester@example.com", "PDF4Free QA", "pw123");
        var pfxPath = PathFor("test-id.pfx");
        File.WriteAllBytes(pfxPath, pfx);
        using var certificate = SignatureService.LoadPfx(pfxPath, "pw123");
        AssertEqual("Test Signer", SignatureService.SubjectName(certificate), "certificate name");

        var source = Samples.Basic(2);
        var signed = SignatureService.Sign(source, new SignRequest(certificate, 0, new PdfRect(72, 100, 272, 170), "Approval", "Beirut"));
        File.WriteAllBytes(PathFor("signed.pdf"), signed);
        var reports = SignatureService.Verify(signed);
        AssertEqual(1, reports.Count, "one signature");
        var report = reports[0];
        Console.WriteLine("      " + report.Summary);
        Assert(report.IntegrityValid, "integrity valid");
        Assert(report.CoversWholeDocument, "covers whole document");
        AssertEqual("Test Signer", report.Signer, "signer");
        AssertEqual("Approval", report.Reason, "reason");
        Assert(!report.TrustedCertificate, "self-signed ID is not trusted");

        var field = FormService.ReadFields(signed).Single(f => f.Type == FormFieldType.Signature);
        Assert(Math.Abs(field.Rect.Left - 72) < 0.5 && Math.Abs(field.Rect.Bottom - 100) < 0.5 && Math.Abs(field.Rect.Top - 170) < 0.5, $"visible signature placed at the requested rectangle ({field.Rect})");
        using (var doc = PdfiumDocument.Load(signed)) SavePng(doc.RenderBitmap(0, 80), "signed.png");

        var tampered = (byte[])signed.Clone();
        var at = IndexOfAscii(tampered, "quick");
        if (at < 0) at = 600;
        tampered[at] ^= 0x01;
        var broken = SignatureService.Verify(tampered);
        Assert(broken.Count == 1 && !broken[0].IntegrityValid, "tampering is detected");

        var edited = PageService.Rotate(signed, [1], 90);
        Assert(SignatureService.Verify(edited).All(r => !r.IntegrityValid || !r.CoversWholeDocument), "an edit after signing no longer verifies cleanly");
        AssertEqual(0, SignatureService.Verify(source).Count, "unsigned document has no signatures");
    }

    private static int IndexOfAscii(byte[] haystack, string needle)
    {
        var n = System.Text.Encoding.ASCII.GetBytes(needle);
        for (var i = 0; i <= haystack.Length - n.Length; i++)
        {
            var match = true;
            for (var j = 0; j < n.Length && match; j++) match = haystack[i + j] == n[j];
            if (match) return i;
        }
        return -1;
    }
}
