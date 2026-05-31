using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace FAAM.UI.Views;

/// <summary>
/// Live drag feedback over the Engineer flow chart: a cyan horizontal line showing
/// where a dropped command will be inserted, plus a translucent label of the
/// command tracking the cursor. Hit-test-invisible so it never interferes with the
/// drop. Coordinates are relative to the adorned element (the flow ScrollViewer,
/// i.e. viewport space — so it stays correct while the list is scrolled).
/// </summary>
internal sealed class DragInsertionAdorner : Adorner
{
    private static readonly SolidColorBrush Accent = new(Color.FromRgb(0x00, 0xC8, 0xFF));
    private static readonly Pen LinePen = new(Accent, 2);
    private static readonly SolidColorBrush GhostFill = new(Color.FromArgb(0xCC, 0x00, 0xC8, 0xFF));
    private static readonly Typeface GhostTypeface = new("Segoe UI");

    static DragInsertionAdorner()
    {
        Accent.Freeze();
        LinePen.Freeze();
        GhostFill.Freeze();
    }

    private double _lineY;
    private Point _cursor;
    private string _label = string.Empty;

    public DragInsertionAdorner(UIElement adornedElement) : base(adornedElement)
    {
        IsHitTestVisible = false;
    }

    /// <summary>Updates the insertion-line Y, the cursor position, and the ghost label, then repaints.</summary>
    public void Update(double lineY, Point cursor, string label)
    {
        _lineY = lineY;
        _cursor = cursor;
        _label = label;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = AdornedElement.RenderSize.Width;

        // Insertion line with end caps.
        dc.DrawLine(LinePen, new Point(12, _lineY), new Point(w - 12, _lineY));
        dc.DrawEllipse(Accent, null, new Point(12, _lineY), 3, 3);
        dc.DrawEllipse(Accent, null, new Point(w - 12, _lineY), 3, 3);

        // Ghost label near the cursor.
        if (string.IsNullOrEmpty(_label)) return;
        var text = new FormattedText(
            _label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            GhostTypeface, 12, Brushes.Black, 1.0);
        const double pad = 6;
        var box = new Rect(_cursor.X + 14, _cursor.Y + 6, text.Width + pad * 2, text.Height + pad);
        dc.DrawRoundedRectangle(GhostFill, null, box, 3, 3);
        dc.DrawText(text, new Point(box.X + pad, box.Y + pad / 2));
    }
}
