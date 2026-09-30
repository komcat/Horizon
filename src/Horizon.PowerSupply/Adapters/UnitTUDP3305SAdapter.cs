using System.Globalization;
using Horizon.Config;
using Horizon.PowerSupply.Models;
using Serilog;

namespace Horizon.PowerSupply;

/// <summary>
/// UNI-T UDP3305S-E 3-channel programmable DC power supply.
/// SCPI command set matches the bobcat-engine <c>UnitTUDP3305S</c> reference
/// driver, verified on hardware via <c>docs/UDP3305S/test_channels.py</c>.
///
/// Channels (datasheet):
///   CH1, CH2 : 0..32 V / 0..5 A, independent
///   CH3      : 0..6  V / 0..3 A
/// Setpoints outside a channel's range are refused (<see cref="PowerSupplyError.OutOfRange"/>)
/// before anything is sent.
///
/// Transport (first match wins):
///   <see cref="PowerSupplyConfig.VisaResource"/> set → NI-VISA — USBTMC
///     <c>USB0::0x0483::0x5740::&lt;serial&gt;::INSTR</c> (UNI-T enumerates with the
///     STMicro USB VID 0x0483) or VXI-11 <c>TCPIP0::&lt;ip&gt;::INSTR</c>.
///   <see cref="PowerSupplyConfig.Ip"/> set → LAN, SCPI on a raw TCP socket at
///     <see cref="PowerSupplyConfig.Port"/> (no VISA runtime needed).
///
/// On <see cref="Disconnect"/> every output is forced off (best-effort) before
/// the link is torn down.
/// </summary>
public sealed class UnitTUDP3305SAdapter : IPowerSupplyDevice
{
    private const int NumChannels = 3;

    // Index = channel - 1.
    private static readonly double[] MaxVolts = { 32.0, 32.0, 6.0 };
    private static readonly double[] MaxAmps = { 5.0, 5.0, 3.0 };

    private readonly object _lock = new();
    private readonly PowerSupplyConfig _config;
    private IScpiTransport? _link;
    private bool _connected;
    private string _lastError = string.Empty;

    public UnitTUDP3305SAdapter(PowerSupplyConfig config) => _config = config;

    public string DeviceName => _config.Name;
    public string ControllerType => _config.Type;
    public int ChannelCount => NumChannels;
    public bool IsConnected { get { lock (_lock) return _connected; } }

    public bool Connect()
    {
        lock (_lock)
        {
            if (_connected) return true;

            var link = CreateTransport();
            if (link == null)
            {
                _lastError = "Neither a VISA resource nor an IP address is configured";
                Log.Error("[PSU] {Device}: {Err}", _config.Name, _lastError);
                return false;
            }

            try
            {
                link.Open();
                _link = link;
                _connected = true;
                _lastError = string.Empty;

                // Best-effort *IDN? for the log; non-fatal.
                try
                {
                    Log.Information("[PSU] {Device}: connected ({Addr}) IDN={Idn}",
                        _config.Name, link.Address, link.Query("*IDN?"));
                }
                catch
                {
                    Log.Information("[PSU] {Device}: connected ({Addr})", _config.Name, link.Address);
                }
                return true;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                Log.Error(ex, "[PSU] {Device}: connect to {Addr} failed", _config.Name, link.Address);
                link.Dispose();
                Cleanup();
                return false;
            }
        }
    }

    private IScpiTransport? CreateTransport()
    {
        if (!string.IsNullOrWhiteSpace(_config.VisaResource))
            return new VisaScpiTransport(_config.VisaResource.Trim());
        if (!string.IsNullOrWhiteSpace(_config.Ip))
            return new TcpScpiTransport(_config.Ip.Trim(), _config.Port);
        return null;
    }

    public bool Disconnect()
    {
        lock (_lock)
        {
            if (!_connected) return true;
            // Safety habit: force every output off before tearing down the link.
            for (int ch = 1; ch <= NumChannels; ch++)
            {
                try { _link?.Write($"OUTPut CH{ch},OFF"); } catch { }
            }
            Cleanup();
            Log.Information("[PSU] {Device}: disconnected", _config.Name);
            return true;
        }
    }

    private void Cleanup()
    {
        try { _link?.Dispose(); } catch { }
        _link = null;
        _connected = false;
    }

    public PowerSupplyError SetVoltage(int channel, double volts)
    {
        if (!InRange(channel, volts, MaxVolts)) return RangeError(channel, volts, MaxVolts, "V");
        return Command(channel, $"SOURce{channel}:VOLTage {volts.ToString("G6", CultureInfo.InvariantCulture)}");
    }

    public PowerSupplyError SetCurrentLimit(int channel, double amps)
    {
        if (!InRange(channel, amps, MaxAmps)) return RangeError(channel, amps, MaxAmps, "A");
        return Command(channel, $"SOURce{channel}:CURRent {amps.ToString("G6", CultureInfo.InvariantCulture)}");
    }

    public PowerSupplyError OutputOn(int channel) => Command(channel, $"OUTPut CH{channel},ON");

    public PowerSupplyError OutputOff(int channel) => Command(channel, $"OUTPut CH{channel},OFF");

    // Channel validity is checked by Command; an invalid channel passes here so
    // the caller gets InvalidChannel rather than OutOfRange.
    private static bool InRange(int channel, double value, double[] max)
        => channel < 1 || channel > NumChannels
           || (double.IsFinite(value) && value >= 0.0 && value <= max[channel - 1]);

    private PowerSupplyError RangeError(int channel, double value, double[] max, string unit)
    {
        lock (_lock)
            _lastError = $"CH{channel}: {value.ToString("G6", CultureInfo.InvariantCulture)} {unit} " +
                         $"outside 0..{max[channel - 1]} {unit}";
        Log.Warning("[PSU] {Device}: {Err}", _config.Name, _lastError);
        return PowerSupplyError.OutOfRange;
    }

    private PowerSupplyError Command(int channel, string scpi)
    {
        lock (_lock)
        {
            if (!_connected || _link == null) return PowerSupplyError.Disconnected;
            if (channel < 1 || channel > NumChannels) return PowerSupplyError.InvalidChannel;
            try
            {
                _link.Write(scpi);
                return PowerSupplyError.Success;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                Log.Error(ex, "[PSU] {Device}: command '{Scpi}' failed", _config.Name, scpi);
                return PowerSupplyError.CommunicationError;
            }
        }
    }

    public PowerSupplyStatus GetStatus()
    {
        lock (_lock)
        {
            if (!_connected || _link == null)
                return new PowerSupplyStatus { Connected = false, LastError = _lastError };

            var channels = new List<PowerSupplyChannelStatus>();
            try
            {
                for (int ch = 1; ch <= NumChannels; ch++)
                {
                    channels.Add(new PowerSupplyChannelStatus
                    {
                        Channel = ch,
                        Voltage = QueryDouble($"MEASure:VOLTage? CH{ch}"),
                        Current = QueryDouble($"MEASure:CURRent? CH{ch}"),
                        OutputOn = QueryOutputState(ch),
                    });
                }
                return new PowerSupplyStatus { Connected = true, Channels = channels };
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                return new PowerSupplyStatus { Connected = true, Channels = channels, LastError = ex.Message };
            }
        }
    }

    private double QueryDouble(string query)
        => double.TryParse(_link!.Query(query), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value : 0.0;

    // UDP3305S returns "ON"/"OFF" as a string; defensively also accept "1"/"0"
    // in case a firmware variant returns numeric form (per HANDOFF section 2).
    private bool QueryOutputState(int channel)
    {
        var response = _link!.Query($"OUTPut? CH{channel}").ToUpperInvariant();
        return response == "ON" || response == "1";
    }
}
