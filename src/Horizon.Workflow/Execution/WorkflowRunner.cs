using System.Globalization;
using Horizon.Common;
using Horizon.Engine;
using Horizon.Workflow.HillClimb;
using Horizon.Workflow.Signals;

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
    private readonly IHorizonEngine _engine;
    private readonly Func<string, string, NamedPosition?> _getPosition;
    private readonly Func<string, string, MotionPosition, double, Result> _savePosition;
    private readonly Func<string, WorkflowProgram?> _loadProgram;
    private readonly IOperatorPrompt? _prompt;
    private readonly Func<string, WorkflowContext, Result>? _setDutSerial;
    private readonly Func<DutVerdict, WorkflowContext, Result>? _closeDut;
    private readonly PauseController _pause;
    private readonly Action<WorkflowStepEvent> _onStep;
    private readonly Action<LogLevel, string> _log;

    // Nesting depth of the sequence currently executing on this async flow. Set at the
    // top of RunCoreAsync; an async method's AsyncLocal writes don't leak back to its
    // caller, so each sub-program / parallel branch sees its own value. Stamped onto
    // every step event so listeners can tell top-level steps from nested ones.
    private readonly AsyncLocal<int> _depth = new();

    public WorkflowRunner(
        IHorizonEngine engine,
        Func<string, string, MoveOverrides, CancellationToken, Task<Result>> moveToNamed,
        Func<string, WorkflowProgram?> loadProgram,
        Func<string, string, NamedPosition?> getPosition,
        Func<string, string, MotionPosition, double, Result> savePosition,
        Func<string, Task<Result<VisionDetectResult>>> runVisionProfile,
        Func<string, CancellationToken, Task<Result>> runDispenseRoutine,
        Func<double, double, CancellationToken, Task<Result>> runNeedleApproach,
        Func<string, double, bool, CancellationToken, Task<Result>> moveCameraToTool,
        Func<string, double, bool, double?, CancellationToken, Task<Result>> moveToolToCamera,
        Func<string, CancellationToken, Task<Result>> moveToolToPose,
        Func<string, double, bool, double?, CancellationToken, Task<Result>> moveObjectToCamera,
        Func<string, CancellationToken, Task<Result<Registration.PartMeasurement>>> runPartAlignment,
        Func<string, int, CancellationToken, Task<Result>> moveCameraToPartFiducial,
        Func<string, string, bool, CancellationToken, Task<Result>> moveCameraToPartObject,
        Func<string, string, CancellationToken, Task<Result>> runScript,
        Func<SiphogSnapshotRequest, CancellationToken, Task<Result<SiphogSnapshotSet>>> saveSiphogSnapshot,
        Func<IHillClimbFeedback?> hillClimbFeedback,
        Func<IUtilityAppHost?> utilityAppHost,
        Func<ISiphogSheetSink?> siphogSheetSink,
        SignalRegistry signals,
        DeviceLinkStore deviceLinks,
        Func<string, PositionMapLayout> loadPositionMap,
        IOperatorPrompt? prompt,
        Func<string, WorkflowContext, Result>? setDutSerial,
        Func<DutVerdict, WorkflowContext, Result>? closeDut,
        PauseController pause,
        Action<WorkflowStepEvent> onStep,
        Action<LogLevel, string> log)
    {
        _dispatcher = new StepDispatcher(engine, moveToNamed, getPosition, savePosition, runVisionProfile, runDispenseRoutine, runNeedleApproach, moveCameraToTool, moveToolToCamera, moveToolToPose, moveObjectToCamera, runPartAlignment, moveCameraToPartFiducial, moveCameraToPartObject, runScript, saveSiphogSnapshot, hillClimbFeedback, utilityAppHost, siphogSheetSink, signals, deviceLinks, loadPositionMap, log);
        _engine = engine;
        _getPosition = getPosition;
        _savePosition = savePosition;
        _loadProgram = loadProgram;
        _prompt = prompt;
        _setDutSerial = setDutSerial;
        _closeDut = closeDut;
        _pause = pause;
        _onStep = e => onStep(e with { Depth = _depth.Value });
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
        _depth.Value = depth;
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
            if (IsIfOpener(op))
            {
                bool taken;
                if (op == "flow.if")
                {
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
                }
                else
                {
                    // Guided If (flow.ifSignal / flow.ifPosition) — the dispatcher reads the
                    // left-hand value live and compares it. A read failure aborts the run.
                    var cond = await _dispatcher.EvaluateGuidedConditionAsync(step, ctx, ct).ConfigureAwait(false);
                    if (!cond.IsSuccess)
                    {
                        _log(LogLevel.Error, $"Step {index + 1} '{desc}' failed: {cond.Message}");
                        return Result.Fail(cond.Error, $"Step {index + 1} '{desc}': {cond.Message}");
                    }
                    taken = cond.Value;
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

            // Real-time countdown the operator watches in a popup. Completing the
            // countdown continues the run; the operator pressing Cancel fails it.
            if (op == "flow.waitpopup")
            {
                var fail = await WaitPopupAsync(step, index, steps.Count, desc, lane, ct).ConfigureAwait(false);
                if (fail != null) return fail.Value;
                index++;
                continue;
            }

            // Non-modal always-on-top manual-jog window with a count-up timer. The
            // operator nudges the configured devices/axes then Continues (run resumes)
            // or Cancels (fails the run); the timeout resolves as Cancel.
            if (op == "flow.jogpopup")
            {
                var fail = await JogPopupAsync(step, index, steps.Count, desc, lane, ct).ConfigureAwait(false);
                if (fail != null) return fail.Value;
                index++;
                continue;
            }

            // Teach-or-approach a final position: first run = manual jog then save it; later
            // runs offer a crawl to the saved point under a big STOP window (velocity
            // snapshot → crawl → restore), then the manual jog for fine adjust.
            if (op == "motion.slowapproach")
            {
                var fail = await SlowApproachStepAsync(step, index, steps.Count, desc, lane, ct).ConfigureAwait(false);
                if (fail != null) return fail.Value;
                index++;
                continue;
            }

            // Yellow notice (e.g. "UV complete") the operator must acknowledge, with a
            // buzzer output pulsed in a gentle pattern until they do.
            if (op == "io.alertbuzzer")
            {
                var fail = await AlertBuzzerAsync(step, ctx, index, steps.Count, desc, lane, ct).ConfigureAwait(false);
                if (fail != null) return fail.Value;
                index++;
                continue;
            }

            // Prompts the operator for a serial number and opens the DUT record for
            // this run, so later dut.* steps record into it. Cancelling fails the run.
            if (op == "dut.promptserial")
            {
                var fail = await PromptSerialAsync(step, ctx, index, steps.Count, desc, lane, ct).ConfigureAwait(false);
                if (fail != null) return fail.Value;
                index++;
                continue;
            }

            // Asks the operator to type free text and files it in the open DUT record
            // under 'key' (and/or a context variable).
            if (op == "dut.prompttext")
            {
                var fail = await PromptTextAsync(step, ctx, index, steps.Count, desc, lane, ct).ConfigureAwait(false);
                if (fail != null) return fail.Value;
                index++;
                continue;
            }

            if (op == "dut.close")
            {
                CloseDut(step, ctx, index, steps.Count, desc, lane);
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

            // Best-effort ops (Save SAG to Sheet, Save SiPhOG Snapshot): a missing DUT serial,
            // an unregistered sink, a disconnected client or a server ERR / reply timeout is a
            // nuisance, not necessarily a fault worth stopping the process over. With the
            // block's 'askOnFailure' on (default; also for blocks saved before it existed) the
            // operator is asked Continue / Stop; off — or headless with no prompt — it skips
            // with a warning. A skip counts as done (not red). STOP still propagates above.
            if (!result.IsSuccess && IsNonFatalOp(op))
            {
                bool ask = !step.Parameters.TryGetValue("askOnFailure", out var askRaw)
                           || askRaw.Trim().ToLowerInvariant() is "true" or "1" or "on" or "yes";
                if (ask && _prompt != null && !ct.IsCancellationRequested
                    && !await PromptNonFatalFailureAsync(index, desc, result, ct).ConfigureAwait(false))
                {
                    _onStep(new WorkflowStepEvent(index, steps.Count, desc, Completed: true, Success: false,
                        Message: $"stopped by operator: {result.Message}", Lane: lane));
                    _log(LogLevel.Error, $"Step {index + 1} '{desc}' failed — operator stopped the run: {result.Message}");
                    return Result.Fail(ErrorCode.ProcessCancelled,
                        $"Step {index + 1} '{desc}': {result.Message} (operator stopped the run)");
                }
                _log(LogLevel.Warning, $"Step {index + 1} '{desc}' skipped (non-fatal): {result.Message}" +
                    (ask && _prompt != null ? " — operator chose to continue." : ""));
                result = Result.Ok($"skipped — {result.Message}");
            }

            if (result.IsSuccess && step.DelayAfterMs > 0)
                await Task.Delay(step.DelayAfterMs, ct).ConfigureAwait(false);

            _onStep(new WorkflowStepEvent(index, steps.Count, desc, Completed: true, result.IsSuccess, result.Message, Lane: lane));

            if (!result.IsSuccess)
            {
                // A motion move the controller rejected (e.g. an unreachable target
                // because the axis is already near a travel limit) need not kill the
                // whole program — let the operator skip the move and carry on. Only
                // offered when a prompt handler is registered and the run is not being
                // cancelled; headless / console runs (no prompt) still abort.
                if (IsRecoverableMoveOp(op) && _prompt != null && !ct.IsCancellationRequested
                    && await PromptMoveFailureAsync(step, index, steps.Count, desc, result, lane, ct)
                        .ConfigureAwait(false))
                {
                    index++;
                    continue;
                }

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
    /// — a failure when no handler is set, or an <see cref="ErrorCode.ProcessCancelled"/>
    /// failure on decline.
    ///
    /// Declining is reported as a <b>failure</b>, not a successful early stop:
    /// this step guards physical actions (grip / move onto the part), so every
    /// caller — the deck runner, the Run page's auto-chain, a parallel sibling
    /// branch — must treat Cancel as "stop now", never as "this program finished".
    /// Front-ends tell an operator Cancel apart from a real error by the
    /// <see cref="ErrorCode.ProcessCancelled"/> code.
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

        _onStep(new WorkflowStepEvent(index, total, desc, Completed: true, Success: confirmed,
            Message: confirmed ? "confirmed" : "declined", Lane: lane));

        if (confirmed) return null;

        var reason = $"operator cancelled at step {index + 1} '{desc}'";
        _log(LogLevel.Warning, $"Run stopped — {reason}.");
        return Result.Fail(ErrorCode.ProcessCancelled, $"Run stopped — {reason}.");
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

    /// <summary>
    /// Handles a <c>flow.waitPopup</c> step: shows the operator a real-time
    /// countdown via the front-end and blocks until it elapses or the operator
    /// cancels. Returns <c>null</c> when the countdown completes (the run
    /// continues); a failure <see cref="Result"/> when the operator cancels or no
    /// operator-prompt handler is registered.
    /// </summary>
    private async Task<Result?> WaitPopupAsync(
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

        var ms = ParseMs(step.Parameters.GetValueOrDefault("ms"));
        var title = step.Parameters.GetValueOrDefault("title", "Please wait");
        var message = step.Parameters.GetValueOrDefault("message", string.Empty);

        var completed = await _prompt.CountdownAsync(title, message, ms, ct).ConfigureAwait(false);

        if (completed)
        {
            _onStep(new WorkflowStepEvent(index, total, desc, Completed: true, Success: true, Message: "elapsed", Lane: lane));
            return null;
        }

        const string cancelled = "wait cancelled by operator";
        _onStep(new WorkflowStepEvent(index, total, desc, Completed: true, Success: false, Message: cancelled, Lane: lane));
        _log(LogLevel.Warning, $"Step {index + 1} '{desc}' — {cancelled}.");
        return Result.Fail(ErrorCode.OperationFailed, $"Step {index + 1} '{desc}': {cancelled}");
    }

    /// <summary>
    /// Handles an <c>io.alertBuzzer</c> step: shows the operator an alert to acknowledge
    /// while the dispatcher pulses the step's buzzer output in the background; the
    /// buzzer is stopped (and its output left OFF) as soon as the alert is acknowledged
    /// or the run is stopped. A buzzer IO fault is logged but does not fail the step —
    /// the visual alert still did its job. Returns <c>null</c> once acknowledged; a
    /// failure <see cref="Result"/> only when no operator-prompt handler is registered.
    /// </summary>
    private async Task<Result?> AlertBuzzerAsync(
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

        var title = step.Parameters.GetValueOrDefault("title", "Process complete");
        var message = step.Parameters.GetValueOrDefault("message", string.Empty);
        _log(LogLevel.Info, $"Step {index + 1}/{total}: alert '{title}' — waiting for operator acknowledge");

        Result buzzer;
        using (var buzzCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            var buzzTask = _dispatcher.RunBuzzerAsync(step, ctx, buzzCts.Token);
            try
            {
                await _prompt.AcknowledgeAsync(title, message, ct).ConfigureAwait(false);
            }
            finally
            {
                // Acknowledge or Stop — silence the buzzer before anything else happens.
                buzzCts.Cancel();
                buzzer = await buzzTask.ConfigureAwait(false);
            }
        }

        if (!buzzer.IsSuccess)
            _log(LogLevel.Warning, $"Step {index + 1} '{desc}': buzzer fault — {buzzer.Message}");
        _onStep(new WorkflowStepEvent(index, total, desc, Completed: true, Success: true,
            Message: buzzer.IsSuccess ? "acknowledged" : $"acknowledged (buzzer fault: {buzzer.Message})", Lane: lane));
        _log(LogLevel.Info, $"Step {index + 1}: alert '{title}' acknowledged.");
        return null;
    }

    /// <summary>
    /// Handles a <c>dut.promptSerial</c> step: asks the operator for a serial number
    /// and opens the DUT record for this run (so later <c>dut.*</c> steps record into
    /// it), optionally stashing the serial in the <c>var</c> context variable. Returns
    /// <c>null</c> on success; a failure <see cref="Result"/> when the operator cancels,
    /// no handler is registered, or the serial cannot be set.
    /// </summary>
    private async Task<Result?> PromptSerialAsync(
        WorkflowStep step, WorkflowContext ctx, int index, int total, string desc, string? lane, CancellationToken ct)
    {
        _onStep(new WorkflowStepEvent(index, total, desc, Completed: false, Success: false, Message: "", Lane: lane));

        // If a unit is already active — an open Run-page "Start DUT" session, an
        // inherited active serial, or an earlier prompt this run — adopt it and skip
        // the prompt, so this block and the Start button are fully interchangeable.
        // 'newUnit' (FireDog's dut_open new): the inherited sticky serial is the LAST unit,
        // not this one — prompt instead (the new serial overrides that fallback).
        var newUnit = step.Parameters.TryGetValue("newUnit", out var nu)
                      && bool.TryParse(nu, out var isNew) && isNew;
        bool inheritedOnly = ctx.DutIsFallback && !ctx.DutIsSession;
        if (ctx.Dut != null && !(newUnit && inheritedOnly))
        {
            var adopted = ctx.DutSerial ?? string.Empty;
            ctx.DutIsFallback = false;   // the block has explicitly chosen this DUT
            var adoptVar = step.Parameters.GetValueOrDefault("var", string.Empty);
            if (!string.IsNullOrWhiteSpace(adoptVar) && !string.IsNullOrWhiteSpace(adopted))
                ctx.Set(adoptVar, adopted);
            _onStep(new WorkflowStepEvent(index, total, desc, Completed: true, Success: true,
                Message: $"using active DUT {adopted}", Lane: lane));
            _log(LogLevel.Info, $"Step {index + 1}: DUT already active → {adopted} (prompt skipped).");
            return null;
        }

        if (_prompt == null)
        {
            const string err = "no operator-prompt handler registered";
            _onStep(new WorkflowStepEvent(index, total, desc, Completed: true, Success: false, Message: err, Lane: lane));
            _log(LogLevel.Error, $"Step {index + 1} '{desc}' failed: {err}");
            return Result.Fail(ErrorCode.NotSupported, $"Step {index + 1} '{desc}': {err}");
        }

        var title = step.Parameters.GetValueOrDefault("title", "DUT Serial");
        var message = step.Parameters.GetValueOrDefault("message", "Enter or scan the unit serial number:");
        var defaultValue = step.Parameters.GetValueOrDefault("default", string.Empty);

        var serial = await _prompt.InputTextAsync(title, message, defaultValue, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(serial))
        {
            const string cancelled = "serial entry cancelled by operator";
            _onStep(new WorkflowStepEvent(index, total, desc, Completed: true, Success: false, Message: cancelled, Lane: lane));
            _log(LogLevel.Warning, $"Step {index + 1} '{desc}' — {cancelled}.");
            return Result.Fail(ErrorCode.OperationFailed, $"Step {index + 1} '{desc}': {cancelled}");
        }

        serial = serial.Trim();
        if (_setDutSerial != null)
        {
            var set = _setDutSerial(serial, ctx);
            if (!set.IsSuccess)
            {
                _onStep(new WorkflowStepEvent(index, total, desc, Completed: true, Success: false, Message: set.Message, Lane: lane));
                _log(LogLevel.Error, $"Step {index + 1} '{desc}' failed: {set.Message}");
                return Result.Fail(set.Error, $"Step {index + 1} '{desc}': {set.Message}");
            }
        }

        var varName = step.Parameters.GetValueOrDefault("var", string.Empty);
        if (!string.IsNullOrWhiteSpace(varName))
            ctx.Set(varName, serial);

        _onStep(new WorkflowStepEvent(index, total, desc, Completed: true, Success: true, Message: serial, Lane: lane));
        _log(LogLevel.Info, $"Step {index + 1}: DUT serial set → {serial}" +
            (string.IsNullOrWhiteSpace(varName) ? "" : $" → {varName}"));
        return null;
    }

    /// <summary>
    /// Handles a <c>dut.promptText</c> step: asks the operator for free text and records
    /// it in the open DUT record under <c>key</c> (always as a String, so e.g. "007" keeps
    /// its zeros), optionally stashing it in the <c>var</c> context variable. With no DUT
    /// open and no <c>var</c> the prompt is skipped (nowhere to put the answer). Cancel
    /// fails the run when <c>required</c> (default); otherwise it is skipped with a warning.
    /// </summary>
    private async Task<Result?> PromptTextAsync(
        WorkflowStep step, WorkflowContext ctx, int index, int total, string desc, string? lane, CancellationToken ct)
    {
        _onStep(new WorkflowStepEvent(index, total, desc, Completed: false, Success: false, Message: "", Lane: lane));

        var key = step.Parameters.GetValueOrDefault("key", "note").Trim();
        if (string.IsNullOrEmpty(key)) key = "note";
        var varName = step.Parameters.GetValueOrDefault("var", string.Empty);
        var required = !step.Parameters.TryGetValue("required", out var reqRaw)
                       || reqRaw.Trim().ToLowerInvariant() is "true" or "1" or "on" or "yes";

        if (ctx.Dut == null && string.IsNullOrWhiteSpace(varName))
        {
            const string skipped = "no DUT open — text entry skipped";
            _onStep(new WorkflowStepEvent(index, total, desc, Completed: true, Success: true, Message: skipped, Lane: lane));
            _log(LogLevel.Warning, $"Step {index + 1} '{desc}' — {skipped}.");
            return null;
        }

        if (_prompt == null)
        {
            const string err = "no operator-prompt handler registered";
            _onStep(new WorkflowStepEvent(index, total, desc, Completed: true, Success: false, Message: err, Lane: lane));
            _log(LogLevel.Error, $"Step {index + 1} '{desc}' failed: {err}");
            return Result.Fail(ErrorCode.NotSupported, $"Step {index + 1} '{desc}': {err}");
        }

        var title = step.Parameters.GetValueOrDefault("title", "DUT Entry");
        var message = step.Parameters.GetValueOrDefault("message", "Enter text for this unit:");
        var defaultValue = step.Parameters.GetValueOrDefault("default", string.Empty);

        var text = await _prompt.InputTextAsync(title, message, defaultValue, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(text))
        {
            const string cancelled = "text entry cancelled by operator";
            _onStep(new WorkflowStepEvent(index, total, desc, Completed: true, Success: !required,
                Message: required ? cancelled : $"skipped — {cancelled}", Lane: lane));
            _log(LogLevel.Warning, $"Step {index + 1} '{desc}' — {cancelled}.");
            return required
                ? Result.Fail(ErrorCode.OperationFailed, $"Step {index + 1} '{desc}': {cancelled}")
                : null;
        }

        text = text.Trim();
        if (ctx.Dut != null)
            ctx.Dut.Entries.Add(new DutEntry(DateTime.UtcNow, key, text, DutValueType.String));
        else
            _log(LogLevel.Warning, $"Step {index + 1} '{desc}' — no DUT open; '{key}' not recorded.");
        if (!string.IsNullOrWhiteSpace(varName))
            ctx.Set(varName, text);

        _onStep(new WorkflowStepEvent(index, total, desc, Completed: true, Success: true, Message: $"{key} = {text}", Lane: lane));
        _log(LogLevel.Info, $"Step {index + 1}: DUT text '{key}' = {text}" +
            (string.IsNullOrWhiteSpace(varName) ? "" : $" → {varName}"));
        return null;
    }

    /// <summary>
    /// Handles a <c>dut.close</c> step: finalises the DUT this run is recording into
    /// with the chosen verdict (default Pass), persists the record, and detaches it
    /// so later <c>dut.*</c> steps skip and the program's own end-of-run close doesn't
    /// double-record. Best-effort — closing with no DUT open, or a persist hiccup, is
    /// logged but never aborts the program (the bookend to <c>dut.promptSerial</c>).
    /// </summary>
    private void CloseDut(WorkflowStep step, WorkflowContext ctx, int index, int total, string desc, string? lane)
    {
        _onStep(new WorkflowStepEvent(index, total, desc, Completed: false, Success: false, Message: "", Lane: lane));

        if (ctx.Dut == null)
        {
            _onStep(new WorkflowStepEvent(index, total, desc, Completed: true, Success: true, Message: "no DUT open", Lane: lane));
            _log(LogLevel.Warning, $"Step {index + 1} '{desc}' — no DUT open (nothing to close).");
            return;
        }

        var verdictText = step.Parameters.GetValueOrDefault("verdict", "Pass");
        if (!Enum.TryParse<DutVerdict>(verdictText, ignoreCase: true, out var verdict))
            verdict = DutVerdict.Pass;

        var serial = ctx.DutSerial ?? string.Empty;
        var r = _closeDut?.Invoke(verdict, ctx) ?? Result.Ok();

        _onStep(new WorkflowStepEvent(index, total, desc, Completed: true, r.IsSuccess,
            r.IsSuccess ? $"{serial} {verdict}" : r.Message, Lane: lane));
        _log(r.IsSuccess ? LogLevel.Info : LogLevel.Warning, r.IsSuccess
            ? $"Step {index + 1}: DUT closed → {serial} [{verdict}]."
            : $"Step {index + 1} '{desc}' — DUT close issue: {r.Message}");
    }

    /// <summary>
    /// Motion ops whose failure is worth offering the operator a "skip and continue"
    /// choice — a rejected target (near a limit, unreachable) is a recoverable nuisance
    /// rather than a hard fault. Non-move ops always abort on failure.
    /// </summary>
    private static bool IsRecoverableMoveOp(string op) => op is
        "motion.moverelative" or "motion.movetoposition" or
        "motion.parallelmove" or "motion.compensatedevicelink";

    /// <summary>
    /// Best-effort ops whose failure is logged but never aborts the run. Writing the
    /// SiPhOG SAG row to the Google Sheet can fail for several benign reasons (no
    /// active DUT serial, no sink registered when run from a non-SiPhOG head, a server
    /// ERR / Google round-trip hiccup); none of those should stop the process.
    /// </summary>
    private static bool IsNonFatalOp(string op) => op is "siphog.updatesheet" or "siphog.savesnapshot";

    /// <summary>
    /// A motion move failed (e.g. the controller rejected an unreachable target).
    /// Asks the operator whether to skip the move and continue the run. Returns
    /// <c>true</c> to continue (logs a warning, re-marks the step as continued),
    /// <c>false</c> to abort. A run Stop arrives via <paramref name="ct"/>.
    /// </summary>
    /// <summary>
    /// A best-effort step failed: asks the operator whether to carry on without it
    /// (<c>true</c>) or stop the run (<c>false</c>). A run Stop arrives via <paramref name="ct"/>.
    /// </summary>
    private Task<bool> PromptNonFatalFailureAsync(int index, string desc, Result failure, CancellationToken ct)
        => _prompt!.ConfirmAsync("Step failed",
            $"Step {index + 1} '{desc}' did not complete:\n\n{failure.Message}\n\n" +
            "This step is optional — the process can carry on without it.\n\n" +
            "Continue the program? (No stops the run.)", ct);

    private async Task<bool> PromptMoveFailureAsync(
        WorkflowStep step, int index, int total, string desc, Result failure, string? lane, CancellationToken ct)
    {
        var message =
            $"Step {index + 1} '{desc}' could not move:\n\n{failure.Message}\n\n" +
            "This can happen when the target is unreachable — e.g. the axis is already near a travel limit.\n\n" +
            "Skip this move and continue the program?";

        var carryOn = await _prompt!.ConfirmAsync("Move failed", message, ct).ConfigureAwait(false);

        if (carryOn)
        {
            _log(LogLevel.Warning,
                $"Step {index + 1} '{desc}' move failed ({failure.Message}) — operator chose to continue.");
            _onStep(new WorkflowStepEvent(index, total, desc, Completed: true, Success: true,
                Message: $"move failed — operator continued: {failure.Message}", Lane: lane));
        }
        return carryOn;
    }

    /// <summary>
    /// Handles a <c>flow.jogPopup</c> step: opens an always-on-top manual-jog window
    /// (via the front-end) and blocks until the operator Continues or Cancels (or the
    /// count-up timeout elapses). Returns <c>null</c> on Continue (the run proceeds); a
    /// failure <see cref="Result"/> on Cancel / timeout, or when no prompt handler is set.
    /// </summary>
    private async Task<Result?> JogPopupAsync(
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

        var devices = (step.Parameters.GetValueOrDefault("devices") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var request = new JogPopupRequest(
            Title: step.Parameters.GetValueOrDefault("title", "Manual jog"),
            Message: step.Parameters.GetValueOrDefault("message", string.Empty),
            Devices: devices,
            Axes: step.Parameters.GetValueOrDefault("axes", "XYZUVW"),
            DefaultStepMm: ParseDouble(step.Parameters.GetValueOrDefault("step"), 0.01),
            MaxSeconds: Math.Clamp((int)ParseDouble(step.Parameters.GetValueOrDefault("timeoutSec"), 3600), 1, 3600));

        var carryOn = await _prompt.JogPopupAsync(request, ct).ConfigureAwait(false);

        if (carryOn)
        {
            _onStep(new WorkflowStepEvent(index, total, desc, Completed: true, Success: true, Message: "continued", Lane: lane));
            return null;
        }

        const string cancelled = "manual jog cancelled by operator";
        _onStep(new WorkflowStepEvent(index, total, desc, Completed: true, Success: false, Message: cancelled, Lane: lane));
        _log(LogLevel.Warning, $"Step {index + 1} '{desc}' — {cancelled}.");
        return Result.Fail(ErrorCode.OperationFailed, $"Step {index + 1} '{desc}': {cancelled}");
    }

    /// <summary>
    /// Handles a <c>motion.slowApproach</c> step — teach-or-approach a final position that
    /// the operator reaches by hand (e.g. the dispense point after the head is down).
    /// <list type="bullet">
    /// <item>Position not saved yet: the manual-jog popup opens; on Continue the device's
    /// current coordinates are saved under the position name.</item>
    /// <item>Position saved: the operator is asked whether to crawl there automatically. Yes →
    /// <see cref="SlowApproach"/> snapshots the velocity, crawls at <c>speed</c> under a big
    /// STOP window, and restores the velocity. Then the manual-jog popup opens for fine
    /// adjust (skipped after a clean arrival when <c>jogAfterApproach</c> is off).</item>
    /// </list>
    /// A refused / failed / STOPPED approach falls back to the manual jog, so the operator
    /// can recover or Cancel. Jog Cancel fails the run. Returns <c>null</c> on success.
    /// </summary>
    private async Task<Result?> SlowApproachStepAsync(
        WorkflowStep step, int index, int total, string desc, string? lane, CancellationToken ct)
    {
        _onStep(new WorkflowStepEvent(index, total, desc, Completed: false, Success: false, Message: "", Lane: lane));

        Result Fail(ErrorCode code, string err)
        {
            _onStep(new WorkflowStepEvent(index, total, desc, Completed: true, Success: false, Message: err, Lane: lane));
            _log(LogLevel.Error, $"Step {index + 1} '{desc}' failed: {err}");
            return Result.Fail(code, $"Step {index + 1} '{desc}': {err}");
        }
        bool Flag(string key, bool fallback) => step.Parameters.TryGetValue(key, out var raw)
            ? raw.Trim().ToLowerInvariant() is "true" or "1" or "on" or "yes"
            : fallback;

        if (_prompt == null)
            return Fail(ErrorCode.NotSupported, "no operator-prompt handler registered");

        var device = step.Device;
        var name = step.Parameters.GetValueOrDefault("position", string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(device))
            return Fail(ErrorCode.InvalidParameter, "no motion device.");
        if (string.IsNullOrEmpty(name))
            return Fail(ErrorCode.InvalidParameter, "no position name.");

        var speed = ParseDouble(step.Parameters.GetValueOrDefault("speed"), 0.2);
        // Translational axes only — the distance guard is in mm.
        var axes = new string(step.Parameters.GetValueOrDefault("axes", "XYZ").ToUpperInvariant()
            .Where(c => c is 'X' or 'Y' or 'Z').Distinct().ToArray());
        if (axes.Length == 0) axes = "XYZ";
        var maxDistance = ParseDouble(step.Parameters.GetValueOrDefault("maxDistance"), 10.0);
        var jogAfter = Flag("jogAfterApproach", true);
        var updateAfterJog = Flag("updateAfterJog", false);
        var userMessage = step.Parameters.GetValueOrDefault("message", string.Empty);

        var saved = _getPosition(device, name);
        string note;
        bool arrived = false;

        if (saved == null)
        {
            note = $"No saved '{name}' yet — jog {device} to the final position. " +
                   "It is saved when you press Continue.";
            _log(LogLevel.Info, $"Step {index + 1}: '{name}' not saved on {device} — manual teach.");
        }
        else
        {
            (note, arrived) = await OfferApproachAsync(step, index, desc, device, name, saved, axes, speed, maxDistance, ct)
                .ConfigureAwait(false);
        }

        bool showJog = !arrived || jogAfter;
        if (showJog)
        {
            var devices = (step.Parameters.GetValueOrDefault("jogDevices") ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var request = new JogPopupRequest(
                Title: step.Parameters.GetValueOrDefault("jogTitle", "Manual adjust"),
                Message: string.IsNullOrWhiteSpace(userMessage) ? note : $"{note}\n\n{userMessage}",
                Devices: devices.Length > 0 ? devices : new[] { device },
                Axes: step.Parameters.GetValueOrDefault("jogAxes", "XYZ"),
                DefaultStepMm: ParseDouble(step.Parameters.GetValueOrDefault("step"), 0.01),
                MaxSeconds: Math.Clamp((int)ParseDouble(step.Parameters.GetValueOrDefault("timeoutSec"), 3600), 1, 3600));

            if (!await _prompt.JogPopupAsync(request, ct).ConfigureAwait(false))
            {
                const string cancelled = "manual adjust cancelled by operator";
                _onStep(new WorkflowStepEvent(index, total, desc, Completed: true, Success: false, Message: cancelled, Lane: lane));
                _log(LogLevel.Warning, $"Step {index + 1} '{desc}' — {cancelled}.");
                return Result.Fail(ErrorCode.OperationFailed, $"Step {index + 1} '{desc}': {cancelled}");
            }
        }

        // First run (nothing saved) always records the point; later runs only when asked to.
        string outcome = arrived ? "arrived" : "adjusted";
        if (saved == null || (updateAfterJog && showJog))
        {
            var cur = await _engine.Motion.GetPositionAsync(device).ConfigureAwait(false);
            if (!cur.IsSuccess) return Fail(cur.Error, $"read position to save '{name}': {cur.Message}");
            var write = _savePosition(device, name, cur.Value, saved?.Velocity ?? 0.0);
            if (!write.IsSuccess) return Fail(write.Error, $"save '{name}': {write.Message}");
            var p = cur.Value;
            _log(LogLevel.Info, $"Step {index + 1}: saved '{name}' on {device} = ({p.X:F4}, {p.Y:F4}, {p.Z:F4}).");
            outcome = saved == null ? "taught" : "updated";
        }

        _onStep(new WorkflowStepEvent(index, total, desc, Completed: true, Success: true, Message: outcome, Lane: lane));
        return null;
    }

    /// <summary>
    /// The saved-position half of <c>motion.slowApproach</c>: asks the operator whether to
    /// crawl to <paramref name="saved"/>, and if so runs <see cref="SlowApproach"/>. Returns the
    /// note for the manual-jog popup and whether the device arrived cleanly. Never fails the
    /// step — a declined / refused / faulted / STOPPED approach just hands over to manual jog.
    /// </summary>
    private async Task<(string Note, bool Arrived)> OfferApproachAsync(
        WorkflowStep step, int index, string desc, string device, string name, NamedPosition saved,
        string axes, double speed, double maxDistance, CancellationToken ct)
    {
        var prompt = _prompt!;   // the caller has already checked for a prompt handler
        var go = await prompt.ConfirmAsync(
            step.Parameters.GetValueOrDefault("title", "Auto approach"),
            $"Move {device} automatically to the last saved '{name}' position?\n\n" +
            $"One axis at a time ({string.Join(" → ", "ZYX".Where(c => axes.Contains(c)))}) at {speed:0.###} mm/s. " +
            "A STOP window is shown during the move.\n\n" +
            "No = adjust manually.", ct).ConfigureAwait(false);
        if (!go)
        {
            _log(LogLevel.Info, $"Step {index + 1}: auto approach declined — manual adjust.");
            return ($"Adjust {device} manually to the final position.", false);
        }

        // Only the chosen axes go to the saved coordinates; the rest hold where they are.
        var cur = await _engine.Motion.GetPositionAsync(device).ConfigureAwait(false);
        if (!cur.IsSuccess)
            return ($"Automatic approach did not run: read position — {cur.Message}\nAdjust manually, or Cancel to stop the run.", false);
        var c = cur.Value;
        var s = saved.Coordinates;
        var target = c with
        {
            X = axes.Contains('X') ? s.X : c.X,
            Y = axes.Contains('Y') ? s.Y : c.Y,
            Z = axes.Contains('Z') ? s.Z : c.Z,
        };

        var stopTitle = $"Approaching '{name}'";
        var stopMessage = $"{device} is moving slowly to '{name}' at {speed:0.###} mm/s.\nPress STOP if anything looks wrong.";
        var r = await SlowApproach.RunAsync(_engine.Motion, device, target, axes, speed, maxDistance,
            t => prompt.MotionStopAsync(stopTitle, stopMessage, t), _log, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        if (!r.IsSuccess)
        {
            _log(LogLevel.Warning, $"Step {index + 1} '{desc}': approach failed — {r.Message}");
            return ($"Automatic approach did not complete: {r.Message}\nAdjust manually, or Cancel to stop the run.", false);
        }
        if (r.Value)
            return ("Approach STOPPED. Jog clear / adjust manually, or Cancel to stop the run.", false);

        _log(LogLevel.Info, $"Step {index + 1}: reached '{name}' on {device}.");
        return ($"Arrived at '{name}'. Fine-adjust if needed, then Continue.", true);
    }

    private static double ParseDouble(string? raw, double fallback)
        => double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static int ParseMs(string? raw)
        => double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0
            ? (int)v : 0;

    private static int ParseCount(string? raw)
        => double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0
            ? (int)v : 1;

    /// <summary>True for any If-opening op — the text <c>flow.if</c> and the guided variants.</summary>
    private static bool IsIfOpener(string op)
        => op is "flow.if" or "flow.ifsignal" or "flow.ifposition";

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
                case "flow.if":
                case "flow.ifsignal":
                case "flow.ifposition": depth++; break;
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
                case "flow.if":
                case "flow.ifsignal":
                case "flow.ifposition": depth++; break;
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
