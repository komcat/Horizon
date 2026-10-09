using Horizon.Motion.Models;

namespace Horizon.Motion.Interfaces;

public interface IMotionController
{
    bool Connect();
    bool Disconnect();
    bool IsConnected { get; }

    bool MoveToPosition(string axis, double position, bool blocking);
    bool MoveRelative(string axis, double distance, bool blocking);

    /// <summary>
    /// Absolute move of several axes to one target pose. Default: one
    /// <see cref="MoveToPosition"/> per axis, in order (fine for Cartesian stages).
    /// Parallel-kinematics controllers (PI hexapods) override this to send the pose
    /// as a single command — per-axis moves are validated one at a time against the
    /// workspace, so an intermediate pose can be rejected even when the final is valid.
    /// </summary>
    bool MoveToPositions(IReadOnlyList<string> axes, IReadOnlyList<double> positions, bool blocking)
    {
        for (int i = 0; i < axes.Count; i++)
            if (!MoveToPosition(axes[i], positions[i], blocking))
                return false;
        return true;
    }
    bool IsMoving(string axis);
    bool IsOnTarget(string axis);
    bool IsDeviceInMotion();
    PositionStruct GetCurrentPosition();

    bool HomeAxis(string axis);
    bool StopAxis(string axis);
    bool StopAll();

    bool SetVelocity(string axis, double speed);
    double GetVelocity(string axis);

    /// <summary>
    /// System (path) velocity — the coordinated platform speed for
    /// parallel-kinematics controllers (PI hexapods). Default: not supported
    /// (Cartesian controllers use per-axis <see cref="SetVelocity"/>).
    /// </summary>
    bool SetSystemVelocity(double speed) => false;
    double GetSystemVelocity() => 0;

    /// <summary>
    /// Enables (servo on) or disables (servo off) one axis. Default: not supported
    /// (PI controllers keep servo control internal to the hexapod).
    /// </summary>
    bool SetAxisEnabled(string axis, bool enabled) => false;

    /// <summary>
    /// Bit i set = axis <c>AxisNames[i]</c> is enabled. Null when the controller
    /// does not report a per-axis enable state.
    /// </summary>
    int? GetEnabledMask() => null;

    /// <summary>
    /// Sends one raw controller command, as typed in the vendor terminal (ACS: e.g.
    /// <c>START 1, STARTUP</c> / <c>STOP 1</c>). <paramref name="reply"/> carries the
    /// controller's answer or the error text. Default: not supported.
    /// </summary>
    bool SendTerminalCommand(string command, out string reply)
    {
        reply = "not supported";
        return false;
    }

    /// <summary>Whether a program buffer is executing; null when unsupported or unreadable.</summary>
    bool? IsBufferRunning(int buffer) => null;

    /// <summary>
    /// Starts a reference move of every axis (PI hexapod: FRF). Non-blocking — poll
    /// <see cref="IsReferenced"/>. Default: not supported.
    /// </summary>
    bool StartReference() => false;

    /// <summary>True when every axis is referenced; null when unsupported or unreadable.</summary>
    bool? IsReferenced() => null;

    /// <summary>Sets the pivot point (PI hexapod R, S, T). Default: not supported.</summary>
    bool SetPivot(double r, double s, double t) => false;

    /// <summary>Reads the pivot point (PI hexapod R, S, T); null when unsupported or unreadable.</summary>
    (double R, double S, double T)? GetPivot() => null;

    /// <summary>Controller's last error as text (clears it on PI), or null when none / unsupported.</summary>
    string? GetLastErrorText() => null;

    string ControllerType { get; }
    string DeviceName { get; }
    List<string> AxisNames { get; }
}
