using Horizon.Common;
using Horizon.Engine;
using Spectre.Console;
using Spectre.Console.Rendering;

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
    private const string Cameras = "Cameras";
    private const string Vision = "Vision (HALCON)";
    private const string Safety = "Safety";
    private const string ParallelTest = "Parallel testing";
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
                                CurrentMeasurement, LaserControllers, Cameras, Vision, Safety,
                                ParallelTest, Reconnect, Exit));

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

                case Cameras:
                    await CameraMenuAsync();
                    break;

                case Vision:
                    await VisionMenuAsync();
                    break;

                case Safety:
                    await SafetyMenuAsync();
                    break;

                case ParallelTest:
                    await ParallelTestAsync();
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
        const string MoveTo = "Move to coordinates";
        const string SetVelocity = "Set velocity";
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
                {
                    choices.Add(Jog);
                    choices.Add(MoveTo);
                    choices.Add(SetVelocity);
                }
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

                case MoveTo:
                    await MoveToCoordinatesAsync(device, info!);
                    break;

                case SetVelocity:
                    await SetVelocityAsync(device, info!);
                    break;

                case Refresh:
                    break;

                case Back:
                    return;
            }
        }
    }

    private async Task SetVelocityAsync(string device, MotionDeviceInfo info)
    {
        // PI hexapods have a single system (path) velocity; ACS is per-axis.
        if (info.ControllerType.Equals("PI", StringComparison.OrdinalIgnoreCase))
        {
            var sysVel = AnsiConsole.Prompt(
                new TextPrompt<double>($"System velocity for [aqua]{Markup.Escape(device)}[/]:"));
            ReportResult(await _engine.Motion.SetSystemVelocityAsync(device, sysVel),
                $"Set {device} system velocity = {sysVel}");
            return;
        }

        var axis = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Select axis:")
                .AddChoices(info.Axes));

        var velocity = AnsiConsole.Prompt(
            new TextPrompt<double>($"Velocity for [aqua]{Markup.Escape(axis)}[/]:"));

        var result = await _engine.Motion.SetVelocityAsync(device, axis, velocity);
        ReportResult(result, $"Set velocity {device}.{axis} = {velocity}");
    }

    private async Task MoveToCoordinatesAsync(string device, MotionDeviceInfo info)
    {
        AnsiConsole.MarkupLine("[yellow]Warning: this moves the physical stage to an absolute coordinate.[/]");

        var current = _engine.Motion.GetState(device).Position.Components;
        var values = new double[6];   // X, Y, Z, U, V, W
        for (int i = 0; i < info.Axes.Count && i < 6; i++)
        {
            var def = i < current.Count ? current[i] : 0.0;
            values[i] = AnsiConsole.Prompt(
                new TextPrompt<double>($"Target [aqua]{Markup.Escape(info.Axes[i])}[/]:").DefaultValue(def));
        }

        var target = new MotionPosition(values[0], values[1], values[2], values[3], values[4], values[5]);
        var result = await _engine.Motion.MoveToPositionAsync(device, target);
        ReportResult(result, $"Move {device} to coordinates");

        await Task.Delay(200);
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
        table.AddRow("Last error",
            string.IsNullOrEmpty(state.LastError) ? "[grey]none[/]" : $"[red]{Markup.Escape(state.LastError)}[/]");
        AnsiConsole.Write(table);
    }

    private async Task CameraMenuAsync()
    {
        var cameras = _engine.Camera.CameraIds;
        if (cameras.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No cameras configured (check camera-config.json).[/]");
            return;
        }

        while (true)
        {
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("\n[aqua]Cameras[/] — select a camera:")
                    .AddChoices(cameras.Append(Back)));

            if (choice == Back) return;
            await CameraDeviceMenuAsync(choice);
        }
    }

    private async Task CameraDeviceMenuAsync(string id)
    {
        const string Connect = "Connect";
        const string Disconnect = "Disconnect";
        const string StartGrab = "Start grabbing";
        const string StopGrab = "Stop grabbing";
        const string Snapshot = "Snapshot to file";
        const string SetExposure = "Set exposure";
        const string Refresh = "Refresh";

        while (true)
        {
            RenderCameraDevice(id);

            var connected = _engine.Camera.IsConnected(id);
            var choices = new List<string>();
            if (connected)
                choices.AddRange(new[] { StartGrab, StopGrab, Snapshot, SetExposure, Disconnect });
            else
                choices.Add(Connect);
            choices.Add(Refresh);
            choices.Add(Back);

            var action = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title($"\n[aqua]{Markup.Escape(id)}[/] — action:")
                    .AddChoices(choices));

            switch (action)
            {
                case Connect:
                    ReportResult(await _engine.Camera.ConnectAsync(id), $"Connect {id}");
                    break;
                case Disconnect:
                    ReportResult(await _engine.Camera.DisconnectAsync(id), $"Disconnect {id}");
                    break;
                case StartGrab:
                    ReportResult(await _engine.Camera.StartGrabbingAsync(id), $"Start grabbing {id}");
                    await Task.Delay(300);
                    break;
                case StopGrab:
                    ReportResult(await _engine.Camera.StopGrabbingAsync(id), $"Stop grabbing {id}");
                    break;
                case Snapshot:
                    var snap = await _engine.Camera.SnapToFileAsync(id);
                    if (snap.IsSuccess)
                        AnsiConsole.MarkupLineInterpolated($"[green]Saved[/] — {snap.Value}");
                    else
                        AnsiConsole.MarkupLineInterpolated($"[red]Snapshot failed[/] — {snap.Message}");
                    break;
                case SetExposure:
                    var us = AnsiConsole.Prompt(new TextPrompt<double>("Exposure (us):").DefaultValue(10000d));
                    var gain = AnsiConsole.Prompt(new TextPrompt<double>("Gain:").DefaultValue(0d));
                    var exposure = new CameraExposure(us, gain, false, false, 10.0);
                    ReportResult(await _engine.Camera.ApplyExposureAsync(id, exposure), $"Set exposure {id}");
                    break;
                case Refresh:
                    break;
                case Back:
                    return;
            }
        }
    }

    private void RenderCameraDevice(string id)
    {
        var info = _engine.Camera.GetCamera(id);
        if (info == null)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Unknown camera: {id}[/]");
            return;
        }

        var connected = _engine.Camera.IsConnected(id);
        var state = _engine.Camera.GetState(id);

        var connectedText = connected ? "[green]yes[/]" : "[red]no[/]";
        var grabText = state.Grabbing ? "[yellow]grabbing[/]" : "[grey]idle[/]";
        AnsiConsole.MarkupLine(
            $"\n[aqua]{Markup.Escape(info.DisplayName)}[/]  type: {Markup.Escape(info.CameraType)}  " +
            $"connected: {connectedText}  {grabText}");

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Property");
        table.AddColumn("Value");
        table.AddRow("Id", Markup.Escape(info.Id));
        table.AddRow("Model", Markup.Escape(string.IsNullOrEmpty(info.Model) ? "—" : info.Model));
        table.AddRow("Serial", Markup.Escape(string.IsNullOrEmpty(info.SerialNumber) ? "—" : info.SerialNumber));
        table.AddRow("Resolution", state.Width > 0 ? $"{state.Width} x {state.Height}" : "—");
        table.AddRow("Exposure", $"{state.Exposure.ExposureUs:F0} us  gain {state.Exposure.Gain:F1}");
        table.AddRow("Frames received", state.FramesReceived.ToString());
        AnsiConsole.Write(table);
    }

    private async Task VisionMenuAsync()
    {
        var vision = _engine.Vision;
        var availText = vision.HalconAvailable ? "[green]available[/]" : "[red]unavailable[/]";
        AnsiConsole.MarkupLine($"\n[aqua]Vision (HALCON)[/] — {availText}  version: {Markup.Escape(vision.HalconVersion)}");

        if (!vision.HalconAvailable)
        {
            AnsiConsole.MarkupLine("[yellow]HALCON is not available — cannot run vision operations.[/]");
            return;
        }

        var cameras = _engine.Camera.CameraIds;
        if (cameras.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No cameras configured.[/]");
            return;
        }

        while (true)
        {
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("\n[aqua]Vision[/] — select a camera:")
                    .AddChoices(cameras.Append(Back)));

            if (choice == Back) return;
            await VisionCameraMenuAsync(choice);
        }
    }

    private async Task VisionCameraMenuAsync(string cameraId)
    {
        const string Analyze = "Analyze frame";
        const string CountBlobs = "Count blobs";
        const string Refresh = "Refresh";

        while (true)
        {
            var hasFrame = _engine.Vision.HasFrame(cameraId);
            AnsiConsole.MarkupLineInterpolated(
                $"\n[aqua]{cameraId}[/] — frame available: {(hasFrame ? "yes" : "no (start grabbing first)")}");

            var action = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Action:")
                    .AddChoices(Analyze, CountBlobs, Refresh, Back));

            switch (action)
            {
                case Analyze:
                    var stats = await _engine.Vision.AnalyzeFrameAsync(cameraId);
                    if (stats.IsSuccess)
                    {
                        var s = stats.Value;
                        var table = new Table().Border(TableBorder.Rounded);
                        table.AddColumn("Property");
                        table.AddColumn("Value");
                        table.AddRow("Resolution", $"{s.Width} x {s.Height}");
                        table.AddRow("Channels", s.Channels.ToString());
                        table.AddRow("Min gray", $"{s.MinGray:F1}");
                        table.AddRow("Mean gray", $"{s.MeanGray:F1}");
                        table.AddRow("Max gray", $"{s.MaxGray:F1}");
                        AnsiConsole.Write(table);
                    }
                    else
                    {
                        AnsiConsole.MarkupLineInterpolated($"[red]Analyze failed[/] — {stats.Message}");
                    }
                    break;

                case CountBlobs:
                    var min = AnsiConsole.Prompt(new TextPrompt<int>("Threshold min (0-255):").DefaultValue(0));
                    var max = AnsiConsole.Prompt(new TextPrompt<int>("Threshold max (0-255):").DefaultValue(128));
                    var blobs = await _engine.Vision.CountBlobsAsync(cameraId, min, max);
                    if (blobs.IsSuccess)
                        AnsiConsole.MarkupLineInterpolated(
                            $"[green]Blobs[/] — count={blobs.Value.BlobCount}  total area={blobs.Value.TotalArea:F0} px");
                    else
                        AnsiConsole.MarkupLineInterpolated($"[red]Count blobs failed[/] — {blobs.Message}");
                    break;

                case Refresh:
                    break;

                case Back:
                    return;
            }
        }
    }

    private async Task SafetyMenuAsync()
    {
        const string WatchedInputs = "Watched inputs";
        const string InterlockRules = "Interlock rules";
        const string ToggleOverride = "Toggle interlock override";
        const string Acknowledge = "Acknowledge safety flag";
        const string Refresh = "Refresh";

        while (true)
        {
            var state = _engine.Safety.GetState();

            var watchText = state.IsWatching ? "[green]watching[/]" : "[red]stopped[/]";
            var flagText = state.FlagRaised
                ? (state.CriticalRaised ? "[red]CRITICAL FLAG RAISED[/]" : "[yellow]FLAG RAISED[/]")
                : "[green]clear[/]";
            var ovrText = state.OverrideMode ? "[yellow]ON[/]" : "[grey]off[/]";

            AnsiConsole.MarkupLine(
                $"\n[aqua]Safety[/] — watchdog: {watchText}  flag: {flagText}  " +
                $"interlocks: {(state.InterlocksEnabled ? "enabled" : "disabled")}  override: {ovrText}");

            var action = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Action:")
                    .AddChoices(WatchedInputs, InterlockRules, ToggleOverride, Acknowledge, Refresh, Back));

            switch (action)
            {
                case WatchedInputs:
                    RenderWatchedInputs();
                    break;

                case InterlockRules:
                    RenderInterlockRules();
                    break;

                case ToggleOverride:
                    var next = !_engine.Safety.OverrideMode;
                    ReportResult(await _engine.Safety.SetOverrideAsync(next),
                        $"Interlock override {(next ? "ON" : "OFF")}");
                    break;

                case Acknowledge:
                    ReportResult(await _engine.Safety.AcknowledgeAsync(), "Acknowledge safety flag");
                    break;

                case Refresh:
                    break;

                case Back:
                    return;
            }
        }
    }

    private void RenderWatchedInputs()
    {
        var inputs = _engine.Safety.WatchedInputs;
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Device");
        table.AddColumn("Pin");
        table.AddColumn("Trigger");
        table.AddColumn("Critical");

        if (inputs.Count == 0)
        {
            table.AddRow("[grey]—[/]", "[grey]no watched inputs[/]", "[grey]—[/]", "[grey]—[/]");
        }
        else
        {
            foreach (var w in inputs)
                table.AddRow(
                    Markup.Escape(w.Device),
                    Markup.Escape(w.Pin),
                    Markup.Escape(w.TriggerEdge),
                    w.IsCritical ? "[red]yes[/]" : "[grey]no[/]");
        }
        AnsiConsole.Write(table);
    }

    private void RenderInterlockRules()
    {
        var rules = _engine.Safety.GetInterlockStatuses();
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Rule");
        table.AddColumn("Enabled");
        table.AddColumn("Status");
        table.AddColumn("Detail");

        if (rules.Count == 0)
        {
            table.AddRow("[grey]—[/]", "[grey]no rules[/]", "[grey]—[/]", "[grey]—[/]");
        }
        else
        {
            foreach (var r in rules)
            {
                var status = !r.Enabled ? "[grey]disabled[/]"
                    : r.Satisfied ? "[green]satisfied[/]" : "[red]BLOCKING[/]";
                table.AddRow(
                    Markup.Escape(r.Name),
                    r.Enabled ? "yes" : "no",
                    status,
                    Markup.Escape(string.IsNullOrEmpty(r.BlockReason) ? r.Target : r.BlockReason));
            }
        }
        AnsiConsole.Write(table);
    }

    /// <summary>
    /// Parallel test: every connected motion device runs an X +1/-1 cycle
    /// concurrently at 0.5 mm/s while positions and all measurement channels
    /// stream to a live display. Demonstrates the engine's cross-device
    /// concurrency. Press any key to stop.
    /// </summary>
    private async Task ParallelTestAsync()
    {
        var devices = _engine.Motion.DeviceNames.Where(_engine.Motion.IsConnected).ToList();
        if (devices.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No connected motion devices.[/]");
            return;
        }

        AnsiConsole.MarkupLine(
            "[yellow]Parallel test: sets each motion device's X axis to 0.5 mm/s and " +
            "cycles X +1 / -1 repeatedly on all devices at once.[/]");
        if (!AnsiConsole.Confirm("Start parallel test? (moves all stages)", defaultValue: false))
            return;

        // Capture current velocity, then set 0.5 mm/s.
        // ACS: per-axis VEL on every installed axis. PI: system velocity (VLS) —
        // a hexapod's path speed is the system velocity, not per-axis VEL.
        var restores = new List<Func<Task>>();
        foreach (var d in devices)
        {
            var info = _engine.Motion.GetDevice(d);
            if (info == null) continue;

            if (info.ControllerType.Equals("PI", StringComparison.OrdinalIgnoreCase))
            {
                var cur = await _engine.Motion.GetSystemVelocityAsync(d);
                if (cur.IsSuccess && cur.Value > 0)
                {
                    var v = cur.Value;
                    restores.Add(() => _engine.Motion.SetSystemVelocityAsync(d, v));
                }
                ReportResult(await _engine.Motion.SetSystemVelocityAsync(d, 0.5),
                    $"Set {d} system velocity = 0.5");
            }
            else
            {
                foreach (var ax in info.Axes)
                {
                    var cur = await _engine.Motion.GetVelocityAsync(d, ax);
                    if (cur.IsSuccess && cur.Value > 0)
                    {
                        var v = cur.Value;
                        var axis = ax;
                        restores.Add(() => _engine.Motion.SetVelocityAsync(d, axis, v));
                    }
                    ReportResult(await _engine.Motion.SetVelocityAsync(d, ax, 0.5),
                        $"Set {d}.{ax} velocity = 0.5");
                }
            }
        }

        var cycles = new int[devices.Count];
        using var cts = new CancellationTokenSource();

        // One concurrent loop task per device — X +1 then X -1, repeat.
        var loops = new List<Task>();
        for (int i = 0; i < devices.Count; i++)
        {
            var d = devices[i];
            var idx = i;
            loops.Add(Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    var up = await _engine.Motion.MoveRelativeAsync(d, "X", 1.0, cts.Token);
                    if (!up.IsSuccess || cts.IsCancellationRequested) break;
                    var down = await _engine.Motion.MoveRelativeAsync(d, "X", -1.0, cts.Token);
                    if (!down.IsSuccess || cts.IsCancellationRequested) break;
                    Interlocked.Increment(ref cycles[idx]);
                }
            }));
        }

        // Live (flicker-free) display until a key is pressed — Spectre updates
        // the anchored region in place instead of clearing the screen.
        var measDevices = _engine.Measurement.DeviceNames;
        await AnsiConsole.Live(BuildParallelDisplay(devices, cycles, measDevices))
            .StartAsync(async ctx =>
            {
                while (!cts.IsCancellationRequested)
                {
                    ctx.UpdateTarget(BuildParallelDisplay(devices, cycles, measDevices));
                    ctx.Refresh();
                    if (Console.KeyAvailable)
                    {
                        Console.ReadKey(intercept: true);
                        cts.Cancel();
                        break;
                    }
                    await Task.Delay(250);
                }
            });

        AnsiConsole.MarkupLine("\n[grey]Stopping — waiting for moves to settle…[/]");
        try { await Task.WhenAll(loops); }
        catch { /* cancelled moves surface as faulted/failed — expected */ }

        foreach (var d in devices)
            await _engine.Motion.StopAsync(d);

        // Restore the velocities captured before the test.
        foreach (var restore in restores)
            await restore();

        AnsiConsole.MarkupLine("[green]Parallel test stopped — velocities restored.[/]");
    }

    /// <summary>Builds the parallel-test live display: header + motion table + measurement table.</summary>
    private Rows BuildParallelDisplay(IReadOnlyList<string> devices, int[] cycles, IReadOnlyList<string> measDevices)
    {
        var blocks = new List<IRenderable>
        {
            new Markup("[aqua]PARALLEL TEST[/] — running.  [grey]Press any key to stop.[/]"),
            new Markup("[aqua]Motion[/]"),
        };

        var motion = new Table().Border(TableBorder.Rounded);
        motion.AddColumn("Device");
        motion.AddColumn("X position");
        motion.AddColumn("Moving");
        motion.AddColumn("Cycles");
        for (int i = 0; i < devices.Count; i++)
        {
            var s = _engine.Motion.GetState(devices[i]);
            motion.AddRow(
                Markup.Escape(devices[i]),
                $"{s.Position.X:F4}",
                s.IsMoving ? "[yellow]yes[/]" : "[grey]no[/]",
                cycles[i].ToString());
        }
        blocks.Add(motion);

        if (measDevices.Count > 0)
        {
            blocks.Add(new Markup("[aqua]Measurement[/]"));
            var meas = new Table().Border(TableBorder.Rounded);
            meas.AddColumn("Device");
            meas.AddColumn("Channels");
            foreach (var m in measDevices)
            {
                var ms = _engine.Measurement.GetState(m);
                var text = ms.Valid && ms.Channels.Count > 0
                    ? string.Join("   ", ms.Channels.Select((v, i) => $"CH{i + 1}={v:E3} {ms.Unit}"))
                    : "no data";
                meas.AddRow(Markup.Escape(m), Markup.Escape(text));
            }
            blocks.Add(meas);
        }

        return new Rows(blocks);
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
