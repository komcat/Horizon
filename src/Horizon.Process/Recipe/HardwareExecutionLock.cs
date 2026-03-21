namespace Horizon.Process.Recipe;

/// <summary>
/// Shared execution lock for hardware access.
/// Both ProcessEngine and RecipeEngine check this lock to ensure
/// only one engine controls hardware at a time.
/// </summary>
public sealed class HardwareExecutionLock
{
    private int _lock;

    public string? Owner { get; private set; }

    public bool IsLocked => _lock != 0;

    public bool TryAcquire(string owner)
    {
        if (Interlocked.CompareExchange(ref _lock, 1, 0) != 0)
            return false;
        Owner = owner;
        return true;
    }

    public void Release()
    {
        Owner = null;
        Interlocked.Exchange(ref _lock, 0);
    }
}
