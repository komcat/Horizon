using Horizon.Smu.Models;

namespace Horizon.Smu;

/// <summary>A source-measure unit (Keithley 2400 SourceMeter).</summary>
public interface ISmuDevice
{
    bool Connect();
    bool Disconnect();
    bool IsConnected { get; }
    string DeviceName { get; }
    string ControllerType { get; }

    /// <summary>Switches to voltage-source mode and sets the level (V).</summary>
    SmuError SetSourceVoltage(double volts);

    /// <summary>Switches to current-source mode and sets the level (A).</summary>
    SmuError SetSourceCurrent(double amps);

    /// <summary>Sets the compliance limit — current limit when sourcing V, voltage limit when sourcing I.</summary>
    SmuError SetCompliance(double value);

    SmuError OutputOn();
    SmuError OutputOff();

    SmuStatus GetStatus();
}
