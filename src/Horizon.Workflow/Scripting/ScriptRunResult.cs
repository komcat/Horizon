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
