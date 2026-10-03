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

    private readonly IHorizonEngine _engine;
    private readonly NamedPositionMover? _mover;
    private readonly SignalReader? _signalReader;
    private readonly VisionProfileRunner? _visionRunner;
    private readonly IImageDisplaySink? _imageDisplay;
    private readonly PartAlignmentScriptHost? _partAlignment;
    private readonly IOperatorPrompt? _prompt;
    private readonly Action<ScriptProgress>? _progress;

    public MoonScriptEngine(
        IHorizonEngine engine,
        NamedPositionMover? namedPositionMover = null,
        SignalReader? signalReader = null,
        VisionProfileRunner? visionRunner = null,
        IOperatorPrompt? operatorPrompt = null,
        IImageDisplaySink? imageDisplay = null,
        PartAlignmentScriptHost? partAlignment = null,
        Action<ScriptProgress>? progress = null)
    {
        _partAlignment = partAlignment;
        _progress = progress;
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
    /// </summary>
    public Task<ScriptRunResult> RunAsync(
        string source, Action<string>? onOutput = null, CancellationToken ct = default)
        => Task.Run(() => RunCore(source ?? string.Empty, onOutput, ct), ct);

    private ScriptRunResult RunCore(string source, Action<string>? onOutput, CancellationToken ct)
    {
        var output = new List<string>();
        void Log(string line)
        {
            output.Add(line);
            onOutput?.Invoke(line);
        }

        var script = new Script(CoreModules.Preset_HardSandbox);
        script.Options.DebugPrint = Log;     // Lua print(...) → output
        new MachineApi(script, _engine, _mover, _signalReader, _visionRunner, _imageDisplay, _prompt, Log, ct, _partAlignment, _progress).Install();

        DynValue function;
        try
        {
            function = script.LoadString(source, null, "script");
        }
        catch (SyntaxErrorException ex)
        {
            return ScriptRunResult.Failed(ex.DecoratedMessage ?? ex.Message, null, output);
        }

        try
        {
            var co = script.CreateCoroutine(function).Coroutine;
            co.AutoYieldCounter = AutoYieldInstructions;

            _ = co.Resume();
            for (long pump = 0; co.State == CoroutineState.Suspended && pump < MaxPumps; pump++)
            {
                if (ct.IsCancellationRequested) break;   // cooperative stop — no exception thrown
                _ = co.Resume();
            }

            if (ct.IsCancellationRequested)
                return ScriptRunResult.CancelledRun(output);

            if (co.State == CoroutineState.Suspended)
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
            // A facet call that observed cancellation surfaces here; classify it as a cancel.
            if (ct.IsCancellationRequested) return ScriptRunResult.CancelledRun(output);
            return ScriptRunResult.Failed(ex.DecoratedMessage ?? ex.Message, null, output);
        }
        catch (SyntaxErrorException ex)
        {
            return ScriptRunResult.Failed(ex.DecoratedMessage ?? ex.Message, null, output);
        }
    }
}
