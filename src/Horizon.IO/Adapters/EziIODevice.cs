using System.Net;
using Horizon.IO.Interfaces;
using Horizon.IO.Models;
using Horizon.IO.Native;

namespace Horizon.IO.Adapters;

public sealed class EziIODevice : IIODevice
{
    private readonly int _boardId;
    private readonly IPAddress _ipAddress;
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
        DeviceName = config.Name;
        InputCount = config.InputCount;
        OutputCount = config.OutputCount;
    }

    public bool Connect()
    {
        lock (_lock)
        {
            if (_connected) return true;
            _connected = EziNative.Connect(_ipAddress, _boardId);
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
            var result = EziNative.FAS_GetInput(_boardId, ref inputs, ref latch);
            return result == EziNative.FMM_OK ? IOError.Success : IOError.OperationFailed;
        }
    }

    public IOError SetOutput(int pin, bool state)
    {
        lock (_lock)
        {
            if (!_connected) return IOError.Disconnected;
            if (pin < 0 || pin >= OutputCount) return IOError.InvalidPin;

            uint setMask = state ? (1u << pin) : 0u;
            uint clearMask = state ? 0u : (1u << pin);
            var result = EziNative.FAS_SetOutput(_boardId, setMask, clearMask);
            return result == EziNative.FMM_OK ? IOError.Success : IOError.OperationFailed;
        }
    }

    public IOError SetOutputByName(string pinName, bool state)
    {
        if (!_outputPinNames.TryGetValue(pinName, out var pin))
        {
            // Also check input pins in case of bidirectional
            if (!_inputPinNames.TryGetValue(pinName, out pin))
                return IOError.InvalidPin;
        }
        return SetOutput(pin, state);
    }

    public IOError ReadAllOutputs(out uint outputs)
    {
        outputs = 0;
        uint status = 0;
        lock (_lock)
        {
            if (!_connected) return IOError.Disconnected;
            var result = EziNative.FAS_GetOutput(_boardId, ref outputs, ref status);
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
}
