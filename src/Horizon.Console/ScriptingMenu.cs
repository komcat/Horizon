using Horizon.Workflow;
using Horizon.Workflow.Scripting;
using Spectre.Console;

namespace Horizon.ConsoleHost;

/// <summary>
/// Console front-end for the <c>.moon</c> procedural scripting layer — Phase 1
/// test rig. Runs a script (built-in sample or a file) against the live engine
/// and streams its output. Ctrl+C cancels a running script.
/// </summary>
internal sealed partial class ConsoleApp
{
    private const string RunSample = "Run built-in sample";
    private const string RunFile = "Run a .moon file";
    private const string RunXcan = "Run XCAN (pick a hexapod)";

    /// <summary>DLAM's XCAN script (tools/dlam/scripts/XCAN.moon), copied next to the exe at build.</summary>
    private static readonly string XcanScriptPath = Path.Combine(AppContext.BaseDirectory, "scripts", "XCAN.moon");

    private const string SampleScript = """
        -- Sample: pure Lua + the machine API. Safe with no hardware attached.

        -- Enumerate every facet's configured devices (no I/O, just config).
        local facets = {
          machine.motion, machine.io, machine.pneumatic, machine.smu, machine.psu,
          machine.laser, machine.tec, machine.lighting, machine.measurement, machine.camera
        }
        local names = {
          "motion", "io", "pneumatic", "smu", "psu",
          "laser", "tec", "lighting", "measurement", "camera"
        }
        for i, facet in ipairs(facets) do
          local devices = facet.list()
          print(names[i] .. ": " .. #devices .. " device(s)")
          for _, d in ipairs(devices) do print("  - " .. d) end
        end

        print("vision (HALCON) available: " .. tostring(machine.vision.available()))

        -- Real language: variables, loop, math.
        local total = 0
        for i = 1, 5 do total = total + i end
        machine.log("sum 1..5 = " .. total)

        machine.log("waiting 200 ms...")
        machine.wait(200)
        print("done")
        """;

    private async Task ScriptingMenuAsync()
    {
        var engine = new MoonScriptEngine(_engine, namedPositionMover: null, operatorPrompt: new ConsoleOperatorPrompt());

        while (true)
        {
            var action = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("\n[aqua]Scripting (.moon)[/] — choose:")
                    .AddChoices(RunSample, RunFile, RunXcan, Back));

            switch (action)
            {
                case RunSample:
                    await RunScriptAsync(engine, SampleScript);
                    break;

                case RunFile:
                    var path = AnsiConsole.Prompt(
                        new TextPrompt<string>("Path to a [aqua].moon[/] file:").AllowEmpty());
                    if (string.IsNullOrWhiteSpace(path)) break;
                    if (!File.Exists(path))
                    {
                        AnsiConsole.MarkupLineInterpolated($"[red]No such file:[/] {path}");
                        break;
                    }
                    await RunScriptAsync(engine, await File.ReadAllTextAsync(path), PromptArgs());
                    break;

                case RunXcan:
                    await RunXcanAsync(engine);
                    break;

                case Back:
                    return;
            }
        }
    }

    /// <summary>
    /// XCAN on a hexapod picked from a menu: the PI motion devices (hex-left, hex-right), or every
    /// motion device when none is PI. Feedback defaults to Keithley_2602B ch1 (the script's own
    /// default); extra arguments (e.g. <c>exitUa = 400, trace = true</c>) are optional.
    /// </summary>
    private async Task RunXcanAsync(MoonScriptEngine engine)
    {
        if (!File.Exists(XcanScriptPath))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]XCAN script not found:[/] {XcanScriptPath}");
            return;
        }
        var hexapods = _engine.Motion.DeviceNames
            .Where(d => string.Equals(_engine.Motion.GetDevice(d)?.ControllerType, "PI", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (hexapods.Count == 0) hexapods = _engine.Motion.DeviceNames.ToList();
        if (hexapods.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No motion devices configured.[/]");
            return;
        }

        var device = AnsiConsole.Prompt(
            new SelectionPrompt<string>().Title("XCAN on which hexapod?").AddChoices(hexapods.Append(Back)));
        if (device == Back) return;
        if (!_engine.Motion.IsConnected(device))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{device} is not connected.[/]");
            return;
        }

        var extra = AnsiConsole.Prompt(
            new TextPrompt<string>("Extra arguments [grey](e.g. exitUa = 400, trace = true; blank = defaults: Keithley_2602B ch1, 10/5/2/1 um)[/]:")
                .AllowEmpty()).Trim().Trim('{', '}').Trim();
        var args = $"device = \"{device}\"" + (extra.Length > 0 ? ", " + extra : string.Empty);

        if (!AnsiConsole.Confirm($"Run XCAN on [aqua]{device}[/]? It biases the Keithley and moves the hexapod up to 200 um each way per axis."))
            return;
        await RunScriptAsync(engine, await File.ReadAllTextAsync(XcanScriptPath), args);
    }

    /// <summary>Optional script arguments for "Run a .moon file" — validated before the script runs.</summary>
    private static string? PromptArgs()
    {
        var text = AnsiConsole.Prompt(
            new TextPrompt<string>("Arguments [grey](Lua table, e.g. { device = \"hex-right\" }; blank = none)[/]:")
                .AllowEmpty()).Trim();
        return text.Length == 0 ? null : text;
    }

    private static async Task RunScriptAsync(MoonScriptEngine engine, string source, string? args = null)
    {
        var argsError = MoonScriptEngine.ValidateArgs(args);
        if (argsError != null)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Bad arguments:[/] {argsError}");
            return;
        }

        AnsiConsole.MarkupLine("[grey]Running — press Ctrl+C to cancel.[/]\n");

        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cts.Cancel(); };
        Console.CancelKeyPress += onCancel;
        try
        {
            void OnOutput(string line) => AnsiConsole.WriteLine("  " + line);
            var result = await engine.RunAsync(source, OnOutput, cts.Token, args);

            AnsiConsole.WriteLine();
            if (result.Success)
                AnsiConsole.MarkupLine("[green]Script completed.[/]");
            else if (result.Cancelled)
                AnsiConsole.MarkupLine("[yellow]Script cancelled.[/]");
            else
                AnsiConsole.MarkupLineInterpolated($"[red]Script error:[/] {result.Error}");
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }
}

/// <summary>Minimal stdin operator prompt so <c>machine.ask(...)</c> works in the console.</summary>
internal sealed class ConsoleOperatorPrompt : IOperatorPrompt
{
    public Task<bool> ConfirmAsync(string title, string message, CancellationToken ct)
        => Task.FromResult(AnsiConsole.Confirm($"{title}: {message}"));
}
