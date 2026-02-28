using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Horizon.Camera;

namespace Horizon.App;

public partial class LiveFeedPanel : UserControl
{
    private readonly string _cameraId;
    private readonly CameraManager _cameraManager;
    private LiveVideoSubscriber? _subscriber;
    private DispatcherTimer? _timer;
    private WriteableBitmap? _writeableBitmap;
    private byte[]? _rgbBuffer;

    public string CameraId => _cameraId;

    public LiveFeedPanel(string cameraId, CameraManager cameraManager)
    {
        _cameraId = cameraId;
        _cameraManager = cameraManager;
        InitializeComponent();
    }

    public bool Start()
    {
        var result = _cameraManager.StartGrabbingWithBroadcast(_cameraId);
        if (!result)
            return false;

        _subscriber = new LiveVideoSubscriber(_cameraId);
        _cameraManager.SubscribeToFrames(_subscriber);
        _subscriber.InitializeStatusFromManager(_cameraManager);

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _timer.Tick += Timer_Tick;
        _timer.Start();

        FeedStatus.Text = $"Live: {_cameraId}";
        return true;
    }

    public void Stop()
    {
        if (_timer != null)
        {
            _timer.Stop();
            _timer.Tick -= Timer_Tick;
            _timer = null;
        }

        if (_subscriber != null)
        {
            _cameraManager.UnsubscribeFromFrames(_subscriber.SubscriberId);
            _subscriber = null;
        }

        _cameraManager.StopGrabbing(_cameraId);

        _writeableBitmap = null;
        _rgbBuffer = null;
        FeedStatus.Text = "Feed stopped";
    }

    private unsafe void Timer_Tick(object? sender, EventArgs e)
    {
        if (_subscriber == null || !_subscriber.HasNewFrame)
            return;

        _rgbBuffer ??= new byte[4096 * 4096 * 3];

        if (!_subscriber.GetLatestFrameInto(_rgbBuffer, out int width, out int height, out int channels))
            return;

        if (_writeableBitmap == null ||
            _writeableBitmap.PixelWidth != width ||
            _writeableBitmap.PixelHeight != height)
        {
            _writeableBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
            FeedImage.Source = _writeableBitmap;
        }

        int pixelCount = width * height;

        _writeableBitmap.Lock();
        try
        {
            byte* dst = (byte*)_writeableBitmap.BackBuffer;
            fixed (byte* src = _rgbBuffer)
            {
                int srcIdx = 0;
                int dstIdx = 0;
                for (int i = 0; i < pixelCount; i++)
                {
                    dst[dstIdx + 0] = src[srcIdx + 2]; // B <- R
                    dst[dstIdx + 1] = src[srcIdx + 1]; // G <- G
                    dst[dstIdx + 2] = src[srcIdx + 0]; // R <- B
                    dst[dstIdx + 3] = 255;              // A
                    srcIdx += channels;
                    dstIdx += 4;
                }
            }
            _writeableBitmap.AddDirtyRect(new Int32Rect(0, 0, width, height));
        }
        finally
        {
            _writeableBitmap.Unlock();
        }
    }
}
