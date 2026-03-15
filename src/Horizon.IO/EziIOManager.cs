using System.Collections.Concurrent;
using Horizon.IO.Adapters;
using Horizon.IO.Interfaces;
using Horizon.IO.Models;
using Serilog;

namespace Horizon.IO;

public sealed class EziIOManager : IDisposable
{
    private readonly ConcurrentDictionary<string, IIODevice> _devices = new(StringComparer.OrdinalIgnoreCase);
    private PeriodicTimer? _pollTimer;
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;
    private bool _disposed;

    public event Action<string, IOStatus>? OnIOStatusUpdated;

    public void AddDevice(IODeviceConfig config)
    {
        if (!config.Device.Enabled) return;

        var device = new EziIODevice(config.Device);
        foreach (var pin in config.InputPins)
            device.SetPinName(true, pin.Pin, pin.Name);
        foreach (var pin in config.OutputPins)
            device.SetPinName(false, pin.Pin, pin.Name);

        _devices[config.Device.Name] = device;
        Log.Information("[IO] Device added: {DeviceName}", config.Device.Name);
    }

    public void RemoveDevice(string name)
    {
        if (_devices.TryRemove(name, out var device))
        {
            device.Disconnect();
            Log.Information("[IO] Device removed: {DeviceName}", name);
        }
    }

    public bool ConnectAll()
    {
        Log.Information("[IO] ConnectAll started for {DeviceCount} device(s)", _devices.Count);
        bool allOk = true;
        foreach (var kvp in _devices)
        {
            if (!kvp.Value.Connect())
                allOk = false;
        }
        if (allOk)
            Log.Information("[IO] ConnectAll OK — all devices connected");
        else
            Log.Warning("[IO] ConnectAll partial failure — one or more devices failed to connect");
        return allOk;
    }

    public void DisconnectAll()
    {
        Log.Information("[IO] DisconnectAll started");
        foreach (var kvp in _devices)
            kvp.Value.Disconnect();
    }

    public bool ConnectDevice(string name)
    {
        bool result = _devices.TryGetValue(name, out var device) && device.Connect();
        if (result)
            Log.Information("[IO] ConnectDevice OK: {DeviceName}", name);
        else
            Log.Warning("[IO] ConnectDevice FAILED: {DeviceName}", name);
        return result;
    }

    public IOError ReadInputs(string deviceName, out uint inputs)
    {
        inputs = 0;
        if (!_devices.TryGetValue(deviceName, out var device))
            return IOError.DeviceNotFound;
        return device.ReadInputs(out inputs);
    }

    public IOError ReadInputs(string deviceName, out uint inputs, out uint latch)
    {
        inputs = 0;
        latch = 0;
        if (!_devices.TryGetValue(deviceName, out var device))
            return IOError.DeviceNotFound;
        return device.ReadInputs(out inputs, out latch);
    }

    public IOError SetOutput(string deviceName, int pin, bool state)
    {
        if (!_devices.TryGetValue(deviceName, out var device))
            return IOError.DeviceNotFound;
        var result = device.SetOutput(pin, state);
        if (result == IOError.Success)
            Log.Information("[IO] SetOutput {DeviceName} pin {Pin} = {State}", deviceName, pin, state);
        return result;
    }

    public IOError SetOutputByName(string deviceName, string pinName, bool state)
    {
        if (!_devices.TryGetValue(deviceName, out var device))
            return IOError.DeviceNotFound;
        var result = device.SetOutputByName(pinName, state);
        if (result == IOError.Success)
            Log.Information("[IO] SetOutputByName {DeviceName} pin '{PinName}' = {State}", deviceName, pinName, state);
        return result;
    }

    public IIODevice? GetDevice(string name)
    {
        _devices.TryGetValue(name, out var device);
        return device;
    }

    public List<string> GetAllDeviceNames() => _devices.Keys.ToList();

    public void StartPolling(int intervalMs = 100)
    {
        StopPolling();
        _pollCts = new CancellationTokenSource();
        _pollTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(intervalMs));
        _pollTask = PollLoopAsync(_pollCts.Token);
        Log.Information("[IO] Polling started with interval {IntervalMs} ms", intervalMs);
    }

    public void StopPolling()
    {
        _pollCts?.Cancel();
        _pollTimer?.Dispose();
        _pollTimer = null;
        try { _pollTask?.Wait(1000); } catch { }
        _pollCts?.Dispose();
        _pollCts = null;
        _pollTask = null;
        Log.Information("[IO] Polling stopped");
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _pollTimer != null)
        {
            try
            {
                if (!await _pollTimer.WaitForNextTickAsync(ct))
                    break;
            }
            catch (OperationCanceledException) { break; }

            foreach (var kvp in _devices)
            {
                if (ct.IsCancellationRequested) break;
                if (!kvp.Value.IsConnected) continue;

                uint inputs = 0, latch = 0, outputs = 0;
                var inResult = kvp.Value.ReadInputs(out inputs, out latch);
                var outResult = kvp.Value.ReadAllOutputs(out outputs);

                if (inResult == IOError.Success || outResult == IOError.Success)
                {
                    var status = new IOStatus
                    {
                        Inputs = inputs,
                        Latch = latch,
                        Outputs = outputs,
                        Valid = inResult == IOError.Success && outResult == IOError.Success
                    };
                    OnIOStatusUpdated?.Invoke(kvp.Key, status);
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopPolling();
        DisconnectAll();
    }
}
