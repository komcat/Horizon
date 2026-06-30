using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using FAAM.UI.ViewModels;

namespace FAAM.UI.Views;

/// <summary>
/// Engineer "Dispensing" tab — browse / edit the active product's dispense routines with a
/// live pattern preview. The preview maps the pattern's expanded dot offsets into the canvas
/// (origin = the camera-centred anchor, +Y up), redrawn whenever a pattern field changes.
/// </summary>
public partial class DispenseEditorView : UserControl
{
    private readonly DispenseEditorViewModel _vm;

    public DispenseEditorView(EngineHost host)
    {
        InitializeComponent();
        _vm = new DispenseEditorViewModel(host);
        DataContext = _vm;
        _vm.PreviewChanged += RedrawPreview;
        PreviewCanvas.SizeChanged += (_, _) => RedrawPreview();
        Loaded += (_, _) => RedrawPreview();
    }

    /// <summary>Re-read routine names (the active product may have changed) and redraw.</summary>
    public void Activate()
    {
        _vm.ReloadCommand.Execute(null);
        RedrawPreview();
    }

    private void RedrawPreview()
    {
        PreviewCanvas.Children.Clear();
        double w = PreviewCanvas.ActualWidth, h = PreviewCanvas.ActualHeight;
        var pts = _vm.PreviewPoints();
        if (w < 10 || h < 10 || pts.Count == 0) return;

        // Bounds, always including the origin (anchor).
        double minX = Math.Min(0, pts.Min(p => p.Dx)), maxX = Math.Max(0, pts.Max(p => p.Dx));
        double minY = Math.Min(0, pts.Min(p => p.Dy)), maxY = Math.Max(0, pts.Max(p => p.Dy));
        double spanX = Math.Max(maxX - minX, 0.001), spanY = Math.Max(maxY - minY, 0.001);

        const double margin = 18;
        double scale = Math.Min((w - 2 * margin) / spanX, (h - 2 * margin) / spanY);
        if (double.IsInfinity(scale) || scale <= 0) scale = 1;
        double cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;

        Point Map(double dx, double dy) => new(w / 2 + (dx - cx) * scale, h / 2 - (dy - cy) * scale);

        var accent = (Brush)FindResource("Accent");
        var subtle = (Brush)FindResource("Subtle");

        // Anchor crosshair (camera centre).
        var o = Map(0, 0);
        PreviewCanvas.Children.Add(Line(o.X - 7, o.Y, o.X + 7, o.Y, subtle));
        PreviewCanvas.Children.Add(Line(o.X, o.Y - 7, o.X, o.Y + 7, subtle));

        // Dots.
        foreach (var p in pts)
        {
            var s = Map(p.Dx, p.Dy);
            var dot = new Ellipse { Width = 8, Height = 8, Fill = accent };
            Canvas.SetLeft(dot, s.X - 4);
            Canvas.SetTop(dot, s.Y - 4);
            PreviewCanvas.Children.Add(dot);
        }

        PreviewLabel.Text = $"{pts.Count} dot(s)  ·  extent {spanX:F2} × {spanY:F2} mm  ·  + = camera anchor";
    }

    private static Line Line(double x1, double y1, double x2, double y2, Brush stroke)
        => new() { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = stroke, StrokeThickness = 1 };
}
