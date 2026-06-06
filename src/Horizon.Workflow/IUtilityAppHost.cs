using Horizon.Common;

namespace Horizon.Workflow;

/// <summary>
/// A Layer 3 utility-app host (the FAAM Run-page "springboard"), registered via
/// <see cref="IProcessService.SetUtilityAppHost"/>. Lets <c>app.*</c> workflow steps
/// navigate to and run the Run-page utility apps. This is the same inverted-dependency
/// seam as <see cref="IOperatorPrompt"/> and the hill-climb feedback provider: Layer 2
/// defines the contract, a Layer 3 head implements it. When no head is registered the
/// <c>app.*</c> steps fail with a clear message rather than crash.
///
/// App ids: <c>ucorrection</c>, <c>faupre</c>, <c>hillclimb</c>, <c>siphog</c>.
///
/// Note on semantics: <see cref="StartAsync"/> <i>activates</i> an app (foregrounds it
/// and begins its routine / connection) and returns once started — it does NOT await
/// the routine's completion. The headless, await-to-completion hill-climb path remains
/// the parameterized <c>hillclimb.start</c> step. See
/// <c>docs/utility-app-launcher-design.md</c>.
/// </summary>
public interface IUtilityAppHost
{
    /// <summary>Brings the app's screen to the foreground (navigation only). Fails on an unknown id.</summary>
    Result Open(string appId);

    /// <summary>Returns to the launcher home grid (apps keep running in the background).</summary>
    Result Home();

    /// <summary>Foregrounds the app and starts its routine / connection. Returns once started, not on completion.</summary>
    Task<Result> StartAsync(string appId, CancellationToken ct);

    /// <summary>Stops the app's routine / disconnects it. Leaves the current screen as-is.</summary>
    Task<Result> StopAsync(string appId);
}
