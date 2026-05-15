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
    private const string MotionDevices = "Motion devices";
    private const string PneumaticSlides = "Pneumatic slides";
    private const string CurrentMeasurement = "Current measurement";
    private const string LaserControllers = "Laser controllers";
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
                    .AddChoices(ShowStatus, IoDevices, MotionDevices, PneumaticSlides,
                                CurrentMeasurement, LaserControllers, Reconnect, Exit));

            switch (choice)
            {
                case ShowStatus:
                    ShowSnapshot();
                    break;

                case IoDevices:
                    await IoMenuAsync();
                    break;

                case MotionDevices:
                    await MotionMenuAsync();
                    break;

                case PneumaticSlides:
                    await PneumaticMenuAsync();
                    break;

                case CurrentMeasurement:
                    await MeasurementMenuAsync();
                    break;

                case LaserControllers:
                    await LaserMenuAsync();
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

    private async Task MotionMenuAsync()
    {
        var devices = _engine.Motion.DeviceNames;
        if (devices.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No motion devices configured (check motor_devices.json).[/]");
            return;
        }

        while (true)
        {
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("\n[aqua]Motion devices[/] — select a device:")
                    .AddChoices(devices.Append(Back)));

            if (choice == Back) return;
            await MotionDeviceMenuAsync(choice);
        }
    }

    private async Task MotionDeviceMenuAsync(string device)
    {
        const string Connect = "Connect";
        const string Disconnect = "Disconnect";
        const string Stop = "Stop (halt motion)";
        const string Jog = "Jog axis";
        const string Refresh = "Refresh";

        while (true)
        {
            RenderMotionDevice(device);

            var info = _engine.Motion.GetDevice(device);
            var connected = _engine.Motion.IsConnected(device);

            var choices = new List<string>();
            if (connected)
            {
                choices.Add(Stop);
                if (info is { Axes.Count: > 0 })
                    choices.Add(Jog);
                choices.Add(Disconnect);
            }
            else
            {
                choices.Add(Connect);
            }
            choices.Add(Refresh);
            choices.Add(Back);

            var action = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title($"\n[aqua]{Markup.Escape(device)}[/] — action:")
                    .AddChoices(choices));

            switch (action)
            {
                case Connect:
                    ReportResult(await _engine.Motion.ConnectAsync(device), $"Connect {device}");
                    break;

                case Disconnect:
                    ReportResult(await _engine.Motion.DisconnectAsync(device), $"Disconnect {device}");
                    break;

                case Stop:
                    ReportResult(await _engine.Motion.StopAsync(device), $"Stop {device}");
                    break;

                case Jog:
                    await JogAxisAsync(device, info!);
                    break;

                case Refresh:
                    break;

                case Back:
                    return;
            }
        }
    }

    private void RenderMotionDevice(string device)
    {
        var info = _engine.Motion.GetDevice(device);
        if (info == null)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Unknown motion device: {device}[/]");
            return;
        }

        var connected = _engine.Motion.IsConnected(device);
        var state = _engine.Motion.GetState(device);

        var connectedText = connected ? "[green]yes[/]" : "[red]no[/]";
        var movingText = state.IsMoving ? "[yellow]MOVING[/]" : "[grey]idle[/]";
        AnsiConsole.MarkupLine(
            $"\n[aqua]{Markup.Escape(info.Name)}[/]  type: {Markup.Escape(info.ControllerType)}  " +
            $"connected: {connectedText}  motion: {movingText}");

        if (!state.Valid)
            AnsiConsole.MarkupLine("[grey]No polled data yet.[/]");

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Axis");
        table.AddColumn("Position");

        var components = state.Position.Components;
        for (int i = 0; i < info.Axes.Count; i++)
        {
            var pos = i < components.Count ? components[i].ToString("F4") : "—";
            table.AddRow(Markup.Escape(info.Axes[i]), pos);
        }

        AnsiConsole.Write(table);
    }

    private async Task JogAxisAsync(string device, MotionDeviceInfo info)
    {
        var axis = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Select axis to jog:")
                .AddChoices(info.Axes));

        AnsiConsole.MarkupLine("[yellow]Warning: this moves the physical stage.[/]");
        var distance = AnsiConsole.Prompt(
            new TextPrompt<double>($"Relative distance for [aqua]{Markup.Escape(axis)}[/]:")
                .DefaultValue(0d));

        if (distance == 0d)
        {
            AnsiConsole.MarkupLine("[grey]Distance 0 — no move.[/]");
            return;
        }

        var result = await _engine.Motion.MoveRelativeAsync(device, axis, distance);
        ReportResult(result, $"Jog {device}.{axis} by {distance}");

        // Let the poll loop catch up before re-rendering.
        await Task.Delay(200);
    }

    private async Task PneumaticMenuAsync()
    {
        var slides = _engine.Pneumatic.SlideNames;
        if (slides.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No pneumatic slides configured (check pneumatic_slides.json).[/]");
            return;
        }

        while (true)
        {
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("\n[aqua]Pneumatic slides[/] — select a slide:")
                    .AddChoices(slides.Append(Back)));

            if (choice == Back) return;
            await PneumaticSlideMenuAsync(choice);
        }
    }

    private async Task PneumaticSlideMenuAsync(string slide)
    {
        const string Extend = "Extend";
        const string Retract = "Retract";
        const string ExtendWait = "Extend & wait";
        const string RetractWait = "Retract & wait";
        const string Refresh = "Refresh";

        while (true)
        {
            RenderPneumaticSlide(slide);

            var action = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title($"\n[aqua]{Markup.Escape(slide)}[/] — action:")
                    .AddChoices(Extend, Retract, ExtendWait, RetractWait, Refresh, Back));

            switch (action)
            {
                case Extend:
                    ReportResult(await _engine.Pneumatic.ExtendAsync(slide), $"Extend {slide}");
                    await Task.Delay(250);
                    break;

                case Retract:
                    ReportResult(await _engine.Pneumatic.RetractAsync(slide), $"Retract {slide}");
                    await Task.Delay(250);
                    break;

                case ExtendWait:
                    ReportResult(await _engine.Pneumatic.ExtendAndWaitAsync(slide), $"Extend & wait {slide}");
                    break;

                case RetractWait:
                    ReportResult(await _engine.Pneumatic.RetractAndWaitAsync(slide), $"Retract & wait {slide}");
                    break;

                case Refresh:
                    break;

                case Back:
                    return;
            }
        }
    }

    private void RenderPneumaticSlide(string slide)
    {
        var state = _engine.Pneumatic.GetState(slide);

        AnsiConsole.MarkupLineInterpolated($"\n[aqua]{slide}[/]");
        if (!state.Valid)
            AnsiConsole.MarkupLine("[grey]No polled data yet.[/]");

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Property");
        table.AddColumn("Value");
        table.AddRow("Position", $"[aqua]{state.Position}[/]");
        table.AddRow("Output", state.OutputActive ? "[green]active[/]" : "[grey]off[/]");
        table.AddRow("Extended sensor", state.ExtendedSensor ? "[green]ON[/]" : "[grey]off[/]");
        table.AddRow("Retracted sensor", state.RetractedSensor ? "[green]ON[/]" : "[grey]off[/]");
        AnsiConsole.Write(table);
    }

    private async Task MeasurementMenuAsync()
    {
        var devices = _engine.Measurement.DeviceNames;
        if (devices.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No current-measurement devices configured.[/]");
            return;
        }

        while (true)
        {
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("\n[aqua]Current measurement[/] — select a device:")
                    .AddChoices(devices.Append(Back)));

            if (choice == Back) return;
            await MeasurementDeviceMenuAsync(choice);
        }
    }

    private async Task MeasurementDeviceMenuAsync(string device)
    {
        const string Connect = "Connect";
        const string Disconnect = "Disconnect";
        const string SetVoltage = "Set voltage";
        const string VoltageOn = "Voltage output ON";
        const string VoltageOff = "Voltage output OFF";
        const string Reset = "Reset (*RST)";
        const string Identity = "Read identity";
        const string Refresh = "Refresh";

        while (true)
        {
            RenderMeasurementDevice(device);

            var connected = _engine.Measurement.IsConnected(device);
            var choices = new List<string>();
            if (connected)
                choices.AddRange(new[] { SetVoltage, VoltageOn, VoltageOff, Reset, Identity, Disconnect });
            else
                choices.Add(Connect);
            choices.Add(Refresh);
            choices.Add(Back);

            var action = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title($"\n[aqua]{Markup.Escape(device)}[/] — action:")
                    .AddChoices(choices));

            switch (action)
            {
                case Connect:
                    ReportResult(await _engine.Measurement.ConnectAsync(device), $"Connect {device}");
                    break;
                case Disconnect:
                    ReportResult(await _engine.Measurement.DisconnectAsync(device), $"Disconnect {device}");
                    break;
                case SetVoltage:
                    var volts = AnsiConsole.Prompt(new TextPrompt<double>("Source voltage (V):").DefaultValue(0d));
                    ReportResult(await _engine.Measurement.SetVoltageAsync(device, volts), $"Set voltage {volts}V");
                    break;
                case VoltageOn:
                    ReportResult(await _engine.Measurement.SetVoltageEnabledAsync(device, true), "Voltage output ON");
                    break;
                case VoltageOff:
                    ReportResult(await _engine.Measurement.SetVoltageEnabledAsync(device, false), "Voltage output OFF");
                    break;
                case Reset:
                    ReportResult(await _engine.Measurement.ResetAsync(device), $"Reset {device}");
                    break;
                case Identity:
                    var idn = await _engine.Measurement.GetIdentityAsync(device);
                    if (idn.IsSuccess)
                        AnsiConsole.MarkupLineInterpolated($"[green]IDN[/] — {idn.Value}");
                    else
                        AnsiConsole.MarkupLineInterpolated($"[red]IDN failed[/] — {idn.Message}");
                    break;
                case Refresh:
                    break;
                case Back:
                    return;
            }
        }
    }

    private void RenderMeasurementDevice(string device)
    {
        var info = _engine.Measurement.GetDevice(device);
        if (info == null)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Unknown measurement device: {device}[/]");
            return;
        }

        var connected = _engine.Measurement.IsConnected(device);
        var state = _engine.Measurement.GetState(device);

        var connectedText = connected ? "[green]yes[/]" : "[red]no[/]";
        AnsiConsole.MarkupLine($"\n[aqua]{Markup.Escape(info.Name)}[/]  connected: {connectedText}  channels: {info.ChannelCount}");
        if (!state.Valid)
            AnsiConsole.MarkupLine("[grey]No polled data yet.[/]");

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Channel");
        table.AddColumn("Reading");
        for (int i = 0; i < state.Channels.Count; i++)
            table.AddRow($"CH{i + 1}", $"{state.Channels[i]:E4} {Markup.Escape(state.Unit)}");
        if (state.Channels.Count == 0)
            table.AddRow("[grey]—[/]", "[grey]no data[/]");
        AnsiConsole.Write(table);
    }

    private async Task LaserMenuAsync()
    {
        var devices = _engine.Laser.DeviceNames;
        if (devices.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No laser controllers configured.[/]");
            return;
        }

        while (true)
        {
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("\n[aqua]Laser controllers[/] — select a device:")
                    .AddChoices(devices.Append(Back)));

            if (choice == Back) return;
            await LaserDeviceMenuAsync(choice);
        }
    }

    private async Task LaserDeviceMenuAsync(string device)
    {
        const string Connect = "Connect";
        const string Disconnect = "Disconnect";
        const string SetTemp = "Set temperature";
        const string TecOn = "TEC ON";
        const string TecOff = "TEC OFF";
        const string SetCurrent = "Set laser current";
        const string LaserOn = "Laser ON";
        const string LaserOff = "Laser OFF";
        const string Refresh = "Refresh";

        while (true)
        {
            RenderLaserDevice(device);

            var connected = _engine.Laser.IsConnected(device);
            var choices = new List<string>();
            if (connected)
                choices.AddRange(new[] { SetTemp, TecOn, TecOff, SetCurrent, LaserOn, LaserOff, Disconnect });
            else
                choices.Add(Connect);
            choices.Add(Refresh);
            choices.Add(Back);

            var action = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title($"\n[aqua]{Markup.Escape(device)}[/] — action:")
                    .AddChoices(choices));

            switch (action)
            {
                case Connect:
                    ReportResult(await _engine.Laser.ConnectAsync(device), $"Connect {device}");
                    break;
                case Disconnect:
                    ReportResult(await _engine.Laser.DisconnectAsync(device), $"Disconnect {device}");
                    break;
                case SetTemp:
                    var temp = AnsiConsole.Prompt(new TextPrompt<double>("TEC temperature (C):").DefaultValue(25d));
                    ReportResult(await _engine.Laser.SetTemperatureAsync(device, temp), $"Set temperature {temp}C");
                    break;
                case TecOn:
                    ReportResult(await _engine.Laser.TecOnAsync(device), $"TEC on {device}");
                    break;
                case TecOff:
                    ReportResult(await _engine.Laser.TecOffAsync(device), $"TEC off {device}");
                    break;
                case SetCurrent:
                    var ma = AnsiConsole.Prompt(new TextPrompt<double>("Laser current (mA):").DefaultValue(0d));
                    ReportResult(await _engine.Laser.SetLaserCurrentAsync(device, ma), $"Set laser current {ma}mA");
                    break;
                case LaserOn:
                    AnsiConsole.MarkupLine("[yellow]Warning: this enables laser diode emission.[/]");
                    if (AnsiConsole.Confirm("Enable laser emission?", defaultValue: false))
                        ReportResult(await _engine.Laser.LaserOnAsync(device), $"Laser ON {device}");
                    break;
                case LaserOff:
                    ReportResult(await _engine.Laser.LaserOffAsync(device), $"Laser OFF {device}");
                    break;
                case Refresh:
                    break;
                case Back:
                    return;
            }
        }
    }

    private void RenderLaserDevice(string device)
    {
        var info = _engine.Laser.GetDevice(device);
        if (info == null)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Unknown laser device: {device}[/]");
            return;
        }

        var connected = _engine.Laser.IsConnected(device);
        var state = _engine.Laser.GetState(device);

        var connectedText = connected ? "[green]yes[/]" : "[red]no[/]";
        AnsiConsole.MarkupLine($"\n[aqua]{Markup.Escape(info.Name)}[/]  type: {Markup.Escape(info.ControllerType)}  connected: {connectedText}");
        if (!state.Valid)
            AnsiConsole.MarkupLine("[grey]No polled data yet.[/]");

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Property");
        table.AddColumn("Value");
        table.AddRow("TEC", state.TecOn ? "[green]ON[/]" : "[grey]off[/]");
        table.AddRow("Laser", state.LaserOn ? "[red]ON (emitting)[/]" : "[grey]off[/]");
        table.AddRow("Temperature", $"{state.Temperature:F2} C  (setpoint {state.SetpointTemperature:F2} C)");
        table.AddRow("Laser current", $"{state.LaserCurrent:F2} mA  (setpoint {state.SetpointLaserCurrent:F2} mA)");
        AnsiConsole.Write(table);
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
