namespace Horizon.Common;

public sealed class CameraCalibration
{
    public double PixelToMmX { get; set; } = 0.01;
    public double PixelToMmY { get; set; } = 0.01;
    public int CalibrationRefWidth { get; set; } = 1920;
    public int CalibrationRefHeight { get; set; } = 1200;
    public double Magnification { get; set; } = 1.0;
    public bool InvertX { get; set; }
    public bool InvertY { get; set; }
}

public sealed class ClickToMoveConfig
{
    public bool Enabled { get; set; } = true;
    public string MotionDevice { get; set; } = "gantry-main";
    public double MoveSpeedMmPerSec { get; set; } = 10.0;

    /// <summary>
    /// Per-camera calibration keyed by camera ID (e.g. serial number).
    /// Falls back to Default if the camera ID is not found.
    /// </summary>
    public Dictionary<string, CameraCalibration> Cameras { get; set; } = new();

    /// <summary>
    /// Default calibration used when no per-camera entry matches.
    /// </summary>
    public CameraCalibration Default { get; set; } = new();

    // Legacy top-level fields — kept for backward compatibility on load.
    // New configs should use Cameras / Default instead.
    public double PixelToMmX { get; set; } = 0.01;
    public double PixelToMmY { get; set; } = 0.01;
    public int CalibrationRefWidth { get; set; } = 1920;
    public int CalibrationRefHeight { get; set; } = 1200;
    public double Magnification { get; set; } = 1.0;
    public bool InvertX { get; set; }
    public bool InvertY { get; set; }

    /// <summary>
    /// Get calibration for a specific camera, falling back to Default, then legacy fields.
    /// </summary>
    public CameraCalibration GetCalibration(string? cameraId)
    {
        if (!string.IsNullOrEmpty(cameraId) &&
            Cameras.TryGetValue(cameraId, out var cal))
            return cal;

        // If Default was explicitly configured (non-default values), use it
        if (Default.PixelToMmX != 0.01 || Default.PixelToMmY != 0.01)
            return Default;

        // Fall back to legacy top-level fields
        return new CameraCalibration
        {
            PixelToMmX = PixelToMmX,
            PixelToMmY = PixelToMmY,
            CalibrationRefWidth = CalibrationRefWidth,
            CalibrationRefHeight = CalibrationRefHeight,
            Magnification = Magnification,
            InvertX = InvertX,
            InvertY = InvertY
        };
    }
}
