using System.Globalization;
using Horizon.Config;
using Horizon.Smu.Models;
using Serilog;

namespace Horizon.Smu;

/// <summary>
/// Keithley 2400 SourceMeter over VISA (GPIB). SCPI per the bobcat-engine
/// reference driver: voltage- or current-source with the complementary
/// measurement, compliance, NPLC, and auto/fixed range.
/// </summary>
public sealed class Keithley2400Adapter : ISmuDevice
{
    private readonly object _lock = new();
    private readonly SmuDeviceConfig _config;
    private int _rmSession;
    private int _instrSession;
    private bool _connected;
    private SmuSourceMode _mode = SmuSourceMode.Voltage;
    private bool _outputOn;
    private string _lastError = string.Empty;

    public Keithley2400Adapter(SmuDeviceConfig config) => _config = config;

    public string DeviceName => _config.Name;
    public string ControllerType => _config.Type;
    public IReadOnlyList<int> Channels { get; } = new[] { 1 };   // 2400 is single-channel
    public bool IsConnected { get { lock (_lock) return _connected; } }

    public bool Connect()
    {
        lock (_lock)
        {
            if (_connected) return true;
            try
            {
                var status = VisaNative.OpenDefaultRM(out _rmSession);
                if (status < VisaNative.VI_SUCCESS)
                {
                    _lastError = $"OpenDefaultRM failed: 0x{status:X8}";
                    return false;
                }

                status = VisaNative.Open(_rmSession, _config.VisaResource, 0, 5000, out _instrSession);
                if (status < VisaNative.VI_SUCCESS)
                {
                    _lastError = $"Open failed: 0x{status:X8}";
                    VisaNative.Close(_rmSession);
                    return false;
                }

                VisaNative.SetAttribute(_instrSession, VisaNative.VI_ATTR_TMO_VALUE, 5000);
                VisaNative.Clear(_instrSession);
                _connected = true;

                // Default to voltage-source mode and apply the configured setup.
                WriteCommand("*RST");
                WriteCommand("*CLS");
                WriteCommand(":SOUR:FUNC VOLT");
                WriteCommand(":SOUR:VOLT:MODE FIXED");
                WriteCommand(":SENS:FUNC \"CURR\"");
                WriteCommand($":SENS:CURR:NPLC {Fmt(_config.MeasurementNplc)}");
                WriteCommand(_config.AutoRange
                    ? ":SENS:CURR:RANG:AUTO ON"
                    : $":SENS:CURR:RANG {Fmt(_config.FixedRange)}");
                if (_config.DefaultCompliance > 0)
                    WriteCommand($":SENS:CURR:PROT {Fmt(_config.DefaultCompliance)}");
                WriteCommand(":FORM:ELEM VOLT,CURR");
                WriteCommand($":SOUR:VOLT:LEV {Fmt(_config.DefaultVoltage)}");
                _mode = SmuSourceMode.Voltage;
                _outputOn = false;

                _lastError = string.Empty;
                Log.Information("[SMU] {Device}: connected ({Visa})", _config.Name, _config.VisaResource);
                return true;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                Log.Error(ex, "[SMU] {Device}: connect failed", _config.Name);
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
            Cleanup();
            Log.Information("[SMU] {Device}: disconnected", _config.Name);
            return true;
        }
    }

    private void Cleanup()
    {
        try { VisaNative.Close(_instrSession); } catch { }
        try { VisaNative.Close(_rmSession); } catch { }
        _instrSession = 0;
        _rmSession = 0;
        _connected = false;
    }

    public SmuError SetSourceVoltage(double volts)
        => Command(() =>
        {
            if (_mode != SmuSourceMode.Voltage)
            {
                WriteCommand(":SOUR:FUNC VOLT");
                WriteCommand(":SENS:FUNC \"CURR\"");
                _mode = SmuSourceMode.Voltage;
            }
            WriteCommand($":SOUR:VOLT:LEV {Fmt(volts)}");
        });

    public SmuError SetSourceCurrent(double amps)
        => Command(() =>
        {
            if (_mode != SmuSourceMode.Current)
            {
                WriteCommand(":SOUR:FUNC CURR");
                WriteCommand(":SENS:FUNC \"VOLT\"");
                _mode = SmuSourceMode.Current;
            }
            WriteCommand($":SOUR:CURR {Fmt(amps)}");
        });

    public SmuError SetCompliance(double value)
        => Command(() => WriteCommand(_mode == SmuSourceMode.Voltage
            ? $":SENS:CURR:PROT {Fmt(value)}"
            : $":SENS:VOLT:PROT {Fmt(value)}"));

    public SmuError OutputOn()
        => Command(() => { WriteCommand(":OUTP ON"); _outputOn = true; });

    public SmuError OutputOff()
        => Command(() => { WriteCommand(":OUTP OFF"); _outputOn = false; });

    public SmuStatus GetStatus()
    {
        lock (_lock)
        {
            if (!_connected)
                return new SmuStatus { Connected = false, LastError = _lastError };
            try
            {
                _outputOn = QueryDouble(":OUTP?") != 0.0;
                double voltage = 0, current = 0;
                if (_outputOn)
                {
                    // :FORM:ELEM VOLT,CURR -> :READ? returns "voltage,current".
                    var parts = Query(":READ?").Split(',');
                    if (parts.Length >= 2)
                    {
                        voltage = Clean(ParseDouble(parts[0]));
                        current = Clean(ParseDouble(parts[1]));
                    }
                }
                return new SmuStatus
                {
                    Connected = true,
                    OutputOn = _outputOn,
                    SourceMode = _mode,
                    Voltage = voltage,
                    Current = current,
                };
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                return new SmuStatus
                {
                    Connected = true,
                    OutputOn = _outputOn,
                    SourceMode = _mode,
                    LastError = ex.Message,
                };
            }
        }
    }

    public SmuReading ReadMeasurement(int channel)
    {
        lock (_lock)
        {
            if (!_connected)
                return new SmuReading { Channel = channel, Timestamp = DateTime.UtcNow };
            try
            {
                // :FORM:ELEM VOLT,CURR is set at connect -> :READ? returns "volts,current".
                // The 2400 is single-channel; the channel argument is ignored.
                var parts = Query(":READ?").Split(',');
                return new SmuReading
                {
                    Channel = channel,
                    Voltage = parts.Length > 0 ? Clean(ParseDouble(parts[0])) : 0.0,
                    Current = parts.Length > 1 ? Clean(ParseDouble(parts[1])) : 0.0,
                    Timestamp = DateTime.UtcNow,
                };
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                Log.Error(ex, "[SMU] {Device}: ReadMeasurement failed", _config.Name);
                return new SmuReading { Channel = channel, Timestamp = DateTime.UtcNow };
            }
        }
    }

    public SmuError SetFastMode(bool fast)
        => Command(() => WriteCommand(fast
            ? ":SENS:CURR:NPLC 0.01"
            : $":SENS:CURR:NPLC {Fmt(_config.MeasurementNplc)}"));

    private SmuError Command(Action action)
    {
        lock (_lock)
        {
            if (!_connected) return SmuError.Disconnected;
            try
            {
                action();
                return SmuError.Success;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                Log.Error(ex, "[SMU] {Device}: command failed", _config.Name);
                return SmuError.CommunicationError;
            }
        }
    }

    private void WriteCommand(string command)
    {
        var status = VisaNative.WriteString(_instrSession, command);
        if (status < VisaNative.VI_SUCCESS)
            throw new InvalidOperationException($"VISA write failed: 0x{status:X8}");
    }

    private string Query(string query)
    {
        WriteCommand(query);
        var status = VisaNative.ReadString(_instrSession, out var response);
        if (status < VisaNative.VI_SUCCESS)
            throw new InvalidOperationException($"VISA read failed: 0x{status:X8}");
        return response;
    }

    private double QueryDouble(string query) => ParseDouble(Query(query));

    private static double ParseDouble(string text)
        => double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value : 0.0;

    /// <summary>Maps the SCPI not-a-number sentinel (~9.9E37) to NaN.</summary>
    private static double Clean(double v) => Math.Abs(v) >= 9e37 ? double.NaN : v;

    private static string Fmt(double value) => value.ToString("G6", CultureInfo.InvariantCulture);
}
