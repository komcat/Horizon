using Horizon.Camera;
using Horizon.CurrentMeasurement;
using Horizon.IO;
using Horizon.LaserController;
using Horizon.Motion;
using Horizon.Pneumatic;
using Horizon.Process;
using Horizon.Core.Interlock;
using Horizon.Process.Recipe;
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
    RecipeEngine RecipeEngine { get; }
    HardwareExecutionLock ExecutionLock { get; }
    SafetyWatchManager? SafetyWatchManager { get; }
    MeasurementHub MeasurementHub { get; }
    TeachPointManager TeachPointManager { get; }
    ProductManager ProductManager { get; }
    AxisTransformService AxisTransformService { get; }
    InterlockService? InterlockService { get; }
    bool IsInitialized { get; }
    bool SimulationMode { get; set; }
    void Initialize();
    void Reinitialize();

    /// <summary>
    /// Fired by ProcessPanel when a step starts/completes.
    /// Args: (deviceName, positionName, success: null=started, true=ok, false=fail, ""=clear all)
    /// </summary>
    event Action<string, string?, bool?>? ProcessStepNotify;
    void RaiseProcessStepNotify(string device, string? position, bool? success);
}
