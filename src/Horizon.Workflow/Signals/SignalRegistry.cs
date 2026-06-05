using System.Collections.Concurrent;
using Horizon.Common;

namespace Horizon.Workflow.Signals;

/// <summary>
/// Aggregates the registered <see cref="ISignalSource"/>s behind one façade. The
/// built-in <c>EngineSignalSource</c> is always present; Layer 3 heads add product
/// sources (FAAM's SiPhOG) via <c>IProcessService.AddSignalSource</c>. The editor
/// reads <see cref="Describe"/> to populate the <c>signal.read</c> drop-down; the
/// runner calls <see cref="ReadAsync"/> to resolve a chosen key at run time.
/// </summary>
internal sealed class SignalRegistry
{
    // CopyOnWrite-friendly: sources are added at startup, read on every signal.read.
    private readonly ConcurrentBag<ISignalSource> _sources = new();

    public SignalRegistry(ISignalSource builtIn) => _sources.Add(builtIn);

    /// <summary>Registers an additional (product) source. Duplicates are ignored.</summary>
    public void Add(ISignalSource source)
    {
        if (source != null && !_sources.Contains(source)) _sources.Add(source);
    }

    /// <summary>The union of every source's current catalog, ordered by category then display.</summary>
    public IReadOnlyList<SignalDescriptor> Describe()
        => _sources
            .SelectMany(s => s.Describe())
            .OrderBy(d => d.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Display, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Resolves <paramref name="key"/> through the one source that owns it.
    /// Fails clearly when no source claims the key (e.g. the product source isn't
    /// registered on this head).
    /// </summary>
    public Task<Result<double>> ReadAsync(string key, CancellationToken ct = default)
    {
        var owner = _sources.FirstOrDefault(s => s.Owns(key));
        return owner == null
            ? Task.FromResult(Result<double>.Fail(ErrorCode.NotSupported,
                $"signal.read: no source provides '{key}' (is the product signal source registered?)."))
            : owner.ReadAsync(key, ct);
    }
}
