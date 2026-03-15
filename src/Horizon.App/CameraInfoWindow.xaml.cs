using System.Windows;
using System.Windows.Controls;
using Horizon.Camera;
using Horizon.Camera.Models;
using Serilog;

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

    /// <summary>Raised when sensor config is saved. Passes (cameraId, updatedConfig) so the host can persist to disk.</summary>
    public event Action<string, RegisteredCamera>? SensorConfigSaved;

    public CameraInfoPanel(CameraStatus status, CameraManager cameraManager)
    {
        InitializeComponent();
        _cameraManager = cameraManager;
        _cameraId = status.Id;

        PopulateCameraInfo(status);
        PopulateSensorConfig(status);
        PopulateExposure(status.ExposureSettings);

        ExposureInput.Text = status.ExposureSettings.ExposureTimeUs.ToString("F2");
        GainInput.Text = status.ExposureSettings.Gain.ToString("F2");

        MmPerPixelInput.Text = status.MmPerPixel > 0 ? status.MmPerPixel.ToString("G6") : "0";
        MagnificationInput.Text = status.Magnification.ToString("G6");
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        var ok = _cameraManager.ConnectCamera(_cameraId);
        Log.Information("Camera '{CameraId}': Connect {Result}", _cameraId, ok ? "OK" : "FAILED");
        RefreshStatus();
        ConnectionChanged?.Invoke();
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        var ok = _cameraManager.DisconnectCamera(_cameraId);
        Log.Information("Camera '{CameraId}': Disconnect {Result}", _cameraId, ok ? "OK" : "FAILED");
        RefreshStatus();
        ConnectionChanged?.Invoke();
    }

    private void StartFeed_Click(object sender, RoutedEventArgs e)
    {
        Log.Information("Camera '{CameraId}': Start Feed requested", _cameraId);
        StartFeedRequested?.Invoke(_cameraId);
        RefreshStatus();
    }

    private void StopFeed_Click(object sender, RoutedEventArgs e)
    {
        Log.Information("Camera '{CameraId}': Stop Feed requested", _cameraId);
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
        Log.Information("Camera '{CameraId}': Exposure set to {Exposure} us, Gain {Gain}", _cameraId, exposureUs, gain);

        PopulateExposure(_cameraManager.GetExposureSettings(_cameraId));
    }

    private void AutoExposure_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressAutoEvents) return;

        var enabled = AutoExposureCheck.IsChecked == true;
        var current = _cameraManager.GetExposureSettings(_cameraId);
        var updated = current with { AutoExposure = enabled };
        _cameraManager.ApplyExposureSettings(_cameraId, updated);
        Log.Information("Camera '{CameraId}': AutoExposure {State}", _cameraId, enabled ? "ON" : "OFF");

        PopulateExposure(_cameraManager.GetExposureSettings(_cameraId));
    }

    private void AutoGain_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressAutoEvents) return;

        var enabled = AutoGainCheck.IsChecked == true;
        var current = _cameraManager.GetExposureSettings(_cameraId);
        var updated = current with { AutoGain = enabled };
        _cameraManager.ApplyExposureSettings(_cameraId, updated);
        Log.Information("Camera '{CameraId}': AutoGain {State}", _cameraId, enabled ? "ON" : "OFF");

        PopulateExposure(_cameraManager.GetExposureSettings(_cameraId));
    }

    private void RefreshStatus()
    {
        var status = _cameraManager.GetCameraStatus(_cameraId);
        PopulateCameraInfo(status);
        PopulateSensorConfig(status);
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

    private void SaveSensor_Click(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(MmPerPixelInput.Text, out var mmPerPixel))
            return;
        if (!double.TryParse(MagnificationInput.Text, out var magnification) || magnification <= 0)
            magnification = 1.0;

        // Get current config or create new one
        var config = _cameraManager.GetCameraConfig(_cameraId) ?? new RegisteredCamera();

        // Use live image size as configured resolution if not already set
        var status = _cameraManager.GetCameraStatus(_cameraId);
        if (config.Width == 0 && status.LiveImageWidth > 0)
            config.Width = status.LiveImageWidth;
        if (config.Height == 0 && status.LiveImageHeight > 0)
            config.Height = status.LiveImageHeight;

        config.MmPerPixel = mmPerPixel;
        config.Magnification = magnification;

        // Update in-memory config (fires CameraConfigChanged event for other classes)
        _cameraManager.UpdateCameraConfig(_cameraId, config);

        // Notify host to persist to disk
        SensorConfigSaved?.Invoke(_cameraId, config);

        Log.Information("Camera '{CameraId}': Sensor config saved — mm/px={MmPx}, Mag={Mag}, W={Width}, H={Height}",
            _cameraId, mmPerPixel, magnification, config.Width, config.Height);

        // Refresh display
        RefreshStatus();
    }

    private void PopulateSensorConfig(CameraStatus status)
    {
        LiveImageSizeText.Text = (status.LiveImageWidth > 0 && status.LiveImageHeight > 0)
            ? $"{status.LiveImageWidth} x {status.LiveImageHeight}"
            : "(no frame)";
        ResolutionText.Text = (status.ConfiguredWidth > 0 && status.ConfiguredHeight > 0)
            ? $"{status.ConfiguredWidth} x {status.ConfiguredHeight}"
            : "Auto";
        MmPerPixelText.Text = status.MmPerPixel > 0
            ? $"{status.MmPerPixel:G4} mm/px  (Mag: {status.Magnification:G4}x)"
            : "Uncalibrated";
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
