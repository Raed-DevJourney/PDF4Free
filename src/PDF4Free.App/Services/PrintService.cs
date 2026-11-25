using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using PDF4Free.App.Dialogs;
using PDF4Free.Core;
using PDF4Free.Core.Pdfium;

namespace PDF4Free.App.Services;

/// <summary>Prints a document through the Windows print dialog by rendering each page for the chosen printer.</summary>
public static class PrintService
{
    public static bool Print(Window owner, PdfSession session, int currentPage)
    {
        var options = new FormDialog("Print", owner, "Choose printer…");
        var which = options.AddChoice("Pages", ["All pages", $"Current page ({currentPage + 1})", "A range"], 0);
        var range = options.AddPageRange("Range", session.PageCount);
        var scaling = options.AddChoice("Size", ["Fit to the paper", "Actual size"], 0);
        var rotate = options.AddCheck("Turn wide pages to match the paper", true);
        var comments = options.AddCheck("Print comments and form fields", true);
        if (!options.Ask()) return false;

        var pages = which.Value switch
        {
            1 => [currentPage],
            2 => range.Value,
            _ => Enumerable.Range(0, session.PageCount).ToList(),
        };
        if (pages.Count == 0) return false;

        var dialog = new PrintDialog();
        if (dialog.ShowDialog() != true) return false;
        var paper = new Size(dialog.PrintableAreaWidth, dialog.PrintableAreaHeight);
        var paginator = new Paginator(session, pages, paper, scaling.Value == 0, rotate.Value, comments.Value);
        dialog.PrintDocument(paginator, session.DisplayName);
        return true;
    }

    private sealed class Paginator(PdfSession session, IReadOnlyList<int> pages, Size paper, bool fit, bool autoRotate, bool comments) : DocumentPaginator
    {
        private const double PrintDpi = 300;
        private const long MaxPixels = 40_000_000;

        public override bool IsPageCountValid => true;
        public override int PageCount => pages.Count;
        public override Size PageSize { get; set; } = paper;
        public override IDocumentPaginatorSource? Source => null;

        public override DocumentPage GetPage(int pageNumber)
        {
            var index = pages[pageNumber];
            var geometry = session.Document.GetGeometry(index);
            var turn = autoRotate && geometry.ViewWidth > geometry.ViewHeight != PageSize.Width > PageSize.Height;
            // Sizes in device-independent pixels (1/96 inch).
            var width = (turn ? geometry.ViewHeight : geometry.ViewWidth) * 96 / 72;
            var height = (turn ? geometry.ViewWidth : geometry.ViewHeight) * 96 / 72;
            var scale = fit ? Math.Min(PageSize.Width / width, PageSize.Height / height) : 1;
            var target = new Rect((PageSize.Width - width * scale) / 2, (PageSize.Height - height * scale) / 2, width * scale, height * scale);

            var pixelWidth = (int)Math.Round(target.Width / 96 * PrintDpi);
            var pixelHeight = (int)Math.Round(target.Height / 96 * PrintDpi);
            var reduce = Math.Min(1, Math.Sqrt((double)MaxPixels / Math.Max(1, (long)pixelWidth * pixelHeight)));
            pixelWidth = Math.Max(1, (int)(pixelWidth * reduce));
            pixelHeight = Math.Max(1, (int)(pixelHeight * reduce));
            var renderOptions = new PageRenderOptions(Annotations: comments, ForPrint: true, ExtraRotation: turn ? 1 : 0);
            var bitmap = PdfiumDocument.ToBitmap(session.Document.Render(index, pixelWidth, pixelHeight, renderOptions), pixelWidth, pixelHeight);

            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.DrawImage(bitmap, target);
            }
            var bounds = new Rect(PageSize);
            return new DocumentPage(visual, PageSize, bounds, bounds);
        }
    }
}
