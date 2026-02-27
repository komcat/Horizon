using Horizon.Process.Models;

namespace Horizon.Process;

public sealed class ProcessBuilder
{
    private readonly Stack<BlockContext> _blockStack = new();
    private List<ProcessStep> _currentSteps = new();

    private sealed class BlockContext
    {
        public List<ProcessStep> Steps { get; init; } = new();
        public object? Owner { get; set; }
        public bool IsElseBranch { get; init; }
    }

    public ProcessBuilder AddAction(string device, string method, Dictionary<string, string>? parms = null)
    {
        _currentSteps.Add(new ActionStep
        {
            DeviceName = device,
            MethodName = method,
            Params = parms ?? new()
        });
        return this;
    }

    public ProcessBuilder AddStepMarker(int index, string name, string type)
    {
        _currentSteps.Add(new StepMarkerStep
        {
            StepIndex = index,
            StepName = name,
            StepTypeName = type
        });
        return this;
    }

    public ProcessBuilder BeginLoop(int count)
    {
        _blockStack.Push(new BlockContext { Steps = _currentSteps });
        _currentSteps = new List<ProcessStep>();
        _blockStack.Peek().Owner = new LoopStep { Count = count };
        return this;
    }

    public ProcessBuilder EndLoop()
    {
        if (_blockStack.Count == 0)
            throw new InvalidOperationException("No matching BeginLoop.");

        var block = _blockStack.Pop();
        if (block.Owner is not LoopStep loop)
            throw new InvalidOperationException("EndLoop without matching BeginLoop.");

        var completedLoop = new LoopStep
        {
            Count = loop.Count,
            Steps = _currentSteps
        };

        _currentSteps = block.Steps;
        _currentSteps.Add(completedLoop);
        return this;
    }

    public ProcessBuilder BeginIf(string contextKey, ComparisonType comparison, float threshold = 0)
    {
        _blockStack.Push(new BlockContext { Steps = _currentSteps });
        _currentSteps = new List<ProcessStep>();
        _blockStack.Peek().Owner = new ConditionalStep
        {
            ContextKey = contextKey,
            Comparison = comparison,
            Threshold = threshold
        };
        return this;
    }

    public ProcessBuilder Else()
    {
        if (_blockStack.Count == 0 || _blockStack.Peek().Owner is not ConditionalStep)
            throw new InvalidOperationException("Else without matching BeginIf.");

        var thenSteps = _currentSteps;
        _currentSteps = new List<ProcessStep>();

        // Store the then-steps temporarily on the block context
        var block = _blockStack.Peek();
        var cond = (ConditionalStep)block.Owner!;
        block.Owner = new ConditionalStep
        {
            ContextKey = cond.ContextKey,
            Comparison = cond.Comparison,
            Threshold = cond.Threshold,
            ThenSteps = thenSteps
        };

        return this;
    }

    public ProcessBuilder EndIf()
    {
        if (_blockStack.Count == 0)
            throw new InvalidOperationException("No matching BeginIf.");

        var block = _blockStack.Pop();
        if (block.Owner is not ConditionalStep cond)
            throw new InvalidOperationException("EndIf without matching BeginIf.");

        ConditionalStep completedCond;
        if (cond.ThenSteps.Count > 0)
        {
            // Had an Else branch
            completedCond = new ConditionalStep
            {
                ContextKey = cond.ContextKey,
                Comparison = cond.Comparison,
                Threshold = cond.Threshold,
                ThenSteps = cond.ThenSteps,
                ElseSteps = _currentSteps
            };
        }
        else
        {
            completedCond = new ConditionalStep
            {
                ContextKey = cond.ContextKey,
                Comparison = cond.Comparison,
                Threshold = cond.Threshold,
                ThenSteps = _currentSteps,
                ElseSteps = new()
            };
        }

        _currentSteps = block.Steps;
        _currentSteps.Add(completedCond);
        return this;
    }

    public List<ProcessStep> Build()
    {
        if (_blockStack.Count > 0)
            throw new InvalidOperationException($"Unclosed block(s): {_blockStack.Count} remaining.");
        return _currentSteps;
    }
}
