namespace Horizon.Camera.Models;

public class CameraStatus
{
    public string Id { get; set; } = string.Empty;
    public CameraType Type { get; set; } = CameraType.Unknown;
    public bool Connected { get; set; }
    public bool Grabbing { get; set; }
    public bool DeviceRemoved { get; set; }
    public string ModelName { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;
    public string DeviceInfo { get; set; } = string.Empty;
    public ExposureSettings ExposureSettings { get; set; } = new();
    public int ConfiguredWidth { get; set; }
    public int ConfiguredHeight { get; set; }
    public double MmPerPixel { get; set; }
    public double Magnification { get; set; } = 1.0;
    public int LiveImageWidth { get; set; }
    public int LiveImageHeight { get; set; }

    public string TypeName => Type.ToString();
}
