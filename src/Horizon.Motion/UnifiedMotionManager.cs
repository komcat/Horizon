using Horizon.Motion.Adapters;
using Horizon.Motion.Interfaces;
using Horizon.Motion.Models;
using Serilog;

namespace Horizon.Motion;

public sealed class UnifiedMotionManager
{
    private readonly Dictionary<string, IMotionControllerManager> _managers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _deviceTypes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, PositionStruct>> _namedPositions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, PositionStruct>> _teachPositions = new(StringComparer.OrdinalIgnoreCase);

    public event Action? PresetsChanged;

    /// <summary>
    /// Optional interlock check delegate. If set, called before every motion command.
    /// Args: (device, axis, source) → (allowed, reason).
    /// Injected by AppContext from InterlockService.
    /// </summary>
    public Func<string, string, int, (bool Allowed, string? Reason)>? InterlockCheck { get; set; }

    /// <summary>Reason string from the last interlock block, or null if last motion was allowed.</summary>
    public string? LastInterlockReason { get; private set; }

    /// <summary>Fired when a motion command is blocked by an interlock. Args: (device, axis, reason).</summary>
    public event Action<string, string, string>? MotionBlocked;

    /// <summary>Current motion source context. Set by callers before motion commands.</summary>
    public int MotionSourceContext { get; set; } // 0=Jog, 1=Process, 2=Recipe, 3=Manual

    private bool CheckInterlock(string device, string axis)
    {
        var check = InterlockCheck;
        if (check == null) { LastInterlockReason = null; return true; }
        var (allowed, reason) = check(device, axis, MotionSourceContext);
        if (!allowed)
        {
            LastInterlockReason = reason;
            Log.Warning("[Motion] Interlock blocked: {Device}.{Axis} — {Reason}", device, axis, reason);
            MotionBlocked?.Invoke(device, axis, reason ?? "Interlock");
            return false;
        }
        LastInterlockReason = null;
        return true;
    }

    public UnifiedMotionManager()
    {
#if HAS_ACS_SDK
        _managers["ACS"] = new ACSControllerManager();
#endif
        _managers["PI"] = new PIControllerManager();
    }

    public void AddDevice(MotionDevice device)
    {
        var type = device.ControllerType.ToUpperInvariant();
        if (!_managers.TryGetValue(type, out var manager)) return;
        manager.AddDevice(device);
        _deviceTypes[device.Name] = type;
        Log.Information("[Motion] Device added: '{Device}' ({Type}, {AxisCount} axes)",
            device.Name, type, device.InstalledAxes.Count);

        if (device.Positions.Count > 0)
            _namedPositions[device.Name] = new Dictionary<string, PositionStruct>(device.Positions, StringComparer.OrdinalIgnoreCase);
    }

    public bool ConnectAll()
    {
        Log.Information("[Motion] ConnectAll requested");
        bool allOk = true;
        foreach (var mgr in _managers.Values)
            if (!mgr.ConnectAll()) allOk = false;
        Log.Information("[Motion] ConnectAll {Result}", allOk ? "OK" : "partial failure");
        return allOk;
    }

    public void DisconnectAll()
    {
        Log.Information("[Motion] DisconnectAll requested");
        foreach (var mgr in _managers.Values)
            mgr.DisconnectAll();
    }

    public bool ConnectDevice(string name)
    {
        if (!_deviceTypes.TryGetValue(name, out var type))
        {
            Log.Warning("[Motion] ConnectDevice '{Device}': unknown device", name);
            return false;
        }
        var ok = _managers[type].ConnectDevice(name);
        Log.Information("[Motion] ConnectDevice '{Device}' ({Type}): {Result}", name, type, ok ? "OK" : "FAILED");
        return ok;
    }

    public IMotionController? GetController(string deviceName)
    {
        if (!_deviceTypes.TryGetValue(deviceName, out var type)) return null;
        return _managers[type].GetController(deviceName);
    }

    public bool MoveToAbs(string device, string axis, double position, bool blocking = true)
    {
        if (!CheckInterlock(device, axis)) return false;
        var controller = GetController(device);
        var ok = controller?.MoveToPosition(axis, position, blocking) ?? false;
        if (ok)
            Log.Information("[Motion] '{Device}' MoveAbs {Axis} -> {Position:F4} (blocking={Blocking})",
                device, axis, position, blocking);
        else
            Log.Error("[Motion] '{Device}' MoveAbs {Axis} -> {Position:F4} FAILED", device, axis, position);
        return ok;
    }

    public bool MoveToAbs6(string device, PositionStruct pos, bool blocking = true)
    {
        var controller = GetController(device);
        if (controller == null)
        {
            Log.Error("[Motion] '{Device}' MoveAbs6 FAILED: controller not found", device);
            return false;
        }
        // Pre-check all axes before moving any (atomic interlock check)
        var axes = controller.AxisNames;
        for (int i = 0; i < Math.Min(axes.Count, 6); i++)
        {
            if (!CheckInterlock(device, axes[i]))
            {
                Log.Warning("[Motion] '{Device}' MoveAbs6 BLOCKED: interlock on axis {Axis}", device, axes[i]);
                return false;
            }
        }
        Log.Information("[Motion] '{Device}' MoveAbs6 X={X:F4} Y={Y:F4} Z={Z:F4} U={U:F4} V={V:F4} W={W:F4}",
            device, pos.X, pos.Y, pos.Z, pos.U, pos.V, pos.W);
        var values = new[] { pos.X, pos.Y, pos.Z, pos.U, pos.V, pos.W };
        int n = Math.Min(axes.Count, 6);
        // Send the pose as one move — a hexapod rejects per-axis moves whose
        // intermediate pose is outside its workspace (see IMotionController.MoveToPositions).
        if (!controller.MoveToPositions(axes.Take(n).ToList(), values.Take(n).ToList(), blocking))
        {
            Log.Error("[Motion] '{Device}' MoveAbs6 FAILED (axes {Axes})", device, string.Join(" ", axes.Take(n)));
            return false;
        }
        return true;
    }

    public bool MoveToNamedPosition(string device, string positionName, bool blocking = true)
    {
        var pos = GetNamedPosition(device, positionName);
        if (pos == null)
        {
            Log.Warning("[Motion] '{Device}' MoveToNamed '{Position}': position not found", device, positionName);
            return false;
        }
        Log.Information("[Motion] '{Device}' MoveToNamed '{Position}'", device, positionName);
        return MoveToAbs6(device, pos, blocking);
    }

    public bool MoveRelative(string device, string axis, double distance, bool blocking = true)
    {
        if (!CheckInterlock(device, axis)) return false;
        var controller = GetController(device);
        var ok = controller?.MoveRelative(axis, distance, blocking) ?? false;
        if (ok)
            Log.Information("[Motion] '{Device}' MoveRel {Axis} {Distance:+0.####;-0.####} (blocking={Blocking})",
                device, axis, distance, blocking);
        else
            Log.Error("[Motion] '{Device}' MoveRel {Axis} {Distance:+0.####;-0.####} FAILED", device, axis, distance);
        return ok;
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
        var ok = GetController(device)?.StopAll() ?? false;
        Log.Information("[Motion] '{Device}' StopAll {Result}", device, ok ? "OK" : "FAILED");
        return ok;
    }

    public bool SetVelocity(string device, string axis, double speed)
    {
        var ok = GetController(device)?.SetVelocity(axis, speed) ?? false;
        if (ok)
            Log.Information("[Motion] '{Device}' SetVelocity {Axis} = {Speed:F3}", device, axis, speed);
        else
            Log.Error("[Motion] '{Device}' SetVelocity {Axis} = {Speed:F3} FAILED", device, axis, speed);
        return ok;
    }

    public double GetVelocity(string device, string axis)
    {
        return GetController(device)?.GetVelocity(axis) ?? 0;
    }

    public bool SetSystemVelocity(string device, double speed)
    {
        var ok = GetController(device)?.SetSystemVelocity(speed) ?? false;
        if (ok)
            Log.Information("[Motion] '{Device}' SetSystemVelocity = {Speed:F3}", device, speed);
        else
            Log.Error("[Motion] '{Device}' SetSystemVelocity = {Speed:F3} FAILED", device, speed);
        return ok;
    }

    public double GetSystemVelocity(string device)
    {
        return GetController(device)?.GetSystemVelocity() ?? 0;
    }

    public void StopAllDevices()
    {
        Log.Warning("[Motion] StopAllDevices — emergency stop all motion");
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
        Log.Information("[Motion] '{Device}' Teach position set: '{Position}' X={X:F4} Y={Y:F4} Z={Z:F4}",
            device, positionName, position.X, position.Y, position.Z);
        PresetsChanged?.Invoke();
    }

    public void RemoveTeachPosition(string device, string positionName)
    {
        if (_teachPositions.TryGetValue(device, out var positions))
        {
            positions.Remove(positionName);
            if (positions.Count == 0)
                _teachPositions.Remove(device);
            Log.Information("[Motion] '{Device}' Teach position removed: '{Position}'", device, positionName);
            PresetsChanged?.Invoke();
        }
    }

    public void ClearTeachPositions()
    {
        _teachPositions.Clear();
        Log.Information("[Motion] All teach positions cleared");
        PresetsChanged?.Invoke();
    }
}
