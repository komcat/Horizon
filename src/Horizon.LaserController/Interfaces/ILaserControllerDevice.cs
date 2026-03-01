using Horizon.LaserController.Models;

namespace Horizon.LaserController.Interfaces;

public interface ILaserControllerDevice
{
    bool Connect();
    bool Disconnect();
    bool IsConnected { get; }
    string DeviceName { get; }
    string ControllerType { get; }

    // TEC control
    LaserControllerError SetTemperature(double celsius);
    LaserControllerError ReadTemperature(out double celsius);
    LaserControllerError TECOn();
    LaserControllerError TECOff();
    bool IsTECOn { get; }

    // Laser control
    LaserControllerError SetLaserCurrent(double milliamps);
    LaserControllerError ReadLaserCurrent(out double milliamps);
    LaserControllerError LaserOn();
    LaserControllerError LaserOff();
    bool IsLaserOn { get; }

    LaserControllerStatus GetStatus();
}
