using Horizon.Common;

namespace Horizon.Workflow;

/// <summary>
/// A Layer 3 sink that writes one staged SAG-PWR measurement to the SiPhOG
/// server's Google Sheet — the V11 <c>UPDATE_SAG:&lt;bench&gt;:&lt;stage&gt;[:&lt;completed&gt;]</c>
/// TCP command (see <c>C:\SOFTWARE\SiphogSW\FNW_SERVER_V11.py</c>). Registered via
/// <see cref="IProcessService.SetSiphogSheetSink"/>. Same inverted-dependency seam as
/// <see cref="IUtilityAppHost"/> and the hill-climb feedback provider: Horizon.Workflow
/// defines the contract; FAAM.UI implements it over its live SiPhOG client. The engine
/// stays SiPhOG-free.
///
/// The server sources the measured values from its own live device stream, so the
/// command only carries the row key + stage + completed flag:
/// <list type="bullet">
///   <item><c>bench</c> — the row key matched against column A of the sheet tab. The
///     <c>siphog.updateSheet</c> step supplies the active DUT serial.</item>
///   <item><c>stage</c> — which column receives the SAG PWR: <c>ACTUAL</c> | <c>WET</c> | <c>UV</c>.</item>
///   <item><c>completed</c> — sets the <c>saa_completed</c> cell (Completed / Not completed).</item>
/// </list>
/// The sheet write is a Google round-trip, so the implementation must await the
/// server's <c>ACK:</c>/<c>ERR:</c> with a timeout well above the streaming-command one.
/// </summary>
public interface ISiphogSheetSink
{
    /// <summary>
    /// Writes the bench's current SAG-PWR readings into <paramref name="stage"/>'s column.
    /// Returns success on the server's <c>ACK:</c>, a failure (with the server message) on
    /// <c>ERR:</c>, timeout, or a disconnected client.
    /// </summary>
    Task<Result> UpdateSagAsync(string bench, string stage, bool completed, CancellationToken ct);

    /// <summary>
    /// Sets the SiPhOG SLED drive current — the server's <c>SET_CURRENT:&lt;mA&gt;</c> command
    /// (drives <c>machine.siphog.setCurrent</c>). Returns success on <c>ACK:</c>, a failure
    /// (with the server message) on <c>ERR:</c> — e.g. out of the server's allowed range —,
    /// timeout, or a disconnected client.
    /// </summary>
    Task<Result> SetSledCurrentAsync(double milliAmps, CancellationToken ct);
}
