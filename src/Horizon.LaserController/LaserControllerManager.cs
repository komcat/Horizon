using System.Collections.Concurrent;
using Horizon.LaserController.Adapters;
using Horizon.LaserController.Interfaces;
using Horizon.LaserController.Models;
using Serilog;

namespace Horizon.LaserController;

public sealed class LaserControllerManager : IDisposable
{
    private readonly ConcurrentDictionary<string, ILaserControllerDevice> _devices = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public void AddDevice(LaserControllerDeviceConfig config)
    {
        if (!config.Enabled) return;

        ILaserControllerDevice device = config.ControllerType.ToLowerInvariant() switch
        {
            "cld101x_direct" => new CLD101xAdapter(config),
            _ => throw new ArgumentException($"Unknown laser controller type: {config.ControllerType}")
        };

        _devices[config.Name] = device;
        Log.Information("[Laser] Device added: {DeviceName} (type: {ControllerType})", config.Name, config.ControllerType);
    }

    public bool ConnectAll()
    {
        Log.Information("[Laser] ConnectAll: connecting {Count} device(s)", _devices.Count);
        bool allOk = true;
        foreach (var kvp in _devices)
        {
            if (!kvp.Value.Connect())
                allOk = false;
        }
        Log.Information("[Laser] ConnectAll: result={AllOk}", allOk);
        return allOk;
    }

    public void DisconnectAll()
    {
        Log.Information("[Laser] DisconnectAll: disconnecting {Count} device(s)", _devices.Count);
        foreach (var kvp in _devices)
            kvp.Value.Disconnect();
    }

    public bool ConnectDevice(string name)
    {
        var result = _devices.TryGetValue(name, out var device) && device.Connect();
        Log.Information("[Laser] ConnectDevice {DeviceName}: result={Result}", name, result);
        return result;
    }

    public bool DisconnectDevice(string name)
    {
        var result = _devices.TryGetValue(name, out var device) && device.Disconnect();
        Log.Information("[Laser] DisconnectDevice {DeviceName}: result={Result}", name, result);
        return result;
    }

    public ILaserControllerDevice? GetDevice(string name)
    {
        _devices.TryGetValue(name, out var device);
        return device;
    }

    public List<string> GetAllDeviceNames() => _devices.Keys.ToList();

    // TEC passthrough
    public LaserControllerError SetTemperature(string deviceName, double celsius)
    {
        if (!_devices.TryGetValue(deviceName, out var device))
        {
            Log.Warning("[Laser] SetTemperature: device not found: {DeviceName}", deviceName);
            return LaserControllerError.DeviceNotFound;
        }
        Log.Information("[Laser] SetTemperature {DeviceName}: setpoint={Celsius:F2} C", deviceName, celsius);
        return device.SetTemperature(celsius);
    }

    public LaserControllerError ReadTemperature(string deviceName, out double celsius)
    {
        celsius = 0;
        if (!_devices.TryGetValue(deviceName, out var device))
            return LaserControllerError.DeviceNotFound;
        return device.ReadTemperature(out celsius);
    }

    public LaserControllerError TECOn(string deviceName)
    {
        if (!_devices.TryGetValue(deviceName, out var device))
        {
            Log.Warning("[Laser] TECOn: device not found: {DeviceName}", deviceName);
            return LaserControllerError.DeviceNotFound;
        }
        Log.Information("[Laser] TECOn {DeviceName}", deviceName);
        return device.TECOn();
    }

    public LaserControllerError TECOff(string deviceName)
    {
        if (!_devices.TryGetValue(deviceName, out var device))
        {
            Log.Warning("[Laser] TECOff: device not found: {DeviceName}", deviceName);
            return LaserControllerError.DeviceNotFound;
        }
        Log.Information("[Laser] TECOff {DeviceName}", deviceName);
        return device.TECOff();
    }

    // Laser passthrough
    public LaserControllerError SetLaserCurrent(string deviceName, double milliamps)
    {
        if (!_devices.TryGetValue(deviceName, out var device))
        {
            Log.Warning("[Laser] SetLaserCurrent: device not found: {DeviceName}", deviceName);
            return LaserControllerError.DeviceNotFound;
        }
        Log.Information("[Laser] SetLaserCurrent {DeviceName}: setpoint={Milliamps:F3} mA", deviceName, milliamps);
        return device.SetLaserCurrent(milliamps);
    }

    public LaserControllerError ReadLaserCurrent(string deviceName, out double milliamps)
    {
        milliamps = 0;
        if (!_devices.TryGetValue(deviceName, out var device))
            return LaserControllerError.DeviceNotFound;
        return device.ReadLaserCurrent(out milliamps);
    }

    public LaserControllerError LaserOn(string deviceName)
    {
        if (!_devices.TryGetValue(deviceName, out var device))
        {
            Log.Warning("[Laser] LaserOn: device not found: {DeviceName}", deviceName);
            return LaserControllerError.DeviceNotFound;
        }
        Log.Information("[Laser] LaserOn {DeviceName}", deviceName);
        return device.LaserOn();
    }

    public LaserControllerError LaserOff(string deviceName)
    {
        if (!_devices.TryGetValue(deviceName, out var device))
        {
            Log.Warning("[Laser] LaserOff: device not found: {DeviceName}", deviceName);
            return LaserControllerError.DeviceNotFound;
        }
        Log.Information("[Laser] LaserOff {DeviceName}", deviceName);
        return device.LaserOff();
    }

    public LaserControllerStatus? GetStatus(string deviceName)
    {
        if (!_devices.TryGetValue(deviceName, out var device))
            return null;
        return device.GetStatus();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisconnectAll();
    }
}
