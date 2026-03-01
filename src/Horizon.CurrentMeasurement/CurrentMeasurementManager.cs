using System.Collections.Concurrent;
using Horizon.CurrentMeasurement.Adapters;
using Horizon.CurrentMeasurement.Interfaces;
using Horizon.CurrentMeasurement.Models;

namespace Horizon.CurrentMeasurement;

public sealed class CurrentMeasurementManager : IDisposable
{
    private readonly ConcurrentDictionary<string, ICurrentMeasurementDevice> _devices = new(StringComparer.OrdinalIgnoreCase);
    private PeriodicTimer? _pollTimer;
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;
    private bool _disposed;

    public event Action<string, CurrentMeasurementStatus>? OnStatusUpdated;

    public void AddDevice(CurrentMeasurementDeviceConfig config)
    {
        if (!config.Enabled) return;

        ICurrentMeasurementDevice device = config.Type.ToLowerInvariant() switch
        {
            "keithley_6482" => new Keithley6482Adapter(config),
            _ => throw new ArgumentException($"Unknown current measurement device type: {config.Type}")
        };

        _devices[config.Name] = device;
    }

    public void RemoveDevice(string name)
    {
        if (_devices.TryRemove(name, out var device))
            device.Disconnect();
    }

    public bool ConnectAll()
    {
        bool allOk = true;
        foreach (var kvp in _devices)
        {
            if (!kvp.Value.Connect())
                allOk = false;
        }
        return allOk;
    }

    public void DisconnectAll()
    {
        foreach (var kvp in _devices)
            kvp.Value.Disconnect();
    }

    public bool ConnectDevice(string name)
    {
        return _devices.TryGetValue(name, out var device) && device.Connect();
    }

    public bool DisconnectDevice(string name)
    {
        return _devices.TryGetValue(name, out var device) && device.Disconnect();
    }

    public CurrentMeasurementError ReadCurrent(string deviceName, int channel, out double current)
    {
        current = 0;
        if (!_devices.TryGetValue(deviceName, out var device))
            return CurrentMeasurementError.DeviceNotFound;
        return device.ReadCurrent(channel, out current);
    }

    public CurrentMeasurementError ReadAllChannels(string deviceName, out double[] currents)
    {
        currents = Array.Empty<double>();
        if (!_devices.TryGetValue(deviceName, out var device))
            return CurrentMeasurementError.DeviceNotFound;
        return device.ReadAllChannels(out currents);
    }

    public CurrentMeasurementError GetIdentity(string deviceName, out string identity)
    {
        identity = string.Empty;
        if (!_devices.TryGetValue(deviceName, out var device))
            return CurrentMeasurementError.DeviceNotFound;
        return device.GetIdentity(out identity);
    }

    public CurrentMeasurementError ResetDevice(string deviceName)
    {
        if (!_devices.TryGetValue(deviceName, out var device))
            return CurrentMeasurementError.DeviceNotFound;
        return device.Reset();
    }

    public ICurrentMeasurementDevice? GetDevice(string name)
    {
        _devices.TryGetValue(name, out var device);
        return device;
    }

    public List<string> GetAllDeviceNames() => _devices.Keys.ToList();

    public void StartPolling(int intervalMs = 500)
    {
        StopPolling();
        _pollCts = new CancellationTokenSource();
        _pollTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(intervalMs));
        _pollTask = PollLoopAsync(_pollCts.Token);
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

                var result = kvp.Value.ReadAllChannels(out var currents);
                var status = new CurrentMeasurementStatus
                {
                    ChannelReadings = currents,
                    Unit = "A",
                    Valid = result == CurrentMeasurementError.Success,
                    Timestamp = DateTimeOffset.UtcNow
                };
                OnStatusUpdated?.Invoke(kvp.Key, status);
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
