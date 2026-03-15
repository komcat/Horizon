using Horizon.Camera;
using Horizon.CurrentMeasurement;
using Horizon.IO;
using Horizon.LaserController;
using Horizon.Motion;
using Horizon.Pneumatic;
using Horizon.Process;
using Horizon.Safety;

namespace Horizon.Core;

public interface IAppContext
{
    CameraManager CameraManager { get; }
    UnifiedMotionManager MotionManager { get; }
    EziIOManager IOManager { get; }
    PneumaticManager PneumaticManager { get; }
    CurrentMeasurementManager CurrentMeasurementManager { get; }
    LaserControllerManager LaserControllerManager { get; }
    ProcessEngine ProcessEngine { get; }
    ProcessPluginRegistry PluginRegistry { get; }
    SafetyWatchManager? SafetyWatchManager { get; }
    MeasurementHub MeasurementHub { get; }
    TeachPointManager TeachPointManager { get; }
    AxisTransformService AxisTransformService { get; }
    bool IsInitialized { get; }
    bool SimulationMode { get; set; }
    void Initialize();
    void Reinitialize();
}
