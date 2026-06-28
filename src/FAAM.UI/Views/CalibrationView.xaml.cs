using System;
using System.Windows;
using System.Windows.Controls;

namespace FAAM.UI.Views;

/// <summary>
/// The Calibration launcher — a tile springboard of calibration procedures. The
/// first (and currently only) tile is Camera → Needle; the rest are reserved.
/// Tiles raise events the shell turns into navigation.
/// </summary>
public partial class CalibrationView : UserControl
{
    /// <summary>Raised when the operator opens the Camera → Needle calibration.</summary>
    public event Action? CameraNeedleRequested;

    public CalibrationView() => InitializeComponent();

    private void OnCameraNeedle(object sender, RoutedEventArgs e) => CameraNeedleRequested?.Invoke();
}
