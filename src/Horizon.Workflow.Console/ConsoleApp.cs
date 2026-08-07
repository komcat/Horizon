using Horizon.Common;
using Horizon.Engine;
using Horizon.Workflow;
using Horizon.Workflow.Migration;
using Horizon.Workflow.Scanning;
using Spectre.Console;

namespace Horizon.WorkflowConsole;

/// <summary>
/// Spectre.Console menu loop over <see cref="IProcessService"/>. Also the
/// reference <see cref="IOperatorPrompt"/> — the bidirectional front-end every
/// future Layer 3 (WPF, web) mirrors.
/// </summary>
internal sealed class ConsoleApp : IOperatorPrompt
{
    private const string ShowProducts = "Show products";
    private const string CreateProduct = "Create product";
    private const string LoadProduct = "Load product";
    private const string UnloadProduct = "Unload product";
    private const string DeleteProduct = "Delete product";
    private const string Positions = "Positions";
    private const string ProcessPrograms = "Process programs";
    private const string Recipes = "Recipes";
    private const string HexapodScan = "Hexapod scan";
    private const string MigrateBobcat = "Migrate bobcat recipes";
    private const string Exit = "Exit";
    private const string Back = "Back";

    private readonly IProcessService _service;
    private readonly IHorizonEngine _engine;

    /// <summary>Set while a blocking operator prompt is on screen, so the
    /// run loop's "press any key to cancel" poll does not steal the answer.</summary>
    private volatile bool _awaitingPrompt;

    public ConsoleApp(IProcessService service, IHorizonEngine engine)
    {
        _service = service;
        _engine = engine;
        _service.OperatorPrompt = this;   // register as the reference front-end
    }

    /// <summary>
    /// <see cref="IOperatorPrompt"/> reference implementation: a blocking
    /// console yes/no. WPF mirrors this with a modal dialog; web with a
    /// SignalR round-trip — all satisfy the same <c>Task&lt;bool&gt;</c> contract.
    /// </summary>
    public Task<bool> ConfirmAsync(string title, string message, CancellationToken ct)
    {
        _awaitingPrompt = true;
        try
        {
            var text = string.IsNullOrWhiteSpace(message)
                ? Markup.Escape(title)
                : $"{Markup.Escape(title)} — {Markup.Escape(message)}";
            AnsiConsole.WriteLine();
            return Task.FromResult(AnsiConsole.Confirm($"[yellow]?[/] {text}"));
        }
        finally
        {
            _awaitingPrompt = false;
        }
    }

    /// <summary>
    /// Renders a numbered menu and reads the choice as a number — preferred over
    /// arrow-key selection once a menu has many entries.
    /// </summary>
    private static string Choose(string title, IEnumerable<string> options)
    {
        var list = options.ToList();
        AnsiConsole.MarkupLine(title);
        for (int i = 0; i < list.Count; i++)
            AnsiConsole.MarkupLineInterpolated($"  [aqua]{i + 1,2}[/]  {Markup.Escape(list[i])}");

        var choice = AnsiConsole.Prompt(
            new TextPrompt<int>($"Select [grey]1-{list.Count}[/]:")
                .Validate(n => n >= 1 && n <= list.Count
                    ? ValidationResult.Success()
                    : ValidationResult.Error($"[red]Enter a number between 1 and {list.Count}[/]")));
        return list[choice - 1];
    }

    public async Task RunAsync()
    {
        while (true)
        {
            var active = _service.ActiveProduct ?? "(none)";
            var choice = Choose(
                $"\n[aqua]Workflow[/] — active product: [aqua]{Markup.Escape(active)}[/]",
                new[] { ShowProducts, CreateProduct, LoadProduct, UnloadProduct,
                        DeleteProduct, Positions, ProcessPrograms, Recipes,
                        HexapodScan, MigrateBobcat, Exit });

            switch (choice)
            {
                case ShowProducts:
                    ShowProductList();
                    break;

                case CreateProduct:
                    var name = AnsiConsole.Prompt(new TextPrompt<string>("Product name:"));
                    Report(_service.CreateProduct(name), $"Create product '{name}'");
                    break;

                case LoadProduct:
                    PickProduct("Select product to load:", p =>
                        Report(_service.LoadProduct(p), $"Load product '{p}'"));
                    break;

                case UnloadProduct:
                    _service.UnloadProduct();
                    break;

                case DeleteProduct:
                    PickProduct("Select product to delete:", p =>
                    {
                        if (AnsiConsole.Confirm($"Delete product '{Markup.Escape(p)}'?", defaultValue: false))
                            Report(_service.DeleteProduct(p), $"Delete product '{p}'");
                    });
                    break;

                case Positions:
                    await PositionsMenuAsync();
                    break;

                case ProcessPrograms:
                    await ProgramsMenuAsync();
                    break;

                case Recipes:
                    await RecipesMenuAsync();
                    break;

                case HexapodScan:
                    await HexapodScanMenuAsync();
                    break;

                case MigrateBobcat:
                    MigrateBobcatMenu();
                    break;

                case Exit:
                    return;
            }
        }
    }

    private void ShowProductList()
    {
        var products = _service.Products;
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Product");
        table.AddColumn("Active");

        if (products.Count == 0)
        {
            table.AddRow("[grey]no products[/]", "[grey]—[/]");
        }
        else
        {
            foreach (var p in products)
            {
                var isActive = string.Equals(p, _service.ActiveProduct, StringComparison.OrdinalIgnoreCase);
                table.AddRow(Markup.Escape(p), isActive ? "[green]yes[/]" : "[grey]—[/]");
            }
        }

        AnsiConsole.Write(table);
    }

    private void PickProduct(string title, Action<string> action)
    {
        var products = _service.Products;
        if (products.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No products.[/]");
            return;
        }

        var pick = Choose(title, products.Append(Back));

        if (pick == Back) return;
        action(pick);
    }

    // --- Bobcat migration ---

    /// <summary>
    /// Stage 1 of the bobcat → Horizon recipe migration: converts a folder of
    /// legacy bobcat recipe/program JSON into Horizon <c>.hwf</c>/<c>.hwd</c>
    /// files for review. See <c>docs/recipe-migration.md</c>.
    /// </summary>
    private void MigrateBobcatMenu()
    {
        var input = AnsiConsole.Prompt(
            new TextPrompt<string>("Bobcat recipes/programs folder:")).Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(input))
            return;
        if (!Directory.Exists(input))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Folder not found: {input}[/]");
            return;
        }

        var output = AnsiConsole.Prompt(
            new TextPrompt<string>("Output folder:")
                .DefaultValue(Path.Combine(input, "converted"))).Trim().Trim('"');

        try
        {
            var summary = new BobcatMigration().ConvertFolder(input, output);

            AnsiConsole.MarkupLineInterpolated(
                $"\n[green]Converted[/] {summary.RecipesConverted} recipe(s), {summary.ProgramsConverted} program(s); skipped {summary.Skipped}.");

            if (summary.UnsupportedSteps > 0)
            {
                var unsupported = string.Join(", ", summary.Issues
                    .Where(i => i.Severity == ConversionSeverity.Unsupported)
                    .Select(i => i.StepType)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(t => t, StringComparer.OrdinalIgnoreCase));
                AnsiConsole.MarkupLineInterpolated(
                    $"[yellow]{summary.UnsupportedSteps} step(s)[/] had no Horizon op (emitted as disabled placeholders): {unsupported}");
            }

            AnsiConsole.MarkupLineInterpolated($"[grey]Report:[/] {summary.ReportPath}");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Migration failed: {ex.Message}[/]");
        }
    }

    // --- Positions ---

    private async Task PositionsMenuAsync()
    {
        if (_service.ActiveProduct == null)
        {
            AnsiConsole.MarkupLine("[yellow]No active product — load one first.[/]");
            return;
        }

        var devices = _engine.Motion.DeviceNames;
        if (devices.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No motion devices.[/]");
            return;
        }

        while (true)
        {
            var pick = Choose("\n[aqua]Positions[/] — select a motion device:", devices.Append(Back));

            if (pick == Back) return;
            await PositionDeviceMenuAsync(pick);
        }
    }

    private async Task PositionDeviceMenuAsync(string device)
    {
        const string Teach = "Teach position (capture current)";
        const string SetManual = "Set position (manual entry)";
        const string MoveTo = "Move to position";
        const string Remove = "Remove position";
        const string Refresh = "Refresh";

        while (true)
        {
            RenderPositions(device);

            var action = Choose(
                $"\n[aqua]{Markup.Escape(device)}[/] — action:",
                new[] { Teach, SetManual, MoveTo, Remove, Refresh, Back });

            switch (action)
            {
                case Teach:
                    var name = AnsiConsole.Prompt(new TextPrompt<string>("Position name:"));
                    var teachVel = AnsiConsole.Prompt(
                        new TextPrompt<double>("Velocity (0 = no change):").DefaultValue(0d));
                    Report(await _service.TeachPositionAsync(device, name, teachVel), $"Teach {device}.{name}");
                    break;

                case SetManual:
                    SetPositionManual(device);
                    break;

                case MoveTo:
                    await MoveToNamedAsync(device);
                    break;

                case Remove:
                    RemoveNamed(device);
                    break;

                case Refresh:
                    break;

                case Back:
                    return;
            }
        }
    }

    private void RenderPositions(string device)
    {
        var names = _service.GetPositionNames(device);

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Position");
        table.AddColumn("X"); table.AddColumn("Y"); table.AddColumn("Z");
        table.AddColumn("U"); table.AddColumn("V"); table.AddColumn("W");
        table.AddColumn("Velocity");

        if (names.Count == 0)
        {
            table.AddRow("[grey]no positions[/]", "—", "—", "—", "—", "—", "—", "—");
        }
        else
        {
            foreach (var n in names)
            {
                var p = _service.GetPosition(device, n);
                if (p == null) continue;
                var c = p.Coordinates;
                table.AddRow(Markup.Escape(n),
                    $"{c.X:F3}", $"{c.Y:F3}", $"{c.Z:F3}",
                    $"{c.U:F3}", $"{c.V:F3}", $"{c.W:F3}",
                    p.Velocity > 0 ? $"{p.Velocity:F3}" : "[grey]—[/]");
            }
        }

        AnsiConsole.Write(table);
    }

    private async Task MoveToNamedAsync(string device)
    {
        var names = _service.GetPositionNames(device);
        if (names.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No positions taught.[/]");
            return;
        }

        var pick = Choose("Move to:", names.Append(Back));
        if (pick == Back) return;

        AnsiConsole.MarkupLine("[yellow]Warning: this moves the physical stage.[/]");
        Report(await _service.MoveToNamedPositionAsync(device, pick), $"Move {device} -> {pick}");
        await Task.Delay(200);
    }

    private void SetPositionManual(string device)
    {
        var info = _engine.Motion.GetDevice(device);
        IReadOnlyList<string> axes = info?.Axes ?? new List<string> { "X", "Y", "Z" };

        var name = AnsiConsole.Prompt(new TextPrompt<string>("Position name:"));

        var values = new double[6];   // X, Y, Z, U, V, W
        for (int i = 0; i < axes.Count && i < 6; i++)
            values[i] = AnsiConsole.Prompt(
                new TextPrompt<double>($"{Markup.Escape(axes[i])}:").DefaultValue(0d));

        var pos = new MotionPosition(values[0], values[1], values[2], values[3], values[4], values[5]);
        var velocity = AnsiConsole.Prompt(
            new TextPrompt<double>("Velocity (0 = no change):").DefaultValue(0d));

        Report(_service.SetPosition(device, name, pos, velocity), $"Set {device}.{name}");
    }

    private void RemoveNamed(string device)
    {
        var names = _service.GetPositionNames(device);
        if (names.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No positions taught.[/]");
            return;
        }

        var pick = Choose("Remove:", names.Append(Back));
        if (pick == Back) return;

        Report(_service.RemovePosition(device, pick), $"Remove {device}.{pick}");
    }

    // --- Process programs ---

    private async Task ProgramsMenuAsync()
    {
        if (_service.ActiveProduct == null)
        {
            AnsiConsole.MarkupLine("[yellow]No active product — load one first.[/]");
            return;
        }

        while (true)
        {
            var programs = _service.ListPrograms();
            if (programs.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No process programs in this product.[/]");
                return;
            }

            var pick = Choose("\n[aqua]Process programs[/] — select to run:", programs.Append(Back));

            if (pick == Back) return;
            await RunProgramAsync(pick);
        }
    }

    private async Task RunProgramAsync(string name)
    {
        var load = _service.LoadProgram(name);
        if (!load.IsSuccess)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Load failed[/] — {load.Message}");
            return;
        }

        var program = load.Value;

        // Optional DUT serial — blank runs the program without a test record.
        var serial = AnsiConsole.Prompt(
            new TextPrompt<string>("DUT serial [grey](blank = no record)[/]:")
                .AllowEmpty()).Trim();

        AnsiConsole.MarkupLineInterpolated(
            $"\n[aqua]Running[/] '{program.Name}' — {program.Steps.Count} step(s).");
        if (serial.Length > 0)
            AnsiConsole.MarkupLineInterpolated($"[grey]DUT serial:[/] {serial}");
        AnsiConsole.MarkupLine("[grey](keys: [[p]]ause  [[s]]tep  [[r]]esume  [[c]]ancel)[/]");

        void OnStep(WorkflowStepEvent e)
        {
            if (!e.Completed) return;   // print on completion only
            var tag = e.Success ? "[green]OK[/]" : "[red]FAIL[/]";
            var msg = e.Success || string.IsNullOrEmpty(e.Message)
                ? string.Empty
                : $" — {Markup.Escape(e.Message)}";
            AnsiConsole.MarkupLine($"  [[{e.Index + 1}/{e.Total}]] {Markup.Escape(e.Description)}  {tag}{msg}");
        }

        void OnPaused(bool paused)
        {
            if (paused)
                AnsiConsole.MarkupLine("  [yellow]|| PAUSED[/] — [grey][[r]]esume  [[s]]tep  [[c]]ancel[/]");
        }

        using var cts = new CancellationTokenSource();
        _service.StepEvent += OnStep;
        _service.PausedChanged += OnPaused;
        try
        {
            var runTask = _service.RunAsync(
                program, serial.Length > 0 ? serial : null, cts.Token);
            while (!runTask.IsCompleted)
            {
                // Skip key polling while a prompt is open — that keystroke is
                // the operator's answer, not a run control.
                if (!_awaitingPrompt && Console.KeyAvailable)
                {
                    switch (Console.ReadKey(intercept: true).Key)
                    {
                        case ConsoleKey.P: _service.Pause();  break;
                        case ConsoleKey.R: _service.Resume(); break;
                        case ConsoleKey.S: _service.Step();   break;
                        case ConsoleKey.C:
                        case ConsoleKey.Escape: cts.Cancel(); break;
                    }
                }
                await Task.Delay(50);
            }
            Report(await runTask, $"Run '{program.Name}'");
        }
        finally
        {
            _service.StepEvent -= OnStep;
            _service.PausedChanged -= OnPaused;
        }
    }

    // --- Recipes ---

    private async Task RecipesMenuAsync()
    {
        if (_service.ActiveProduct == null)
        {
            AnsiConsole.MarkupLine("[yellow]No active product — load one first.[/]");
            return;
        }

        while (true)
        {
            var recipes = _service.ListRecipes();
            if (recipes.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No recipes (.hrec) in this product.[/]");
                return;
            }

            var pick = Choose("\n[aqua]Recipes[/] — select to run:", recipes.Append(Back));

            if (pick == Back) return;
            await RunRecipeByNameAsync(pick);
        }
    }

    private async Task RunRecipeByNameAsync(string name)
    {
        // Parse first — syntax errors are shown without running anything.
        var load = _service.LoadRecipe(name);
        if (!load.IsSuccess)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Load failed[/] — {load.Message}");
            return;
        }

        var program = load.Value;
        if (program.HasErrors)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{program.Errors.Count} parse error(s) in '{name}':[/]");
            foreach (var error in program.Errors)
                AnsiConsole.MarkupLineInterpolated($"  [red]{error}[/]");
            return;
        }

        AnsiConsole.MarkupLineInterpolated($"\n[aqua]Running recipe[/] '{name}'.");
        AnsiConsole.MarkupLine("[grey](keys: [[p]]ause  [[s]]tep  [[r]]esume  [[c]]ancel)[/]");

        void OnPaused(bool paused)
        {
            if (paused)
                AnsiConsole.MarkupLine("  [yellow]|| PAUSED[/] — [grey][[r]]esume  [[s]]tep  [[c]]ancel[/]");
        }

        using var cts = new CancellationTokenSource();
        _service.PausedChanged += OnPaused;
        try
        {
            var runTask = _service.RunRecipeAsync(name, cts.Token);
            while (!runTask.IsCompleted)
            {
                if (!_awaitingPrompt && Console.KeyAvailable)
                {
                    switch (Console.ReadKey(intercept: true).Key)
                    {
                        case ConsoleKey.P: _service.Pause();  break;
                        case ConsoleKey.R: _service.Resume(); break;
                        case ConsoleKey.S: _service.Step();   break;
                        case ConsoleKey.C:
                        case ConsoleKey.Escape: cts.Cancel(); break;
                    }
                }
                await Task.Delay(50);
            }
            Report(await runTask, $"Run recipe '{name}'");
        }
        finally
        {
            _service.PausedChanged -= OnPaused;
        }
    }

    // --- Hexapod scan ---

    private async Task HexapodScanMenuAsync()
    {
        var motionDevices = _engine.Motion.DeviceNames;
        if (motionDevices.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No motion devices.[/]");
            return;
        }

        var device = Choose("\n[aqua]Hexapod scan[/] — motion device:", motionDevices.Append(Back));
        if (device == Back) return;

        // Scan mode preset (step-size schedule + per-direction range).
        const string CoarseChoice = "Coarse (5→2→1→0.5 µm)";
        const string FineChoice = "Fine (2→1→0.5 µm)";
        const string UltraChoice = "Ultra fine (0.5→0.2→0.1 µm)";

        var modePick = Choose("Scan mode:", new[] { CoarseChoice, FineChoice, UltraChoice, Back });
        if (modePick == Back) return;
        var mode = modePick == CoarseChoice ? ScanMode.Coarse
            : modePick == FineChoice ? ScanMode.Fine
            : ScanMode.UltraFine;

        // Scan parameters — start from the mode preset, then let the operator
        // view / edit them before running.
        var reviewed = ReviewScanParameters(mode, ScanParameters.ForMode(mode));
        if (reviewed == null) return;
        ScanParameters parameters = reviewed;

        // Feedback source type.
        const string SmuChoice = "SMU (streaming)";
        const string MeasChoice = "Current measurement (streaming)";
        const string PsuChoice = "Power supply (polled)";

        var kindPick = Choose("Feedback source:", new[] { SmuChoice, MeasChoice, PsuChoice, Back });
        if (kindPick == Back) return;

        FeedbackKind kind;
        IReadOnlyList<string> feedbackDevices;
        switch (kindPick)
        {
            case SmuChoice:
                kind = FeedbackKind.Smu;
                feedbackDevices = _engine.Smu.DeviceNames;
                break;
            case MeasChoice:
                kind = FeedbackKind.Measurement;
                feedbackDevices = _engine.Measurement.DeviceNames;
                break;
            default:
                kind = FeedbackKind.PowerSupply;
                feedbackDevices = _engine.PowerSupply.DeviceNames;
                break;
        }

        if (feedbackDevices.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No devices of that type are configured.[/]");
            return;
        }

        var feedbackDevice = Choose("Feedback device:", feedbackDevices.Append(Back));
        if (feedbackDevice == Back) return;

        var channel = AnsiConsole.Prompt(
            new TextPrompt<int>("Feedback channel (1-based):").DefaultValue(1));

        var valueType = kind == FeedbackKind.Measurement
            ? FeedbackValue.Current
            : (Choose("Optimise:", new[] { "Current", "Voltage" }) == "Voltage"
                ? FeedbackValue.Voltage
                : FeedbackValue.Current);

        var descriptor = new FeedbackDescriptor
        {
            Kind = kind,
            Device = feedbackDevice,
            Channel = channel,
            ValueType = valueType,
            IntervalMs = 50,
        };

        var verbose = AnsiConsole.Confirm(
            "Verbose step log? (per-step detail, no progress bar)", defaultValue: false);

        AnsiConsole.MarkupLine("[yellow]Warning: this moves the physical hexapod.[/]");
        if (!AnsiConsole.Confirm("Start scan?", defaultValue: false))
            return;

        AnsiConsole.MarkupLine(
            "[grey](press any key to stop — the hexapod returns to the best position found)[/]");

        var result = verbose
            ? await RunScanVerboseAsync(device, descriptor, parameters)
            : await RunScanWithBarAsync(device, descriptor, parameters);

        if (result != null)
            RenderScanResult(result);
    }

    /// <summary>Runs a scan with a live progress bar; engine logs are buffered until the bar closes.</summary>
    private async Task<ScanResult?> RunScanWithBarAsync(
        string device, FeedbackDescriptor descriptor, ScanParameters parameters)
    {
        // Buffer non-debug log lines so they do not corrupt the live progress bar.
        var logLines = new List<string>();
        var scanEngine = new HexapodScanEngine(
            _engine,
            (level, message) =>
            {
                if (level != LogLevel.Debug)   // per-step Debug detail is for verbose mode
                    logLines.Add($"[grey]{level}[/] {Markup.Escape(message)}");
            });

        ScanResult? result = null;
        await AnsiConsole.Progress()
            .AutoClear(false)
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("Starting…", maxValue: 100);
                var progress = new Progress<ScanProgress>(sp =>
                {
                    task.Value = Math.Clamp(sp.Fraction * 100, 0, 100);
                    task.Description = Markup.Escape(TrimDescription(
                        $"{sp.State}: {sp.Message}  (samples {sp.FeedbackSampleCount})"));
                });

                using var cts = new CancellationTokenSource();
                var runTask = scanEngine.RunAsync(device, descriptor, parameters, progress, ct: cts.Token);
                while (!runTask.IsCompleted)
                {
                    if (!_awaitingPrompt && Console.KeyAvailable)
                    {
                        Console.ReadKey(intercept: true);
                        cts.Cancel();
                        task.Description = "Stopping — returning to peak…";
                    }
                    await Task.Delay(50);
                }
                result = await runTask;
                task.Value = 100;
            });

        foreach (var line in logLines)
            AnsiConsole.MarkupLine($"  {line}");

        return result;
    }

    /// <summary>Runs a scan with a live scrolling per-step log (no progress bar).</summary>
    private async Task<ScanResult?> RunScanVerboseAsync(
        string device, FeedbackDescriptor descriptor, ScanParameters parameters)
    {
        var scanEngine = new HexapodScanEngine(
            _engine,
            (level, message) =>
            {
                var colour = level switch
                {
                    LogLevel.Debug => "grey",
                    LogLevel.Warning => "yellow",
                    LogLevel.Error => "red",
                    _ => "white",
                };
                AnsiConsole.MarkupLine($"  [{colour}]{level,-7}[/] {Markup.Escape(message)}");
            });

        // Verbose mode relies on the Debug log for detail — no progress bar needed.
        using var cts = new CancellationTokenSource();
        var runTask = scanEngine.RunAsync(device, descriptor, parameters, progress: null, ct: cts.Token);
        while (!runTask.IsCompleted)
        {
            if (!_awaitingPrompt && Console.KeyAvailable)
            {
                Console.ReadKey(intercept: true);
                cts.Cancel();
                AnsiConsole.MarkupLine("  [yellow]Stopping — returning to peak…[/]");
            }
            await Task.Delay(50);
        }
        return await runTask;
    }

    private static string TrimDescription(string text)
        => text.Length <= 80 ? text : string.Concat(text.AsSpan(0, 77), "…");

    // --- Scan parameters: view / edit ---

    /// <summary>
    /// Shows the scan parameters and lets the operator edit them. Returns the
    /// (possibly edited) parameters, or null if cancelled.
    /// </summary>
    private static ScanParameters? ReviewScanParameters(ScanMode mode, ScanParameters parameters)
    {
        const string Continue = "Continue with these parameters";
        const string Edit = "Edit a parameter";
        const string Reset = "Reset to mode preset";
        const string Cancel = "Cancel";

        while (true)
        {
            RenderScanParameters(mode, parameters);

            var action = Choose(
                "\n[aqua]Scan parameters[/]:",
                new[] { Continue, Edit, Reset, Cancel });

            switch (action)
            {
                case Continue:
                    var validation = parameters.Validate();
                    if (!validation.IsSuccess)
                    {
                        AnsiConsole.MarkupLineInterpolated($"[red]Invalid:[/] {validation.Message}");
                        break;
                    }
                    return parameters;

                case Reset:
                    parameters = ScanParameters.ForMode(mode);
                    break;

                case Edit:
                    parameters = EditScanParameter(parameters);
                    break;

                case Cancel:
                    return null;
            }
        }
    }

    private static void RenderScanParameters(ScanMode mode, ScanParameters p)
    {
        var table = new Table().Border(TableBorder.Rounded);
        table.Title = new TableTitle($"Scan parameters — {mode} preset");
        table.AddColumn("Parameter");
        table.AddColumn("Value");
        table.AddRow("Axes", Markup.Escape(string.Join(", ", p.AxesToScan)));
        table.AddRow("Step sizes (µm)",
            Markup.Escape(string.Join(", ", p.StepSizes.Select(s => (s * 1000).ToString("0.###")))));
        table.AddRow("Settle time (ms)", p.MotionSettleMs.ToString());
        table.AddRow("Scan velocity (mm/s)", p.ScanVelocityMmPerSec.ToString("0.###"));
        table.AddRow("Consecutive decreases limit", p.ConsecutiveDecreasesLimit.ToString());
        table.AddRow("Significant decrease threshold", p.SignificantDecreaseThreshold.ToString("0.###"));
        table.AddRow("Max distance per direction (mm)", p.MaxTotalDistanceMm.ToString("0.####"));
        table.AddRow("Improvement threshold", p.ImprovementThreshold.ToString("0.###"));
        table.AddRow("Averaging count", p.AveragingCount.ToString());
        table.AddRow("Max step size (mm)", p.MaxStepSizeMm.ToString("0.###"));
        table.AddRow("Scan timeout (min)", p.ScanTimeout.TotalMinutes.ToString("0.###"));
        table.AddRow("Min peak σ (noise gate)", p.MinPeakSigma.ToString("0.#"));
        AnsiConsole.Write(table);
    }

    private static ScanParameters EditScanParameter(ScanParameters p)
    {
        const string Axes = "Axes";
        const string Steps = "Step sizes (µm)";
        const string Settle = "Settle time (ms)";
        const string Velocity = "Scan velocity (mm/s)";
        const string Decreases = "Consecutive decreases limit";
        const string SigThreshold = "Significant decrease threshold";
        const string MaxDist = "Max distance per direction (mm)";
        const string Improvement = "Improvement threshold";
        const string Averaging = "Averaging count";
        const string MaxStep = "Max step size (mm)";
        const string Timeout = "Scan timeout (min)";
        const string MinSigma = "Min peak σ (noise gate)";

        var field = Choose(
            "Edit which parameter?",
            new[] { Axes, Steps, Settle, Velocity, Decreases, SigThreshold,
                    MaxDist, Improvement, Averaging, MaxStep, Timeout, MinSigma, Back });

        switch (field)
        {
            case Axes:
            {
                var text = AnsiConsole.Prompt(
                    new TextPrompt<string>("Axes (comma-separated, e.g. Z,Y,X):")
                        .DefaultValue(string.Join(",", p.AxesToScan)));
                var axes = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                return axes.Length > 0 ? p with { AxesToScan = axes } : p;
            }

            case Steps:
            {
                var text = AnsiConsole.Prompt(
                    new TextPrompt<string>("Step sizes in µm (comma-separated, coarse→fine):")
                        .DefaultValue(string.Join(",", p.StepSizes.Select(s => (s * 1000).ToString("0.###")))));
                var tokens = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var steps = new List<double>();
                foreach (var token in tokens)
                {
                    if (double.TryParse(token, out var um) && um > 0)
                    {
                        steps.Add(um / 1000.0);
                    }
                    else
                    {
                        AnsiConsole.MarkupLineInterpolated(
                            $"[red]Invalid step size '{token}' — keeping previous values.[/]");
                        return p;
                    }
                }
                return steps.Count > 0 ? p with { StepSizes = steps } : p;
            }

            case Settle:
                return p with { MotionSettleMs = AnsiConsole.Prompt(
                    new TextPrompt<int>("Settle time (ms):").DefaultValue(p.MotionSettleMs)) };

            case Velocity:
                return p with { ScanVelocityMmPerSec = AnsiConsole.Prompt(
                    new TextPrompt<double>("Scan velocity (mm/s):").DefaultValue(p.ScanVelocityMmPerSec)) };

            case Decreases:
                return p with { ConsecutiveDecreasesLimit = AnsiConsole.Prompt(
                    new TextPrompt<int>("Consecutive decreases limit:").DefaultValue(p.ConsecutiveDecreasesLimit)) };

            case SigThreshold:
                return p with { SignificantDecreaseThreshold = AnsiConsole.Prompt(
                    new TextPrompt<double>("Significant decrease threshold (0-1):")
                        .DefaultValue(p.SignificantDecreaseThreshold)) };

            case MaxDist:
                return p with { MaxTotalDistanceMm = AnsiConsole.Prompt(
                    new TextPrompt<double>("Max distance per direction (mm):").DefaultValue(p.MaxTotalDistanceMm)) };

            case Improvement:
                return p with { ImprovementThreshold = AnsiConsole.Prompt(
                    new TextPrompt<double>("Improvement threshold (0-1):").DefaultValue(p.ImprovementThreshold)) };

            case Averaging:
                return p with { AveragingCount = AnsiConsole.Prompt(
                    new TextPrompt<int>("Averaging count:").DefaultValue(p.AveragingCount)) };

            case MaxStep:
                return p with { MaxStepSizeMm = AnsiConsole.Prompt(
                    new TextPrompt<double>("Max step size (mm):").DefaultValue(p.MaxStepSizeMm)) };

            case Timeout:
                return p with { ScanTimeout = TimeSpan.FromMinutes(AnsiConsole.Prompt(
                    new TextPrompt<double>("Scan timeout (minutes):").DefaultValue(p.ScanTimeout.TotalMinutes))) };

            case MinSigma:
                return p with { MinPeakSigma = AnsiConsole.Prompt(
                    new TextPrompt<double>("Min peak σ — noise gate, e.g. 3:").DefaultValue(p.MinPeakSigma)) };

            default:
                return p;
        }
    }

    private static void RenderScanResult(ScanResult result)
    {
        string header;
        if (!result.Success)
            header = "[red]Scan failed[/]";
        else if (result.PeakIsSignificant)
            header = "[green]Scan complete — real peak[/]";
        else
            header = "[yellow]Scan complete — noise, no significant peak[/]";

        AnsiConsole.MarkupLine($"\n{header} — [grey]{result.FinalState}[/]");
        if (!string.IsNullOrEmpty(result.Message))
            AnsiConsole.MarkupLineInterpolated($"  {result.Message}");

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Metric");
        table.AddColumn("Value");
        table.AddRow("Device", Markup.Escape(result.Device));
        table.AddRow("Duration", $"{result.Duration.TotalSeconds:F1} s");
        table.AddRow("Baseline", result.Baseline.Valid ? $"{result.Baseline.Value:G6}" : "[grey]—[/]");
        table.AddRow("Peak", result.Peak.Valid ? $"{result.Peak.Value:G6}" : "[grey]—[/]");
        if (result.Baseline.Valid && result.Peak.Valid)
        {
            // Signed delta — a ratio is meaningless when the signal can be negative.
            double improvement = result.Peak.Value - result.Baseline.Value;
            table.AddRow("Improvement", $"{(improvement >= 0 ? "+" : "")}{improvement:G4}");
        }
        table.AddRow("Noise σ", result.NoiseSigma > 0 ? $"{result.NoiseSigma:G4}" : "[grey]—[/]");
        table.AddRow("Peak SNR",
            double.IsPositiveInfinity(result.PeakSnr) ? "∞ σ" : $"{result.PeakSnr:F1} σ");
        table.AddRow("Verdict", result.PeakIsSignificant
            ? "[green]real peak[/]"
            : "[yellow]noise — no significant peak[/]");
        table.AddRow("Measurements", result.Measurements.Count.ToString());
        AnsiConsole.Write(table);

        if (result.AxisScans.Count == 0) return;

        var axisTable = new Table().Border(TableBorder.Rounded);
        axisTable.AddColumn("Axis");
        axisTable.AddColumn("Step (µm)");
        axisTable.AddColumn("Final value");
        axisTable.AddColumn("Points");
        foreach (var axis in result.AxisScans)
            axisTable.AddRow(
                Markup.Escape(axis.Axis),
                $"{axis.StepSize * 1000:F3}",
                $"{axis.FinalValue:G6}",
                axis.TotalMeasurements.ToString());
        AnsiConsole.Write(axisTable);
    }

    private static void Report(Result result, string action)
    {
        if (!result.IsSuccess)
            AnsiConsole.MarkupLineInterpolated($"[red]FAILED[/] — {action}: {result.Message}");
        else if (string.IsNullOrEmpty(result.Message))
            AnsiConsole.MarkupLineInterpolated($"[green]OK[/] — {action}");
        else   // success carrying a message — e.g. a run that ended early by design
            AnsiConsole.MarkupLineInterpolated($"[green]OK[/] — {action}: {result.Message}");
    }
}
