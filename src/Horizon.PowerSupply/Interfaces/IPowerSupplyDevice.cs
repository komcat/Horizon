using Horizon.PowerSupply.Models;

namespace Horizon.PowerSupply;

/// <summary>A single programmable power-supply instrument. Channels are 1-based.</summary>
public interface IPowerSupplyDevice
{
    bool Connect();
    bool Disconnect();
    bool IsConnected { get; }
    string DeviceName { get; }
    string ControllerType { get; }
    int ChannelCount { get; }

    PowerSupplyError SetVoltage(int channel, double volts);
    PowerSupplyError SetCurrentLimit(int channel, double amps);
    PowerSupplyError OutputOn(int channel);
    PowerSupplyError OutputOff(int channel);

    PowerSupplyStatus GetStatus();
}
