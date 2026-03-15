using System.Globalization;
using Horizon.LaserController.Interfaces;
using Horizon.LaserController.Models;
using Horizon.LaserController.Native;
using Serilog;

namespace Horizon.LaserController.Adapters;

public sealed class CLD101xAdapter : ILaserControllerDevice
{
    private readonly object _lock = new();
    private readonly LaserControllerDeviceConfig _config;
    private int _rmSession;
    private int _instrSession;
    private bool _connected;
    private bool _tecOn;
    private bool _laserOn;
    private double _setpointTemp;
    private double _setpointCurrent;
    private string _lastError = string.Empty;

    public string DeviceName => _config.Name;
    public string ControllerType => _config.ControllerType;
    public bool IsConnected => _connected;
    public bool IsTECOn => _tecOn;
    public bool IsLaserOn => _laserOn;

    public CLD101xAdapter(LaserControllerDeviceConfig config)
    {
        _config = config;
        _setpointTemp = config.DefaultTemperature;
        _setpointCurrent = config.DefaultLaserCurrent;
    }

    public bool Connect()
    {
        lock (_lock)
        {
            if (_connected) return true;

            Log.Information("[Laser] CLD101x {DeviceName}: connecting to VISA resource {VisaResource}", _config.Name, _config.VisaResource);

            try
            {
                var status = VisaNative.OpenDefaultRM(out _rmSession);
                if (status < VisaNative.VI_SUCCESS)
                {
                    _lastError = $"OpenDefaultRM failed: 0x{status:X8}";
                    Log.Error("[Laser] CLD101x {DeviceName}: OpenDefaultRM failed 0x{Status:X8}", _config.Name, status);
                    return false;
                }

                status = VisaNative.Open(_rmSession, _config.VisaResource, 0, 2000, out _instrSession);
                if (status < VisaNative.VI_SUCCESS)
                {
                    _lastError = $"Open failed: 0x{status:X8}";
                    Log.Error("[Laser] CLD101x {DeviceName}: VISA Open failed 0x{Status:X8}", _config.Name, status);
                    VisaNative.Close(_rmSession);
                    return false;
                }

                VisaNative.SetAttribute(_instrSession, VisaNative.VI_ATTR_TMO_VALUE, 2000);
                _connected = true;

                WriteCommand("*CLS");

                // Query initial states
                _tecOn = QueryBoolState("output2:state?");
                _laserOn = QueryBoolState("output1:state?");

                _lastError = string.Empty;
                Log.Information("[Laser] CLD101x {DeviceName}: connected (TEC={TECOn}, Laser={LaserOn})", _config.Name, _tecOn, _laserOn);
                return true;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                Log.Error(ex, "[Laser] CLD101x {DeviceName}: Connect exception", _config.Name);
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
            Log.Information("[Laser] CLD101x {DeviceName}: disconnecting", _config.Name);
            Cleanup();
            return true;
        }
    }

    public LaserControllerError SetTemperature(double celsius)
    {
        lock (_lock)
        {
            if (!_connected) return LaserControllerError.Disconnected;

            try
            {
                WriteCommand($"source2:temperature:spoint {celsius.ToString("G6", CultureInfo.InvariantCulture)}");
                _setpointTemp = celsius;
                Log.Information("[Laser] CLD101x {DeviceName}: SetTemperature setpoint={Celsius:F2} C", _config.Name, celsius);
                return LaserControllerError.Success;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                Log.Error(ex, "[Laser] CLD101x {DeviceName}: SetTemperature communication error", _config.Name);
                return LaserControllerError.CommunicationError;
            }
        }
    }

    public LaserControllerError ReadTemperature(out double celsius)
    {
        celsius = 0;
        lock (_lock)
        {
            if (!_connected) return LaserControllerError.Disconnected;

            try
            {
                WriteCommand("SENSe2:temperature:data?");
                var response = ReadResponse();
                if (double.TryParse(response.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out celsius))
                    return LaserControllerError.Success;
                return LaserControllerError.ParseError;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                return LaserControllerError.CommunicationError;
            }
        }
    }

    public LaserControllerError TECOn()
    {
        lock (_lock)
        {
            if (!_connected) return LaserControllerError.Disconnected;

            try
            {
                WriteCommand("output2:state on");
                _tecOn = true;
                Log.Information("[Laser] CLD101x {DeviceName}: TEC ON", _config.Name);
                return LaserControllerError.Success;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                Log.Error(ex, "[Laser] CLD101x {DeviceName}: TECOn communication error", _config.Name);
                return LaserControllerError.CommunicationError;
            }
        }
    }

    public LaserControllerError TECOff()
    {
        lock (_lock)
        {
            if (!_connected) return LaserControllerError.Disconnected;

            try
            {
                WriteCommand("output2:state off");
                _tecOn = false;
                Log.Information("[Laser] CLD101x {DeviceName}: TEC OFF", _config.Name);
                return LaserControllerError.Success;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                Log.Error(ex, "[Laser] CLD101x {DeviceName}: TECOff communication error", _config.Name);
                return LaserControllerError.CommunicationError;
            }
        }
    }

    public LaserControllerError SetLaserCurrent(double milliamps)
    {
        lock (_lock)
        {
            if (!_connected) return LaserControllerError.Disconnected;

            try
            {
                // CLD101x expects amps, convert from mA
                var amps = milliamps / 1000.0;
                WriteCommand($"source1:current:level:amplitude {amps.ToString("G6", CultureInfo.InvariantCulture)}");
                _setpointCurrent = milliamps;
                Log.Information("[Laser] CLD101x {DeviceName}: SetLaserCurrent setpoint={Milliamps:F3} mA", _config.Name, milliamps);
                return LaserControllerError.Success;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                Log.Error(ex, "[Laser] CLD101x {DeviceName}: SetLaserCurrent communication error", _config.Name);
                return LaserControllerError.CommunicationError;
            }
        }
    }

    public LaserControllerError ReadLaserCurrent(out double milliamps)
    {
        milliamps = 0;
        lock (_lock)
        {
            if (!_connected) return LaserControllerError.Disconnected;

            try
            {
                WriteCommand("sense3:current:dc:data?");
                var response = ReadResponse();
                if (double.TryParse(response.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var amps))
                {
                    milliamps = amps * 1000.0; // Convert amps to mA
                    return LaserControllerError.Success;
                }
                return LaserControllerError.ParseError;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                return LaserControllerError.CommunicationError;
            }
        }
    }

    public LaserControllerError LaserOn()
    {
        lock (_lock)
        {
            if (!_connected) return LaserControllerError.Disconnected;

            try
            {
                WriteCommand("output1:state on");
                _laserOn = true;
                Log.Information("[Laser] CLD101x {DeviceName}: Laser ON", _config.Name);
                return LaserControllerError.Success;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                Log.Error(ex, "[Laser] CLD101x {DeviceName}: LaserOn communication error", _config.Name);
                return LaserControllerError.CommunicationError;
            }
        }
    }

    public LaserControllerError LaserOff()
    {
        lock (_lock)
        {
            if (!_connected) return LaserControllerError.Disconnected;

            try
            {
                WriteCommand("output1:state off");
                _laserOn = false;
                Log.Information("[Laser] CLD101x {DeviceName}: Laser OFF", _config.Name);
                return LaserControllerError.Success;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                Log.Error(ex, "[Laser] CLD101x {DeviceName}: LaserOff communication error", _config.Name);
                return LaserControllerError.CommunicationError;
            }
        }
    }

    public LaserControllerStatus GetStatus()
    {
        lock (_lock)
        {
            double temp = 0, current = 0;
            if (_connected)
            {
                try
                {
                    WriteCommand("SENSe2:temperature:data?");
                    var tempResp = ReadResponse();
                    double.TryParse(tempResp.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out temp);

                    WriteCommand("sense3:current:dc:data?");
                    var currentResp = ReadResponse();
                    if (double.TryParse(currentResp.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var amps))
                        current = amps * 1000.0;

                    _tecOn = QueryBoolState("output2:state?");
                    _laserOn = QueryBoolState("output1:state?");
                }
                catch { }
            }

            return new LaserControllerStatus
            {
                Connected = _connected,
                TECOn = _tecOn,
                LaserOn = _laserOn,
                CurrentTemperature = temp,
                SetpointTemperature = _setpointTemp,
                CurrentLaserCurrent = current,
                SetpointLaserCurrent = _setpointCurrent,
                LastError = _lastError
            };
        }
    }

    private bool QueryBoolState(string query)
    {
        try
        {
            WriteCommand(query);
            var response = ReadResponse().Trim().ToUpperInvariant();
            return response is "ON" or "1";
        }
        catch
        {
            return false;
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
        _tecOn = false;
        _laserOn = false;
    }
}
