namespace Horizon.Camera.Models;

public class CameraInfo
{
    public string Id { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public string VendorName { get; set; } = string.Empty;
    public ConnectionMethod ConnectionMethod { get; set; } = ConnectionMethod.Auto;
    public CameraType Type { get; set; } = CameraType.Unknown;
    public int DeviceIndex { get; set; }
    public string DeviceInfo { get; set; } = string.Empty;

    public string GetConnectionInfo() => ConnectionMethod switch
    {
        ConnectionMethod.IPAddress => $"IP: {IpAddress}",
        ConnectionMethod.SerialNumber => $"Serial: {SerialNumber}",
        ConnectionMethod.DeviceIndex => $"Index: {DeviceIndex}",
        _ => "Auto"
    };
}
