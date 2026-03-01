using System.Globalization;
using Horizon.LaserController.Interfaces;
using Horizon.LaserController.Models;
using Horizon.LaserController.Native;

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
                _connected = true;

                WriteCommand("*CLS");

                // Query initial states
                _tecOn = QueryBoolState("output2:state?");
                _laserOn = QueryBoolState("output1:state?");

                _lastError = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
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
                return LaserControllerError.Success;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
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
                return LaserControllerError.Success;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
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
                return LaserControllerError.Success;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
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
                return LaserControllerError.Success;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
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
                return LaserControllerError.Success;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
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
                return LaserControllerError.Success;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
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
