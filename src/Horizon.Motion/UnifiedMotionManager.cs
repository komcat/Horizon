using Horizon.Motion.Adapters;
using Horizon.Motion.Interfaces;
using Horizon.Motion.Models;

namespace Horizon.Motion;

public sealed class UnifiedMotionManager
{
    private readonly Dictionary<string, IMotionControllerManager> _managers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _deviceTypes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, PositionStruct>> _namedPositions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, PositionStruct>> _teachPositions = new(StringComparer.OrdinalIgnoreCase);

    public event Action? PresetsChanged;

    public UnifiedMotionManager()
    {
        _managers["ACS"] = new ACSControllerManager();
        _managers["PI"] = new PIControllerManager();
    }

    public void AddDevice(MotionDevice device)
    {
        var type = device.ControllerType.ToUpperInvariant();
        if (!_managers.TryGetValue(type, out var manager)) return;
        manager.AddDevice(device);
        _deviceTypes[device.Name] = type;

        if (device.Positions.Count > 0)
            _namedPositions[device.Name] = new Dictionary<string, PositionStruct>(device.Positions, StringComparer.OrdinalIgnoreCase);
    }

    public bool ConnectAll()
    {
        bool allOk = true;
        foreach (var mgr in _managers.Values)
            if (!mgr.ConnectAll()) allOk = false;
        return allOk;
    }

    public void DisconnectAll()
    {
        foreach (var mgr in _managers.Values)
            mgr.DisconnectAll();
    }

    public bool ConnectDevice(string name)
    {
        if (!_deviceTypes.TryGetValue(name, out var type)) return false;
        return _managers[type].ConnectDevice(name);
    }

    public IMotionController? GetController(string deviceName)
    {
        if (!_deviceTypes.TryGetValue(deviceName, out var type)) return null;
        return _managers[type].GetController(deviceName);
    }

    public bool MoveToAbs(string device, string axis, double position, bool blocking = true)
    {
        var controller = GetController(device);
        return controller?.MoveToPosition(axis, position, blocking) ?? false;
    }

    public bool MoveToAbs6(string device, PositionStruct pos, bool blocking = true)
    {
        var controller = GetController(device);
        if (controller == null) return false;
        var axes = controller.AxisNames;
        var values = new[] { pos.X, pos.Y, pos.Z, pos.U, pos.V, pos.W };
        for (int i = 0; i < Math.Min(axes.Count, 6); i++)
            if (!controller.MoveToPosition(axes[i], values[i], blocking)) return false;
        return true;
    }

    public bool MoveToNamedPosition(string device, string positionName, bool blocking = true)
    {
        var pos = GetNamedPosition(device, positionName);
        if (pos == null) return false;
        return MoveToAbs6(device, pos, blocking);
    }

    public bool MoveRelative(string device, string axis, double distance, bool blocking = true)
    {
        var controller = GetController(device);
        return controller?.MoveRelative(axis, distance, blocking) ?? false;
    }

    public PositionStruct? GetCurrentPosition(string device)
    {
        return GetController(device)?.GetCurrentPosition();
    }

    public bool IsMoving(string device)
    {
        return GetController(device)?.IsDeviceInMotion() ?? false;
    }

    public bool StopAll(string device)
    {
        return GetController(device)?.StopAll() ?? false;
    }

    public void StopAllDevices()
    {
        foreach (var name in GetAllDeviceNames())
            StopAll(name);
    }

    public List<string> GetAllDeviceNames()
    {
        return _managers.Values.SelectMany(m => m.GetDeviceNames()).ToList();
    }

    public List<string> GetNamedPositionNames(string device)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (_namedPositions.TryGetValue(device, out var configPositions))
            foreach (var key in configPositions.Keys)
                names.Add(key);

        if (_teachPositions.TryGetValue(device, out var teachPositions))
            foreach (var key in teachPositions.Keys)
                names.Add(key);

        return names.ToList();
    }

    public PositionStruct? GetNamedPosition(string device, string positionName)
    {
        // Teach positions override config positions
        if (_teachPositions.TryGetValue(device, out var teachPositions)
            && teachPositions.TryGetValue(positionName, out var teachPos))
            return teachPos;

        if (_namedPositions.TryGetValue(device, out var configPositions))
            return configPositions.GetValueOrDefault(positionName);

        return null;
    }

    public void SetTeachPosition(string device, string positionName, PositionStruct position)
    {
        if (!_teachPositions.TryGetValue(device, out var positions))
        {
            positions = new Dictionary<string, PositionStruct>(StringComparer.OrdinalIgnoreCase);
            _teachPositions[device] = positions;
        }
        positions[positionName] = position;
        PresetsChanged?.Invoke();
    }

    public void RemoveTeachPosition(string device, string positionName)
    {
        if (_teachPositions.TryGetValue(device, out var positions))
        {
            positions.Remove(positionName);
            if (positions.Count == 0)
                _teachPositions.Remove(device);
            PresetsChanged?.Invoke();
        }
    }

    public void ClearTeachPositions()
    {
        _teachPositions.Clear();
        PresetsChanged?.Invoke();
    }
}
