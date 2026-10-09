using System.Globalization;
using Horizon.Common;
using Horizon.Workflow;

namespace FAAM.UI.Siphog;

/// <summary>
/// FAAM's implementation of the Layer 2 <see cref="ISiphogSheetSink"/> seam: turns a
/// <c>siphog.updateSheet</c> workflow step into the SiPhOG server's V11
/// <c>UPDATE_SAG:&lt;bench&gt;:&lt;stage&gt;[:&lt;completed&gt;]</c> TCP command over the shared
/// <see cref="SiphogClient"/>. Keeps all SiPhOG/sheet knowledge in FAAM.UI; the engine and
/// Horizon.Workflow stay product-free. Sibling to <see cref="SiphogSignalSource"/> and
/// <see cref="SiphogHillClimbFeedback"/>, which ride the same client/connection.
///
/// The server writes the bench row from its own live stream (date, SLED current, PD µA,
/// target V, the stage's SAG-PWR column, and completed), so the command carries no values —
/// only bench + stage + the completed flag. The ACK only returns after a Google Sheets
/// round-trip, so we wait on a longer timeout than the streaming SET_CURRENT command.
/// </summary>
public sealed class SiphogSheetSink : ISiphogSheetSink
{
    // A Google Sheets write is seconds, not the few-ms of a streaming command — well above
    // the client's 2 s default. 20 s leaves headroom for an auth refresh / slow round-trip.
    private const int SheetWriteTimeoutMs = 20_000;

    private readonly SiphogClient _client;

    public SiphogSheetSink(SiphogClient client) => _client = client;

    public async Task<Result> UpdateSagAsync(string bench, string stage, bool completed, CancellationToken ct)
    {
        if (!_client.IsConnected)
            return Result.Fail(ErrorCode.OperationFailed,
                "siphog.updateSheet: SiPhOG client not connected (open the SiPhOG panel on the Run page first).");

        // The server maps any of {0,not,no,false,incomplete,pending} to "Not completed";
        // anything else (incl. "completed") to "Completed". Send the explicit token.
        var completedToken = completed ? "completed" : "not";
        var command = $"UPDATE_SAG:{bench}:{stage}:{completedToken}";

        var reply = await _client.SendCommandAsync(command, ct, SheetWriteTimeoutMs).ConfigureAwait(false);

        // Success reply: ACK:SAG:<bench>:<stage>:row<N>.
        return reply.StartsWith("ACK:", StringComparison.Ordinal)
            ? Result.Ok()
            : Result.Fail(ErrorCode.OperationFailed, $"siphog.updateSheet failed: {reply}");
    }

    public async Task<Result> SetSledCurrentAsync(double milliAmps, CancellationToken ct)
    {
        if (!_client.IsConnected)
            return Result.Fail(ErrorCode.OperationFailed,
                "siphog.setCurrent: SiPhOG client not connected (open the SiPhOG panel on the Run page first).");

        var reply = await _client.SendCommandAsync(
            "SET_CURRENT:" + milliAmps.ToString("0.###", CultureInfo.InvariantCulture), ct).ConfigureAwait(false);

        return reply.StartsWith("ACK:", StringComparison.Ordinal)
            ? Result.Ok()
            : Result.Fail(ErrorCode.OperationFailed, $"siphog.setCurrent failed: {reply}");
    }
}
