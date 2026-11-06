using ACS.SPiiPlusNET;
using Horizon.Motion.Interfaces;
using Horizon.Motion.Models;

namespace Horizon.Motion.Adapters;

public sealed class ACSMotionController : IMotionController
{
    private readonly Api _api = new();
    private readonly string _ipAddress;
    private readonly int _port;
    private readonly Dictionary<string, Axis> _axisMap = new(StringComparer.OrdinalIgnoreCase);
    private bool _connected;

    public string ControllerType => "ACS";
    public string DeviceName { get; }
    public List<string> AxisNames { get; }
    public bool IsConnected => _connected;

    public ACSMotionController(MotionDevice device)
    {
        DeviceName = device.Name;
        _ipAddress = device.IpAddress;
        _port = device.Port > 0 ? device.Port : 701;
        AxisNames = new List<string>(device.InstalledAxes);

        // Map axis names to ACS axis enums
        for (int i = 0; i < device.InstalledAxes.Count && i < 8; i++)
        {
            var axisEnum = (Axis)i; // ACSC_AXIS_0 = 0, ACSC_AXIS_1 = 1, etc.
            _axisMap[device.InstalledAxes[i]] = axisEnum;
        }
    }

    public bool Connect()
    {
        try
        {
            if (_connected) return true;
            _api.OpenCommEthernetTCP(_ipAddress, _port);
            _connected = true;
            return true;
        }
        catch { return false; }
    }

    public bool Disconnect()
    {
        try
        {
            if (!_connected) return true;
            _api.CloseComm();
            _connected = false;
            return true;
        }
        catch { _connected = false; return false; }
    }

    public bool MoveToPosition(string axis, double position, bool blocking)
    {
        if (!_connected || !_axisMap.TryGetValue(axis, out var acsAxis)) return false;
        try
        {
            _api.ToPoint(0, acsAxis, position);
            if (blocking) WaitForMotionComplete(acsAxis);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// One coordinated vector move for the whole pose: ToPointM with ACSC_AMF_WAIT plans
    /// every axis but holds the motion, then GoM releases them together — all axes start
    /// at once and travel a straight line at the leading (first) axis's VEL/ACC. The
    /// per-axis default would start each axis a round-trip apart on its own profile (a
    /// dog-leg path, and a half-sent pose if one ToPoint fails). The controller refuses
    /// this while any listed axis is still moving (error 3065). The SPiiPlus axis array
    /// must be ACSC_NONE-terminated (see <see cref="StopAll"/>); the point array is not.
    /// </summary>
    public bool MoveToPositions(IReadOnlyList<string> axes, IReadOnlyList<double> positions, bool blocking)
    {
        if (!_connected || axes.Count == 0 || axes.Count != positions.Count) return false;
        var acsAxes = new Axis[axes.Count + 1];
        for (int i = 0; i < axes.Count; i++)
        {
            if (!_axisMap.TryGetValue(axes[i], out var acsAxis)) return false;
            acsAxes[i] = acsAxis;
        }
        acsAxes[axes.Count] = Axis.ACSC_NONE;
        try
        {
            _api.ToPointM(MotionFlags.ACSC_AMF_WAIT, acsAxes, positions.ToArray());
            _api.GoM(acsAxes);
            if (blocking)
                for (int i = 0; i < axes.Count; i++)
                    WaitForMotionComplete(acsAxes[i]);
            return true;
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[Motion] ACS '{Device}' coordinated move ({Axes}) threw",
                DeviceName, string.Join(" ", axes));
            return false;
        }
    }

    public bool MoveRelative(string axis, double distance, bool blocking)
    {
        if (!_connected || !_axisMap.TryGetValue(axis, out var acsAxis)) return false;
        try
        {
            var current = _api.GetFPosition(acsAxis);
            _api.ToPoint(0, acsAxis, current + distance);
            if (blocking) WaitForMotionComplete(acsAxis);
            return true;
        }
        catch { return false; }
    }

    public bool IsMoving(string axis)
    {
        if (!_connected || !_axisMap.TryGetValue(axis, out var acsAxis)) return false;
        try
        {
            var state = _api.GetMotorState(acsAxis);
            return (state & MotorStates.ACSC_MST_MOVE) != 0;
        }
        catch { return false; }
    }

    public bool IsOnTarget(string axis)
    {
        if (!_connected || !_axisMap.TryGetValue(axis, out var acsAxis)) return false;
        try
        {
            var state = _api.GetMotorState(acsAxis);
            return (state & MotorStates.ACSC_MST_INPOS) != 0;
        }
        catch { return false; }
    }

    public bool IsDeviceInMotion()
    {
        return AxisNames.Any(IsMoving);
    }

    public PositionStruct GetCurrentPosition()
    {
        if (!_connected) return new PositionStruct();
        try
        {
            double GetPos(string name) =>
                _axisMap.TryGetValue(name, out var ax) ? _api.GetFPosition(ax) : 0.0;

            return new PositionStruct
            {
                X = AxisNames.Count > 0 ? GetPos(AxisNames[0]) : 0,
                Y = AxisNames.Count > 1 ? GetPos(AxisNames[1]) : 0,
                Z = AxisNames.Count > 2 ? GetPos(AxisNames[2]) : 0,
                U = AxisNames.Count > 3 ? GetPos(AxisNames[3]) : 0,
                V = AxisNames.Count > 4 ? GetPos(AxisNames[4]) : 0,
                W = AxisNames.Count > 5 ? GetPos(AxisNames[5]) : 0,
            };
        }
        catch { return new PositionStruct(); }
    }

    public bool HomeAxis(string axis)
    {
        // ACS homing via ACSPL+ program execution is controller-specific
        // Basic implementation - run home routine
        return false; // Requires ACSPL+ buffer program
    }

    // STOP uses KILL, not HALT: HALT only decelerates the *currently executing*
    // motion and leaves the ACS motion queue intact, so the next queued ToPoint
    // (e.g. from rapid jog clicks) starts the instant the current one stops. KILL
    // terminates the current motion AND flushes the queued motions — a real stop.
    public bool StopAxis(string axis)
    {
        if (!_connected || !_axisMap.TryGetValue(axis, out var acsAxis)) return false;
        try { _api.Kill(acsAxis); return true; }
        catch { return false; }
    }

    // Kill each axis individually rather than _api.KillM(Axis[]): the SPiiPlus
    // multi-axis array commands expect an ACSC_NONE(-1)-terminated array, and
    // passing a plain axis array throws in the native marshalling — which is why
    // STOP silently failed (the exception was swallowed below). Single-axis Kill
    // has no terminator requirement. Best-effort: kill every axis we can, and
    // report failure only if a Kill actually threw.
    public bool StopAll()
    {
        if (!_connected) return false;
        bool all = true;
        foreach (var name in AxisNames)
        {
            if (!_axisMap.TryGetValue(name, out var acsAxis)) continue;
            try { _api.Kill(acsAxis); }
            catch (Exception ex)
            {
                all = false;
                Serilog.Log.Error(ex, "[Motion] ACS '{Device}' Kill axis {Axis} threw", DeviceName, name);
            }
        }
        return all;
    }

    // Adaptive motion profile: every velocity change re-derives the rest of the axis profile
    // so acceleration, jerk and kill-deceleration scale with the commanded speed.
    //   ACC = DEC = AccPerVelocity × VEL
    //   JERK      = JerkPerAcc     × ACC
    //   KDEC      = KdecPerAcc     × ACC
    // The ratios start at ProfileRatios.Default; the engine replaces them with the saved ones.
    private volatile ProfileRatios _ratios = ProfileRatios.Default;

    public ProfileRatios? GetProfileRatios() => _ratios;

    public bool SetProfileRatios(ProfileRatios ratios)
    {
        if (ratios == null || !ratios.IsValid()) return false;
        _ratios = ratios;
        return true;
    }

    /// <summary>
    /// Sets VEL and, with it, the adaptive ACC / DEC / JERK / KDEC of the axis (current
    /// <see cref="ProfileRatios"/>). Values apply from the next motion, as VEL does. Refuses a
    /// non-positive or non-finite speed. False if any write fails (the failure is logged; the
    /// parameters written before it stay as written).
    /// </summary>
    public bool SetVelocity(string axis, double speed)
    {
        if (!_connected || !_axisMap.TryGetValue(axis, out var acsAxis)) return false;
        if (!double.IsFinite(speed) || speed <= 0)
        {
            Serilog.Log.Warning("[Motion] ACS '{Device}' SetVelocity {Axis} refused: {Speed} is not a positive speed",
                DeviceName, axis, speed);
            return false;
        }

        var r = _ratios;
        var acc = r.AccPerVelocity * speed;
        var jerk = r.JerkPerAcc * acc;
        var kdec = r.KdecPerAcc * acc;
        try
        {
            _api.SetVelocity(acsAxis, speed);
            _api.SetAcceleration(acsAxis, acc);
            _api.SetDeceleration(acsAxis, acc);
            _api.SetJerk(acsAxis, jerk);
            _api.SetKillDeceleration(acsAxis, kdec);
            return true;
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex,
                "[Motion] ACS '{Device}' set profile of axis {Axis} (VEL {Vel} ACC/DEC {Acc} JERK {Jerk} KDEC {Kdec}) threw",
                DeviceName, axis, speed, acc, jerk, kdec);
            return false;
        }
    }

    public double GetVelocity(string axis)
    {
        if (!_connected || !_axisMap.TryGetValue(axis, out var acsAxis)) return 0;
        try { return _api.GetVelocity(acsAxis); }
        catch { return 0; }
    }

    // Reads VEL / ACC / DEC / JERK / KDEC of one axis — the controller's motion profile.
    public AxisProfile? GetAxisProfile(string axis)
    {
        if (!_connected || !_axisMap.TryGetValue(axis, out var acsAxis)) return null;
        try
        {
            return new AxisProfile(
                _api.GetVelocity(acsAxis),
                _api.GetAcceleration(acsAxis),
                _api.GetDeceleration(acsAxis),
                _api.GetJerk(acsAxis),
                _api.GetKillDeceleration(acsAxis));
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[Motion] ACS '{Device}' read profile of axis {Axis} threw", DeviceName, axis);
            return null;
        }
    }

    public bool SetAxisEnabled(string axis, bool enabled)
    {
        if (!_connected || !_axisMap.TryGetValue(axis, out var acsAxis)) return false;
        try
        {
            if (enabled) _api.Enable(acsAxis);
            else _api.Disable(acsAxis);
            return true;
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[Motion] ACS '{Device}' {Op} axis {Axis} threw",
                DeviceName, enabled ? "Enable" : "Disable", axis);
            return false;
        }
    }

    public int? GetEnabledMask()
    {
        if (!_connected) return null;
        try
        {
            int mask = 0;
            for (int i = 0; i < AxisNames.Count; i++)
            {
                if (!_axisMap.TryGetValue(AxisNames[i], out var acsAxis)) continue;
                if ((_api.GetMotorState(acsAxis) & MotorStates.ACSC_MST_ENABLE) != 0)
                    mask |= 1 << i;
            }
            return mask;
        }
        catch { return null; }
    }

    // Transaction sends the line exactly as typed in the SPiiPlus terminal and returns the
    // reply. The controller answers a rejected command with "?<error code>" (the library may
    // also throw) — both are reported as failure with the reply/exception text.
    public bool SendTerminalCommand(string command, out string reply)
    {
        reply = "";
        if (!_connected) { reply = "not connected"; return false; }
        try
        {
            reply = (_api.Transaction(command) ?? "").Trim();
            return !reply.StartsWith('?');
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[Motion] ACS '{Device}' terminal '{Command}' threw", DeviceName, command);
            reply = ex.Message;
            return false;
        }
    }

    public bool? IsBufferRunning(int buffer)
    {
        if (!_connected || buffer < 0) return null;
        try { return (_api.GetProgramState((ProgramBuffer)buffer) & ProgramStates.ACSC_PST_RUN) != 0; }
        catch { return null; }
    }

    private void WaitForMotionComplete(Axis axis, int timeoutMs = 30000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var state = _api.GetMotorState(axis);
            if ((state & MotorStates.ACSC_MST_MOVE) == 0) return;
            Thread.Sleep(10);
        }
    }
}
