using Horizon.Common;
using Horizon.Workflow.Signals;

namespace FAAM.UI.Siphog;

/// <summary>
/// Exposes the live SiPhOG frame to the Layer 2 <c>signal.read</c> catalog, so an
/// Engineer block can pull <c>siphog.sagPwrV</c> (etc.) into a workflow variable and
/// branch on it. This is the product-specific half of the generic
/// <see cref="ISignalSource"/> seam — it keeps ALL SiPhOG knowledge in FAAM.UI; the
/// engine and Horizon.Workflow stay product-free. Sibling to
/// <see cref="SiphogHillClimbFeedback"/>, which feeds the same client's SagPwrV to the
/// hill-climb engine.
/// </summary>
public sealed class SiphogSignalSource : ISignalSource
{
    private const string Prefix = "siphog.";

    private readonly SiphogClient _client;

    public SiphogSignalSource(SiphogClient client) => _client = client;

    public bool Owns(string key) => key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    // The protocol's six fields are fixed, so the catalog is static — listed even when
    // the client is offline (it connects on the Run page) so they're discoverable at
    // edit time. ReadAsync reports the disconnected/no-frame case at run time.
    public IReadOnlyList<SignalDescriptor> Describe() => Catalog;

    private static readonly SignalDescriptor[] Catalog =
    {
        new("siphog.sagPwrV",        "SiPhOG — SAG PWR",        "V",  "SiPhOG"),
        new("siphog.targetSagPwrV",  "SiPhOG — target SAG PWR", "V",  "SiPhOG"),
        new("siphog.photoCurrentUa", "SiPhOG — photo current",  "µA", "SiPhOG"),
        new("siphog.sledCurrentMa",  "SiPhOG — SLED current",   "mA", "SiPhOG"),
        new("siphog.sledTempC",      "SiPhOG — SLED temp",      "°C", "SiPhOG"),
        new("siphog.tecCurrentMa",   "SiPhOG — TEC current",    "mA", "SiPhOG"),
    };

    public Task<Result<double>> ReadAsync(string key, CancellationToken ct = default)
    {
        // Latest survives a dropped connection (cleared only on the next connect) — refuse
        // to hand out that stale frame as a live reading.
        if (!_client.IsConnected)
            return Task.FromResult(Result<double>.Fail(ErrorCode.OperationFailed,
                "signal.read: SiPhOG client not connected (open the SiPhOG panel on the Run page and Connect)."));
        if (_client.Latest is not { } f)
            return Task.FromResult(Result<double>.Fail(ErrorCode.OperationFailed,
                "signal.read: no SiPhOG frame yet (is the SiPhOG client connected and streaming?)."));

        var value = key.ToLowerInvariant() switch
        {
            "siphog.sagpwrv"        => f.SagPwrV,
            "siphog.targetsagpwrv"  => f.TargetSagPwrV,
            "siphog.photocurrentua" => f.PhotoCurrentUa,
            "siphog.sledcurrentma"  => f.SledCurrentMa,
            "siphog.sledtempc"      => f.SledTempC,
            "siphog.teccurrentma"   => f.TecCurrentMa,
            _ => double.NaN,
        };

        return double.IsNaN(value)
            ? Task.FromResult(Result<double>.Fail(ErrorCode.InvalidParameter, $"Unknown SiPhOG signal '{key}'."))
            : Task.FromResult(Result<double>.Ok(value));
    }
}
