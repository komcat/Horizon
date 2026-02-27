using Horizon.Camera;
using Horizon.IO;
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
    ProcessEngine ProcessEngine { get; }
    SafetyWatchManager? SafetyWatchManager { get; }
    bool IsInitialized { get; }
    bool SimulationMode { get; set; }
    void Initialize();
    void Reinitialize();
}
