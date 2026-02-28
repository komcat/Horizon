using Horizon.IO.Models;

namespace Horizon.IO.Interfaces;

public interface IIODevice
{
    bool Connect();
    bool Disconnect();
    bool IsConnected { get; }

    IOError ReadInputs(out uint inputs);
    IOError ReadInputs(out uint inputs, out uint latch);
    IOError SetOutput(int pin, bool state);
    IOError SetOutputByName(string pinName, bool state);
    IOError ReadAllOutputs(out uint outputs);

    string DeviceName { get; }
    int InputCount { get; }
    int OutputCount { get; }

    void SetPinName(bool isInput, int pin, string name);
    int? GetPinByName(string name);
    string? GetPinName(bool isInput, int pin);
}
