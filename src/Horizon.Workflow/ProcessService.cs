using Horizon.Common;
using Horizon.Engine;

namespace Horizon.Workflow;

/// <summary>
/// Default in-process implementation of <see cref="IProcessService"/>.
/// Phase 1: product management over <see cref="ProductStore"/>.
/// </summary>
public sealed class ProcessService : IProcessService
{
    private readonly ProductStore _products = new();

    public IReadOnlyList<string> Products => _products.GetProductNames();

    public string? ActiveProduct => _products.ActiveProduct;

    public event Action<LogEntry>? LogEmitted;

    public Result CreateProduct(string name)
    {
        if (!_products.IsValidName(name))
            return Result.Fail(ErrorCode.InvalidParameter, $"Invalid product name: '{name}'.");
        if (_products.Exists(name))
            return Result.Fail(ErrorCode.OperationFailed, $"Product '{name}' already exists.");

        _products.Create(name);
        Log(LogLevel.Info, $"Product created: {name}");
        return Result.Ok();
    }

    public Result DeleteProduct(string name)
    {
        if (!_products.Exists(name))
            return Result.Fail(ErrorCode.FileNotFound, $"Product '{name}' not found.");

        var wasActive = string.Equals(_products.ActiveProduct, name, StringComparison.OrdinalIgnoreCase);
        _products.Delete(name);
        if (wasActive) _products.SetActive(null);

        Log(LogLevel.Info, $"Product deleted: {name}");
        return Result.Ok();
    }

    public Result LoadProduct(string name)
    {
        if (!_products.Exists(name))
            return Result.Fail(ErrorCode.FileNotFound, $"Product '{name}' not found.");

        _products.SetActive(name);
        Log(LogLevel.Info, $"Product loaded: {name}");
        return Result.Ok();
    }

    public void UnloadProduct()
    {
        _products.SetActive(null);
        Log(LogLevel.Info, "Product unloaded.");
    }

    private void Log(LogLevel level, string message)
        => LogEmitted?.Invoke(new LogEntry(level, DateTimeOffset.Now, "Workflow", message));
}
