using Horizon.Common;
using Horizon.Engine;
using Spectre.Console;

namespace Horizon.ConsoleHost;

/// <summary>
/// Spectre.Console menu loop over the engine. Deliberately minimal — one menu
/// section is added per hardware bring-up step.
/// </summary>
internal sealed class ConsoleApp
{
    private const string ShowStatus = "Show hardware status";
    private const string IoDevices = "IO devices";
    private const string Reconnect = "Reconnect hardware";
    private const string Exit = "Exit";

    private const string Back = "Back";

    private readonly IHorizonEngine _engine;

    public ConsoleApp(IHorizonEngine engine) => _engine = engine;

    public async Task RunAsync()
    {
        while (true)
        {
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("\n[aqua]Horizon engine[/] — choose an action:")
                    .AddChoices(ShowStatus, IoDevices, Reconnect, Exit));

            switch (choice)
            {
                case ShowStatus:
                    ShowSnapshot();
                    break;

                case IoDevices:
                    await IoMenuAsync();
                    break;

                case Reconnect:
                    await Program.ConnectAsync(_engine);
                    break;

                case Exit:
                    return;
            }
        }
    }

    private void ShowSnapshot()
    {
        var snapshot = _engine.GetStatusSnapshot();

        AnsiConsole.MarkupLineInterpolated($"\nEngine state: [aqua]{snapshot.State}[/]");

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Domain");
        table.AddColumn("Device");
        table.AddColumn("Connected");
        table.AddColumn("Detail");

        if (snapshot.Devices.Count == 0)
        {
            table.AddRow("[grey]—[/]", "[grey]no devices[/]", "[grey]—[/]", "[grey]—[/]");
        }
        else
        {
            foreach (var d in snapshot.Devices)
            {
                var connected = d.IsConnected ? "[green]yes[/]" : "[red]no[/]";
                table.AddRow(
                    Markup.Escape(d.Domain),
                    Markup.Escape(d.Name),
                    connected,
                    Markup.Escape(d.Detail));
            }
        }

        AnsiConsole.Write(table);
    }

    private async Task IoMenuAsync()
    {
        var devices = _engine.IO.DeviceNames;
        if (devices.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No IO devices configured (check io_devices.json).[/]");
            return;
        }

        while (true)
        {
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("\n[aqua]IO devices[/] — select a device:")
                    .AddChoices(devices.Append(Back)));

            if (choice == Back) return;
            await IoDeviceMenuAsync(choice);
        }
    }

    private async Task IoDeviceMenuAsync(string device)
    {
        const string Refresh = "Refresh";
        const string ToggleOutput = "Toggle output pin";

        while (true)
        {
            RenderIoDevice(device);

            var info = _engine.IO.GetDevice(device);
            var choices = new List<string> { Refresh };
            if (info is { OutputCount: > 0 })
                choices.Add(ToggleOutput);
            choices.Add(Back);

            var action = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title($"\n[aqua]{Markup.Escape(device)}[/] — action:")
                    .AddChoices(choices));

            switch (action)
            {
                case Refresh:
                    break;

                case ToggleOutput:
                    await ToggleOutputAsync(device, info!);
                    break;

                case Back:
                    return;
            }
        }
    }

    private void RenderIoDevice(string device)
    {
        var info = _engine.IO.GetDevice(device);
        if (info == null)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Unknown IO device: {device}[/]");
            return;
        }

        var connected = _engine.IO.IsConnected(device);
        var state = _engine.IO.GetState(device);

        var connectedText = connected ? "[green]yes[/]" : "[red]no[/]";
        AnsiConsole.MarkupLine(
            $"\n[aqua]{Markup.Escape(info.Name)}[/]  connected: {connectedText}  " +
            $"inputs: {info.InputCount}  outputs: {info.OutputCount}");

        if (!state.Valid)
            AnsiConsole.MarkupLine("[grey]No polled data yet.[/]");

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Bank");
        table.AddColumn("Pin");
        table.AddColumn("Name");
        table.AddColumn("State");

        foreach (var pin in info.InputPins)
            table.AddRow("IN", pin.Pin.ToString(), Markup.Escape(pin.Name), Bit(state.Inputs, pin.Pin));
        foreach (var pin in info.OutputPins)
            table.AddRow("OUT", pin.Pin.ToString(), Markup.Escape(pin.Name), Bit(state.Outputs, pin.Pin));

        AnsiConsole.Write(table);
    }

    private async Task ToggleOutputAsync(string device, IoDeviceInfo info)
    {
        var pin = AnsiConsole.Prompt(
            new SelectionPrompt<IoPin>()
                .Title("Select output pin to toggle:")
                .UseConverter(p => $"{p.Pin}: {p.Name}")
                .AddChoices(info.OutputPins));

        var state = _engine.IO.GetState(device);
        var current = ((state.Outputs >> pin.Pin) & 1u) != 0u;
        var next = !current;

        var result = await _engine.IO.SetOutputAsync(device, pin.Pin, next);
        ReportResult(result, $"Set {device} pin {pin.Pin} ({pin.Name}) = {next}");

        // Let the 100 ms poll loop pick up the change before re-rendering.
        await Task.Delay(150);
    }

    private static string Bit(uint mask, int pin)
        => ((mask >> pin) & 1u) != 0u ? "[green]ON[/]" : "[grey]off[/]";

    private static void ReportResult(Result result, string action)
    {
        if (result.IsSuccess)
            AnsiConsole.MarkupLineInterpolated($"[green]OK[/] — {action}");
        else
            AnsiConsole.MarkupLineInterpolated($"[red]FAILED[/] — {action}: {result.Message}");
    }
}
