using System.Windows;

namespace PDF4Free.Core.Model;

/// <summary>A point in PDF user space (points, origin bottom-left, y up).</summary>
public readonly record struct PdfPoint(double X, double Y);

/// <summary>A rectangle in PDF user space (points, y up).</summary>
public readonly record struct PdfRect(double Left, double Bottom, double Right, double Top)
{
    public double Width => Right - Left;
    public double Height => Top - Bottom;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public PdfPoint Center => new((Left + Right) / 2, (Bottom + Top) / 2);

    public static PdfRect FromPoints(PdfPoint a, PdfPoint b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

    public bool Contains(PdfPoint p) => p.X >= Left && p.X <= Right && p.Y >= Bottom && p.Y <= Top;

    public bool Intersects(PdfRect o) => o.Left < Right && o.Right > Left && o.Bottom < Top && o.Top > Bottom;

    public PdfRect Union(PdfRect o) =>
        new(Math.Min(Left, o.Left), Math.Min(Bottom, o.Bottom), Math.Max(Right, o.Right), Math.Max(Top, o.Top));

    public PdfRect Inflate(double d) => new(Left - d, Bottom - d, Right + d, Top + d);

    public PdfRect Offset(double dx, double dy) => new(Left + dx, Bottom + dy, Right + dx, Top + dy);
}

/// <summary>
/// Maps between PDF user space and "view space": points measured from the top-left corner of the page
/// as it is displayed (crop box applied, page rotation applied, y down).
/// </summary>
public sealed class PageGeometry
{
    public PageGeometry(PdfRect box, int rotation)
    {
        Box = box;
        Rotation = ((rotation % 4) + 4) % 4;
    }

    /// <summary>The visible page box (crop box clipped to the media box) in PDF user space.</summary>
    public PdfRect Box { get; }

    /// <summary>Clockwise rotation in quarter turns (0..3).</summary>
    public int Rotation { get; }

    public bool IsSideways => Rotation is 1 or 3;
    public double ViewWidth => IsSideways ? Box.Height : Box.Width;
    public double ViewHeight => IsSideways ? Box.Width : Box.Height;

    public Point ToView(PdfPoint p) => Rotation switch
    {
        0 => new Point(p.X - Box.Left, Box.Top - p.Y),
        1 => new Point(p.Y - Box.Bottom, p.X - Box.Left),
        2 => new Point(Box.Right - p.X, p.Y - Box.Bottom),
        _ => new Point(Box.Top - p.Y, Box.Right - p.X),
    };

    public PdfPoint ToPage(Point v) => Rotation switch
    {
        0 => new PdfPoint(v.X + Box.Left, Box.Top - v.Y),
        1 => new PdfPoint(v.Y + Box.Left, v.X + Box.Bottom),
        2 => new PdfPoint(Box.Right - v.X, v.Y + Box.Bottom),
        _ => new PdfPoint(Box.Right - v.Y, Box.Top - v.X),
    };

    public Rect ToView(PdfRect r)
    {
        var a = ToView(new PdfPoint(r.Left, r.Bottom));
        var b = ToView(new PdfPoint(r.Right, r.Top));
        return new Rect(a, b);
    }

    public PdfRect ToPage(Rect v)
    {
        var a = ToPage(v.TopLeft);
        var b = ToPage(v.BottomRight);
        return PdfRect.FromPoints(a, b);
    }

    /// <summary>
    /// Matrix (a b c d e f) that maps view space to PDF user space; used to draw upright content on rotated pages.
    /// </summary>
    public (double A, double B, double C, double D, double E, double F) ViewToPageMatrix() => Rotation switch
    {
        0 => (1, 0, 0, -1, Box.Left, Box.Top),
        1 => (0, 1, 1, 0, Box.Left, Box.Bottom),
        2 => (-1, 0, 0, 1, Box.Right, Box.Bottom),
        _ => (0, -1, -1, 0, Box.Right, Box.Top),
    };
}
