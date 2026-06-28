using Horizon.Common;

namespace Horizon.Workflow.Calibration;

/// <summary>
/// Loads / saves the machine-global camera→needle calibration
/// (<c>%APPDATA%\Horizon\config\camera_needle_calibration.json</c>), mirroring
/// <c>DeviceLinkStore</c> / <c>AppSettingsStore</c>. The mount is product-independent,
/// so this is not a per-product store.
/// </summary>
public sealed class CameraNeedleCalibrationStore
{
    private const string FileName = "camera_needle_calibration";

    private readonly string _dir;
    private CameraNeedleCalibrationData _current;

    public CameraNeedleCalibrationStore(string? configDir = null)
    {
        _dir = configDir ?? ConfigLoader.GetConfigDirectory();
        _current = ConfigLoader.LoadJson<CameraNeedleCalibrationData>(_dir, FileName)
                   ?? new CameraNeedleCalibrationData();
    }

    /// <summary>The current calibration (config + last results). Never null.</summary>
    public CameraNeedleCalibrationData Current => _current;

    /// <summary>Replaces and persists the calibration.</summary>
    public void Update(CameraNeedleCalibrationData data)
    {
        _current = data;
        ConfigLoader.SaveJson(_dir, FileName, data);
    }
}
