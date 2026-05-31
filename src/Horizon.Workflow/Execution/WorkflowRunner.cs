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
        Func<string, string, MoveOverrides, CancellationToken, Task<Result>> moveToNamed,
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
        => RunCoreAsync(program, ctx, depth: 0, lane: null, ct);

    private async Task<Result> RunCoreAsync(
        WorkflowProgram program, WorkflowContext ctx, int depth, string? lane, CancellationToken ct)
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
                var sub = await RunCoreAsync(child, ctx, depth + 1, lane, ct).ConfigureAwait(false);
                if (!sub.IsSuccess)
                    return Result.Fail(sub.Error, $"Step {index + 1} '{desc}' (sub-program '{childName}'): {sub.Message}");
                index++;
                continue;
            }

            // Parallel fork-join — run every branch concurrently as its own
            // mini-sequence, then wait for all (the barrier). Fail-fast: the first
            // branch to fail cancels the others in flight and stops the program.
            if (op == "flow.parallel")
            {
                if (depth >= MaxSubProgramDepth)
                    return Result.Fail(ErrorCode.OperationFailed,
                        $"Step {index + 1} '{desc}': parallel nesting exceeds {MaxSubProgramDepth}.");

                var parallel = await RunParallelAsync(step, ctx, depth, lane, index, steps.Count, desc, ct)
                    .ConfigureAwait(false);
                if (!parallel.IsSuccess)
                    return parallel;
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
                var abort = await AskUserAsync(step, index, steps.Count, desc, lane, ct).ConfigureAwait(false);
                if (abort != null) return abort.Value;   // null = confirmed, continue
                index++;
                continue;
            }

            // Operator Yes/No that records the answer instead of aborting: stores
            // "1"/"0" into the 'var' context variable, so a later flow.if can branch
            // on it (No simply continues). Unlike flow.askUser, declining is not an
            // abort. Only fails when no operator-prompt handler is registered.
            if (op == "flow.askyesno")
            {
                var fail = await AskYesNoAsync(step, ctx, index, steps.Count, desc, lane, ct).ConfigureAwait(false);
                if (fail != null) return fail.Value;
                index++;
                continue;
            }

            _onStep(new WorkflowStepEvent(index, steps.Count, desc, Completed: false, Success: false, Message: "", Lane: lane));
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

            _onStep(new WorkflowStepEvent(index, steps.Count, desc, Completed: true, result.IsSuccess, result.Message, Lane: lane));

            if (!result.IsSuccess)
            {
                _log(LogLevel.Error, $"Step {index + 1} '{desc}' failed: {result.Message}");
                return Result.Fail(result.Error, $"Step {index + 1} '{desc}': {result.Message}");
            }

            index++;
        }

        return Result.Ok();
    }

    /// <summary>Message marker for a branch stopped by fail-fast (sibling failure / abort).</summary>
    private const string BranchCancelled = "branch cancelled";

    /// <summary>
    /// Executes a <c>flow.parallel</c> step: every branch runs concurrently as its
    /// own recursive sub-run sharing the context; the step completes when all
    /// branches finish (the barrier). Fail-fast — the first branch failure cancels
    /// the siblings (a linked token) and becomes the step's result. The step's
    /// before/after delays bracket the whole group.
    /// </summary>
    private async Task<Result> RunParallelAsync(
        WorkflowStep step, WorkflowContext ctx, int depth, string? lane,
        int index, int total, string desc, CancellationToken ct)
    {
        var branches = step.Branches.Where(b => b.Steps.Count > 0).ToList();

        _onStep(new WorkflowStepEvent(index, total, desc, Completed: false, Success: false, Message: "", Lane: lane));
        _log(LogLevel.Info, $"Step {index + 1}/{total}: parallel ({branches.Count} branch(es)) — {desc}");

        if (step.DelayBeforeMs > 0)
            await Task.Delay(step.DelayBeforeMs, ct).ConfigureAwait(false);

        var result = Result.Ok();
        if (branches.Count > 0)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var tasks = branches.Select(b => RunBranchAsync(b, ctx, depth + 1, linked)).ToList();
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);

            // The whole program being cancelled propagates as cancellation; a
            // sibling-triggered cancel does not (the program token is still live).
            ct.ThrowIfCancellationRequested();

            // Prefer the causal failure over the siblings it cancelled.
            var failed = Array.FindIndex(results, r => !r.IsSuccess && !r.Message.Contains(BranchCancelled));
            if (failed < 0) failed = Array.FindIndex(results, r => !r.IsSuccess);
            if (failed >= 0)
                result = Result.Fail(results[failed].Error,
                    $"branch '{branches[failed].Lane}': {results[failed].Message}");
        }

        if (result.IsSuccess && step.DelayAfterMs > 0)
            await Task.Delay(step.DelayAfterMs, ct).ConfigureAwait(false);

        _onStep(new WorkflowStepEvent(index, total, desc, Completed: true, result.IsSuccess, result.Message, Lane: lane));

        if (!result.IsSuccess)
        {
            _log(LogLevel.Error, $"Step {index + 1} '{desc}' failed: {result.Message}");
            return Result.Fail(result.Error, $"Step {index + 1} '{desc}': {result.Message}");
        }
        return Result.Ok();
    }

    /// <summary>
    /// Runs one parallel branch as a recursive sub-run on the linked token. On
    /// failure it cancels the token so the sibling branches stop (fail-fast). A
    /// cancellation — sibling failure or program abort — is returned as a failed
    /// Result rather than thrown, so <see cref="Task.WhenAll"/> never faults; the
    /// caller re-checks the program token to tell the two apart.
    /// </summary>
    private async Task<Result> RunBranchAsync(
        WorkflowBranch branch, WorkflowContext ctx, int depth, CancellationTokenSource linked)
    {
        var program = new WorkflowProgram { Name = branch.Lane, Steps = branch.Steps };
        try
        {
            var result = await RunCoreAsync(program, ctx, depth, branch.Lane, linked.Token).ConfigureAwait(false);
            if (!result.IsSuccess)
                linked.Cancel();   // fail-fast: stop the sibling branches
            return result;
        }
        catch (OperationCanceledException)
        {
            return Result.Fail(ErrorCode.OperationFailed, BranchCancelled);
        }
        catch (Exception ex)
        {
            linked.Cancel();
            return Result.Fail(ErrorCode.OperationFailed, ex.Message);
        }
    }

    /// <summary>
    /// Handles a <c>flow.askUser</c> step: raises the step events around the
    /// blocking front-end confirmation. Returns <c>null</c> when the operator
    /// confirmed (the run continues); otherwise a terminating <see cref="Result"/>
    /// — a controlled abort on decline, or a failure when no handler is set.
    /// </summary>
    private async Task<Result?> AskUserAsync(
        WorkflowStep step, int index, int total, string desc, string? lane, CancellationToken ct)
    {
        _onStep(new WorkflowStepEvent(index, total, desc, Completed: false, Success: false, Message: "", Lane: lane));

        if (_prompt == null)
        {
            const string err = "no operator-prompt handler registered";
            _onStep(new WorkflowStepEvent(index, total, desc, Completed: true, Success: false, Message: err, Lane: lane));
            _log(LogLevel.Error, $"Step {index + 1} '{desc}' failed: {err}");
            return Result.Fail(ErrorCode.NotSupported, $"Step {index + 1} '{desc}': {err}");
        }

        var title = step.Parameters.GetValueOrDefault("title", "Confirm");
        var message = step.Parameters.GetValueOrDefault("message", string.Empty);
        var confirmed = await _prompt.ConfirmAsync(title, message, ct).ConfigureAwait(false);

        _onStep(new WorkflowStepEvent(index, total, desc, Completed: true, Success: true,
            Message: confirmed ? "confirmed" : "declined", Lane: lane));

        if (confirmed) return null;

        var reason = $"operator declined at step {index + 1} '{desc}'";
        _log(LogLevel.Warning, $"Run stopped — {reason}.");
        return Result.Ok($"Run stopped — {reason}.");
    }

    /// <summary>
    /// Handles a <c>flow.askYesNo</c> step: asks the operator Yes/No and stores the
    /// answer ("1"/"0") in the <c>var</c> context variable, then ALWAYS continues
    /// (No is not an abort). Returns <c>null</c> normally, or a failure
    /// <see cref="Result"/> only when no operator-prompt handler is registered.
    /// </summary>
    private async Task<Result?> AskYesNoAsync(
        WorkflowStep step, WorkflowContext ctx, int index, int total, string desc, string? lane, CancellationToken ct)
    {
        _onStep(new WorkflowStepEvent(index, total, desc, Completed: false, Success: false, Message: "", Lane: lane));

        if (_prompt == null)
        {
            const string err = "no operator-prompt handler registered";
            _onStep(new WorkflowStepEvent(index, total, desc, Completed: true, Success: false, Message: err, Lane: lane));
            _log(LogLevel.Error, $"Step {index + 1} '{desc}' failed: {err}");
            return Result.Fail(ErrorCode.NotSupported, $"Step {index + 1} '{desc}': {err}");
        }

        var title = step.Parameters.GetValueOrDefault("title", "Confirm");
        var message = step.Parameters.GetValueOrDefault("message", string.Empty);
        var varName = step.Parameters.GetValueOrDefault("var", string.Empty);
        var confirmed = await _prompt.ConfirmAsync(title, message, ct).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(varName))
            ctx.Set(varName, confirmed ? "1" : "0");

        _onStep(new WorkflowStepEvent(index, total, desc, Completed: true, Success: true,
            Message: confirmed ? "yes" : "no", Lane: lane));
        _log(LogLevel.Info, $"Step {index + 1}: askYesNo '{desc}' → {(confirmed ? "yes" : "no")}" +
            (string.IsNullOrWhiteSpace(varName) ? "" : $" → {varName}"));
        return null;   // never aborts — branching is the caller's job (flow.if)
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
