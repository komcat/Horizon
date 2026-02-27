using Horizon.Motion.Models;

namespace Horizon.Motion.Interfaces;

public interface IMotionControllerManager
{
    void AddDevice(MotionDevice device);
    bool ConnectAll();
    void DisconnectAll();
    bool ConnectDevice(string name);
    IMotionController? GetController(string deviceName);
    List<string> GetDeviceNames();
    string ControllerType { get; }
}
