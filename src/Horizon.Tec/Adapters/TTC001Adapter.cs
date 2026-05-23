using Horizon.Config;
using Horizon.Tec.Models;
using Serilog;
using static Horizon.Tec.KinesisNative;

namespace Horizon.Tec;

/// <summary>
/// Kinesis-based adapter for Thorlabs TTC-001 T-Cube TEC Controller. TEC-only
/// device — identified by 8-digit USB serial number (e.g. <c>"87814387"</c>).
///
/// Requires <c>Thorlabs.MotionControl.TCube.TEC.dll</c> +
/// <c>Thorlabs.MotionControl.DeviceManager.dll</c> alongside the executable,
/// and the FTDI driver that ships with the Kinesis SDK installer.
///
/// Hardware envelope:
///   Sensor: AD590 transducer or 20 kΩ / 200 kΩ NTC thermistor — set from
///           <see cref="TecDeviceConfig.SensorMode"/> at <see cref="Connect"/>.
///   Setpoint range: -45 .. +145 °C (rejected at the adapter boundary).
///
/// Polling: <c>TC_StartPolling(250 ms)</c> is launched at <see cref="Connect"/>
/// and stopped at <see cref="Disconnect"/>. Reads return the latest polled
/// value rather than blocking on a USB round-trip. Mirrors bobcat-engine
/// <c>TTC001Adapter.cpp</c>.
/// </summary>
public sealed class TTC001Adapter : ITecDevice
{
    private const double TempMinC = -45.0;
    private const double TempMaxC = 145.0;
    private const int PollIntervalMs = 250;
    private const int PostConnectSettleMs = 300;

    private readonly object _lock = new();
    private readonly TecDeviceConfig _config;
    private readonly string _serial;

    private bool _connected;
    private bool _tecOn;
    private double _setpointTemp;
    private double _currentTemp;
    private SensorType _sensorMode = SensorType.Transducer;
    private bool _sensorIsThermistor;
    private string _lastError = string.Empty;

    public TTC001Adapter(TecDeviceConfig config)
    {
        _config = config;
        _serial = config.SerialNumber;
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
            if (string.IsNullOrWhiteSpace(_serial))
            {
                _lastError = "missing serialNumber in config";
                Log.Error("[TEC] {Device}: {Err}", _config.Name, _lastError);
                return false;
            }

            try
            {
                // Refresh discovery cache. Safe to call repeatedly.
                var build = TLI_BuildDeviceList();
                if (build != FtOk)
                {
                    _lastError = $"TLI_BuildDeviceList failed: {build}";
                    Log.Error("[TEC] {Device}: {Err}", _config.Name, _lastError);
                    return false;
                }

                var open = TC_Open(_serial);
                if (open != FtOk)
                {
                    _lastError = $"TC_Open({_serial}) failed: {open}";
                    Log.Error("[TEC] {Device}: {Err}", _config.Name, _lastError);
                    return false;
                }

                // Load device-persisted settings (sensor type, PID, current limit).
                // Non-fatal — device still runs with current settings on failure.
                if (!TC_LoadSettings(_serial))
                    Log.Warning("[TEC] {Device}: TC_LoadSettings returned false (continuing)", _config.Name);

                // Force the sensor mode from config. Kinesis GUI's "Persist
                // Settings to the Device" is often left unticked, so the
                // device may boot in factory-default Transducer mode and the
                // adapter would otherwise misinterpret raw thermistor counts
                // as 0.01 °C per unit.
                var desired = ParseSensorMode(_config.SensorMode);
                var setRc = TC_SetSensorType(_serial, desired);
                if (setRc != FtOk)
                    Log.Warning("[TEC] {Device}: TC_SetSensorType({Mode}) failed: {Rc}",
                        _config.Name, desired, setRc);

                // Sensor type takes a moment to propagate; let polling pick it up.
                Thread.Sleep(200);

                // Read back the active sensor mode. TC_RequestSensorType is async;
                // we'll read it twice — once before StartPolling and once after
                // the settle window — to be sure the cache is current.
                TC_RequestSensorType(_serial);
                _sensorMode = TC_GetSensorType(_serial);

                if (!TC_StartPolling(_serial, PollIntervalMs))
                {
                    _lastError = "TC_StartPolling failed";
                    Log.Error("[TEC] {Device}: {Err}", _config.Name, _lastError);
                    TC_Close(_serial);
                    return false;
                }

                // Let one poll cycle populate the cache before any caller reads it.
                Thread.Sleep(PostConnectSettleMs);
                _sensorMode = TC_GetSensorType(_serial);
                _sensorIsThermistor = _sensorMode is SensorType.TH20kOhm or SensorType.TH200kOhm;

                _connected = true;
                _lastError = string.Empty;
                Log.Information(
                    "[TEC] {Device}: connected (SN {Serial}, sensor={Sensor}{Thermistor})",
                    _config.Name, _serial, _sensorMode,
                    _sensorIsThermistor
                        ? $" β={_config.ThermistorBeta} R0={_config.ThermistorR0Ohms}Ω @ {_config.ThermistorT0Celsius}°C"
                        : string.Empty);
                return true;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                Log.Error(ex, "[TEC] {Device}: connect failed", _config.Name);
                try { TC_Close(_serial); } catch { }
                _connected = false;
                return false;
            }
        }
    }

    public bool Disconnect()
    {
        lock (_lock)
        {
            if (!_connected) return true;
            try
            {
                // Safety: turn TEC output off before tearing down.
                TC_Disable(_serial);
                TC_StopPolling(_serial);
                TC_Close(_serial);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[TEC] {Device}: disconnect cleanup error", _config.Name);
            }
            _connected = false;
            _tecOn = false;
            Log.Information("[TEC] {Device}: disconnected", _config.Name);
            return true;
        }
    }

    public TecError SetTemperature(double celsius)
    {
        if (!double.IsFinite(celsius) || celsius < TempMinC || celsius > TempMaxC)
        {
            _lastError = $"setpoint {celsius} out of range ({TempMinC}..{TempMaxC} °C)";
            return TecError.OperationFailed;
        }

        return Command(() =>
        {
            // Encode setpoint as Kinesis device units (see Thorlabs.MotionControl.TCube.TEC.h):
            //   Transducer  : 1 unit = 0.01 °C, range -4500..14500
            //   20 kΩ NTC   : 1 unit = 10 Ω,    range 0..2000  (= 0..20 kΩ)
            //   200 kΩ NTC  : 1 unit = 100 Ω,   range 0..2000  (= 0..200 kΩ)
            short deviceUnits;
            if (_sensorIsThermistor)
            {
                var ohms = CelsiusToOhms(celsius);
                var ohmsPerUnit = OhmsPerDeviceUnit();
                var raw = Math.Clamp((long)Math.Round(ohms / ohmsPerUnit), 0L, 2000L);
                if (ohms <= 0.0 || ohms / ohmsPerUnit > 2000.0)
                    Log.Warning(
                        "[TEC] {Device}: setpoint {C} °C → {Ohms} Ω clamped to device range (0..{Max} Ω)",
                        _config.Name, celsius, ohms, 2000.0 * ohmsPerUnit);
                deviceUnits = (short)raw;
            }
            else
            {
                deviceUnits = (short)Math.Round(celsius * 100.0);
            }

            var rc = TC_SetTemperature(_serial, deviceUnits);
            if (rc != FtOk)
                throw new InvalidOperationException($"TC_SetTemperature failed: {rc}");

            _setpointTemp = celsius;
        });
    }

    public TecError TecOn() => Command(() =>
    {
        var rc = TC_Enable(_serial);
        if (rc != FtOk) throw new InvalidOperationException($"TC_Enable failed: {rc}");
        _tecOn = true;
    });

    public TecError TecOff() => Command(() =>
    {
        var rc = TC_Disable(_serial);
        if (rc != FtOk) throw new InvalidOperationException($"TC_Disable failed: {rc}");
        _tecOn = false;
    });

    public TecStatus GetStatus()
    {
        lock (_lock)
        {
            if (!_connected)
                return new TecStatus { Connected = false, LastError = _lastError };
            try
            {
                // Belt-and-suspenders: TC_RequestStatus is async; the read still
                // returns whatever the poll loop most-recently populated.
                TC_RequestStatus(_serial);
                var units = TC_GetTemperatureReading(_serial);
                double celsius;
                if (_sensorIsThermistor)
                {
                    var ohms = units * OhmsPerDeviceUnit();
                    celsius = OhmsToCelsius(ohms);
                }
                else
                {
                    celsius = units / 100.0;
                }
                if (!double.IsFinite(celsius)) celsius = double.NaN;
                _currentTemp = celsius;

                return new TecStatus
                {
                    Connected = true,
                    CurrentTemperature = celsius,
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
                    CurrentTemperature = _currentTemp,
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

    private static SensorType ParseSensorMode(string mode)
    {
        var k = (mode ?? string.Empty).Trim().ToLowerInvariant();
        return k switch
        {
            "transducer" or "ad590" or "ad592"
                => SensorType.Transducer,
            "thermistor_200k" or "200k" or "th200k" or "200kohm"
                => SensorType.TH200kOhm,
            // Default — the 20 kΩ NTC is the typical TTC-001 sensor.
            _   => SensorType.TH20kOhm,
        };
    }

    private double OhmsPerDeviceUnit() => _sensorMode switch
    {
        SensorType.TH20kOhm => 10.0,    // 0..2000 → 0..20 kΩ
        SensorType.TH200kOhm => 100.0,  // 0..2000 → 0..200 kΩ
        _ => 0.0,
    };

    // Beta equation:
    //   1/T = 1/T0 + (1/β) · ln(R/R0)   (T, T0 in Kelvin)
    //   R   = R0 · exp(β · (1/T - 1/T0))
    private double CelsiusToOhms(double celsius)
    {
        var tK = celsius + 273.15;
        var t0K = _config.ThermistorT0Celsius + 273.15;
        if (tK <= 0.0 || t0K <= 0.0 || _config.ThermistorBeta <= 0.0) return 0.0;
        return _config.ThermistorR0Ohms *
               Math.Exp(_config.ThermistorBeta * (1.0 / tK - 1.0 / t0K));
    }

    private double OhmsToCelsius(double ohms)
    {
        var t0K = _config.ThermistorT0Celsius + 273.15;
        if (ohms <= 0.0 || _config.ThermistorR0Ohms <= 0.0 ||
            t0K <= 0.0 || _config.ThermistorBeta <= 0.0)
            return double.NaN;
        var invT = 1.0 / t0K +
                   Math.Log(ohms / _config.ThermistorR0Ohms) / _config.ThermistorBeta;
        if (invT <= 0.0) return double.NaN;
        return 1.0 / invT - 273.15;
    }
}
