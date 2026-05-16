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
    private readonly Action<WorkflowStepEvent> _onStep;
    private readonly Action<LogLevel, string> _log;

    public WorkflowRunner(
        IHorizonEngine engine,
        Func<string, string, CancellationToken, Task<Result>> moveToNamed,
        Action<WorkflowStepEvent> onStep,
        Action<LogLevel, string> log)
    {
        _dispatcher = new StepDispatcher(engine, moveToNamed);
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
