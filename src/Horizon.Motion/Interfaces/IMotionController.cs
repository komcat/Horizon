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

    string ControllerType { get; }
    string DeviceName { get; }
    List<string> AxisNames { get; }
}
