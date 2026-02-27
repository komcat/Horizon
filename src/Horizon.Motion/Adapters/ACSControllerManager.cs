using Horizon.Motion.Interfaces;
using Horizon.Motion.Models;

namespace Horizon.Motion.Adapters;

public sealed class ACSControllerManager : IMotionControllerManager
{
    private readonly Dictionary<string, ACSMotionController> _controllers = new(StringComparer.OrdinalIgnoreCase);

    public string ControllerType => "ACS";

    public void AddDevice(MotionDevice device)
    {
        if (!device.Enabled) return;
        _controllers[device.Name] = new ACSMotionController(device);
    }

    public bool ConnectAll()
    {
        bool allOk = true;
        foreach (var kvp in _controllers)
            if (!kvp.Value.Connect()) allOk = false;
        return allOk;
    }

    public void DisconnectAll()
    {
        foreach (var kvp in _controllers)
            kvp.Value.Disconnect();
    }

    public bool ConnectDevice(string name) =>
        _controllers.TryGetValue(name, out var c) && c.Connect();

    public IMotionController? GetController(string deviceName) =>
        _controllers.TryGetValue(deviceName, out var c) ? c : null;

    public List<string> GetDeviceNames() => _controllers.Keys.ToList();
}
