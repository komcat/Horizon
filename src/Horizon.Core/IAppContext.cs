using Horizon.Camera;

namespace Horizon.Core;

public interface IAppContext
{
    CameraManager CameraManager { get; }
    bool IsInitialized { get; }
    void Initialize();
    void Reinitialize();
}
