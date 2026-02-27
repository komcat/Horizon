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

    public bool StopAxis(string axis)
    {
        if (!_connected || !_axisMap.TryGetValue(axis, out var acsAxis)) return false;
        try { _api.Halt(acsAxis); return true; }
        catch { return false; }
    }

    public bool StopAll()
    {
        if (!_connected) return false;
        try { _api.HaltM(AxisNames.Select(a => _axisMap[a]).ToArray()); return true; }
        catch { return false; }
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
