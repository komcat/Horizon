using Horizon.Common;
using Horizon.Engine;

namespace Horizon.Workflow;

/// <summary>
/// Layer 2 facade — the product / process module on top of
/// <see cref="IHorizonEngine"/>. Consumed by Layer 3 front-ends.
///
/// Phase 1 exposes product management. Teach points / named positions, process
/// execution, and recipe execution are added by later bring-up phases.
/// </summary>
public interface IProcessService
{
    /// <summary>All configured product names.</summary>
    IReadOnlyList<string> Products { get; }

    /// <summary>The currently loaded product, or null if none.</summary>
    string? ActiveProduct { get; }

    /// <summary>Creates a new, empty product.</summary>
    Result CreateProduct(string name);

    /// <summary>Deletes a product and all its data.</summary>
    Result DeleteProduct(string name);

    /// <summary>Loads (activates) a product.</summary>
    Result LoadProduct(string name);

    /// <summary>Clears the active product.</summary>
    void UnloadProduct();

    // --- Teach points / named positions (operate on the active product) ---

    /// <summary>Named-position names for a device.</summary>
    IReadOnlyList<string> GetPositionNames(string device);

    /// <summary>Resolves a named position (coordinates + velocity), or null if unknown.</summary>
    NamedPosition? GetPosition(string device, string name);

    /// <summary>Removes a named position from the active product.</summary>
    Result RemovePosition(string device, string name);

    /// <summary>
    /// Stores a named position from explicit coordinates (manual entry).
    /// <paramref name="velocity"/> of 0 = no velocity applied on recall.
    /// </summary>
    Result SetPosition(string device, string name, MotionPosition position, double velocity);

    /// <summary>
    /// Captures the device's current position and stores it under a name.
    /// <paramref name="velocity"/> of 0 = no velocity applied on recall.
    /// </summary>
    Task<Result> TeachPositionAsync(string device, string name, double velocity);

    /// <summary>Resolves a named position and moves the device to it.</summary>
    Task<Result> MoveToNamedPositionAsync(string device, string name, CancellationToken ct = default);

    // --- Process programs (operate on the active product) ---

    /// <summary>Process program names in the active product.</summary>
    IReadOnlyList<string> ListPrograms();

    /// <summary>Loads a process program by name.</summary>
    Result<WorkflowProgram> LoadProgram(string name);

    /// <summary>Saves a process program into the active product.</summary>
    Result SaveProgram(WorkflowProgram program);

    /// <summary>Runs a process program against the engine.</summary>
    Task<Result> RunAsync(WorkflowProgram program, CancellationToken ct = default);

    /// <summary>Raised before and after each executed step of a running program.</summary>
    event Action<WorkflowStepEvent>? StepEvent;

    /// <summary>Raised for every workflow log line. The Layer 2 logging surface.</summary>
    event Action<LogEntry>? LogEmitted;
}
