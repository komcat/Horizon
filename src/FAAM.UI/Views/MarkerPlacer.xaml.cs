using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace FAAM.UI.Views;

/// <summary>
/// The standard marker-placement gesture for every camera surface: while armed, a click on the
/// feed drops a yellow crosshair at that spot and floats <b>Confirm</b> / <b>Cancel</b> beside it;
/// the operator may re-click to move the crosshair, then confirms to commit the point (or
/// cancels to discard it and click again). Nothing is committed until Confirm, so a marker is
/// never placed by a stray click — including the manual points a run procedure asks for.
///
/// Placed over an <see cref="Image"/> like the other overlays (centred, sized to the image's
/// display box by <see cref="Attach"/>); positions are image-relative display points.
/// The owner routes its feed click to <see cref="Place"/> while <see cref="IsArmed"/>, and
/// commits on <see cref="Confirmed"/>. While a crosshair is pending the placer holds keyboard
/// focus: the <b>arrow keys nudge it one image pixel</b>, Enter confirms, Escape cancels.
/// </summary>
public partial class MarkerPlacer : UserControl
{
    private const double ArmLength = 40;
    private const double Gap = 9;

    private Image? _image;
    private Point? _pending;

    /// <summary>The operator confirmed the crosshair at this image-relative display point.</summary>
    public event Action<Point>? Confirmed;

    /// <summary>The operator discarded the pending placement (still armed).</summary>
    public event Action? Cancelled;

    public MarkerPlacer()
    {
        InitializeComponent();
        Focusable = true;   // takes the arrow keys while a placement is pending
    }

    /// <summary>Display px per source (image) px for the attached Uniform-fit image — one arrow-key step.</summary>
    private double SourcePixelStep()
    {
        if (_image?.Source is BitmapSource b && b.PixelWidth > 0 && b.PixelHeight > 0 && _image.ActualWidth > 0)
            return Math.Min(_image.ActualWidth / b.PixelWidth, _image.ActualHeight / b.PixelHeight);
        return 1;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_pending is not { } p) { base.OnPreviewKeyDown(e); return; }
        double step = SourcePixelStep();
        switch (e.Key)
        {
            case Key.Left:  Place(new Point(p.X - step, p.Y)); break;
            case Key.Right: Place(new Point(p.X + step, p.Y)); break;
            case Key.Up:    Place(new Point(p.X, p.Y - step)); break;
            case Key.Down:  Place(new Point(p.X, p.Y + step)); break;
            case Key.Enter: OnConfirm(this, new RoutedEventArgs()); break;
            case Key.Escape: OnCancel(this, new RoutedEventArgs()); break;
            default: base.OnPreviewKeyDown(e); return;
        }
        e.Handled = true;
    }

    /// <summary>Binds the overlay to the image it sits over (sizes itself to the image's display box).</summary>
    public void Attach(Image image)
    {
        if (_image != null) _image.SizeChanged -= OnImageSizeChanged;
        _image = image;
        image.SizeChanged += OnImageSizeChanged;
        Resize();
    }

    private void OnImageSizeChanged(object sender, SizeChangedEventArgs e) { Resize(); if (_pending is { } p) Layout(p); }

    private void Resize()
    {
        if (_image == null) return;
        Surface.Width = Width = _image.ActualWidth;
        Surface.Height = Height = _image.ActualHeight;
        Canvas.SetLeft(Badge, Math.Max(6, Surface.Width - Badge.ActualWidth - 6));
    }

    /// <summary>True while a placement is expected (armed); the badge shows what is being placed.</summary>
    public bool IsArmed { get; private set; }

    /// <summary>The pending (unconfirmed) crosshair position, or null.</summary>
    public Point? Pending => _pending;

    /// <summary>Arms the placer: shows the prompt badge; the next feed click drops the crosshair.</summary>
    public void Arm(string what)
    {
        IsArmed = true;
        BadgeText.Text = $"Place {what} — click the image, arrows nudge 1 px, Enter / Confirm";
        Visibility = Visibility.Visible;
        Badge.Visibility = Visibility.Visible;
        Badge.UpdateLayout();
        Resize();
        ClearPending();
    }

    /// <summary>Disarms and hides everything (no event).</summary>
    public void Disarm()
    {
        IsArmed = false;
        ClearPending();
        Visibility = Visibility.Collapsed;
    }

    /// <summary>Drops (or moves) the crosshair to an image-relative display point and shows Confirm / Cancel.</summary>
    public void Place(Point p)
    {
        if (!IsArmed) return;
        _pending = p;
        Layout(p);
        Keyboard.Focus(this);   // so the arrow keys nudge this crosshair
    }

    private void Layout(Point p)
    {
        double w = Surface.Width, h = Surface.Height;
        ArmL.X1 = p.X - ArmLength; ArmL.X2 = p.X - Gap; ArmL.Y1 = ArmL.Y2 = p.Y;
        ArmR.X1 = p.X + Gap; ArmR.X2 = p.X + ArmLength; ArmR.Y1 = ArmR.Y2 = p.Y;
        ArmT.Y1 = p.Y - ArmLength; ArmT.Y2 = p.Y - Gap; ArmT.X1 = ArmT.X2 = p.X;
        ArmB.Y1 = p.Y + Gap; ArmB.Y2 = p.Y + ArmLength; ArmB.X1 = ArmB.X2 = p.X;
        Canvas.SetLeft(Ring, p.X - 7); Canvas.SetTop(Ring, p.Y - 7);
        ArmL.Visibility = ArmR.Visibility = ArmT.Visibility = ArmB.Visibility = Ring.Visibility = Visibility.Visible;

        // Buttons below-right of the crosshair, pulled back inside the box near the edges.
        Buttons.Visibility = Visibility.Visible;
        Buttons.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double bw = Buttons.DesiredSize.Width, bh = Buttons.DesiredSize.Height;
        double bx = p.X + ArmLength + 6, by = p.Y + 6;
        if (bx + bw > w - 4) bx = p.X - ArmLength - 6 - bw;
        if (by + bh > h - 4) by = p.Y - 6 - bh;
        Canvas.SetLeft(Buttons, Math.Max(4, bx)); Canvas.SetTop(Buttons, Math.Max(4, by));
    }

    private void ClearPending()
    {
        _pending = null;
        ArmL.Visibility = ArmR.Visibility = ArmT.Visibility = ArmB.Visibility = Ring.Visibility = Visibility.Collapsed;
        Buttons.Visibility = Visibility.Collapsed;
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (_pending is not { } p) return;
        ClearPending();
        Confirmed?.Invoke(p);
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        ClearPending();
        Cancelled?.Invoke();
    }
}
