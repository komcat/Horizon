using Horizon.Motion.Interfaces;
using Horizon.Motion.Models;
using Horizon.Motion.Native;

namespace Horizon.Motion.Adapters;

public sealed class PIMotionController : IMotionController
{
    private int _connectionId = -1;
    private readonly string _ipAddress;
    private readonly int _port;

    // PI hexapod axes: X Y Z U V W
    private static readonly string[] HexapodAxes = { "X", "Y", "Z", "U", "V", "W" };

    public string ControllerType => "PI";
    public string DeviceName { get; }
    public List<string> AxisNames { get; }
    public bool IsConnected => _connectionId >= 0 && PIGcs2.IsConnected(_connectionId) != 0;

    public PIMotionController(MotionDevice device)
    {
        DeviceName = device.Name;
        _ipAddress = device.IpAddress;
        _port = device.Port > 0 ? device.Port : 50000;
        AxisNames = device.InstalledAxes.Count > 0
            ? new List<string>(device.InstalledAxes)
            : new List<string>(HexapodAxes);
    }

    public bool Connect()
    {
        if (IsConnected) return true;
        _connectionId = PIGcs2.ConnectTCPIP(_ipAddress, _port);
        return _connectionId >= 0;
    }

    public bool Disconnect()
    {
        if (_connectionId >= 0)
        {
            PIGcs2.CloseConnection(_connectionId);
            _connectionId = -1;
        }
        return true;
    }

    public bool MoveToPosition(string axis, double position, bool blocking)
    {
        if (!IsConnected) return false;
        var result = PIGcs2.MOV(_connectionId, axis, new[] { position });
        if (result == 0) return false;
        if (blocking) WaitForMotionComplete(axis);
        return true;
    }

    public bool MoveRelative(string axis, double distance, bool blocking)
    {
        if (!IsConnected) return false;
        var result = PIGcs2.MVR(_connectionId, axis, new[] { distance });
        if (result == 0) return false;
        if (blocking) WaitForMotionComplete(axis);
        return true;
    }

    public bool IsMoving(string axis)
    {
        if (!IsConnected) return false;
        var moving = new int[1];
        PIGcs2.IsMoving(_connectionId, axis, moving);
        return moving[0] != 0;
    }

    public bool IsOnTarget(string axis)
    {
        if (!IsConnected) return false;
        var onTarget = new int[1];
        return PIGcs2.qONT(_connectionId, axis, onTarget) != 0 && onTarget[0] != 0;
    }

    public bool IsDeviceInMotion()
    {
        return AxisNames.Any(IsMoving);
    }

    public PositionStruct GetCurrentPosition()
    {
        if (!IsConnected) return new PositionStruct();

        var positions = new Dictionary<string, double>();
        foreach (var axis in AxisNames)
        {
            var pos = new double[1];
            if (PIGcs2.qPOS(_connectionId, axis, pos) != 0)
                positions[axis] = pos[0];
        }

        return new PositionStruct
        {
            X = positions.GetValueOrDefault("X"),
            Y = positions.GetValueOrDefault("Y"),
            Z = positions.GetValueOrDefault("Z"),
            U = positions.GetValueOrDefault("U"),
            V = positions.GetValueOrDefault("V"),
            W = positions.GetValueOrDefault("W"),
        };
    }

    public bool HomeAxis(string axis)
    {
        if (!IsConnected) return false;
        return PIGcs2.FRF(_connectionId, axis) != 0;
    }

    public bool StopAxis(string axis)
    {
        if (!IsConnected) return false;
        return PIGcs2.HLT(_connectionId, axis) != 0;
    }

    public bool StopAll()
    {
        if (!IsConnected) return false;
        return PIGcs2.STP(_connectionId) != 0;
    }

    public bool SetVelocity(string axis, double speed)
    {
        // PI velocity is set via VEL command
        if (!IsConnected) return false;
        try
        {
            var axes = axis + "\n";
            var values = new[] { speed };
            return PIGcs2.VEL(_connectionId, axes, values) != 0;
        }
        catch { return false; }
    }

    public double GetVelocity(string axis)
    {
        if (!IsConnected) return 0;
        try
        {
            var axes = axis + "\n";
            var values = new double[1];
            if (PIGcs2.qVEL(_connectionId, axes, values) != 0)
                return values[0];
            return 0;
        }
        catch { return 0; }
    }

    private void WaitForMotionComplete(string axis, int timeoutMs = 30000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (!IsMoving(axis)) return;
            Thread.Sleep(10);
        }
    }
}
