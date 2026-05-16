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
}

/// <summary>A runnable process program — an ordered sequence of steps.</summary>
public sealed record WorkflowProgram
{
    public string Name { get; init; } = string.Empty;
    public List<WorkflowStep> Steps { get; init; } = new();
}

/// <summary>Progress event raised before and after each executed step.</summary>
public sealed record WorkflowStepEvent(
    int Index,
    int Total,
    string Description,
    bool Completed,
    bool Success,
    string Message);
