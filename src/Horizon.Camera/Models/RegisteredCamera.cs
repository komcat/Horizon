namespace Horizon.Camera.Models;

public class RegisteredCamera
{
    public string Name { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;
    public CameraType SdkType { get; set; } = CameraType.Pylon;
    public string ConnectionType { get; set; } = "USB"; // "USB" or "IP"
    public string IpAddress { get; set; } = string.Empty;
    public int Width { get; set; }              // 0 = auto-detect from hardware
    public int Height { get; set; }             // 0 = auto-detect from hardware
    public double MmPerPixel { get; set; }      // 0 = uncalibrated
    public double Magnification { get; set; } = 1.0;
}
