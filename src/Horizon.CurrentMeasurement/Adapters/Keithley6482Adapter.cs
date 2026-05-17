using System.Globalization;
using Horizon.CurrentMeasurement.Interfaces;
using Horizon.CurrentMeasurement.Models;
using Horizon.CurrentMeasurement.Native;
using Serilog;

namespace Horizon.CurrentMeasurement.Adapters;

public sealed class Keithley6482Adapter : ICurrentMeasurementDevice
{
    private readonly object _lock = new();
    private readonly CurrentMeasurementDeviceConfig _config;
    private int _rmSession;
    private int _instrSession;
    private bool _connected;

    public string DeviceName => _config.Name;
    public int ChannelCount => _config.ChannelCount;
    public List<int> EnabledChannels => _config.EnabledChannels;
    public bool IsConnected => _connected;

    public Keithley6482Adapter(CurrentMeasurementDeviceConfig config)
    {
        _config = config;
    }

    /// <summary>SCPI <c>:FORM:ELEM</c> list covering every channel, e.g. "CURR1,CURR2".</summary>
    private string ChannelElements =>
        string.Join(",", Enumerable.Range(1, _config.ChannelCount).Select(c => $"CURR{c}"));

    /// <summary>Applies the configured integration time and range to every channel.</summary>
    private void ApplySenseConfig()
    {
        var nplc = _config.MeasurementNplc.ToString("G6", CultureInfo.InvariantCulture);
        for (int ch = 1; ch <= _config.ChannelCount; ch++)
        {
            WriteCommand($":SENS{ch}:CURR:NPLC {nplc}");
            if (_config.AutoRange || _config.FixedRange <= 0)
            {
                WriteCommand($":SENS{ch}:CURR:RANG:AUTO ON");
            }
            else
            {
                WriteCommand($":SENS{ch}:CURR:RANG:AUTO OFF");
                WriteCommand($":SENS{ch}:CURR:RANG {_config.FixedRange.ToString("G6", CultureInfo.InvariantCulture)}");
            }
        }
        Log.Information("[CMeas] Keithley6482 {Name} sense config — {Channels} ch, NPLC={Nplc}, AutoRange={Auto}, FixedRange={Range}",
            _config.Name, _config.ChannelCount, _config.MeasurementNplc, _config.AutoRange, _config.FixedRange);
    }

    public bool Connect()
    {
        lock (_lock)
        {
            if (_connected) return true;

            Log.Information("[CMeas] Keithley6482 connecting to {VisaResource}", _config.VisaResource);

            try
            {
                var status = VisaNative.OpenDefaultRM(out _rmSession);
                if (status < VisaNative.VI_SUCCESS)
                {
                    Log.Warning("[CMeas] Keithley6482 {Name} connect FAILED — OpenDefaultRM status 0x{Status:X8}", _config.Name, status);
                    return false;
                }

                status = VisaNative.Open(_rmSession, _config.VisaResource, 0, 1000, out _instrSession);
                if (status < VisaNative.VI_SUCCESS)
                {
                    VisaNative.Close(_rmSession);
                    Log.Warning("[CMeas] Keithley6482 {Name} connect FAILED — Open status 0x{Status:X8}", _config.Name, status);
                    return false;
                }

                // Set I/O timeout to 2 seconds
                VisaNative.SetAttribute(_instrSession, VisaNative.VI_ATTR_TMO_VALUE, 2000);

                _connected = true;

                // Clear any pending errors/data
                WriteCommand("*CLS");
                // Return one current reading per channel, comma-separated.
                WriteCommand($":FORM:ELEM {ChannelElements}");
                // Apply the configured integration time + range to every channel.
                ApplySenseConfig();

                Log.Information("[CMeas] Keithley6482 {Name} connected OK", _config.Name);
                return true;
            }
            catch
            {
                Cleanup();
                Log.Warning("[CMeas] Keithley6482 {Name} connect FAILED — exception during open", _config.Name);
                return false;
            }
        }
    }

    public bool Disconnect()
    {
        lock (_lock)
        {
            if (!_connected) return true;
            Log.Information("[CMeas] Keithley6482 {Name} disconnecting", _config.Name);
            Cleanup();
            return true;
        }
    }

    public CurrentMeasurementError ReadCurrent(int channel, out double current)
    {
        current = 0;
        lock (_lock)
        {
            if (!_connected) return CurrentMeasurementError.Disconnected;
            if (channel < 1 || channel > _config.ChannelCount)
                return CurrentMeasurementError.InvalidChannel;

            try
            {
                // 6482 returns both channels with :READ? — pick the requested one
                WriteCommand(":READ?");
                var response = ReadResponse();

                var parts = response.Split(',');
                int idx = channel - 1;
                if (idx < parts.Length &&
                    double.TryParse(parts[idx].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                {
                    current = value;
                    return CurrentMeasurementError.Success;
                }
                return CurrentMeasurementError.ParseError;
            }
            catch
            {
                return CurrentMeasurementError.CommunicationError;
            }
        }
    }

    public CurrentMeasurementError ReadAllChannels(out double[] currents)
    {
        currents = new double[_config.ChannelCount];
        lock (_lock)
        {
            if (!_connected) return CurrentMeasurementError.Disconnected;

            try
            {
                // 6482: :READ? returns both channel readings comma-separated
                WriteCommand(":READ?");
                var response = ReadResponse();

                var parts = response.Split(',');
                for (int i = 0; i < parts.Length && i < currents.Length; i++)
                {
                    if (double.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                        currents[i] = value;
                }

                return CurrentMeasurementError.Success;
            }
            catch
            {
                return CurrentMeasurementError.CommunicationError;
            }
        }
    }

    public CurrentMeasurementError GetIdentity(out string identity)
    {
        identity = string.Empty;
        lock (_lock)
        {
            if (!_connected) return CurrentMeasurementError.Disconnected;

            try
            {
                WriteCommand("*IDN?");
                identity = ReadResponse().Trim();
                return CurrentMeasurementError.Success;
            }
            catch
            {
                return CurrentMeasurementError.CommunicationError;
            }
        }
    }

    public CurrentMeasurementError SetVoltage(double volts)
    {
        lock (_lock)
        {
            if (!_connected) return CurrentMeasurementError.Disconnected;

            try
            {
                WriteCommand($":SOUR:VOLT {volts.ToString("G6", CultureInfo.InvariantCulture)}");
                return CurrentMeasurementError.Success;
            }
            catch
            {
                return CurrentMeasurementError.CommunicationError;
            }
        }
    }

    public CurrentMeasurementError GetVoltage(out double volts)
    {
        volts = 0;
        lock (_lock)
        {
            if (!_connected) return CurrentMeasurementError.Disconnected;

            try
            {
                WriteCommand(":SOUR:VOLT?");
                var response = ReadResponse();
                if (double.TryParse(response.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out volts))
                    return CurrentMeasurementError.Success;
                return CurrentMeasurementError.ParseError;
            }
            catch
            {
                return CurrentMeasurementError.CommunicationError;
            }
        }
    }

    public CurrentMeasurementError SetVoltageEnabled(bool enabled)
    {
        lock (_lock)
        {
            if (!_connected) return CurrentMeasurementError.Disconnected;

            try
            {
                WriteCommand($":SOUR:VOLT:STAT {(enabled ? "ON" : "OFF")}");
                return CurrentMeasurementError.Success;
            }
            catch
            {
                return CurrentMeasurementError.CommunicationError;
            }
        }
    }

    public CurrentMeasurementError ConfigureFastMode()
    {
        lock (_lock)
        {
            if (!_connected) return CurrentMeasurementError.Disconnected;

            try
            {
                Log.Information("[CMeas] Keithley6482 {Name} switching to FastMode (NPLC=0.1)", _config.Name);
                // FAST mode (~50-80 Hz): NPLC=0.1, averaging=3, auto-zero ON
                // Matches bobcat_engine COARSE/alignment mode
                WriteCommand(":SENS1:CURR:NPLC 0.1");
                WriteCommand(":SENS2:CURR:NPLC 0.1");
                WriteCommand(":SENS1:CURR:RANG:AUTO ON");
                WriteCommand(":SENS2:CURR:RANG:AUTO ON");
                WriteCommand(":SENS1:CURR:AVER:STAT ON");
                WriteCommand(":SENS1:CURR:AVER:COUN 3");
                WriteCommand(":SENS1:CURR:AVER:TCON REP");
                WriteCommand(":SENS2:CURR:AVER:STAT ON");
                WriteCommand(":SENS2:CURR:AVER:COUN 3");
                WriteCommand(":SENS2:CURR:AVER:TCON REP");
                WriteCommand(":SYST:AZER ON");
                WriteCommand(":DISP:ENAB OFF");
                WriteCommand(":INIT:CONT ON");
                WriteCommand($":FORM:ELEM {ChannelElements}");
                WriteCommand(":FORM:DATA ASC");

                return CurrentMeasurementError.Success;
            }
            catch
            {
                return CurrentMeasurementError.CommunicationError;
            }
        }
    }

    public CurrentMeasurementError ConfigureDefaultMode()
    {
        lock (_lock)
        {
            if (!_connected) return CurrentMeasurementError.Disconnected;

            try
            {
                Log.Information("[CMeas] Keithley6482 {Name} switching to DefaultMode (NPLC={Nplc})",
                    _config.Name, _config.MeasurementNplc);
                // Default mode (~10 Hz): configured NPLC + range, accurate.
                WriteCommand(":INIT:CONT OFF");
                WriteCommand(":DISP:ENAB ON");
                ApplySenseConfig();
                WriteCommand(":SENS1:CURR:AVER:STAT OFF");
                WriteCommand(":SENS2:CURR:AVER:STAT OFF");
                WriteCommand(":SYST:AZER ON");
                WriteCommand($":FORM:ELEM {ChannelElements}");

                return CurrentMeasurementError.Success;
            }
            catch
            {
                return CurrentMeasurementError.CommunicationError;
            }
        }
    }

    public CurrentMeasurementError Reset()
    {
        lock (_lock)
        {
            if (!_connected) return CurrentMeasurementError.Disconnected;

            try
            {
                Log.Information("[CMeas] Keithley6482 {Name} resetting (*RST)", _config.Name);
                WriteCommand("*RST");
                Thread.Sleep(500);
                WriteCommand("*CLS");
                WriteCommand($":FORM:ELEM {ChannelElements}");
                // *RST wipes NPLC/range — re-apply the configured values.
                ApplySenseConfig();

                return CurrentMeasurementError.Success;
            }
            catch
            {
                return CurrentMeasurementError.CommunicationError;
            }
        }
    }

    private void WriteCommand(string command)
    {
        var status = VisaNative.WriteString(_instrSession, command);
        if (status < VisaNative.VI_SUCCESS)
            throw new InvalidOperationException($"VISA write failed: 0x{status:X8}");
    }

    private string ReadResponse()
    {
        var status = VisaNative.ReadString(_instrSession, out var response);
        if (status < VisaNative.VI_SUCCESS)
            throw new InvalidOperationException($"VISA read failed: 0x{status:X8}");
        return response;
    }

    private void Cleanup()
    {
        try { VisaNative.Close(_instrSession); } catch { }
        try { VisaNative.Close(_rmSession); } catch { }
        _instrSession = 0;
        _rmSession = 0;
        _connected = false;
    }
}
