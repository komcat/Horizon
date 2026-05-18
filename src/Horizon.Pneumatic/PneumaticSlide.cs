using System.Diagnostics;
using Horizon.IO;
using Horizon.IO.Models;
using Horizon.Pneumatic.Interfaces;
using Horizon.Pneumatic.Models;
using Serilog;

namespace Horizon.Pneumatic;

public sealed class PneumaticSlide : IPneumaticSlide
{
    private readonly EziIOManager _ioManager;
    private readonly PneumaticConfig _config;
    private long _lastMoveTimeMs;

    public string SlideName => _config.Name;

    public PneumaticSlide(PneumaticConfig config, EziIOManager ioManager)
    {
        _config = config;
        _ioManager = ioManager;
    }

    public PneumaticError Extend()
    {
        var result = _ioManager.SetOutputByName(
            _config.Output.DeviceName,
            _config.Output.PinName,
            true);

        if (result == IOError.DeviceNotFound) { Log.Error("[Pneumatic] '{Slide}' Extend: {Result}", SlideName, PneumaticError.DeviceNotFound); return PneumaticError.DeviceNotFound; }
        if (result == IOError.InvalidPin) { Log.Error("[Pneumatic] '{Slide}' Extend: {Result}", SlideName, PneumaticError.PinNotFound); return PneumaticError.PinNotFound; }
        if (result != IOError.Success) { Log.Error("[Pneumatic] '{Slide}' Extend: {Result}", SlideName, PneumaticError.OperationFailed); return PneumaticError.OperationFailed; }

        _lastMoveTimeMs = Stopwatch.GetTimestamp();
        Log.Information("[Pneumatic] '{Slide}' Extend: {Result}", SlideName, PneumaticError.Success);
        return PneumaticError.Success;
    }

    public PneumaticError Retract()
    {
        var result = _ioManager.SetOutputByName(
            _config.Output.DeviceName,
            _config.Output.PinName,
            false);

        if (result == IOError.DeviceNotFound) { Log.Error("[Pneumatic] '{Slide}' Retract: {Result}", SlideName, PneumaticError.DeviceNotFound); return PneumaticError.DeviceNotFound; }
        if (result == IOError.InvalidPin) { Log.Error("[Pneumatic] '{Slide}' Retract: {Result}", SlideName, PneumaticError.PinNotFound); return PneumaticError.PinNotFound; }
        if (result != IOError.Success) { Log.Error("[Pneumatic] '{Slide}' Retract: {Result}", SlideName, PneumaticError.OperationFailed); return PneumaticError.OperationFailed; }

        _lastMoveTimeMs = Stopwatch.GetTimestamp();
        Log.Information("[Pneumatic] '{Slide}' Retract: {Result}", SlideName, PneumaticError.Success);
        return PneumaticError.Success;
    }

    public PneumaticError ExtendAndWait(int pollIntervalMs = 50, CancellationToken ct = default)
    {
        var err = Extend();
        if (err != PneumaticError.Success) return err;

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < _config.TimeoutMs)
        {
            ct.ThrowIfCancellationRequested();   // a Stop interrupts the wait
            var pos = GetPosition();
            if (pos == PneumaticPosition.Extended) return PneumaticError.Success;
            if (pos == PneumaticPosition.HardwareError) return PneumaticError.HardwareError;
            Thread.Sleep(pollIntervalMs);
        }

        Log.Error("[Pneumatic] '{Slide}' ExtendAndWait timed out after {TimeoutMs}ms", SlideName, _config.TimeoutMs);
        return PneumaticError.Timeout;
    }

    public PneumaticError RetractAndWait(int pollIntervalMs = 50, CancellationToken ct = default)
    {
        var err = Retract();
        if (err != PneumaticError.Success) return err;

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < _config.TimeoutMs)
        {
            ct.ThrowIfCancellationRequested();   // a Stop interrupts the wait
            var pos = GetPosition();
            if (pos == PneumaticPosition.Retracted) return PneumaticError.Success;
            if (pos == PneumaticPosition.HardwareError) return PneumaticError.HardwareError;
            Thread.Sleep(pollIntervalMs);
        }

        Log.Error("[Pneumatic] '{Slide}' RetractAndWait timed out after {TimeoutMs}ms", SlideName, _config.TimeoutMs);
        return PneumaticError.Timeout;
    }

    public PneumaticPosition GetPosition()
    {
        bool extended = ReadSensor(_config.ExtendedSensor);
        bool retracted = ReadSensor(_config.RetractedSensor);

        if (extended && retracted) return PneumaticPosition.HardwareError;
        if (extended) return PneumaticPosition.Extended;
        if (retracted) return PneumaticPosition.Retracted;
        return PneumaticPosition.Unknown;
    }

    public PneumaticStatus GetStatus()
    {
        bool extended = ReadSensor(_config.ExtendedSensor);
        bool retracted = ReadSensor(_config.RetractedSensor);
        bool outputActive = ReadOutputState();

        return new PneumaticStatus
        {
            Position = GetPosition(),
            OutputActive = outputActive,
            ExtendedSensorActive = extended,
            RetractedSensorActive = retracted,
            LastMoveTimeMs = _lastMoveTimeMs,
            Valid = true
        };
    }

    private bool ReadSensor(IOPinReference pinRef)
    {
        var device = _ioManager.GetDevice(pinRef.DeviceName);
        if (device == null) return false;

        var pin = device.GetPinByName(pinRef.PinName);
        if (pin == null) return false;

        var err = device.ReadInputs(out uint inputs);
        if (err != IOError.Success) return false;

        return (inputs & (1u << pin.Value)) != 0;
    }

    private bool ReadOutputState()
    {
        var device = _ioManager.GetDevice(_config.Output.DeviceName);
        if (device == null) return false;

        var pin = device.GetPinByName(_config.Output.PinName);
        if (pin == null) return false;

        var err = device.ReadAllOutputs(out uint outputs);
        if (err != IOError.Success) return false;

        return (outputs & (1u << pin.Value)) != 0;
    }
}
