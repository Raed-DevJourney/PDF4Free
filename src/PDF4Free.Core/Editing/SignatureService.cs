using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using PdfSharp.Drawing;
using PdfSharp.Pdf.Annotations;
using PdfSharp.Pdf.Signatures;
using PDF4Free.Core.Model;
using PDF4Free.Core.Pdfium;

namespace PDF4Free.Core.Editing;

public sealed record SignRequest(
    X509Certificate2 Certificate,
    int PageIndex,
    PdfRect? VisibleRect,
    string Reason = "",
    string Location = "",
    string Contact = "");

public sealed record SignatureReport(
    string Signer,
    string Issuer,
    string SignedAt,
    string Reason,
    bool IntegrityValid,
    bool CoversWholeDocument,
    bool TrustedCertificate,
    string Summary);

/// <summary>Certificate-based (digital) signatures: sign, verify and create a self-signed digital ID.</summary>
public static class SignatureService
{
    /// <summary>Signs the document. This must be the last change: any later edit invalidates the signature.</summary>
    public static byte[] Sign(byte[] source, SignRequest request)
    {
        if (!request.Certificate.HasPrivateKey) throw new InvalidOperationException("The selected certificate has no private key and cannot sign.");
        using var doc = PdfIO.Open(source);
        var visible = request.VisibleRect is { IsEmpty: false };
        var rect = request.VisibleRect ?? default;
        var options = new DigitalSignatureOptions
        {
            Reason = request.Reason,
            Location = request.Location,
            ContactInfo = request.Contact,
            AppName = "PDF4Free",
            PageIndex = Math.Clamp(request.PageIndex, 0, doc.PageCount - 1),
            Rectangle = visible ? new XRect(rect.Left, rect.Bottom, rect.Width, rect.Height) : XRect.Empty,
            AppearanceHandler = visible ? new SignatureAppearance(SubjectName(request.Certificate), request.Reason, request.Location) : null,
        };
        DigitalSignatureHandler.ForDocument(doc, new PdfSharpDefaultSigner(request.Certificate, PdfMessageDigestType.SHA256), options);
        return PdfIO.Save(doc);
    }

    private sealed class SignatureAppearance(string signer, string reason, string location) : IAnnotationAppearanceHandler
    {
        public void DrawAppearance(XGraphics gfx, XRect rect)
        {
            var border = new XPen(XColor.FromArgb(255, 30, 100, 200), 1);
            gfx.DrawRectangle(border, new XSolidBrush(XColor.FromArgb(18, 30, 100, 200)), new XRect(0.5, 0.5, rect.Width - 1, rect.Height - 1));
            var lines = new List<string> { "Digitally signed by", signer, DateTime.Now.ToString("yyyy-MM-dd HH:mm zzz") };
            if (reason.Length > 0) lines.Add("Reason: " + reason);
            if (location.Length > 0) lines.Add("Location: " + location);
            var size = Math.Clamp((rect.Height - 8) / lines.Count / 1.2, 4, 11);
            var y = 5 + size;
            for (var i = 0; i < lines.Count; i++)
            {
                var font = new XFont("Arial", i == 1 ? size * 1.1 : size, i == 1 ? XFontStyleEx.Bold : XFontStyleEx.Regular);
                gfx.DrawString(lines[i], font, XBrushes.Black, new XPoint(6, y));
                y += size * 1.25;
            }
        }
    }

    // ------------------------------------------------------------------ verification

    public static List<SignatureReport> Verify(byte[] file)
    {
        using var doc = PdfiumDocument.Load(file);
        return [.. doc.GetSignatures().Select(signature => Check(file, signature))];
    }

    private static SignatureReport Check(byte[] file, RawSignature signature)
    {
        var range = signature.ByteRange;
        var when = DocumentService.FormatDate(signature.Time);
        if (range.Length != 4 || signature.Contents.Length == 0)
        {
            return new SignatureReport("(unknown)", "", when, signature.Reason, false, false, false, "The signature field is empty or malformed.");
        }
        if (signature.SubFilter is not ("adbe.pkcs7.detached" or "ETSI.CAdES.detached"))
        {
            return new SignatureReport("(unknown)", "", when, signature.Reason, false, false, false, $"Signature format '{signature.SubFilter}' is not supported for verification.");
        }
        try
        {
            if (range[0] < 0 || range[1] < 0 || range[2] < 0 || range[3] < 0 || (long)range[0] + range[1] > file.Length || (long)range[2] + range[3] > file.Length)
            {
                return new SignatureReport("(unknown)", "", when, signature.Reason, false, false, false, "The signed byte range lies outside the file.");
            }
            var content = new byte[range[1] + range[3]];
            Buffer.BlockCopy(file, range[0], content, 0, range[1]);
            Buffer.BlockCopy(file, range[2], content, range[1], range[3]);
            var cms = new SignedCms(new ContentInfo(content), detached: true);
            cms.Decode(TrimDer(signature.Contents));
            var signerInfo = cms.SignerInfos[0];
            var certificate = signerInfo.Certificate;
            var integrity = true;
            try
            {
                cms.CheckSignature(verifySignatureOnly: true);
            }
            catch (CryptographicException)
            {
                integrity = false;
            }
            var coversAll = range[0] == 0 && range[2] + range[3] == file.Length;
            var trusted = certificate is not null && IsTrusted(certificate);
            var signedAt = SigningTime(signerInfo) ?? when;
            var summary = !integrity ? "INVALID: the document was changed after it was signed."
                : !coversAll ? "The signature is valid for the part it covers, but content was added after signing."
                : trusted ? "Valid: signed and unchanged; the signer's certificate is trusted on this computer."
                : "Signed and unchanged, but the signer's certificate is not trusted on this computer (for example a self-signed ID).";
            return new SignatureReport(
                certificate is null ? "(no certificate)" : SubjectName(certificate),
                certificate?.GetNameInfo(X509NameType.SimpleName, forIssuer: true) ?? "",
                signedAt, signature.Reason, integrity, coversAll, trusted, summary);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or InvalidOperationException)
        {
            return new SignatureReport("(unknown)", "", when, signature.Reason, false, false, false, "The signature could not be read: " + ex.Message);
        }
    }

    /// <summary>The signature is stored in a zero-padded slot: keep only the DER object itself.</summary>
    private static byte[] TrimDer(byte[] data)
    {
        if (data.Length < 2 || data[0] != 0x30) return data;
        int length;
        int header;
        if ((data[1] & 0x80) == 0)
        {
            length = data[1];
            header = 2;
        }
        else
        {
            var count = data[1] & 0x7F;
            if (count is 0 or > 4 || data.Length < 2 + count) return data;
            length = 0;
            for (var i = 0; i < count; i++) length = (length << 8) | data[2 + i];
            header = 2 + count;
        }
        var total = header + length;
        return total > 0 && total <= data.Length ? data[..total] : data;
    }

    private static string? SigningTime(SignerInfo signer)
    {
        foreach (var attribute in signer.SignedAttributes)
        {
            foreach (var value in attribute.Values)
            {
                if (value is Pkcs9SigningTime time) return time.SigningTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            }
        }
        return null;
    }

    private static bool IsTrusted(X509Certificate2 certificate)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreNotTimeValid;
        return chain.Build(certificate);
    }

    public static string SubjectName(X509Certificate2 certificate)
    {
        var name = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        return string.IsNullOrEmpty(name) ? certificate.Subject : name;
    }

    // ------------------------------------------------------------------ digital IDs

    /// <summary>Certificates in the current user's personal store that can sign.</summary>
    public static List<X509Certificate2> StoreCertificates()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        return [.. store.Certificates.Where(c => c.HasPrivateKey && c.NotAfter > DateTime.Now).OrderBy(SubjectName)];
    }

    public static X509Certificate2 LoadPfx(string path, string password) =>
        X509CertificateLoader.LoadPkcs12FromFile(path, password, X509KeyStorageFlags.DefaultKeySet);

    /// <summary>Creates a self-signed digital ID and returns it as a password-protected PFX file.</summary>
    public static byte[] CreateSelfSignedPfx(string name, string email, string organisation, string password, int years = 5)
    {
        using var rsa = RSA.Create(3072);
        var subject = new X500DistinguishedNameBuilder();
        subject.AddCommonName(name);
        if (!string.IsNullOrWhiteSpace(organisation)) subject.AddOrganizationName(organisation);
        if (!string.IsNullOrWhiteSpace(email)) subject.AddEmailAddress(email);
        var request = new CertificateRequest(subject.Build(), rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, critical: true));
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, critical: true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(Math.Clamp(years, 1, 20)));
        return certificate.Export(X509ContentType.Pfx, password);
    }
}
