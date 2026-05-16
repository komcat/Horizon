using Horizon.Common;
using Horizon.Engine;
using Horizon.Workflow;
using Spectre.Console;

namespace Horizon.WorkflowConsole;

/// <summary>Spectre.Console menu loop over <see cref="IProcessService"/>.</summary>
internal sealed class ConsoleApp
{
    private const string ShowProducts = "Show products";
    private const string CreateProduct = "Create product";
    private const string LoadProduct = "Load product";
    private const string UnloadProduct = "Unload product";
    private const string DeleteProduct = "Delete product";
    private const string Positions = "Positions";
    private const string ProcessPrograms = "Process programs";
    private const string Exit = "Exit";
    private const string Back = "Back";

    private readonly IProcessService _service;
    private readonly IHorizonEngine _engine;

    public ConsoleApp(IProcessService service, IHorizonEngine engine)
    {
        _service = service;
        _engine = engine;
    }

    public async Task RunAsync()
    {
        while (true)
        {
            var active = _service.ActiveProduct ?? "(none)";
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title($"\n[aqua]Workflow[/] — active product: [aqua]{Markup.Escape(active)}[/]")
                    .AddChoices(ShowProducts, CreateProduct, LoadProduct, UnloadProduct,
                                DeleteProduct, Positions, ProcessPrograms, Exit));

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

        var pick = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title(title)
                .AddChoices(products.Append(Back)));

        if (pick == Back) return;
        action(pick);
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
            var pick = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("\n[aqua]Positions[/] — select a motion device:")
                    .AddChoices(devices.Append(Back)));

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

            var action = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title($"\n[aqua]{Markup.Escape(device)}[/] — action:")
                    .AddChoices(Teach, SetManual, MoveTo, Remove, Refresh, Back));

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

        var pick = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Move to:")
                .AddChoices(names.Append(Back)));
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

        var pick = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Remove:")
                .AddChoices(names.Append(Back)));
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

            var pick = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title($"\n[aqua]Process programs[/] — select to run:")
                    .AddChoices(programs.Append(Back)));

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
        AnsiConsole.MarkupLineInterpolated(
            $"\n[aqua]Running[/] '{program.Name}' — {program.Steps.Count} step(s).");
        AnsiConsole.MarkupLine("[grey](press any key to cancel)[/]");

        void OnStep(WorkflowStepEvent e)
        {
            if (!e.Completed) return;   // print on completion only
            var tag = e.Success ? "[green]OK[/]" : "[red]FAIL[/]";
            var msg = e.Success || string.IsNullOrEmpty(e.Message)
                ? string.Empty
                : $" — {Markup.Escape(e.Message)}";
            AnsiConsole.MarkupLine($"  [[{e.Index + 1}/{e.Total}]] {Markup.Escape(e.Description)}  {tag}{msg}");
        }

        using var cts = new CancellationTokenSource();
        _service.StepEvent += OnStep;
        try
        {
            var runTask = _service.RunAsync(program, cts.Token);
            while (!runTask.IsCompleted)
            {
                if (Console.KeyAvailable)
                {
                    Console.ReadKey(intercept: true);
                    cts.Cancel();
                }
                await Task.Delay(50);
            }
            Report(await runTask, $"Run '{program.Name}'");
        }
        finally
        {
            _service.StepEvent -= OnStep;
        }
    }

    private static void Report(Result result, string action)
    {
        if (result.IsSuccess)
            AnsiConsole.MarkupLineInterpolated($"[green]OK[/] — {action}");
        else
            AnsiConsole.MarkupLineInterpolated($"[red]FAILED[/] — {action}: {result.Message}");
    }
}
