using Horizon.Motion.Models;

namespace Horizon.Motion.Interfaces;

public interface IMotionController
{
    bool Connect();
    bool Disconnect();
    bool IsConnected { get; }

    bool MoveToPosition(string axis, double position, bool blocking);
    bool MoveRelative(string axis, double distance, bool blocking);
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
