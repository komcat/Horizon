using System.Globalization;
using Horizon.Config;
using Horizon.PowerSupply.Models;
using Serilog;

namespace Horizon.PowerSupply;

/// <summary>
/// UNI-T UDP3305S-E 3-channel programmable DC power supply over USB/USBTMC/VISA.
/// SCPI command set matches the bobcat-engine <c>UnitTUDP3305S</c> reference
/// driver, verified on hardware via <c>docs/UDP3305S/test_channels.py</c>.
///
/// Channels (datasheet):
///   CH1, CH2 : 0..32 V / 0..5 A, independent
///   CH3      : 0..6  V / 0..3 A
///
/// VISA resource format: <c>USB0::0x0483::0x5740::&lt;serial&gt;::INSTR</c>
/// (UNI-T enumerates with the STMicro USB VID 0x0483, not a UNI-T VID).
///
/// On <see cref="Disconnect"/> every output is forced off (best-effort) before
/// the VISA session is torn down.
/// </summary>
public sealed class UnitTUDP3305SAdapter : IPowerSupplyDevice
{
    private const int NumChannels = 3;

    private readonly object _lock = new();
    private readonly PowerSupplyConfig _config;
    private int _rmSession;
    private int _instrSession;
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
            if (string.IsNullOrWhiteSpace(_config.VisaResource))
            {
                _lastError = "VISA resource string is empty";
                Log.Error("[PSU] {Device}: {Err}", _config.Name, _lastError);
                return false;
            }
            try
            {
                var status = VisaNative.OpenDefaultRM(out _rmSession);
                if (status < VisaNative.VI_SUCCESS)
                {
                    _lastError = $"OpenDefaultRM failed: 0x{status:X8}";
                    return false;
                }

                status = VisaNative.Open(_rmSession, _config.VisaResource, 0, 2000, out _instrSession);
                if (status < VisaNative.VI_SUCCESS)
                {
                    _lastError = $"Open('{_config.VisaResource}') failed: 0x{status:X8}";
                    VisaNative.Close(_rmSession);
                    _rmSession = 0;
                    return false;
                }

                VisaNative.SetAttribute(_instrSession, VisaNative.VI_ATTR_TMO_VALUE, 5000);
                VisaNative.SetAttribute(_instrSession, VisaNative.VI_ATTR_TERMCHAR_EN, 1);
                VisaNative.SetAttribute(_instrSession, VisaNative.VI_ATTR_TERMCHAR, '\n');
                VisaNative.Clear(_instrSession);
                // USBTMC devices need a moment to settle after the session opens.
                Thread.Sleep(200);
                _connected = true;
                _lastError = string.Empty;

                // Best-effort *IDN? for the log; non-fatal.
                try
                {
                    VisaNative.WriteString(_instrSession, "*IDN?");
                    VisaNative.ReadString(_instrSession, out var idn);
                    Log.Information("[PSU] {Device}: connected ({Visa}) IDN={Idn}",
                        _config.Name, _config.VisaResource, idn);
                }
                catch
                {
                    Log.Information("[PSU] {Device}: connected ({Visa})",
                        _config.Name, _config.VisaResource);
                }
                return true;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                Log.Error(ex, "[PSU] {Device}: connect failed", _config.Name);
                Cleanup();
                return false;
            }
        }
    }

    public bool Disconnect()
    {
        lock (_lock)
        {
            if (!_connected) return true;
            // Safety habit: force every output off before tearing down the session.
            for (int ch = 1; ch <= NumChannels; ch++)
            {
                try { VisaNative.WriteString(_instrSession, $"OUTPut CH{ch},OFF"); } catch { }
            }
            Cleanup();
            Log.Information("[PSU] {Device}: disconnected", _config.Name);
            return true;
        }
    }

    private void Cleanup()
    {
        try { if (_instrSession != 0) VisaNative.Close(_instrSession); } catch { }
        try { if (_rmSession != 0) VisaNative.Close(_rmSession); } catch { }
        _instrSession = 0;
        _rmSession = 0;
        _connected = false;
    }

    public PowerSupplyError SetVoltage(int channel, double volts)
        => Command(channel, $"SOURce{channel}:VOLTage {volts.ToString("G6", CultureInfo.InvariantCulture)}");

    public PowerSupplyError SetCurrentLimit(int channel, double amps)
        => Command(channel, $"SOURce{channel}:CURRent {amps.ToString("G6", CultureInfo.InvariantCulture)}");

    public PowerSupplyError OutputOn(int channel) => Command(channel, $"OUTPut CH{channel},ON");

    public PowerSupplyError OutputOff(int channel) => Command(channel, $"OUTPut CH{channel},OFF");

    private PowerSupplyError Command(int channel, string scpi)
    {
        lock (_lock)
        {
            if (!_connected) return PowerSupplyError.Disconnected;
            if (channel < 1 || channel > NumChannels) return PowerSupplyError.InvalidChannel;
            try
            {
                WriteCommand(scpi);
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
            if (!_connected)
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

    private void WriteCommand(string command)
    {
        var status = VisaNative.WriteString(_instrSession, command);
        if (status < VisaNative.VI_SUCCESS)
            throw new InvalidOperationException($"VISA write failed: 0x{status:X8}");
    }

    private string QueryString(string query)
    {
        WriteCommand(query);
        var status = VisaNative.ReadString(_instrSession, out var response);
        if (status < VisaNative.VI_SUCCESS)
            throw new InvalidOperationException($"VISA read failed: 0x{status:X8}");
        return response;
    }

    private double QueryDouble(string query)
        => double.TryParse(QueryString(query), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value : 0.0;

    // UDP3305S returns "ON"/"OFF" as a string; defensively also accept "1"/"0"
    // in case a firmware variant returns numeric form (per HANDOFF section 2).
    private bool QueryOutputState(int channel)
    {
        var response = QueryString($"OUTPut? CH{channel}").Trim().ToUpperInvariant();
        return response == "ON" || response == "1";
    }
}
