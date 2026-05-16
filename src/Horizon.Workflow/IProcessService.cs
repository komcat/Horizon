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

    /// <summary>Raised for every workflow log line. The Layer 2 logging surface.</summary>
    event Action<LogEntry>? LogEmitted;
}
