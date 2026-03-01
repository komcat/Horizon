using Horizon.CurrentMeasurement.Models;

namespace Horizon.CurrentMeasurement.Interfaces;

public interface ICurrentMeasurementDevice
{
    bool Connect();
    bool Disconnect();
    bool IsConnected { get; }

    CurrentMeasurementError ReadCurrent(int channel, out double current);
    CurrentMeasurementError ReadAllChannels(out double[] currents);

    CurrentMeasurementError GetIdentity(out string identity);
    CurrentMeasurementError Reset();

    CurrentMeasurementError SetVoltage(double volts);
    CurrentMeasurementError GetVoltage(out double volts);
    CurrentMeasurementError SetVoltageEnabled(bool enabled);

    CurrentMeasurementError ConfigureFastMode();
    CurrentMeasurementError ConfigureDefaultMode();

    string DeviceName { get; }
    int ChannelCount { get; }
    List<int> EnabledChannels { get; }
}
