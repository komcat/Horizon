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

    /// <summary>
    /// One MOV for the whole pose. The hexapod checks each MOV against its workspace
    /// using the other axes' current targets, so moving axis-by-axis can hit an
    /// unreachable intermediate pose (e.g. Home → ClearGrip rejected at Y) even though
    /// the final pose is valid. A single multi-axis MOV is validated as one pose.
    /// </summary>
    public bool MoveToPositions(IReadOnlyList<string> axes, IReadOnlyList<double> positions, bool blocking)
    {
        if (!IsConnected) return false;
        var result = PIGcs2.MOV(_connectionId, string.Join(" ", axes), positions.ToArray());
        if (result == 0) return false;
        if (blocking)
            foreach (var axis in axes)
                WaitForMotionComplete(axis);
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

    public bool SetSystemVelocity(double speed)
    {
        // PI hexapod path velocity is the system velocity (VLS), not per-axis VEL.
        if (!IsConnected) return false;
        try
        {
            return PIGcs2.VLS(_connectionId, speed) != 0;
        }
        catch { return false; }
    }

    public double GetSystemVelocity()
    {
        if (!IsConnected) return 0;
        try
        {
            double v = 0;
            if (PIGcs2.qVLS(_connectionId, ref v) != 0)
                return v;
            return 0;
        }
        catch { return 0; }
    }

    private string AllAxes => string.Join(" ", AxisNames);

    public bool StartReference()
    {
        if (!IsConnected) return false;
        return PIGcs2.FRF(_connectionId, AllAxes) != 0;
    }

    public bool? IsReferenced()
    {
        if (!IsConnected) return null;
        var flags = new int[AxisNames.Count];
        if (PIGcs2.qFRF(_connectionId, AllAxes, flags) == 0) return null;
        return flags.All(f => f != 0);
    }

    public bool SetPivot(double r, double s, double t)
    {
        if (!IsConnected) return false;
        return PIGcs2.SPI(_connectionId, "R S T", new[] { r, s, t }) != 0;
    }

    public (double R, double S, double T)? GetPivot()
    {
        if (!IsConnected) return null;
        var v = new double[3];
        if (PIGcs2.qSPI(_connectionId, "R S T", v) == 0) return null;
        return (v[0], v[1], v[2]);
    }

    public string? GetLastErrorText()
    {
        if (!IsConnected) return null;
        var code = PIGcs2.GetError(_connectionId);
        if (code == 0) return null;
        var sb = new System.Text.StringBuilder(512);
        return PIGcs2.TranslateError(code, sb, sb.Capacity) != 0
            ? $"PI error {code}: {sb}"
            : $"PI error {code}";
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
