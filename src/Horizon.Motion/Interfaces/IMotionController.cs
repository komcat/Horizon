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
    bool IsDeviceInMotion();
    PositionStruct GetCurrentPosition();

    bool HomeAxis(string axis);
    bool StopAxis(string axis);
    bool StopAll();

    string ControllerType { get; }
    string DeviceName { get; }
    List<string> AxisNames { get; }
}
