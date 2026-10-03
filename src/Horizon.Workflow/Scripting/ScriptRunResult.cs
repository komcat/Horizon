namespace Horizon.Workflow.Scripting;

/// <summary>
/// Outcome of one <c>.moon</c> script run. <see cref="Output"/> is every line the
/// script emitted (via Lua <c>print</c> / <c>machine.log</c> and per-op confirmations),
/// in order. On failure <see cref="Error"/> carries MoonSharp's decorated message
/// (which already embeds the <c>chunk:line</c> location).
/// </summary>
public sealed record ScriptRunResult(
    bool Success,
    bool Cancelled,
    string? Error,
    IReadOnlyList<string> Output)
{
    public static ScriptRunResult Ok(IReadOnlyList<string> output)
        => new(true, false, null, output);

    public static ScriptRunResult Failed(string error, int? line, IReadOnlyList<string> output)
        => new(false, false, line is { } l ? $"{error} (line {l})" : error, output);

    public static ScriptRunResult CancelledRun(IReadOnlyList<string> output)
        => new(false, true, "cancelled", output);
}

/// <summary>
/// Self-reported progress from a running <c>.moon</c> script (<c>machine.progress</c>).
/// <see cref="Percent"/> is clamped to 0–100; <see cref="Message"/> is an optional
/// operator-facing note ("UV point 3"). The script author places these calls — the
/// host cannot know a script's length in advance (loops, branches).
/// </summary>
public sealed record ScriptProgress(double Percent, string? Message);
