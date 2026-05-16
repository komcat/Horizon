using System.Globalization;
using System.Text;
using Horizon.Common;
using Horizon.Engine;

namespace Horizon.Workflow.Recipe;

/// <summary>Dispatches a recipe method call to its hardware / system implementation.</summary>
internal interface IRecipeMethodInvoker
{
    Task<Result> InvokeAsync(
        CallStatement call,
        IReadOnlyDictionary<string, RecipeValue> args,
        RecipeContext ctx,
        CancellationToken ct);
}

/// <summary>A recipe error raised during execution, carrying the 1-based source line.</summary>
internal sealed class RecipeRuntimeException : Exception
{
    public int Line { get; }
    public RecipeRuntimeException(int line, string message) : base(message) => Line = line;
}

/// <summary>
/// Tree-walking interpreter for a parsed <see cref="RecipeProgram"/>. Reuses the
/// Phase 4 <see cref="PauseController"/> for pause / step, and dispatches method
/// calls through an <see cref="IRecipeMethodInvoker"/>.
/// </summary>
internal sealed class RecipeInterpreter
{
    private readonly IRecipeMethodInvoker _methods;
    private readonly PauseController _pause;
    private readonly Action<LogLevel, string> _log;
    private readonly RecipeContext _ctx = new();
    private readonly Stack<int> _loops = new();   // current iteration per nesting level

    public RecipeInterpreter(
        IRecipeMethodInvoker methods,
        PauseController pause,
        Action<LogLevel, string> log)
    {
        _methods = methods;
        _pause = pause;
        _log = log;
    }

    public async Task<Result> RunAsync(RecipeProgram program, CancellationToken ct)
    {
        try
        {
            await RunBlockAsync(program.Statements, ct).ConfigureAwait(false);
            return Result.Ok();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RecipeRuntimeException ex)
        {
            var message = $"line {ex.Line}: {ex.Message}";
            _log(LogLevel.Error, message);
            return Result.Fail(ErrorCode.OperationFailed, message);
        }
    }

    private async Task RunBlockAsync(IReadOnlyList<RecipeStatement> statements, CancellationToken ct)
    {
        foreach (var stmt in statements)
        {
            ct.ThrowIfCancellationRequested();
            // Pause-and-yield before each statement — same gate as a workflow step.
            await _pause.WaitAsync(breakpoint: false, ct).ConfigureAwait(false);
            await ExecuteAsync(stmt, ct).ConfigureAwait(false);
        }
    }

    private async Task ExecuteAsync(RecipeStatement stmt, CancellationToken ct)
    {
        switch (stmt)
        {
            case SetStatement set:
                _ctx.Set(set.Name, Eval(set.Value));
                break;

            case IfStatement iff:
                await RunBlockAsync(Eval(iff.Condition).IsTruthy ? iff.Then : iff.Else, ct)
                    .ConfigureAwait(false);
                break;

            case LoopStatement loop:
                await RunLoopAsync(loop, ct).ConfigureAwait(false);
                break;

            case CallStatement call:
                await RunCallAsync(call, ct).ConfigureAwait(false);
                break;
        }
    }

    private async Task RunLoopAsync(LoopStatement loop, CancellationToken ct)
    {
        // A null count is an infinite loop — bounded only by cancellation.
        long count = loop.Count == null ? long.MaxValue : (long)Eval(loop.Count).AsNumber();

        _loops.Push(0);
        try
        {
            for (long i = 0; i < count; i++)
            {
                _loops.Pop();
                _loops.Push((int)i);
                await RunBlockAsync(loop.Body, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _loops.Pop();
        }
    }

    private async Task RunCallAsync(CallStatement call, CancellationToken ct)
    {
        var args = new Dictionary<string, RecipeValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var arg in call.Args)
            args[arg.Name] = Eval(arg.Value);

        Result result;
        try
        {
            result = await _methods.InvokeAsync(call, args, _ctx, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new RecipeRuntimeException(call.Line, ex.Message);
        }

        if (!result.IsSuccess)
            throw new RecipeRuntimeException(call.Line, result.Message);
    }

    // --- expression evaluation ---

    private RecipeValue Eval(RecipeExpr expr) => expr switch
    {
        NumberLiteral n => RecipeValue.Number(n.Value),
        StringLiteral s => RecipeValue.Text(Interpolate(s.Value)),
        BoolLiteral b   => RecipeValue.Bool(b.Value),
        VariableRef v   => ResolveVariable(v.Name, v.Line),
        UnaryExpr u     => RecipeValue.Number(-Eval(u.Operand).AsNumber()),
        BinaryExpr b    => EvalBinary(b),
        _ => RecipeValue.Zero,
    };

    private RecipeValue EvalBinary(BinaryExpr b)
    {
        double l = Eval(b.Left).AsNumber();
        double r = Eval(b.Right).AsNumber();
        return b.Op switch
        {
            BinaryOp.Add => RecipeValue.Number(l + r),
            BinaryOp.Sub => RecipeValue.Number(l - r),
            BinaryOp.Mul => RecipeValue.Number(l * r),
            BinaryOp.Div => RecipeValue.Number(l / r),   // div-by-zero → IEEE Infinity
            BinaryOp.Eq  => RecipeValue.Bool(l == r),
            BinaryOp.Ne  => RecipeValue.Bool(l != r),
            BinaryOp.Gt  => RecipeValue.Bool(l > r),
            BinaryOp.Lt  => RecipeValue.Bool(l < r),
            BinaryOp.Ge  => RecipeValue.Bool(l >= r),
            BinaryOp.Le  => RecipeValue.Bool(l <= r),
            _ => RecipeValue.Zero,
        };
    }

    private RecipeValue ResolveVariable(string name, int line)
    {
        if (IsLoopIteration(name))
            return RecipeValue.Number(CurrentLoopIteration());
        if (_ctx.TryGet(name, out var value))
            return value;
        throw new RecipeRuntimeException(line, $"undefined variable '{name}'");
    }

    private static bool IsLoopIteration(string name)
        => string.Equals(name, "loop.iteration", StringComparison.OrdinalIgnoreCase);

    private int CurrentLoopIteration() => _loops.Count > 0 ? _loops.Peek() : 0;

    // --- string interpolation: {name} and {=expr} ---

    private string Interpolate(string raw)
    {
        if (raw.IndexOf('{') < 0) return raw;

        var sb = new StringBuilder(raw.Length);
        int i = 0;
        while (i < raw.Length)
        {
            char c = raw[i];
            if (c != '{') { sb.Append(c); i++; continue; }

            int close = raw.IndexOf('}', i + 1);
            if (close < 0) { sb.Append(raw, i, raw.Length - i); break; }

            sb.Append(EvalPlaceholder(raw.Substring(i + 1, close - i - 1).Trim()));
            i = close + 1;
        }
        return sb.ToString();
    }

    private string EvalPlaceholder(string inner)
    {
        if (inner.StartsWith("=", StringComparison.Ordinal))
        {
            var expr = RecipeParser.ParseExpressionSnippet(inner[1..]);
            return expr == null ? "{=?}" : Eval(expr).Display();
        }
        if (IsLoopIteration(inner))
            return CurrentLoopIteration().ToString(CultureInfo.InvariantCulture);
        // An unknown variable stays literal rather than failing the run.
        return _ctx.TryGet(inner, out var value) ? value.Display() : "{" + inner + "}";
    }
}
