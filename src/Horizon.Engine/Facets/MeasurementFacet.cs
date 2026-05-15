using System.Collections.Concurrent;
using Horizon.Common;
using Horizon.CurrentMeasurement;
using Horizon.CurrentMeasurement.Models;

namespace Horizon.Engine;

/// <summary>
/// Current-measurement facet — wraps the kept <see cref="CurrentMeasurementManager"/>
/// behind engine-owned types. Reuses the manager's built-in poll loop; the
/// Keithley adapter locks internally, so no facet semaphore is needed.
/// </summary>
internal sealed class MeasurementFacet : IMeasurementFacet
{
    private readonly CurrentMeasurementManager _manager;
    private readonly Action<HardwareStatusChanged> _emitStatus;
    private readonly Action<LogLevel, string> _log;

    private readonly Dictionary<string, MeasurementDeviceInfo> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, MeasurementState> _state = new(StringComparer.OrdinalIgnoreCase);

    public MeasurementFacet(
        CurrentMeasurementManager manager,
        Action<HardwareStatusChanged> emitStatus,
        Action<LogLevel, string> log)
    {
        _manager = manager;
        _emitStatus = emitStatus;
        _log = log;

        foreach (var name in _manager.GetAllDeviceNames())
        {
            var dev = _manager.GetDevice(name);
            var channelCount = dev?.ChannelCount ?? 0;
            var enabled = dev?.EnabledChannels?.ToList() ?? new List<int>();
            _devices[name] = new MeasurementDeviceInfo(name, channelCount, enabled);
            _state[name] = MeasurementState.Unknown;
        }

        _manager.OnStatusUpdated += OnManagerStatus;
    }

    public IReadOnlyList<string> DeviceNames => _devices.Keys.ToList();

    public MeasurementDeviceInfo? GetDevice(string device)
        => _devices.TryGetValue(device, out var info) ? info : null;

    public MeasurementState GetState(string device)
        => _state.TryGetValue(device, out var s) ? s : MeasurementState.Unknown;

    public bool IsConnected(string device)
        => _manager.GetDevice(device)?.IsConnected ?? false;

    public Task<Result> ConnectAsync(string device, CancellationToken ct = default)
        => Task.Run(() =>
        {
            if (!_devices.ContainsKey(device))
                return Result.Fail(ErrorCode.CMeasDeviceNotFound, $"Measurement device '{device}' not found.");
            return _manager.ConnectDevice(device)
                ? Result.Ok()
                : Result.Fail(ErrorCode.CMeasCommunicationError, $"Measurement device '{device}' failed to connect.");
        }, ct);

    public Task<Result> DisconnectAsync(string device)
        => Task.Run(() => _manager.DisconnectDevice(device)
            ? Result.Ok()
            : Result.Fail(ErrorCode.CMeasDeviceNotFound, $"Measurement device '{device}' not found."));

    public Task<Result> SetVoltageAsync(string device, double volts)
        => Task.Run(() => Map(_manager.SetVoltage(device, volts), $"SetVoltage '{device}' = {volts}V"));

    public Task<Result> SetVoltageEnabledAsync(string device, bool enabled)
        => Task.Run(() => Map(_manager.SetVoltageEnabled(device, enabled), $"VoltageOutput '{device}' = {enabled}"));

    public Task<Result> ResetAsync(string device)
        => Task.Run(() => Map(_manager.ResetDevice(device), $"Reset '{device}'"));

    public Task<Result<string>> GetIdentityAsync(string device)
        => Task.Run(() =>
        {
            var err = _manager.GetIdentity(device, out var identity);
            return err == CurrentMeasurementError.Success
                ? Result<string>.Ok(identity)
                : Result<string>.Fail(MapCode(err), $"GetIdentity '{device}' failed: {err}.");
        });

    /// <summary>Connects all devices and starts polling. Called by the engine during ConnectAll.</summary>
    public async Task ConnectAllInternalAsync(IProgress<string>? progress)
    {
        if (_devices.Count == 0)
        {
            progress?.Report("Measurement: no devices configured.");
            return;
        }

        progress?.Report($"Measurement: connecting {_devices.Count} device(s)…");
        await Task.Run(_manager.ConnectAll).ConfigureAwait(false);
        _manager.StartPolling();

        var connected = _devices.Keys.Count(IsConnected);
        progress?.Report($"Measurement: {connected}/{_devices.Count} device(s) connected.");
        _log(connected == _devices.Count ? LogLevel.Info : LogLevel.Warning,
            $"Measurement ConnectAll — {connected}/{_devices.Count} device(s) connected.");
    }

    public event Action<MeasurementChanged>? StateChanged;

    // --- internals ---

    private void OnManagerStatus(string device, CurrentMeasurementStatus status)
    {
        var next = new MeasurementState(status.Valid, status.ChannelReadings, status.Unit);
        _state[device] = next;
        StateChanged?.Invoke(new MeasurementChanged(device, next));
        _emitStatus(new HardwareStatusChanged("Measurement", device));
    }

    private static Result Map(CurrentMeasurementError error, string what)
        => error == CurrentMeasurementError.Success
            ? Result.Ok()
            : Result.Fail(MapCode(error), $"{what} — {error}.");

    private static ErrorCode MapCode(CurrentMeasurementError error) => error switch
    {
        CurrentMeasurementError.DeviceNotFound      => ErrorCode.CMeasDeviceNotFound,
        CurrentMeasurementError.Disconnected        => ErrorCode.CMeasDisconnected,
        CurrentMeasurementError.InvalidChannel      => ErrorCode.CMeasInvalidChannel,
        CurrentMeasurementError.CommunicationError  => ErrorCode.CMeasCommunicationError,
        CurrentMeasurementError.ParseError          => ErrorCode.CMeasParseError,
        _                                           => ErrorCode.CMeasOperationFailed,
    };
}
