namespace Horizon.Process.Models;

public enum StepType
{
    Action,
    Loop,
    Conditional,
    StepMarker
}

public enum ComparisonType
{
    GreaterThan,
    LessThan,
    Equal,
    NotEqual,
    GreaterOrEqual,
    LessOrEqual,
    IsTrue,
    IsFalse,
    Exists,
    NotExists
}

public abstract class ProcessStep
{
    public StepType Type { get; protected init; }
}

public class ActionStep : ProcessStep
{
    public ActionStep() { Type = StepType.Action; }
    public string DeviceName { get; init; } = string.Empty;
    public string MethodName { get; init; } = string.Empty;
    public Dictionary<string, string> Params { get; init; } = new();
}

public class LoopStep : ProcessStep
{
    public LoopStep() { Type = StepType.Loop; }
    public int Count { get; init; }
    public List<ProcessStep> Steps { get; init; } = new();
}

public class ConditionalStep : ProcessStep
{
    public ConditionalStep() { Type = StepType.Conditional; }
    public string ContextKey { get; init; } = string.Empty;
    public ComparisonType Comparison { get; init; }
    public float Threshold { get; init; }
    public List<ProcessStep> ThenSteps { get; init; } = new();
    public List<ProcessStep> ElseSteps { get; init; } = new();
}

public class StepMarkerStep : ProcessStep
{
    public StepMarkerStep() { Type = StepType.StepMarker; }
    public int StepIndex { get; init; }
    public string StepName { get; init; } = string.Empty;
    public string StepTypeName { get; init; } = string.Empty;
}
