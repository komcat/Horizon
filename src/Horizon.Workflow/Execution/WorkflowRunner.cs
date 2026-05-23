using System.Globalization;
using Horizon.Common;
using Horizon.Engine;

namespace Horizon.Workflow;

/// <summary>
/// Executes a <see cref="WorkflowProgram"/> against <see cref="IHorizonEngine"/>.
/// Walks steps by index so loops and conditionals can jump; dispatches non-flow
/// ops to <see cref="StepDispatcher"/>; raises a step event before and after
/// each step. A <c>flow.subProgram</c> step recurses into another program with
/// the same context.
/// </summary>
internal sealed class WorkflowRunner
{
    /// <summary>Recursion cap on nested <c>flow.subProgram</c> calls.</summary>
    private const int MaxSubProgramDepth = 16;

    private readonly StepDispatcher _dispatcher;
    private readonly Func<string, WorkflowProgram?> _loadProgram;
    private readonly IOperatorPrompt? _prompt;
    private readonly PauseController _pause;
    private readonly Action<WorkflowStepEvent> _onStep;
    private readonly Action<LogLevel, string> _log;

    public WorkflowRunner(
        IHorizonEngine engine,
        Func<string, string, CancellationToken, Task<Result>> moveToNamed,
        Func<string, WorkflowProgram?> loadProgram,
        Func<string, string, NamedPosition?> getPosition,
        Func<string, string, MotionPosition, double, Result> savePosition,
        DeviceLinkStore deviceLinks,
        IOperatorPrompt? prompt,
        PauseController pause,
        Action<WorkflowStepEvent> onStep,
        Action<LogLevel, string> log)
    {
        _dispatcher = new StepDispatcher(engine, moveToNamed, getPosition, savePosition, deviceLinks, log);
        _loadProgram = loadProgram;
        _prompt = prompt;
        _pause = pause;
        _onStep = onStep;
        _log = log;
    }

    /// <summary>Runs a program with its own fresh context.</summary>
    public Task<Result> RunAsync(WorkflowProgram program, CancellationToken ct)
        => RunAsync(program, new WorkflowContext(), ct);

    /// <summary>
    /// Runs a program against a caller-supplied context. A deck run threads one
    /// context through every program, so a later program's transition condition
    /// can read values an earlier program captured.
    /// </summary>
    public Task<Result> RunAsync(WorkflowProgram program, WorkflowContext ctx, CancellationToken ct)
        => RunCoreAsync(program, ctx, depth: 0, ct);

    private async Task<Result> RunCoreAsync(
        WorkflowProgram program, WorkflowContext ctx, int depth, CancellationToken ct)
    {
        var steps = program.Steps;
        var loops = new Stack<LoopFrame>();
        int index = 0;

        while (index < steps.Count)
        {
            ct.ThrowIfCancellationRequested();
            var step = steps[index];

            if (!step.Enabled) { index++; continue; }

            // Pause-and-yield: blocks while paused (front-end Pause/Step) or on
            // a step breakpoint. Resumes when the front-end releases the gate.
            await _pause.WaitAsync(step.Breakpoint, ct).ConfigureAwait(false);

            var op = step.Op.ToLowerInvariant();

            if (op == "flow.loopstart")
            {
                loops.Push(new LoopFrame(index, ParseCount(step.Parameters.GetValueOrDefault("count"))));
                index++;
                continue;
            }
            if (op == "flow.loopend")
            {
                if (loops.Count == 0) { index++; continue; }
                var frame = loops.Peek();
                frame.Remaining--;
                if (frame.Remaining > 0)
                {
                    index = frame.StartIndex + 1;   // re-enter the loop body
                }
                else
                {
                    loops.Pop();
                    index++;
                }
                continue;
            }

            // Group markers — organisational only; no runtime effect.
            if (op == "flow.groupstart")
            {
                var group = step.Parameters.GetValueOrDefault("name", step.Name);
                if (!string.IsNullOrWhiteSpace(group))
                    _log(LogLevel.Info, $"Group: {group}");
                index++;
                continue;
            }
            if (op == "flow.groupend")
            {
                index++;
                continue;
            }

            var desc = string.IsNullOrEmpty(step.Name) ? step.Op : step.Name;

            // Stop Sequence — a controlled, successful early end of the program.
            if (op == "flow.stop")
            {
                var reason = $"Stop Sequence at step {index + 1}";
                _log(LogLevel.Info, $"Run stopped — {reason}.");
                return Result.Ok($"Run stopped — {reason}.");
            }

            // Sub-program — recurse into another program with the same context
            // (shared variables and DUT run), guarded against runaway nesting.
            if (op == "flow.subprogram")
            {
                var childName = step.Parameters.GetValueOrDefault("program", string.Empty);
                if (string.IsNullOrWhiteSpace(childName))
                    return Result.Fail(ErrorCode.InvalidParameter,
                        $"Step {index + 1} '{desc}': sub-program name is empty.");
                if (depth >= MaxSubProgramDepth)
                    return Result.Fail(ErrorCode.OperationFailed,
                        $"Step {index + 1} '{desc}': sub-program nesting exceeds {MaxSubProgramDepth}.");

                var child = _loadProgram(childName);
                if (child == null)
                    return Result.Fail(ErrorCode.FileNotFound,
                        $"Step {index + 1} '{desc}': sub-program '{childName}' not found.");

                _log(LogLevel.Info, $"Step {index + 1}: sub-program '{childName}' →");
                var sub = await RunCoreAsync(child, ctx, depth + 1, ct).ConfigureAwait(false);
                if (!sub.IsSuccess)
                    return Result.Fail(sub.Error, $"Step {index + 1} '{desc}' (sub-program '{childName}'): {sub.Message}");
                index++;
                continue;
            }

            // Conditional block — flow.if / flow.else / flow.endif. Forward-only
            // jumps over a stateless scan that respects nesting; no frame stack
            // is needed (there is no break/continue). Loops nest independently.
            if (op == "flow.if")
            {
                bool taken;
                try
                {
                    taken = ConditionEvaluator.Evaluate(
                        step.Parameters.GetValueOrDefault("condition"), ctx);
                }
                catch (FormatException ex)
                {
                    var err = $"bad condition — {ex.Message}";
                    _log(LogLevel.Error, $"Step {index + 1} '{desc}' failed: {err}");
                    return Result.Fail(ErrorCode.InvalidParameter, $"Step {index + 1} '{desc}': {err}");
                }

                _log(LogLevel.Info, $"Step {index + 1}: if '{desc}' → {(taken ? "true" : "false")}");
                if (taken)
                {
                    index++;   // run the then-branch
                }
                else
                {
                    // Skip to the matching else (run that branch) or endif.
                    var target = MatchForward(steps, index);
                    index = target + 1;
                }
                continue;
            }
            if (op == "flow.else")
            {
                // Reached only by falling out of a taken then-branch — skip the
                // else-branch by jumping past the matching endif.
                index = MatchEndIf(steps, index) + 1;
                continue;
            }
            if (op == "flow.endif")
            {
                index++;
                continue;
            }

            if (op == "flow.askuser")
            {
                var abort = await AskUserAsync(step, index, steps.Count, desc, ct).ConfigureAwait(false);
                if (abort != null) return abort.Value;   // null = confirmed, continue
                index++;
                continue;
            }

            _onStep(new WorkflowStepEvent(index, steps.Count, desc, Completed: false, Success: false, Message: ""));
            _log(LogLevel.Info, $"Step {index + 1}/{steps.Count}: {desc}");

            // Pre/post-step delays — ported from the bobcat recipe model, where
            // most steps carry a settle delay before and/or after the action.
            if (step.DelayBeforeMs > 0)
                await Task.Delay(step.DelayBeforeMs, ct).ConfigureAwait(false);

            Result result;
            try
            {
                result = await _dispatcher.ExecuteAsync(step, ctx, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result = Result.Fail(ErrorCode.OperationFailed, ex.Message);
            }

            if (result.IsSuccess && step.DelayAfterMs > 0)
                await Task.Delay(step.DelayAfterMs, ct).ConfigureAwait(false);

            _onStep(new WorkflowStepEvent(index, steps.Count, desc, Completed: true, result.IsSuccess, result.Message));

            if (!result.IsSuccess)
            {
                _log(LogLevel.Error, $"Step {index + 1} '{desc}' failed: {result.Message}");
                return Result.Fail(result.Error, $"Step {index + 1} '{desc}': {result.Message}");
            }

            index++;
        }

        return Result.Ok();
    }

    /// <summary>
    /// Handles a <c>flow.askUser</c> step: raises the step events around the
    /// blocking front-end confirmation. Returns <c>null</c> when the operator
    /// confirmed (the run continues); otherwise a terminating <see cref="Result"/>
    /// — a controlled abort on decline, or a failure when no handler is set.
    /// </summary>
    private async Task<Result?> AskUserAsync(
        WorkflowStep step, int index, int total, string desc, CancellationToken ct)
    {
        _onStep(new WorkflowStepEvent(index, total, desc, Completed: false, Success: false, Message: ""));

        if (_prompt == null)
        {
            const string err = "no operator-prompt handler registered";
            _onStep(new WorkflowStepEvent(index, total, desc, Completed: true, Success: false, Message: err));
            _log(LogLevel.Error, $"Step {index + 1} '{desc}' failed: {err}");
            return Result.Fail(ErrorCode.NotSupported, $"Step {index + 1} '{desc}': {err}");
        }

        var title = step.Parameters.GetValueOrDefault("title", "Confirm");
        var message = step.Parameters.GetValueOrDefault("message", string.Empty);
        var confirmed = await _prompt.ConfirmAsync(title, message, ct).ConfigureAwait(false);

        _onStep(new WorkflowStepEvent(index, total, desc, Completed: true, Success: true,
            Message: confirmed ? "confirmed" : "declined"));

        if (confirmed) return null;

        var reason = $"operator declined at step {index + 1} '{desc}'";
        _log(LogLevel.Warning, $"Run stopped — {reason}.");
        return Result.Ok($"Run stopped — {reason}.");
    }

    private static int ParseCount(string? raw)
        => double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0
            ? (int)v : 1;

    /// <summary>
    /// From a <c>flow.if</c> at <paramref name="ifIndex"/>, returns the index of
    /// its matching <c>flow.else</c> or <c>flow.endif</c> (whichever comes first
    /// at the same nesting depth). Returns <c>steps.Count</c> when unbalanced.
    /// </summary>
    private static int MatchForward(IReadOnlyList<WorkflowStep> steps, int ifIndex)
    {
        int depth = 0;
        for (int i = ifIndex + 1; i < steps.Count; i++)
        {
            switch (steps[i].Op.ToLowerInvariant())
            {
                case "flow.if": depth++; break;
                case "flow.else" when depth == 0: return i;
                case "flow.endif":
                    if (depth == 0) return i;
                    depth--;
                    break;
            }
        }
        return steps.Count;
    }

    /// <summary>
    /// From a <c>flow.if</c> or <c>flow.else</c> at <paramref name="fromIndex"/>,
    /// returns the index of the matching <c>flow.endif</c>, or <c>steps.Count</c>
    /// when unbalanced.
    /// </summary>
    private static int MatchEndIf(IReadOnlyList<WorkflowStep> steps, int fromIndex)
    {
        int depth = 0;
        for (int i = fromIndex + 1; i < steps.Count; i++)
        {
            switch (steps[i].Op.ToLowerInvariant())
            {
                case "flow.if": depth++; break;
                case "flow.endif":
                    if (depth == 0) return i;
                    depth--;
                    break;
            }
        }
        return steps.Count;
    }

    private sealed class LoopFrame
    {
        public int StartIndex { get; }
        public int Remaining { get; set; }

        public LoopFrame(int startIndex, int count)
        {
            StartIndex = startIndex;
            Remaining = count;
        }
    }
}
