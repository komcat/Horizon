using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Horizon.Camera;
using Horizon.Core;
using Horizon.CurrentMeasurement;

namespace Horizon.App;

public partial class LiveFeedPanel : UserControl
{
    private readonly string _cameraId;
    private readonly CameraManager _cameraManager;
    private MeasurementHub? _hub;
    private CurrentMeasurementManager? _cmeasManager;
    private LiveVideoSubscriber? _subscriber;
    private DispatcherTimer? _timer;
    private WriteableBitmap? _writeableBitmap;
    private byte[]? _rgbBuffer;
    private bool _chartVisible;

    public string CameraId => _cameraId;

    public LiveFeedPanel(string cameraId, CameraManager cameraManager)
    {
        _cameraId = cameraId;
        _cameraManager = cameraManager;
        InitializeComponent();
    }

    public void AttachCurrentChart(MeasurementHub hub, List<string> channelKeys,
        CurrentMeasurementManager? cmeasManager = null)
    {
        _hub = hub;
        _cmeasManager = cmeasManager;

        OverlayChart.Attach(hub, channelKeys);
        ChartToggleButton.Visibility = Visibility.Visible;

        // Populate measurement device combo
        if (_cmeasManager != null)
        {
            var names = _cmeasManager.GetAllDeviceNames();
            MeasCombo.ItemsSource = names;
            MeasCombo.Visibility = Visibility.Visible;
            if (names.Count > 0)
                MeasCombo.SelectedIndex = 0;
        }
    }

    private void MeasCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MeasCombo.SelectedItem is not string deviceName || _cmeasManager == null || _hub == null)
            return;

        var device = _cmeasManager.GetDevice(deviceName);
        if (device == null) return;

        var keys = new List<string>(device.ChannelCount);
        foreach (var ch in device.EnabledChannels)
            keys.Add($"{deviceName}.CH{ch}");

        OverlayChart.Detach();
        OverlayChart.Attach(_hub, keys);
        if (_chartVisible)
            OverlayChart.StartRendering();
    }

    private void ToggleChart_Click(object sender, RoutedEventArgs e)
    {
        _chartVisible = !_chartVisible;
        if (_chartVisible)
        {
            ChartOverlayBorder.Visibility = Visibility.Visible;
            OverlayChart.StartRendering();
        }
        else
        {
            ChartOverlayBorder.Visibility = Visibility.Collapsed;
            OverlayChart.StopRendering();
        }
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
        OverlayChart.StopRendering();

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
