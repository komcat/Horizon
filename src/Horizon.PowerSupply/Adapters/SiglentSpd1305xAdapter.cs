using System.Diagnostics;
using System.Globalization;
using Horizon.Config;
using Horizon.PowerSupply.Models;
using Serilog;

namespace Horizon.PowerSupply;

/// <summary>
/// Siglent SPD1305X single-channel programmable DC power supply (0..30 V / 0..5 A).
/// Command set per the Siglent SPD-series programming guide, verified on hardware
/// over USBTMC (<c>*IDN?</c> → <c>Siglent Technologies,SPD1305X,…</c>).
///
/// Transport (first match wins), same scheme as <see cref="UnitTUDP3305SAdapter"/>:
///   <see cref="PowerSupplyConfig.VisaResource"/> set → NI-VISA — USBTMC
///     <c>USB0::0xF4EC::0x1410::&lt;serial&gt;::INSTR</c> (Siglent VID 0xF4EC) or VXI-11
///     <c>TCPIP0::&lt;ip&gt;::INSTR</c>.
///   <see cref="PowerSupplyConfig.Ip"/> set → LAN, SCPI on a raw TCP socket at
///     <see cref="PowerSupplyConfig.Port"/> (Siglent default 5025; no VISA runtime needed).
///
/// Quirks handled here:
///   • The SPD firmware drops commands that arrive back-to-back; every write/query is
///     paced to at least <see cref="CommandGapMs"/> after the previous one.
///   • There is no <c>OUTPut?</c> query — output state is bit 4 of the <c>SYSTem:STATus?</c>
///     hex bitmask (bit 0 = CC mode, bit 5 = 4-wire sense).
///
/// Setpoints outside the rated range are refused (<see cref="PowerSupplyError.OutOfRange"/>)
/// before anything is sent. On <see cref="Disconnect"/> the output is forced off
/// (best-effort) before the link is torn down.
/// </summary>
public sealed class SiglentSpd1305xAdapter : IPowerSupplyDevice
{
    private const int NumChannels = 1;
    private const double MaxVolts = 30.0;
    private const double MaxAmps = 5.0;

    /// <summary>Minimum spacing between consecutive SCPI transactions (firmware drops faster traffic).</summary>
    private const int CommandGapMs = 60;

    private const int StatusOutputOnBit = 1 << 4;

    private readonly object _lock = new();
    private readonly PowerSupplyConfig _config;
    private readonly Stopwatch _sinceLastCommand = Stopwatch.StartNew();
    private IScpiTransport? _link;
    private bool _connected;
    private string _lastError = string.Empty;

    public SiglentSpd1305xAdapter(PowerSupplyConfig config) => _config = config;

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
                        _config.Name, link.Address, Query("*IDN?"));
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
            // Safety habit: force the output off before tearing down the link.
            try { Write("OUTPut CH1,OFF"); } catch { }
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
        return Command(channel, $"CH1:VOLTage {volts.ToString("F3", CultureInfo.InvariantCulture)}");
    }

    public PowerSupplyError SetCurrentLimit(int channel, double amps)
    {
        if (!InRange(channel, amps, MaxAmps)) return RangeError(channel, amps, MaxAmps, "A");
        return Command(channel, $"CH1:CURRent {amps.ToString("F3", CultureInfo.InvariantCulture)}");
    }

    public PowerSupplyError OutputOn(int channel) => Command(channel, "OUTPut CH1,ON");

    public PowerSupplyError OutputOff(int channel) => Command(channel, "OUTPut CH1,OFF");

    // Channel validity is checked by Command; an invalid channel passes here so
    // the caller gets InvalidChannel rather than OutOfRange.
    private static bool InRange(int channel, double value, double max)
        => channel < 1 || channel > NumChannels
           || (double.IsFinite(value) && value >= 0.0 && value <= max);

    private PowerSupplyError RangeError(int channel, double value, double max, string unit)
    {
        lock (_lock)
            _lastError = $"CH{channel}: {value.ToString("G6", CultureInfo.InvariantCulture)} {unit} " +
                         $"outside 0..{max} {unit}";
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
                Write(scpi);
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
                channels.Add(new PowerSupplyChannelStatus
                {
                    Channel = 1,
                    Voltage = QueryDouble("MEASure:VOLTage? CH1"),
                    Current = QueryDouble("MEASure:CURRent? CH1"),
                    OutputOn = QueryOutputState(),
                });
                return new PowerSupplyStatus { Connected = true, Channels = channels };
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                return new PowerSupplyStatus { Connected = true, Channels = channels, LastError = ex.Message };
            }
        }
    }

    // ---- paced transport access (caller holds _lock) ----

    private void Pace()
    {
        var remaining = CommandGapMs - (int)_sinceLastCommand.ElapsedMilliseconds;
        if (remaining > 0) Thread.Sleep(remaining);
    }

    private void Write(string command)
    {
        Pace();
        try { _link!.Write(command); }
        finally { _sinceLastCommand.Restart(); }
    }

    private string Query(string query)
    {
        Pace();
        try { return _link!.Query(query); }
        finally { _sinceLastCommand.Restart(); }
    }

    private double QueryDouble(string query)
        => double.TryParse(Query(query), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value : 0.0;

    /// <summary>
    /// <c>SYSTem:STATus?</c> returns a hex bitmask such as <c>0x30</c>; bit 4 is the output state.
    /// </summary>
    private bool QueryOutputState()
    {
        var response = Query("SYSTem:STATus?").Trim();
        if (response.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            response = response[2..];
        if (!int.TryParse(response, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var status))
            throw new FormatException($"Unexpected SYSTem:STATus? response '{response}'");
        return (status & StatusOutputOnBit) != 0;
    }
}
