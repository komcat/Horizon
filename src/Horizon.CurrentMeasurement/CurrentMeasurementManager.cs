using System.Collections.Concurrent;
using Horizon.CurrentMeasurement.Adapters;
using Horizon.CurrentMeasurement.Interfaces;
using Horizon.CurrentMeasurement.Models;
using Serilog;

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
        Log.Information("[CMeas] Device added: {Name} ({Type})", config.Name, config.Type);
    }

    public void RemoveDevice(string name)
    {
        if (_devices.TryRemove(name, out var device))
        {
            device.Disconnect();
            Log.Information("[CMeas] Device removed: {Name}", name);
        }
    }

    public bool ConnectAll()
    {
        Log.Information("[CMeas] ConnectAll starting for {Count} device(s)", _devices.Count);
        bool allOk = true;
        foreach (var kvp in _devices)
        {
            if (!kvp.Value.Connect())
                allOk = false;
        }
        if (allOk)
            Log.Information("[CMeas] ConnectAll OK — all devices connected");
        else
            Log.Warning("[CMeas] ConnectAll partial failure — one or more devices failed to connect");
        return allOk;
    }

    public void DisconnectAll()
    {
        Log.Information("[CMeas] DisconnectAll starting for {Count} device(s)", _devices.Count);
        foreach (var kvp in _devices)
            kvp.Value.Disconnect();
    }

    public bool ConnectDevice(string name)
    {
        var result = _devices.TryGetValue(name, out var device) && device.Connect();
        Log.Information("[CMeas] ConnectDevice {Name}: {Result}", name, result ? "OK" : "FAILED");
        return result;
    }

    public bool DisconnectDevice(string name)
    {
        var result = _devices.TryGetValue(name, out var device) && device.Disconnect();
        Log.Information("[CMeas] DisconnectDevice {Name}: {Result}", name, result ? "OK" : "FAILED");
        return result;
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
        var result = device.Reset();
        Log.Information("[CMeas] ResetDevice {Name}: {Result}", deviceName, result);
        return result;
    }

    public CurrentMeasurementError SetVoltage(string deviceName, double volts)
    {
        if (!_devices.TryGetValue(deviceName, out var device))
            return CurrentMeasurementError.DeviceNotFound;
        var result = device.SetVoltage(volts);
        Log.Information("[CMeas] SetVoltage {Name}: {Volts}V — {Result}", deviceName, volts, result);
        return result;
    }

    public CurrentMeasurementError SetVoltageEnabled(string deviceName, bool enabled)
    {
        if (!_devices.TryGetValue(deviceName, out var device))
            return CurrentMeasurementError.DeviceNotFound;
        var result = device.SetVoltageEnabled(enabled);
        Log.Information("[CMeas] SetVoltageEnabled {Name}: {Enabled} — {Result}", deviceName, enabled, result);
        return result;
    }

    public ICurrentMeasurementDevice? GetDevice(string name)
    {
        _devices.TryGetValue(name, out var device);
        return device;
    }

    public List<string> GetAllDeviceNames() => _devices.Keys.ToList();

    public void StartPolling(int intervalMs = 500)
    {
        if (_disposed) return;   // never resurrect a poll loop after shutdown
        StopPolling();
        _pollCts = new CancellationTokenSource();
        _pollTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(intervalMs));
        _pollTask = PollLoopAsync(_pollCts.Token);
        Log.Information("[CMeas] Polling started with interval {IntervalMs} ms", intervalMs);
    }

    /// <summary>
    /// Start fast polling — configures NPLC=0.1 and runs a tight read loop (~20+ Hz).
    /// Same behavior as the UI "Start" button on CurrentMeasurementPanel.
    /// </summary>
    public bool StartFastPolling(string deviceName)
    {
        if (_disposed) return false;   // never resurrect a poll loop after shutdown
        if (!_devices.TryGetValue(deviceName, out var device) || !device.IsConnected)
            return false;

        StopPolling(); // stop any existing polling

        var result = device.ConfigureFastMode();
        if (result != CurrentMeasurementError.Success)
        {
            Log.Error("[CMeas] FastPoll: ConfigureFastMode failed for {Name} — {Error}", deviceName, result);
            return false;
        }

        _pollCts = new CancellationTokenSource();
        _pollTask = FastPollLoopAsync(deviceName, device, _pollCts.Token);
        Log.Information("[CMeas] Fast polling started for {Name}", deviceName);
        return true;
    }

    public void StopFastPolling(string deviceName)
    {
        StopPolling(); // stops any active poll task

        // Restore default mode
        if (_devices.TryGetValue(deviceName, out var device) && device.IsConnected)
        {
            device.ConfigureDefaultMode();
            Log.Information("[CMeas] Fast polling stopped for {Name}, default mode restored", deviceName);
        }
    }

    public void StopPolling()
    {
        _pollCts?.Cancel();
        _pollTimer?.Dispose();
        _pollTimer = null;
        try { _pollTask?.Wait(2000); } catch { }
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

    private async Task FastPollLoopAsync(string deviceName, ICurrentMeasurementDevice device, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var (error, readings) = await Task.Run(() =>
                {
                    var err = device.ReadAllChannels(out var currents);
                    return (err, currents);
                }, ct);

                if (ct.IsCancellationRequested) break;

                if (error == CurrentMeasurementError.Success && readings != null)
                {
                    var status = new CurrentMeasurementStatus
                    {
                        ChannelReadings = readings,
                        Unit = "A",
                        Valid = true,
                        Timestamp = DateTimeOffset.UtcNow
                    };
                    OnStatusUpdated?.Invoke(deviceName, status);
                }
                else
                {
                    try { await Task.Delay(50, ct); } catch { break; }
                }
            }
            catch (OperationCanceledException) { break; }
            catch { try { await Task.Delay(50, ct); } catch { break; } }
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
