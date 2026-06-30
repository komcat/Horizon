using System.Net;
using Horizon.IO.Interfaces;
using Horizon.IO.Models;
using Horizon.IO.Native;
using Serilog;

namespace Horizon.IO.Adapters;

public sealed class EziIODevice : IIODevice
{
    private readonly int _boardId;
    private readonly IPAddress _ipAddress;
    private readonly EziDeviceType _deviceType;
    private readonly object _lock = new();
    private bool _connected;

    private readonly Dictionary<string, int> _inputPinNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _outputPinNames = new(StringComparer.OrdinalIgnoreCase);

    public string DeviceName { get; }
    public int InputCount { get; }
    public int OutputCount { get; }
    public bool IsConnected => _connected;

    public EziIODevice(EziIODeviceConfig config)
    {
        _boardId = config.DeviceId;
        _ipAddress = IPAddress.Parse(config.IpAddress);
        _deviceType = config.DeviceType;
        DeviceName = config.Name;
        InputCount = config.InputCount;
        OutputCount = config.OutputCount;
    }

    public bool Connect()
    {
        lock (_lock)
        {
            if (_connected) return true;
            Log.Information("[IO] Connecting to {DeviceName} at {IpAddress}", DeviceName, _ipAddress);
            _connected = EziNative.Connect(_ipAddress, _boardId);
            if (_connected)
                Log.Information("[IO] Connect OK: {DeviceName} ({IpAddress})", DeviceName, _ipAddress);
            else
                Log.Error("[IO] Connect FAILED: {DeviceName} ({IpAddress})", DeviceName, _ipAddress);
            return _connected;
        }
    }

    public bool Disconnect()
    {
        lock (_lock)
        {
            if (!_connected) return true;
            EziNative.FAS_Close(_boardId);
            _connected = false;
            Log.Information("[IO] Disconnected: {DeviceName}", DeviceName);
            return true;
        }
    }

    public IOError ReadInputs(out uint inputs)
    {
        inputs = 0;
        uint latch = 0;
        return ReadInputs(out inputs, out latch);
    }

    public IOError ReadInputs(out uint inputs, out uint latch)
    {
        inputs = 0;
        latch = 0;
        lock (_lock)
        {
            if (!_connected) return IOError.Disconnected;

            int result;
            if (_deviceType == EziDeviceType.MotorIO)
            {
                // Motor controller built-in IO
                result = EziNative.FAS_GetIOInput(_boardId, ref inputs);
                // No latch concept for motor IO
            }
            else
            {
                // Standalone IO module
                result = EziNative.FAS_GetInput(_boardId, ref inputs, ref latch);
            }
            return result == EziNative.FMM_OK ? IOError.Success : IOError.OperationFailed;
        }
    }

    public IOError SetOutput(int pin, bool state)
    {
        lock (_lock)
        {
            if (!_connected) return IOError.Disconnected;
            if (pin < 0 || pin >= OutputCount) return IOError.InvalidPin;

            int result;
            if (_deviceType == EziDeviceType.MotorIO)
            {
                uint setMask = state ? (1u << pin) : 0u;
                uint clearMask = state ? 0u : (1u << pin);
                result = EziNative.FAS_SetIOOutput(_boardId, setMask, clearMask);
            }
            else
            {
                // Ezi-IO uses combined register: bits [0..InputCount-1] = inputs,
                // bits [InputCount..InputCount+OutputCount-1] = outputs
                int bitPos = pin + InputCount;
                uint setMask = state ? (1u << bitPos) : 0u;
                uint clearMask = state ? 0u : (1u << bitPos);
                result = EziNative.FAS_SetOutput(_boardId, setMask, clearMask);
            }

            var error = result == EziNative.FMM_OK ? IOError.Success : IOError.OperationFailed;
            if (error != IOError.Success)
                Log.Error("[IO] SetOutput FAILED on {DeviceName} pin {Pin} = {State} (native result {Result})", DeviceName, pin, state, result);
            return error;
        }
    }

    public IOError SetOutputByName(string pinName, bool state)
    {
        if (!_outputPinNames.TryGetValue(pinName, out var pin))
        {
            // Also check input pins in case of bidirectional
            if (!_inputPinNames.TryGetValue(pinName, out pin))
            {
                Log.Warning("[IO] SetOutputByName unknown pin '{PinName}' on {DeviceName}", pinName, DeviceName);
                return IOError.InvalidPin;
            }
        }
        return SetOutput(pin, state);
    }

    public IOError ReadOutputByName(string pinName, out bool state)
    {
        state = false;
        // Resolve via the OUTPUT map specifically (an input could share a name).
        if (!_outputPinNames.TryGetValue(pinName, out var pin))
        {
            Log.Warning("[IO] ReadOutputByName unknown output '{PinName}' on {DeviceName}", pinName, DeviceName);
            return IOError.InvalidPin;
        }
        var result = ReadAllOutputs(out uint outputs);   // already shifted so output pin N is at bit N
        if (result != IOError.Success) return result;
        state = (outputs & (1u << pin)) != 0;
        return IOError.Success;
    }

    public IOError ReadAllOutputs(out uint outputs)
    {
        return ReadAllOutputs(out outputs, out _);
    }

    public IOError ReadAllOutputs(out uint outputs, out uint status)
    {
        outputs = 0;
        status = 0;
        lock (_lock)
        {
            if (!_connected) return IOError.Disconnected;

            int result;
            if (_deviceType == EziDeviceType.MotorIO)
            {
                // Motor controller built-in IO
                result = EziNative.FAS_GetIOOutput(_boardId, ref outputs);
                status = outputs;
            }
            else
            {
                // Standalone IO module — combined register, shift outputs down to bit 0
                result = EziNative.FAS_GetOutput(_boardId, ref outputs, ref status);
                outputs >>= InputCount;
                status >>= InputCount;
            }
            return result == EziNative.FMM_OK ? IOError.Success : IOError.OperationFailed;
        }
    }

    public void SetPinName(bool isInput, int pin, string name)
    {
        var dict = isInput ? _inputPinNames : _outputPinNames;
        dict[name] = pin;
    }

    public int? GetPinByName(string name)
    {
        if (_inputPinNames.TryGetValue(name, out var pin)) return pin;
        if (_outputPinNames.TryGetValue(name, out pin)) return pin;
        return null;
    }

    public string? GetPinName(bool isInput, int pin)
    {
        var dict = isInput ? _inputPinNames : _outputPinNames;
        foreach (var kvp in dict)
        {
            if (kvp.Value == pin)
                return kvp.Key;
        }
        return null;
    }
}
