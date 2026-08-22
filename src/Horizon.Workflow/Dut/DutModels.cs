namespace Horizon.Workflow;

/// <summary>Type tag for a recorded DUT value.</summary>
public enum DutValueType
{
    String,
    Double,
    Integer,
    /// <summary>A JSON object fragment — used by <c>dut.recordGroup</c>.</summary>
    Object,
}

/// <summary>Disposition of one run against a DUT.</summary>
public enum DutVerdict
{
    /// <summary>The run has started but not yet finished — persisted on Start so the
    /// record exists immediately; replaced by a final verdict when the run ends.</summary>
    InProgress,
    /// <summary>The run completed successfully.</summary>
    Pass,
    /// <summary>A step failed or a condition stopped the run.</summary>
    Fail,
    /// <summary>The run was cancelled before it finished.</summary>
    Aborted,
}

/// <summary>One value recorded into a DUT during a run.</summary>
public sealed record DutEntry(DateTime Timestamp, string Key, string Value, DutValueType Type);

/// <summary>
/// One run against a DUT — a single program or deck execution. Built up in
/// memory while the run executes, then appended to the <see cref="DutRecord"/>.
/// </summary>
public sealed class DutRun
{
    /// <summary>When the run started (UTC).</summary>
    public DateTime StartedUtc { get; init; }

    /// <summary>When the run finished (UTC); default until the run ends.</summary>
    public DateTime EndedUtc { get; set; }

    /// <summary>Name of the program or deck that ran.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>The run's disposition — set when the run ends.</summary>
    public DutVerdict Verdict { get; set; }

    /// <summary>Operator id that ran the unit (traceability); null if no operator was set.</summary>
    public string? OperatorId { get; set; }

    /// <summary>Operator display name at run time; null if no operator was set.</summary>
    public string? OperatorName { get; set; }

    /// <summary>Access level the operator signed in at; null if the head has no levels.</summary>
    public string? OperatorLevel { get; set; }

    /// <summary>Station / machine number the run executed on; null if unknown.</summary>
    public string? StationId { get; set; }

    /// <summary>
    /// The station's credential string (its signature) at run time — what the machine
    /// was certified/tagged as when it ran this unit. Null if the station is unsigned.
    /// </summary>
    public string? MachineCredential { get; set; }

    /// <summary>Values recorded by <c>dut.*</c> steps, in execution order.</summary>
    public List<DutEntry> Entries { get; init; } = new();
}

/// <summary>
/// The persistent test record for one physical Device Under Test, identified by
/// product + serial number. Accumulates one <see cref="DutRun"/> per run — so a
/// unit crossing multiple stations/recipes builds a traceable history.
/// </summary>
public sealed class DutRecord
{
    public string Product { get; init; } = string.Empty;
    public string Serial { get; init; } = string.Empty;
    public DateTime CreatedUtc { get; init; }
    public List<DutRun> Runs { get; init; } = new();
}
