using System.Text.Json;
using System.Threading;
using Horizon.Engine;
using Horizon.Workflow;

namespace FAAM.UI;

/// <summary>
/// Mirrors <see cref="OperatorActivity"/> events into the open interactive DUT run and
/// (optionally) onto the log surface. This is the <em>only</em> component that couples
/// operator actions to the DUT log — the routines that produce the actions stay ignorant
/// of it. When no DUT run is open, activities are dropped for the DUT record (still
/// logged), matching <see cref="IProcessService.RecordDutValue"/>'s no-op contract.
///
/// Application-specific signal enrichment (the FAAM SiPhOG Sagnac value) is attached here
/// via <see cref="SignalProbe"/>, so the generic activity layer never learns about it.
///
/// Durability: <see cref="IProcessService.RecordDutValue"/> only mutates in memory. To
/// survive a mid-run crash we flush to disk on every section boundary immediately, and on
/// a short debounce for jog bursts (so a rapid jog streak is one write, not dozens).
/// </summary>
public sealed class DutActivityMirror : IDisposable
{
    private const int JogFlushDebounceMs = 750;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly OperatorActivityHub _hub;
    private readonly IProcessService _process;
    private readonly LogHub? _log;
    private readonly Timer _jogFlushTimer;
    private bool _disposed;

    /// <summary>
    /// Optional live-signal probe. When set, each jog activity is enriched with the probed
    /// value (FAAM installs a SiPhOG Sagnac probe) so the DUT log shows how the operator's
    /// jog moved the signal. Returns null when no sample is available. Signal-agnostic by
    /// design — the mirror records whatever key/value the probe yields.
    /// </summary>
    public Func<KeyValuePair<string, double>?>? SignalProbe { get; set; }

    public DutActivityMirror(OperatorActivityHub hub, IProcessService process, LogHub? log = null)
    {
        _hub = hub;
        _process = process;
        _log = log;
        _jogFlushTimer = new Timer(_ => SafeFlush(), null, Timeout.Infinite, Timeout.Infinite);
        _hub.Published += OnActivity;
    }

    private void OnActivity(OperatorActivity a)
    {
        if (_disposed || a == null) return;

        // Assemble the structured payload; enrich jogs with the live signal if probed.
        var data = a.Data is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(a.Data);
        if (a.Category == ActivityCategory.Jog && SignalProbe is { } probe)
        {
            try
            {
                if (probe() is { } sig) data[sig.Key] = sig.Value;
            }
            catch { /* a flaky signal source must never break action logging */ }
        }

        // Surface jogs on the live log panel — successful jogs are otherwise unlogged. Section
        // boundaries are already logged by their driving panels, so we don't echo them (no dupes).
        if (_log != null && a.Category == ActivityCategory.Jog && a.Detail is { Length: > 0 })
            _log.Add(new LogEntry(LogLevel.Info, a.Timestamp, "Jog", $"jog {a.Detail}"));

        // Mirror into the DUT record (no-op if no run is open).
        if (_process.HasActiveDutRun)
        {
            var payload = new Dictionary<string, object?> { ["action"] = a.Action, ["detail"] = a.Detail };
            foreach (var kv in data) payload[kv.Key] = kv.Value;
            _process.RecordDutValue(
                $"{a.Category}:{a.Action}", JsonSerializer.Serialize(payload, JsonOptions), DutValueType.Object);

            // Persist: sections flush now (few, meaningful); jogs debounce (many, bursty).
            if (a.Category == ActivityCategory.Jog)
                _jogFlushTimer.Change(JogFlushDebounceMs, Timeout.Infinite);
            else
                SafeFlush();
        }
    }

    private void SafeFlush()
    {
        try { _process.FlushDutRun(); }
        catch { /* flush is best-effort durability; EndDutRun still writes the final record */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _hub.Published -= OnActivity;
        _jogFlushTimer.Dispose();
    }
}
