namespace Horizon.Workflow;

/// <summary>One step of a process program — an operation plus its parameters.</summary>
public sealed record WorkflowStep
{
    public string Id { get; init; } = string.Empty;
    /// <summary>Display nickname.</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>Operation key, e.g. <c>motion.moveToPosition</c>.</summary>
    public string Op { get; init; } = string.Empty;
    /// <summary>Target device (empty for ops that take none).</summary>
    public string Device { get; init; } = string.Empty;
    public Dictionary<string, string> Parameters { get; init; } = new();
    public bool Enabled { get; init; } = true;
    /// <summary>When true, the runner pauses before executing this step.</summary>
    public bool Breakpoint { get; init; }
    /// <summary>Milliseconds the runner waits before dispatching this step. 0 = none.</summary>
    public int DelayBeforeMs { get; init; }
    /// <summary>Milliseconds the runner waits after this step succeeds. 0 = none.</summary>
    public int DelayAfterMs { get; init; }
    /// <summary>
    /// Concurrent branches of a <c>flow.parallel</c> step — each runs as its own
    /// mini-sequence, all started together and joined (wait-all). Empty for every
    /// other op, so existing programs deserialize unchanged. See
    /// <c>docs/timeline-sequence-editor-design.md</c>.
    /// </summary>
    public List<WorkflowBranch> Branches { get; init; } = new();
}

/// <summary>
/// One concurrent lane of a <c>flow.parallel</c> step: an ordered sub-sequence run by
/// a recursive runner. <see cref="Lane"/> is the authoring/UI row id (often a device
/// name) — it carries no execution meaning beyond tagging the lane's step events.
/// </summary>
public sealed record WorkflowBranch
{
    public string Lane { get; init; } = string.Empty;
    public List<WorkflowStep> Steps { get; init; } = new();
}

/// <summary>A runnable process program — an ordered sequence of steps.</summary>
public sealed record WorkflowProgram
{
    public string Name { get; init; } = string.Empty;
    public List<WorkflowStep> Steps { get; init; } = new();
}

/// <summary>
/// Progress event raised before and after each executed step. <see cref="Lane"/> is
/// null for top-level steps and the branch lane id for steps run inside a
/// <c>flow.parallel</c> branch (so a timeline UI can highlight the right row).
/// <see cref="Depth"/> is 0 for the program's own steps and grows by one per
/// <c>flow.subProgram</c> / parallel-branch level, so a progress bar can count only
/// top-level steps (Index/Total of a nested event refer to the nested sequence).
/// </summary>
public sealed record WorkflowStepEvent(
    int Index,
    int Total,
    string Description,
    bool Completed,
    bool Success,
    string Message,
    string? Lane = null,
    int Depth = 0);
