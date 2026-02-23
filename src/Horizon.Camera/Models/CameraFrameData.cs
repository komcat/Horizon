namespace Horizon.Camera.Models;

public class CameraFrameData
{
    public string CameraId { get; set; } = string.Empty;
    public ulong Timestamp { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int Channels { get; set; } = 3; // RGB

    public byte[] ImageData { get; set; } = Array.Empty<byte>();

    // Metadata
    public double ExposureTime { get; set; }
    public double Gain { get; set; }
    public ulong FrameNumber { get; set; }

    public int DataSize => Width * Height * Channels;
    public bool IsValid => Width > 0 && Height > 0 && ImageData.Length > 0;

    public CameraFrameData() { }

    public CameraFrameData(string cameraId, int width, int height)
    {
        CameraId = cameraId;
        Width = width;
        Height = height;
        ImageData = new byte[width * height * 3];
    }
}
