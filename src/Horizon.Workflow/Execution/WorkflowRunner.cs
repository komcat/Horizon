using System.Globalization;
using Horizon.Common;
using Horizon.Engine;

namespace Horizon.Workflow;

/// <summary>
/// Executes a <see cref="WorkflowProgram"/> against <see cref="IHorizonEngine"/>.
/// Walks steps by index so loops can jump; dispatches non-flow ops to
/// <see cref="StepDispatcher"/>; raises a step event before and after each step.
/// </summary>
internal sealed class WorkflowRunner
{
    private readonly StepDispatcher _dispatcher;
    private readonly IOperatorPrompt? _prompt;
    private readonly Action<WorkflowStepEvent> _onStep;
    private readonly Action<LogLevel, string> _log;

    public WorkflowRunner(
        IHorizonEngine engine,
        Func<string, string, CancellationToken, Task<Result>> moveToNamed,
        IOperatorPrompt? prompt,
        Action<WorkflowStepEvent> onStep,
        Action<LogLevel, string> log)
    {
        _dispatcher = new StepDispatcher(engine, moveToNamed, log);
        _prompt = prompt;
        _onStep = onStep;
        _log = log;
    }

    public async Task<Result> RunAsync(WorkflowProgram program, CancellationToken ct)
    {
        var ctx = new WorkflowContext();
        var steps = program.Steps;
        var loops = new Stack<LoopFrame>();
        int index = 0;

        while (index < steps.Count)
        {
            ct.ThrowIfCancellationRequested();
            var step = steps[index];

            if (!step.Enabled) { index++; continue; }

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

            var desc = string.IsNullOrEmpty(step.Name) ? step.Op : step.Name;

            if (op == "flow.askuser")
            {
                var abort = await AskUserAsync(step, index, steps.Count, desc, ct).ConfigureAwait(false);
                if (abort != null) return abort.Value;   // null = confirmed, continue
                index++;
                continue;
            }

            _onStep(new WorkflowStepEvent(index, steps.Count, desc, Completed: false, Success: false, Message: ""));

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
