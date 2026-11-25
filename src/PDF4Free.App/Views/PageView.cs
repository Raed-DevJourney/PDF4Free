using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PDF4Free.App.Views;

/// <summary>A transparent layer whose content is painted by a callback (selection, search hits, tool previews).</summary>
internal sealed class OverlayLayer : FrameworkElement
{
    public Action<DrawingContext>? Painter { get; set; }

    protected override void OnRender(DrawingContext drawingContext) => Painter?.Invoke(drawingContext);
}

/// <summary>The visual for one page: the rendered bitmap, an optional sharp tile for high zoom, an overlay and inline editors.</summary>
internal sealed class PageView : Canvas
{
    public PageView(int index)
    {
        Index = index;
        Background = Brushes.White;
        ClipToBounds = true;
        RenderOptions.SetBitmapScalingMode(Base, BitmapScalingMode.HighQuality);
        Children.Add(Base);
        Children.Add(Tile);
        Children.Add(Overlay);
        Children.Add(Editors);
    }

    public int Index { get; set; }
    public Image Base { get; } = new() { Stretch = Stretch.Fill };
    public Image Tile { get; } = new() { Stretch = Stretch.Fill, Visibility = Visibility.Collapsed };
    public OverlayLayer Overlay { get; } = new() { IsHitTestVisible = false };
    public Canvas Editors { get; } = new();

    /// <summary>Document version and pixel scale (pixels per point) of the bitmap currently shown in <see cref="Base"/>.</summary>
    public int BaseVersion { get; set; } = -1;
    public double BaseScale { get; set; }

    /// <summary>Area (in page DIPs), version and scale of the sharp tile; empty when none is shown.</summary>
    public Rect TileArea { get; set; } = Rect.Empty;
    public int TileVersion { get; set; } = -1;
    public double TileScale { get; set; }

    public void Resize(double width, double height)
    {
        Width = width;
        Height = height;
        Base.Width = width;
        Base.Height = height;
        Overlay.Width = width;
        Overlay.Height = height;
        Editors.Width = width;
        Editors.Height = height;
    }

    public void ShowTile(ImageSource source, Rect area)
    {
        Tile.Source = source;
        Tile.Width = area.Width;
        Tile.Height = area.Height;
        SetLeft(Tile, area.X);
        SetTop(Tile, area.Y);
        Tile.Visibility = Visibility.Visible;
        TileArea = area;
    }

    public void HideTile()
    {
        Tile.Visibility = Visibility.Collapsed;
        Tile.Source = null;
        TileArea = Rect.Empty;
        TileVersion = -1;
    }
}
