using System.Globalization;
using Horizon.Config;
using Horizon.Tec.Models;
using Serilog;

namespace Horizon.Tec;

/// <summary>
/// Thorlabs TED4015 standalone TEC controller over VISA (USB). SCPI command
/// strings below are plausible but should be confirmed against the TED4015
/// programming manual — they are isolated to this adapter.
/// </summary>
public sealed class Ted4015Adapter : ITecDevice
{
    private readonly object _lock = new();
    private readonly TecDeviceConfig _config;
    private int _rmSession;
    private int _instrSession;
    private bool _connected;
    private bool _tecOn;
    private double _setpointTemp;
    private string _lastError = string.Empty;

    public Ted4015Adapter(TecDeviceConfig config)
    {
        _config = config;
        _setpointTemp = config.DefaultTemperature;
    }

    public string DeviceName => _config.Name;
    public string ControllerType => _config.ControllerType;
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

                status = VisaNative.Open(_rmSession, _config.VisaResource, 0, 2000, out _instrSession);
                if (status < VisaNative.VI_SUCCESS)
                {
                    _lastError = $"Open failed: 0x{status:X8}";
                    VisaNative.Close(_rmSession);
                    return false;
                }

                VisaNative.SetAttribute(_instrSession, VisaNative.VI_ATTR_TMO_VALUE, 2000);
                VisaNative.Clear(_instrSession);
                _connected = true;
                WriteCommand("*CLS");
                _tecOn = QueryDouble("output:state?") != 0.0;
                _lastError = string.Empty;
                Log.Information("[TEC] {Device}: connected ({Visa})", _config.Name, _config.VisaResource);
                return true;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                Log.Error(ex, "[TEC] {Device}: connect failed", _config.Name);
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
            Log.Information("[TEC] {Device}: disconnected", _config.Name);
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

    public TecError SetTemperature(double celsius)
        => Command(() =>
        {
            WriteCommand($"source:temperature:spoint {celsius.ToString("G6", CultureInfo.InvariantCulture)}");
            _setpointTemp = celsius;
        });

    public TecError TecOn()
        => Command(() => { WriteCommand("output:state on"); _tecOn = true; });

    public TecError TecOff()
        => Command(() => { WriteCommand("output:state off"); _tecOn = false; });

    public TecStatus GetStatus()
    {
        lock (_lock)
        {
            if (!_connected)
                return new TecStatus { Connected = false, LastError = _lastError };
            try
            {
                var temp = QueryDouble("measure:temperature?");
                return new TecStatus
                {
                    Connected = true,
                    CurrentTemperature = temp,
                    SetpointTemperature = _setpointTemp,
                    TecOn = _tecOn,
                };
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                return new TecStatus
                {
                    Connected = true,
                    SetpointTemperature = _setpointTemp,
                    TecOn = _tecOn,
                    LastError = ex.Message,
                };
            }
        }
    }

    private TecError Command(Action action)
    {
        lock (_lock)
        {
            if (!_connected) return TecError.Disconnected;
            try
            {
                action();
                return TecError.Success;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                Log.Error(ex, "[TEC] {Device}: command failed", _config.Name);
                return TecError.CommunicationError;
            }
        }
    }

    private void WriteCommand(string command)
    {
        var status = VisaNative.WriteString(_instrSession, command);
        if (status < VisaNative.VI_SUCCESS)
            throw new InvalidOperationException($"VISA write failed: 0x{status:X8}");
    }

    private double QueryDouble(string query)
    {
        WriteCommand(query);
        var status = VisaNative.ReadString(_instrSession, out var response);
        if (status < VisaNative.VI_SUCCESS)
            throw new InvalidOperationException($"VISA read failed: 0x{status:X8}");
        return double.TryParse(response, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value : 0.0;
    }
}
