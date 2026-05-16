using Horizon.Tec.Models;

namespace Horizon.Tec;

/// <summary>A standalone TEC (thermo-electric cooler) controller.</summary>
public interface ITecDevice
{
    bool Connect();
    bool Disconnect();
    bool IsConnected { get; }
    string DeviceName { get; }
    string ControllerType { get; }

    TecError SetTemperature(double celsius);
    TecError TecOn();
    TecError TecOff();

    TecStatus GetStatus();
}
