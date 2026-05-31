using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using FAAM.UI.ViewModels.Sections;
using Horizon.Engine;

namespace FAAM.UI;

/// <summary>
/// Pop-out live camera view — bound to the same <c>CameraSectionViewModel</c> as
/// the Machine-page Camera section (shares the one frame pipeline). Adds the same
/// MoveClick behaviour as the Run page: a center crosshair, a short cursor
/// crosshair on hover, and click-to-jog the configured stage by the real-world
/// offset from the crosshair centre to the click point.
/// </summary>
public partial class DisplayWindow : Window
{
    public DisplayWindow() => InitializeComponent();

    private EngineHost? Host => (DataContext as CameraSectionViewModel)?.Host;

    private void OnFeedMouseMove(object sender, MouseEventArgs e) => MoveCursor(e);
    private void OnFeedMouseLeave(object sender, MouseEventArgs e) => MainCursor.Visibility = Visibility.Collapsed;
    private void OnMoveDisarmed(object sender, RoutedEventArgs e) => MainCursor.Visibility = Visibility.Collapsed;

    /// <summary>Centres the 20×20 cursor crosshair on the mouse (only while armed).</summary>
    private void MoveCursor(MouseEventArgs e)
    {
        if (MoveToggle.IsChecked != true) { MainCursor.Visibility = Visibility.Collapsed; return; }
        var p = e.GetPosition(FeedHost);
        MainCursor.Margin = new Thickness(p.X - MainCursor.Width / 2, p.Y - MainCursor.Height / 2, 0, 0);
        MainCursor.Visibility = Visibility.Visible;
    }

    private async void OnFeedClick(object sender, MouseButtonEventArgs e)
    {
        if (MoveToggle.IsChecked != true) return;
        if (MainImage.Source is not BitmapSource bmp) return;

        var p = e.GetPosition(MainImage);
        // Ignore clicks in the letterbox margin (outside the rendered image).
        if (p.X < 0 || p.Y < 0 || p.X > MainImage.ActualWidth || p.Y > MainImage.ActualHeight) return;

        await DoMoveClickAsync(p, MainImage.ActualWidth, MainImage.ActualHeight, bmp.PixelWidth, bmp.PixelHeight);
    }

    /// <summary>Measures the click offset and jogs the configured device — shared with the Run page via <see cref="MoveClick"/>.</summary>
    private async Task DoMoveClickAsync(Point p, double availW, double availH, int srcW, int srcH)
    {
        var host = Host;
        if (host == null) return;

        var m = MoveClick.Measure(host, p, availW, availH, srcW, srcH);
        if (m is not { } meas) return;

        MoveReadout.Text = meas.Readout;
        MoveReadoutBox.Visibility = Visibility.Visible;
        Log(host, $"MoveClick (camera popout): {meas.Readout}");

        await MoveClick.JogAsync(host, meas, msg => Log(host, msg));
    }

    private static void Log(EngineHost host, string message)
        => host.Log.Add(new LogEntry(LogLevel.Info, DateTimeOffset.Now, "Camera", message));
}
