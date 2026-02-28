using System.Windows;
using System.Windows.Controls;
using Horizon.Camera;
using Horizon.Camera.Models;

namespace Horizon.App;

public partial class CameraInfoPanel : UserControl
{
    private readonly CameraManager _cameraManager;
    private readonly string _cameraId;
    private bool _suppressAutoEvents;

    /// <summary>Raised after connect/disconnect so the host can refresh trees etc.</summary>
    public event Action? ConnectionChanged;

    /// <summary>Raised when the user clicks Start Feed. Passes the camera ID.</summary>
    public event Action<string>? StartFeedRequested;

    /// <summary>Raised when the user clicks Stop Feed. Passes the camera ID.</summary>
    public event Action<string>? StopFeedRequested;

    public CameraInfoPanel(CameraStatus status, CameraManager cameraManager)
    {
        InitializeComponent();
        _cameraManager = cameraManager;
        _cameraId = status.Id;

        PopulateCameraInfo(status);
        PopulateExposure(status.ExposureSettings);

        ExposureInput.Text = status.ExposureSettings.ExposureTimeUs.ToString("F2");
        GainInput.Text = status.ExposureSettings.Gain.ToString("F2");
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        _cameraManager.ConnectCamera(_cameraId);
        RefreshStatus();
        ConnectionChanged?.Invoke();
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        _cameraManager.DisconnectCamera(_cameraId);
        RefreshStatus();
        ConnectionChanged?.Invoke();
    }

    private void StartFeed_Click(object sender, RoutedEventArgs e)
    {
        StartFeedRequested?.Invoke(_cameraId);
        RefreshStatus();
    }

    private void StopFeed_Click(object sender, RoutedEventArgs e)
    {
        StopFeedRequested?.Invoke(_cameraId);
        RefreshStatus();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(ExposureInput.Text, out var exposureUs))
            return;
        if (!double.TryParse(GainInput.Text, out var gain))
            return;

        var current = _cameraManager.GetExposureSettings(_cameraId);
        // Setting manual values implies disabling the corresponding auto modes
        var updated = current with
        {
            ExposureTimeUs = exposureUs,
            AutoExposure = false,
            Gain = gain,
            AutoGain = false
        };
        _cameraManager.ApplyExposureSettings(_cameraId, updated);

        PopulateExposure(_cameraManager.GetExposureSettings(_cameraId));
    }

    private void AutoExposure_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressAutoEvents) return;

        var current = _cameraManager.GetExposureSettings(_cameraId);
        var updated = current with { AutoExposure = AutoExposureCheck.IsChecked == true };
        _cameraManager.ApplyExposureSettings(_cameraId, updated);

        PopulateExposure(_cameraManager.GetExposureSettings(_cameraId));
    }

    private void AutoGain_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressAutoEvents) return;

        var current = _cameraManager.GetExposureSettings(_cameraId);
        var updated = current with { AutoGain = AutoGainCheck.IsChecked == true };
        _cameraManager.ApplyExposureSettings(_cameraId, updated);

        PopulateExposure(_cameraManager.GetExposureSettings(_cameraId));
    }

    private void RefreshStatus()
    {
        var status = _cameraManager.GetCameraStatus(_cameraId);
        PopulateCameraInfo(status);
        PopulateExposure(status.ExposureSettings);
        ExposureInput.Text = status.ExposureSettings.ExposureTimeUs.ToString("F2");
        GainInput.Text = status.ExposureSettings.Gain.ToString("F2");
    }

    private void PopulateCameraInfo(CameraStatus status)
    {
        ModelText.Text = status.ModelName;
        SerialText.Text = status.SerialNumber;
        IdText.Text = status.Id;
        SdkTypeText.Text = status.TypeName;
        ConnectedText.Text = status.Connected.ToString();
        GrabbingText.Text = status.Grabbing.ToString();
        DeviceInfoText.Text = string.IsNullOrEmpty(status.DeviceInfo) ? "(none)" : status.DeviceInfo;
    }

    private void PopulateExposure(ExposureSettings exp)
    {
        ExposureText.Text = $"{exp.ExposureTimeUs:F2} \u00b5s";
        GainText.Text = $"{exp.Gain:F2}";
        FrameRateText.Text = $"{exp.FrameRate:F2} fps";

        _suppressAutoEvents = true;
        AutoExposureCheck.IsChecked = exp.AutoExposure;
        AutoGainCheck.IsChecked = exp.AutoGain;
        _suppressAutoEvents = false;
    }
}
