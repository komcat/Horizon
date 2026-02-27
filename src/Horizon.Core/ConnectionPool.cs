namespace Horizon.Core;

public sealed class ConnectionPool
{
    private readonly AppContext _context;

    public event Action<string, int, int>? OnProgress;

    public ConnectionPool(AppContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Connects all hardware in phased order with parallel connections within each phase.
    /// Phase 0: Cameras
    /// Phase 1: Motion controllers + IO devices (parallel)
    /// Phase 2: Safety watch start (after IO connected)
    /// </summary>
    public async Task ConnectAllAsync(int perTaskTimeoutMs = 10000, CancellationToken ct = default)
    {
        // Phase 0: Cameras
        if (_context.IsInitialized)
        {
            var cameraIds = _context.CameraManager.GetCameraIds();
            int done = 0;
            var cameraTasks = cameraIds.Select(id => Task.Run(() =>
            {
                try
                {
                    _context.CameraManager.ConnectCamera(id);
                }
                catch { }
                Interlocked.Increment(ref done);
                OnProgress?.Invoke($"Camera:{id}", done, cameraIds.Count);
            }, ct)).ToList();

            await WhenAllWithTimeout(cameraTasks, perTaskTimeoutMs, ct);
        }

        // Phase 1: Motion + IO in parallel
        if (_context.IsInitialized)
        {
            var phase1Tasks = new List<Task>();
            int total = 0, done = 0;

            // IO connect
            var ioNames = _context.IOManager.GetAllDeviceNames();
            total += ioNames.Count;
            foreach (var name in ioNames)
            {
                phase1Tasks.Add(Task.Run(() =>
                {
                    try { _context.IOManager.ConnectDevice(name); } catch { }
                    Interlocked.Increment(ref done);
                    OnProgress?.Invoke($"IO:{name}", done, total);
                }, ct));
            }

            // Motion connect
            var motionNames = _context.MotionManager.GetAllDeviceNames();
            total += motionNames.Count;
            foreach (var name in motionNames)
            {
                phase1Tasks.Add(Task.Run(() =>
                {
                    try { _context.MotionManager.ConnectDevice(name); } catch { }
                    Interlocked.Increment(ref done);
                    OnProgress?.Invoke($"Motion:{name}", done, total);
                }, ct));
            }

            await WhenAllWithTimeout(phase1Tasks, perTaskTimeoutMs, ct);
        }

        // Phase 2: Start safety watch (after IO connected)
        if (_context.SafetyWatchManager != null)
        {
            _context.SafetyWatchManager.StartWatching();
            OnProgress?.Invoke("SafetyWatch", 1, 1);
        }
    }

    private static async Task WhenAllWithTimeout(List<Task> tasks, int timeoutMs, CancellationToken ct)
    {
        if (tasks.Count == 0) return;
        var allTasks = Task.WhenAll(tasks);
        var delay = Task.Delay(timeoutMs, ct);
        await Task.WhenAny(allTasks, delay);
    }
}
