using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Horizon.Common;
using Horizon.Workflow;

namespace FAAM.UI.Views;

/// <summary>
/// The Run page's still-picture box (it took the SiPhOG chart's place beside the
/// Zoom 2× inset). A process shows the operator what the machine is looking at
/// right now by snapping a frame and pushing the file here — see the
/// <c>capture_snapshot.moon</c> script run before the DUT-serial prompt.
///
/// This is FAAM's implementation of the Layer 2 <see cref="IImageDisplaySink"/>
/// seam, so <c>machine.ui.showImage(path [, caption])</c> lands in this panel.
/// <see cref="Show"/> is called from a script worker thread, so every touch of the
/// visual tree is marshalled onto the UI dispatcher. The bitmap is loaded with
/// <see cref="BitmapCacheOption.OnLoad"/> and frozen: the file handle is released
/// before this returns (the capture directory is recycled) and the image can be
/// handed to the UI thread from wherever it was decoded.
/// </summary>
public partial class SnapshotView : UserControl, IImageDisplaySink
{
    private readonly Dispatcher _dispatcher;

    public SnapshotView()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        InitializeComponent();
    }

    public Result Show(string imagePath, string? caption)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
            return Result.Fail(ErrorCode.InvalidParameter, "Snapshot: no image path given.");
        if (!File.Exists(imagePath))
            return Result.Fail(ErrorCode.FileNotFound, $"Snapshot: '{imagePath}' not found.");

        BitmapImage image;
        try
        {
            image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;         // decode now, then close the file
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.UriSource = new Uri(Path.GetFullPath(imagePath));
            image.EndInit();
            image.Freeze();                                       // cross-thread handoff to the UI
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or UriFormatException)
        {
            return Result.Fail(ErrorCode.OperationFailed, $"Snapshot: cannot read '{imagePath}' — {ex.Message}");
        }

        var stamp = DateTime.Now.ToString("HH:mm:ss");
        Ui(() =>
        {
            Shot.Source = image;
            Empty.Visibility = Visibility.Collapsed;
            CaptionText.Text = caption ?? string.Empty;
            StampText.Text = stamp;
            CaptionBand.Visibility = Visibility.Visible;
        });
        return Result.Ok();
    }

    public Result Clear()
    {
        Ui(() =>
        {
            Shot.Source = null;
            Empty.Visibility = Visibility.Visible;
            CaptionBand.Visibility = Visibility.Collapsed;
        });
        return Result.Ok();
    }

    private void Ui(Action action)
    {
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(action);
    }
}
