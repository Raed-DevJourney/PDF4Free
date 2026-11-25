namespace PDF4Free.App.Views;

/// <summary>What the mouse does in the page view.</summary>
public enum ViewerTool
{
    Select,
    Hand,
    Snapshot,
    Measure,

    // Comments
    Highlight,
    Underline,
    StrikeOut,
    Squiggly,
    Note,
    TextBox,
    Ink,
    Rectangle,
    Ellipse,
    Line,
    Arrow,
    Stamp,

    // Editing page content
    EditObjects,
    AddText,
    PlaceImage,
    AddLink,
    Whiteout,
    Crop,

    // Redaction
    RedactArea,
    RedactText,

    // Form design
    FieldText,
    FieldCheck,
    FieldRadio,
    FieldCombo,
    FieldList,
    FieldSignature,

    // Signing
    CertificateArea,
}

public static class ViewerToolInfo
{
    public static bool IsTextMarkup(this ViewerTool tool) =>
        tool is ViewerTool.Highlight or ViewerTool.Underline or ViewerTool.StrikeOut or ViewerTool.Squiggly or ViewerTool.RedactText;

    /// <summary>Tools that are used by dragging out a rectangle.</summary>
    public static bool IsRectangleDrag(this ViewerTool tool) => tool is
        ViewerTool.Snapshot or ViewerTool.TextBox or ViewerTool.Rectangle or ViewerTool.Ellipse or ViewerTool.Stamp or ViewerTool.PlaceImage
        or ViewerTool.AddLink or ViewerTool.Whiteout or ViewerTool.Crop or ViewerTool.RedactArea or ViewerTool.CertificateArea
        or ViewerTool.FieldText or ViewerTool.FieldCheck or ViewerTool.FieldRadio or ViewerTool.FieldCombo or ViewerTool.FieldList or ViewerTool.FieldSignature;

    public static bool IsLineDrag(this ViewerTool tool) => tool is ViewerTool.Line or ViewerTool.Arrow or ViewerTool.Measure;

    public static bool IsFormDesign(this ViewerTool tool) => tool is
        ViewerTool.FieldText or ViewerTool.FieldCheck or ViewerTool.FieldRadio or ViewerTool.FieldCombo or ViewerTool.FieldList or ViewerTool.FieldSignature;

    public static string Hint(this ViewerTool tool) => tool switch
    {
        ViewerTool.Select => "Select text, click links, fill form fields, or click a comment to select it.",
        ViewerTool.Hand => "Drag to pan the page.",
        ViewerTool.Snapshot => "Drag a rectangle to copy that area to the clipboard as a picture.",
        ViewerTool.Measure => "Drag from one point to another to measure the distance.",
        ViewerTool.Highlight or ViewerTool.Underline or ViewerTool.StrikeOut or ViewerTool.Squiggly => "Drag across the text to mark it.",
        ViewerTool.Note => "Click where the sticky note should go.",
        ViewerTool.TextBox => "Drag a box, then type its text.",
        ViewerTool.Ink => "Draw freehand with the mouse. Each stroke is saved as a drawing.",
        ViewerTool.Rectangle or ViewerTool.Ellipse => "Drag to draw the shape.",
        ViewerTool.Line or ViewerTool.Arrow => "Drag from the start to the end of the line.",
        ViewerTool.Stamp => "Click to place the stamp, or drag to set its size.",
        ViewerTool.EditObjects => "Click text, pictures or shapes to select them. Drag to move, drag the corner to resize, Delete to remove, double-click text to edit it.",
        ViewerTool.AddText => "Click where the new text should start, type, then click elsewhere to finish.",
        ViewerTool.PlaceImage => "Click to place the picture, or drag to set its size.",
        ViewerTool.AddLink => "Drag over the area that should become a link.",
        ViewerTool.Whiteout => "Drag over an area to cover it with white. (Use Redact to remove content permanently.)",
        ViewerTool.Crop => "Drag over the part of the page to keep.",
        ViewerTool.RedactArea => "Drag over the area to mark for redaction. Nothing is removed until you apply the redactions.",
        ViewerTool.RedactText => "Drag across the text to mark it for redaction.",
        ViewerTool.CertificateArea => "Drag the area where the visible signature should appear.",
        _ => "Drag the area for the new form field.",
    };
}

/// <summary>Style settings shared by the tools; the tool options bar edits them.</summary>
public sealed class ToolOptions
{
    public uint Color { get; set; } = 0xD32F2F;
    public uint HighlightColor { get; set; } = 0xFFD400;
    public uint? Fill { get; set; }
    public double Opacity { get; set; } = 1.0;
    public double LineWidth { get; set; } = 2.0;
    public string FontFamily { get; set; } = "Arial";
    public double FontSize { get; set; } = 12;
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public uint TextColor { get; set; } = 0x000000;
    public string StampText { get; set; } = "APPROVED";
    public uint StampColor { get; set; } = 0x2E7D32;
    public bool StampWithDate { get; set; } = true;

    /// <summary>The picture the Place Image tool will put down (a signature, a stamp picture or any image file).</summary>
    public byte[]? Image { get; set; }

    /// <summary>Width divided by height of <see cref="Image"/>.</summary>
    public double ImageAspect { get; set; } = 3;

    /// <summary>When true the placed picture becomes a stamp comment instead of page content.</summary>
    public bool ImageAsStamp { get; set; }

    /// <summary>Text the next Add Text box starts with (used by "Add Date"); cleared once used.</summary>
    public string? PresetText { get; set; }
}
