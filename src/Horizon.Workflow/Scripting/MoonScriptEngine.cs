using Horizon.Common;
using Horizon.Engine;
using MoonSharp.Interpreter;

namespace Horizon.Workflow.Scripting;

/// <summary>
/// Runs <c>.moon</c> procedural scripts (Lua, via MoonSharp) against the hardware
/// engine. The script reaches the machine through the global <c>machine</c> table
/// (<see cref="MachineApi"/>); the language itself (variables, loops, if/else,
/// functions, math) comes from Lua.
///
/// <para><b>Threading.</b> The script runs on a background worker thread, so the
/// <c>machine.*</c> functions may block on async facet calls without ever
/// stalling a UI thread. A Layer 3 head calls <see cref="RunAsync"/> and marshals
/// <paramref name="onOutput"/> lines back to the UI itself.</para>
///
/// <para><b>Cancellation.</b> Hardware calls honour the run token directly. To keep
/// a pure-Lua loop (<c>while true do end</c>) cancellable too, the script is pumped
/// as a coroutine with an auto-yield counter: control returns to the host every
/// ~20k instructions, where the token is checked. The pump is bounded so a runaway
/// script cannot pin the host thread forever.</para>
///
/// <para><b>Sandbox.</b> <see cref="CoreModules.Preset_HardSandbox"/> removes
/// <c>io</c>/<c>os</c>/file access and <c>require</c>; a script can touch hardware
/// only through <c>machine</c>.</para>
/// </summary>
public sealed class MoonScriptEngine
{
    // ~20k Lua instructions between cancellation checks. Hardware calls block
    // inside a single resume, so they don't consume the budget — this only bounds
    // pure-Lua compute between facet calls.
    private const int AutoYieldInstructions = 20_000;

    // Safety backstop: max coroutine resumes for one run. At the yield rate above
    // this is ~1e12 Lua instructions — unreachable by any legitimate script, but it
    // keeps the host pump provably terminating (Power-of-10: no unbounded loops).
    private const long MaxPumps = 50_000_000L;

    // Budget for evaluating the argument text (~20M Lua instructions) — a table literal needs
    // a handful; this only bounds a pathological expression.
    private const long MaxArgPumps = 1_000L;

    // Upper bound on a machine.onAbort handler (it typically makes one return move).
    private static readonly TimeSpan AbortHandlerTimeout = TimeSpan.FromSeconds(60);

    private readonly IHorizonEngine _engine;
    private readonly NamedPositionMover? _mover;
    private readonly SignalReader? _signalReader;
    private readonly VisionProfileRunner? _visionRunner;
    private readonly IImageDisplaySink? _imageDisplay;
    private readonly PartAlignmentScriptHost? _partAlignment;
    private readonly IOperatorPrompt? _prompt;
    private readonly Action<ScriptProgress>? _progress;
    private readonly Func<SiphogSnapshotRequest, CancellationToken, Task<Result<SiphogSnapshotSet>>>? _siphogSnapshot;
    private readonly Func<double, CancellationToken, Task<Result>>? _siphogSetCurrent;

    public MoonScriptEngine(
        IHorizonEngine engine,
        NamedPositionMover? namedPositionMover = null,
        SignalReader? signalReader = null,
        VisionProfileRunner? visionRunner = null,
        IOperatorPrompt? operatorPrompt = null,
        IImageDisplaySink? imageDisplay = null,
        PartAlignmentScriptHost? partAlignment = null,
        Action<ScriptProgress>? progress = null,
        Func<SiphogSnapshotRequest, CancellationToken, Task<Result<SiphogSnapshotSet>>>? siphogSnapshot = null,
        Func<double, CancellationToken, Task<Result>>? siphogSetCurrent = null)
    {
        _partAlignment = partAlignment;
        _progress = progress;
        _siphogSnapshot = siphogSnapshot;
        _siphogSetCurrent = siphogSetCurrent;
        _engine = engine;
        _mover = namedPositionMover;
        _signalReader = signalReader;
        _visionRunner = visionRunner;
        _prompt = operatorPrompt;
        _imageDisplay = imageDisplay;
    }

    /// <summary>
    /// Parses and runs <paramref name="source"/>. Never throws for script-level
    /// problems — a syntax error, a runtime error, a failed hardware call, or a
    /// cancellation all come back as a <see cref="ScriptRunResult"/>.
    /// <paramref name="onOutput"/> receives each emitted line live (off the UI
    /// thread); the full list is also on the result.
    /// <paramref name="args"/> is optional script-argument text — a Lua table literal such as
    /// <c>{ preset = "fine", zBudgetUm = 30 }</c> (the outer braces may be omitted). It is
    /// evaluated before <c>machine</c> is installed, so it is data only and cannot reach the
    /// hardware, and it reaches the script as the global <c>args</c> (an empty table when none).
    /// </summary>
    public Task<ScriptRunResult> RunAsync(
        string source, Action<string>? onOutput = null, CancellationToken ct = default, string? args = null)
        => Task.Run(() => RunCore(source ?? string.Empty, args, onOutput, ct), ct);

    /// <summary>
    /// Syntax-checks script-argument text without running it — for editors that store
    /// arguments (presets, the Run Script block). Returns null when the text is valid
    /// (blank counts as valid: no arguments), otherwise the parser's message.
    /// </summary>
    public static string? ValidateArgs(string? args)
    {
        if (string.IsNullOrWhiteSpace(args)) return null;
        try
        {
            new Script(CoreModules.Preset_HardSandbox).LoadString(ArgsChunk(args), null, "args");
            return null;
        }
        catch (SyntaxErrorException ex)
        {
            return ex.DecoratedMessage ?? ex.Message;
        }
    }

    // "return <table>" — braces added when the author left them off; newlines keep a trailing
    // '--' comment in the text from swallowing the closing brace.
    private static string ArgsChunk(string args)
    {
        var text = args.Trim();
        return text.StartsWith('{') ? $"return {text}\n" : $"return {{\n{text}\n}}\n";
    }

    /// <summary>
    /// Evaluates the argument text to a table, under the same auto-yield pump as the script
    /// (a runaway expression is bounded and cancellable). Returns the error text, or null.
    /// </summary>
    private static string? EvaluateArgs(Script script, string? args, CancellationToken ct, out Table table)
    {
        table = new Table(script);
        if (string.IsNullOrWhiteSpace(args)) return null;
        try
        {
            var co = script.CreateCoroutine(script.LoadString(ArgsChunk(args), null, "args")).Coroutine;
            co.AutoYieldCounter = AutoYieldInstructions;
            var value = co.Resume();
            for (long pump = 0; IsSuspended(co) && pump < MaxArgPumps; pump++)
            {
                if (ct.IsCancellationRequested) return "cancelled";
                value = co.Resume();
            }
            if (IsSuspended(co)) return "script arguments took too long to evaluate";
            if (value.Type != DataType.Table)
                return "script arguments must be a Lua table, e.g. { preset = \"fine\" }";
            table = value.Table;
            return null;
        }
        catch (InterpreterException ex)
        {
            return $"script arguments: {ex.DecoratedMessage ?? ex.Message}";
        }
    }

    private ScriptRunResult RunCore(string source, string? args, Action<string>? onOutput, CancellationToken ct)
    {
        var output = new List<string>();
        void Log(string line)
        {
            output.Add(line);
            onOutput?.Invoke(line);
        }

        var script = new Script(CoreModules.Preset_HardSandbox);
        script.Options.DebugPrint = Log;     // Lua print(...) → output

        // Arguments first — before 'machine' exists, so the text can only build data.
        if (!string.IsNullOrWhiteSpace(args))
            Log("Script arguments: " + string.Join(" ", args.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
        var argsError = EvaluateArgs(script, args, ct, out var argsTable);
        if (ct.IsCancellationRequested) return ScriptRunResult.CancelledRun(output);
        if (argsError != null) return ScriptRunResult.Failed(argsError, null, output);
        script.Globals["args"] = DynValue.NewTable(argsTable);

        var api = new MachineApi(script, _engine, _mover, _signalReader, _visionRunner, _imageDisplay, _prompt, Log, ct, _partAlignment, _progress, _siphogSnapshot, _siphogSetCurrent);
        api.Install();

        DynValue function;
        try
        {
            function = script.LoadString(source, null, "script");
        }
        catch (SyntaxErrorException ex)
        {
            return ScriptRunResult.Failed(ex.DecoratedMessage ?? ex.Message, null, output);
        }

        var result = RunMain(script, function, output, ct);

        // Stopped or failed: give the script's machine.onAbort handler its chance to put the
        // hardware somewhere safe (e.g. the FAU back on its best point). The run still reports
        // as stopped / failed.
        if (!result.Success && api.AbortHandler is { } handler)
            RunAbortHandler(script, api, handler, result.Cancelled ? "stopped" : "error", Log);
        return result;
    }

    /// <summary>
    /// Runs the script's <c>machine.onAbort</c> handler once, after a STOP or an error. The run's
    /// own token is already cancelled (or the run is over), so the machine API is re-bound to a
    /// fresh token bounded by <see cref="AbortHandlerTimeout"/> — the handler may move hardware,
    /// but cannot run forever; on the timeout its move in progress is stopped. A failure inside
    /// the handler is logged, never thrown.
    /// </summary>
    private static void RunAbortHandler(Script script, MachineApi api, DynValue handler, string reason, Action<string> log)
    {
        using var cts = new CancellationTokenSource(AbortHandlerTimeout);
        api.UseToken(cts.Token);
        log($"Running the script's abort handler ({reason})…");
        try
        {
            var co = script.CreateCoroutine(handler).Coroutine;
            co.AutoYieldCounter = AutoYieldInstructions;
            _ = co.Resume(DynValue.NewString(reason));
            for (long pump = 0; IsSuspended(co) && pump < MaxPumps; pump++)
            {
                if (cts.IsCancellationRequested) break;
                _ = co.Resume();
            }
            if (cts.IsCancellationRequested) log($"Abort handler timed out after {AbortHandlerTimeout.TotalSeconds:0} s.");
            else if (IsSuspended(co)) log("Abort handler exceeded its execution budget.");
            else log("Abort handler finished.");
        }
        catch (InterpreterException ex)
        {
            log($"Abort handler failed: {ex.DecoratedMessage ?? ex.Message}");
        }
        catch (OperationCanceledException)
        {
            log($"Abort handler timed out after {AbortHandlerTimeout.TotalSeconds:0} s.");
        }
    }

    private static ScriptRunResult RunMain(Script script, DynValue function, List<string> output, CancellationToken ct)
    {
        try
        {
            var co = script.CreateCoroutine(function).Coroutine;
            co.AutoYieldCounter = AutoYieldInstructions;

            // An auto-yield leaves the coroutine ForceSuspended (a STOP yield request leaves it
            // Suspended) — both must be resumed, or a script ends silently after ~20k instructions.
            _ = co.Resume();
            for (long pump = 0; IsSuspended(co) && pump < MaxPumps; pump++)
            {
                if (ct.IsCancellationRequested) break;   // cooperative stop — no exception thrown
                _ = co.Resume();
            }

            if (ct.IsCancellationRequested)
                return ScriptRunResult.CancelledRun(output);

            if (IsSuspended(co))
                return ScriptRunResult.Failed(
                    "script exceeded its execution budget (possible runaway loop)", null, output);

            return ScriptRunResult.Ok(output);
        }
        catch (OperationCanceledException)
        {
            return ScriptRunResult.CancelledRun(output);
        }
        catch (ScriptRuntimeException ex)
        {
            // A STOP normally arrives as a yield request from MachineApi.Cb (no exception). It can
            // still surface here if a callback was reached where MoonSharp can't yield (e.g. from a
            // metamethod) — classify any error raised after a cancel as the cancel.
            if (ct.IsCancellationRequested) return ScriptRunResult.CancelledRun(output);
            return ScriptRunResult.Failed(ex.DecoratedMessage ?? ex.Message, null, output);
        }
        catch (SyntaxErrorException ex)
        {
            return ScriptRunResult.Failed(ex.DecoratedMessage ?? ex.Message, null, output);
        }
    }

    private static bool IsSuspended(Coroutine co)
        => co.State is CoroutineState.Suspended or CoroutineState.ForceSuspended;
}
