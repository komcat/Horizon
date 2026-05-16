using Horizon.Common;
using Horizon.Config;
using Horizon.Core.Interlock;
using Horizon.CurrentMeasurement;
using Horizon.CurrentMeasurement.Models;
using Horizon.IO;
using Horizon.IO.Models;
using Horizon.LaserController;
using Horizon.LaserController.Models;
using Horizon.Motion;
using Horizon.Motion.Models;
using Horizon.Pneumatic;
using Horizon.Pneumatic.Models;
using Horizon.Safety.Models;
using Spectre.Console;

namespace Horizon.ConfigTool;

/// <summary>
/// Spectre.Console menu over the hardware config files. Each domain offers a
/// read-only view and a preset-driven add: a preset materializes a fully
/// defaulted device (with an auto-unique name), then a field editor lets the
/// user adjust whatever they want before saving.
/// </summary>
internal sealed class ConfigToolApp
{
    private const string Motion = "Motion";
    private const string IO = "IO";
    private const string Pneumatic = "Pneumatic";
    private const string Laser = "Laser";
    private const string Measurement = "Measurement";
    private const string Safety = "Safety";
    private const string Interlocks = "Interlocks";
    private const string Lighting = "Lighting";
    private const string Tec = "TEC";
    private const string Smu = "SMU";
    private const string PowerSupply = "Power supply";
    private const string TcpClients = "TCP clients";
    private const string ChangeDir = "Change config directory";
    private const string Exit = "Exit";
    private const string Save = "Save";
    private const string Cancel = "Cancel";

    private string _configDir;

    public ConfigToolApp(string configDir) => _configDir = configDir;

    public void Run()
    {
        while (true)
        {
            AnsiConsole.MarkupLineInterpolated($"\nConfig directory: [aqua]{_configDir}[/]");
            if (!Directory.Exists(_configDir))
                AnsiConsole.MarkupLine("[red](directory does not exist — it will be created on save)[/]");

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Select a domain:")
                    .AddChoices(Motion, IO, Pneumatic, Laser, Measurement, Safety, Interlocks,
                                Lighting, Tec, Smu, PowerSupply, TcpClients, ChangeDir, Exit));

            switch (choice)
            {
                case Motion:      DomainMenu(Motion, "motor_devices.json", ViewMotion, AddMotion); break;
                case IO:          DomainMenu(IO, "io_devices.json", ViewIO, AddIO); break;
                case Pneumatic:   DomainMenu(Pneumatic, "pneumatic_slides.json", ViewPneumatic, AddPneumatic); break;
                case Laser:       DomainMenu(Laser, "laser_controller_devices.json", ViewLaser, AddLaser); break;
                case Measurement: DomainMenu(Measurement, "current_measurement_devices.json", ViewMeasurement, AddMeasurement); break;
                case Safety:      DomainMenu(Safety, "safety_watch.json", ViewSafety, AddSafety); break;
                case Interlocks:  DomainMenu(Interlocks, "motion_interlocks.json", ViewInterlocks, AddInterlock); break;
                case Lighting:    DomainMenu(Lighting, "lighting_config.json", ViewLighting, AddLighting); break;
                case Tec:         DomainMenu(Tec, "tec_devices.json", ViewTec, AddTec); break;
                case Smu:         DomainMenu(Smu, "smu_devices.json", ViewSmu, AddSmu); break;
                case PowerSupply: DomainMenu(PowerSupply, "power_supply_config.json", ViewPowerSupply, AddPowerSupply); break;
                case TcpClients:  DomainMenu(TcpClients, "tcp_clients.json", ViewTcp, AddTcp); break;
                case ChangeDir:   _configDir = AnsiConsole.Prompt(
                                      new TextPrompt<string>("New config directory:").DefaultValue(_configDir)); break;
                case Exit:        return;
            }
        }
    }

    /// <summary>A domain sub-menu: view current entries, or add one from a preset.</summary>
    private void DomainMenu(string title, string file, Action view, Action add)
    {
        while (true)
        {
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title($"\n[aqua]{title}[/] — {file}")
                    .AddChoices("View", "Add (from preset)", "Back"));

            if (choice == "View") view();
            else if (choice == "Back") return;
            else add();
        }
    }

    // --- viewers ---

    private void ViewMotion()
    {
        var devices = MotionJsonLoader.Load(_configDir);
        if (devices.Count == 0) { Empty("motor_devices.json"); return; }

        var table = NewTable("Motion devices",
            "Name", "Id", "Controller", "IP", "Port", "Axes", "Positions", "Enabled");
        foreach (var d in devices)
            table.AddRow(Markup.Escape(d.Name), d.Id.ToString(), Markup.Escape(d.ControllerType),
                Markup.Escape(d.IpAddress), d.Port.ToString(),
                Markup.Escape(string.Join(",", d.InstalledAxes)),
                d.Positions.Count.ToString(), YesNo(d.Enabled));
        AnsiConsole.Write(table);
    }

    private void ViewIO()
    {
        var devices = EziIOJsonLoader.Load(_configDir);
        if (devices.Count == 0) { Empty("io_devices.json"); return; }

        var table = NewTable("IO devices",
            "Name", "Id", "IP", "Type", "In", "Out", "Named pins", "Enabled");
        foreach (var d in devices)
            table.AddRow(Markup.Escape(d.Device.Name), d.Device.DeviceId.ToString(),
                Markup.Escape(d.Device.IpAddress), d.Device.DeviceType.ToString(),
                d.Device.InputCount.ToString(), d.Device.OutputCount.ToString(),
                $"{d.InputPins.Count} in / {d.OutputPins.Count} out", YesNo(d.Device.Enabled));
        AnsiConsole.Write(table);
    }

    private void ViewPneumatic()
    {
        var slides = PneumaticJsonLoader.Load(_configDir);
        if (slides.Count == 0) { Empty("pneumatic_slides.json"); return; }

        var table = NewTable("Pneumatic slides",
            "Name", "Output", "Extended sensor", "Retracted sensor", "Timeout ms");
        foreach (var s in slides)
            table.AddRow(Markup.Escape(s.Name), Pin(s.Output), Pin(s.ExtendedSensor),
                Pin(s.RetractedSensor), s.TimeoutMs.ToString());
        AnsiConsole.Write(table);
    }

    private void ViewLaser()
    {
        var devices = LaserControllerJsonLoader.Load(_configDir);
        if (devices.Count == 0) { Empty("laser_controller_devices.json"); return; }

        var table = NewTable("Laser controllers",
            "Name", "Type", "VISA resource", "Enabled", "Default temp", "Default current");
        foreach (var d in devices)
            table.AddRow(Markup.Escape(d.Name), Markup.Escape(d.ControllerType),
                Markup.Escape(d.VisaResource), YesNo(d.Enabled),
                $"{d.DefaultTemperature} C", $"{d.DefaultLaserCurrent} mA");
        AnsiConsole.Write(table);
    }

    private void ViewMeasurement()
    {
        var devices = CurrentMeasurementJsonLoader.Load(_configDir);
        if (devices.Count == 0) { Empty("current_measurement_devices.json"); return; }

        var table = NewTable("Current-measurement devices",
            "Name", "Type", "VISA resource", "Enabled", "Channels", "Enabled channels");
        foreach (var d in devices)
            table.AddRow(Markup.Escape(d.Name), Markup.Escape(d.Type), Markup.Escape(d.VisaResource),
                YesNo(d.Enabled), d.ChannelCount.ToString(),
                Markup.Escape(string.Join(",", d.EnabledChannels)));
        AnsiConsole.Write(table);
    }

    private void ViewSafety()
    {
        var config = ConfigLoader.LoadJson<SafetyWatchConfig>(_configDir, "safety_watch");
        if (config == null) { Empty("safety_watch.json"); return; }

        AnsiConsole.MarkupLineInterpolated($"Poll interval: [aqua]{config.PollIntervalMs} ms[/]");
        var table = NewTable("Watched inputs",
            "Device", "Pin", "Trigger", "Critical", "Enabled", "Alert title");
        if (config.WatchedInputs.Count == 0)
            table.AddRow("[grey]none[/]", "", "", "", "", "");
        else
            foreach (var w in config.WatchedInputs)
                table.AddRow(Markup.Escape(w.DeviceName), Markup.Escape(w.PinName), w.Trigger.ToString(),
                    YesNo(w.IsCritical), YesNo(w.Enabled), Markup.Escape(w.AlertTitle));
        AnsiConsole.Write(table);
    }

    private void ViewInterlocks()
    {
        var config = ConfigLoader.LoadJson<InterlockConfig>(_configDir, "motion_interlocks");
        if (config == null) { Empty("motion_interlocks.json"); return; }

        AnsiConsole.MarkupLineInterpolated($"Interlocks enabled: [aqua]{config.Enabled}[/]");
        var table = NewTable("Interlock rules",
            "Name", "Enabled", "Target devices", "Axis block", "Layer", "Conditions");
        if (config.Rules.Count == 0)
            table.AddRow("[grey]none[/]", "", "", "", "", "");
        else
            foreach (var r in config.Rules)
                table.AddRow(Markup.Escape(r.Name), YesNo(r.Enabled),
                    Markup.Escape(string.Join(",", r.TargetDevices)),
                    r.AxisBlock.ToString(), r.Layer.ToString(), r.Conditions.Count.ToString());
        AnsiConsole.Write(table);
    }

    private void ViewLighting()
    {
        var controllers = LightingJsonLoader.Load(_configDir);
        if (controllers.Count == 0) { Empty("lighting_config.json"); return; }

        var table = NewTable("Lighting controllers",
            "Device id", "Name", "Type", "IP", "Port", "Channels", "Enabled");
        foreach (var c in controllers)
            table.AddRow(Markup.Escape(c.DeviceId), Markup.Escape(c.DeviceName), Markup.Escape(c.ControllerType),
                Markup.Escape(c.IpAddress), c.Port.ToString(), c.Channels.Count.ToString(), YesNo(c.Enabled));
        AnsiConsole.Write(table);
    }

    private void ViewTec()
    {
        var devices = TecJsonLoader.Load(_configDir);
        if (devices.Count == 0) { Empty("tec_devices.json"); return; }

        var table = NewTable("TEC / laser devices",
            "Name", "Controller", "VISA / host", "Enabled", "Default temp", "Default current");
        foreach (var d in devices)
        {
            var conn = !string.IsNullOrEmpty(d.VisaResource) ? d.VisaResource
                     : !string.IsNullOrEmpty(d.IpAddress) ? $"{d.IpAddress}:{d.Port}"
                     : "—";
            table.AddRow(Markup.Escape(d.Name), Markup.Escape(d.ControllerType), Markup.Escape(conn),
                YesNo(d.Enabled), $"{d.DefaultTemperature} C", $"{d.DefaultLaserCurrent} mA");
        }
        AnsiConsole.Write(table);
    }

    private void ViewSmu()
    {
        var devices = SmuJsonLoader.Load(_configDir);
        if (devices.Count == 0) { Empty("smu_devices.json"); return; }

        var table = NewTable("SMU devices",
            "Name", "Type", "VISA resource", "Enabled", "Channels", "NPLC", "Auto range");
        foreach (var d in devices)
            table.AddRow(Markup.Escape(d.Name), Markup.Escape(d.Type), Markup.Escape(d.VisaResource),
                YesNo(d.Enabled), d.ChannelCount.ToString(), d.MeasurementNplc.ToString(), YesNo(d.AutoRange));
        AnsiConsole.Write(table);
    }

    private void ViewPowerSupply()
    {
        var devices = PowerSupplyJsonLoader.Load(_configDir);
        if (devices.Count == 0) { Empty("power_supply_config.json"); return; }

        var table = NewTable("Power supplies", "Name", "Type", "IP", "Port", "Channels");
        foreach (var d in devices)
            table.AddRow(Markup.Escape(d.Name), Markup.Escape(d.Type), Markup.Escape(d.Ip),
                d.Port.ToString(), d.Channels.ToString());
        AnsiConsole.Write(table);
    }

    private void ViewTcp()
    {
        var clients = TcpClientJsonLoader.Load(_configDir);
        if (clients.Count == 0) { Empty("tcp_clients.json"); return; }

        var table = NewTable("TCP clients", "Id", "Name", "Host", "Port", "Protocol", "Channels");
        foreach (var c in clients)
        {
            var channels = c.ProtocolType == "multi" ? string.Join(",", c.DataChannels) : c.DataChannel;
            table.AddRow(Markup.Escape(c.Id), Markup.Escape(c.Name), Markup.Escape(c.Host),
                c.Port.ToString(), Markup.Escape(c.ProtocolType), Markup.Escape(channels));
        }
        AnsiConsole.Write(table);
    }

    // --- add flows: materialize from preset → field editor → save ---

    private void AddMotion()
    {
        var preset = PickPreset("ACS gantry (SPiiPlus)", "PI hexapod (GCS2)");
        if (preset == null) return;

        var devices = MotionJsonLoader.Load(_configDir).ToList();
        var others = devices.Select(d => d.Name).ToList();
        var isPi = preset.StartsWith("PI");
        var device = new MotionDevice
        {
            Id = devices.Count == 0 ? 0 : devices.Max(d => d.Id) + 1,
            Name = UniqueName(isPi ? "hex" : "gantry", others),
            IpAddress = isPi ? "192.168.0.10" : "192.168.0.50",
            Port = isPi ? 50000 : 701,
            ControllerType = isPi ? "PI" : "ACS",
            InstalledAxes = isPi
                ? new List<string> { "X", "Y", "Z", "U", "V", "W" }
                : new List<string> { "X", "Y", "Z" },
            Enabled = true,
        };

        var result = RunEditor("Motion device", device, new List<FieldSpec<MotionDevice>>
        {
            new("Name", d => d.Name, d => d with { Name = Ask("Name", d.Name) }),
            new("IP address", d => d.IpAddress, d => d with { IpAddress = Ask("IP address", d.IpAddress) }),
            new("Port", d => d.Port.ToString(), d => d with { Port = AskInt("Port", d.Port) }),
            new("Controller", d => d.ControllerType, d => d with { ControllerType = Ask("Controller", d.ControllerType) }),
            new("Axes", d => string.Join(",", d.InstalledAxes), d => d with { InstalledAxes = AskList("Axes", d.InstalledAxes) }),
            new("Enabled", d => YesNo(d.Enabled), d => d with { Enabled = AnsiConsole.Confirm("Enabled?", d.Enabled) }),
        }, d => NameError(d.Name, others));
        if (result == null) return;

        devices.Add(result);
        MotionJsonLoader.Save(devices, _configDir);
        Saved(result.Name, "motor_devices.json", devices.Count);
    }

    private void AddIO()
    {
        var preset = PickPreset("Fastech Ezi-IO — 16 in / 16 out", "Fastech Ezi-IO — 8 in / 8 out");
        if (preset == null) return;

        var devices = EziIOJsonLoader.Load(_configDir).ToList();
        var others = devices.Select(d => d.Device.Name).ToList();
        var is16 = preset.Contains("16");
        var device = new IODeviceConfig
        {
            Device = new EziIODeviceConfig
            {
                DeviceId = devices.Count == 0 ? 0 : devices.Max(d => d.Device.DeviceId) + 1,
                Name = UniqueName("io", others),
                IpAddress = "192.168.0.3",
                InputCount = is16 ? 16 : 8,
                OutputCount = is16 ? 16 : 8,
                Enabled = true,
                DeviceType = EziDeviceType.IOModule,
            },
            InputPins = new(),
            OutputPins = new(),
        };

        var result = RunEditor("IO device", device, new List<FieldSpec<IODeviceConfig>>
        {
            new("Name", d => d.Device.Name, d => d with { Device = d.Device with { Name = Ask("Name", d.Device.Name) } }),
            new("IP address", d => d.Device.IpAddress, d => d with { Device = d.Device with { IpAddress = Ask("IP address", d.Device.IpAddress) } }),
            new("Input count", d => d.Device.InputCount.ToString(), d => d with { Device = d.Device with { InputCount = AskInt("Input count", d.Device.InputCount) } }),
            new("Output count", d => d.Device.OutputCount.ToString(), d => d with { Device = d.Device with { OutputCount = AskInt("Output count", d.Device.OutputCount) } }),
            new("Type", d => d.Device.DeviceType.ToString(), d => d with { Device = d.Device with { DeviceType = AskEnum("Type", d.Device.DeviceType) } }),
            new("Enabled", d => YesNo(d.Device.Enabled), d => d with { Device = d.Device with { Enabled = AnsiConsole.Confirm("Enabled?", d.Device.Enabled) } }),
        }, d => NameError(d.Device.Name, others));
        if (result == null) return;

        devices.Add(result);
        EziIOJsonLoader.Save(devices, _configDir);
        AnsiConsole.MarkupLine("[grey]Pin names can be filled in once edit lands (Phase 3).[/]");
        Saved(result.Device.Name, "io_devices.json", devices.Count);
    }

    private void AddPneumatic()
    {
        if (PickPreset("Pneumatic slide (output + 2 sensors)") == null) return;

        var slides = PneumaticJsonLoader.Load(_configDir).ToList();
        var others = slides.Select(s => s.Name).ToList();
        var slide = new PneumaticConfig
        {
            Name = UniqueName("slide", others),
            Output = new IOPinReference { DeviceName = "IOBottom", PinName = "" },
            ExtendedSensor = new IOPinReference { DeviceName = "IOTop", PinName = "" },
            RetractedSensor = new IOPinReference { DeviceName = "IOTop", PinName = "" },
            TimeoutMs = 5000,
        };

        var result = RunEditor("Pneumatic slide", slide, new List<FieldSpec<PneumaticConfig>>
        {
            new("Name", s => s.Name, s => s with { Name = Ask("Name", s.Name) }),
            new("Output device", s => s.Output.DeviceName, s => s with { Output = s.Output with { DeviceName = Ask("Output device", s.Output.DeviceName) } }),
            new("Output pin", s => s.Output.PinName, s => s with { Output = s.Output with { PinName = Ask("Output pin", s.Output.PinName) } }),
            new("Extended sensor device", s => s.ExtendedSensor.DeviceName, s => s with { ExtendedSensor = s.ExtendedSensor with { DeviceName = Ask("Extended sensor device", s.ExtendedSensor.DeviceName) } }),
            new("Extended sensor pin", s => s.ExtendedSensor.PinName, s => s with { ExtendedSensor = s.ExtendedSensor with { PinName = Ask("Extended sensor pin", s.ExtendedSensor.PinName) } }),
            new("Retracted sensor device", s => s.RetractedSensor.DeviceName, s => s with { RetractedSensor = s.RetractedSensor with { DeviceName = Ask("Retracted sensor device", s.RetractedSensor.DeviceName) } }),
            new("Retracted sensor pin", s => s.RetractedSensor.PinName, s => s with { RetractedSensor = s.RetractedSensor with { PinName = Ask("Retracted sensor pin", s.RetractedSensor.PinName) } }),
            new("Timeout ms", s => s.TimeoutMs.ToString(), s => s with { TimeoutMs = AskInt("Timeout ms", s.TimeoutMs) }),
        }, s => NameError(s.Name, others));
        if (result == null) return;

        slides.Add(result);
        PneumaticJsonLoader.Save(slides, _configDir);
        Saved(result.Name, "pneumatic_slides.json", slides.Count);
    }

    private void AddLaser()
    {
        if (PickPreset("Thorlabs CLD101x") == null) return;

        var devices = LaserControllerJsonLoader.Load(_configDir).ToList();
        var others = devices.Select(d => d.Name).ToList();
        var device = new LaserControllerDeviceConfig
        {
            Name = UniqueName("laser", others),
            ControllerType = "cld101x_direct",
            VisaResource = "USB0::0x1313::0x804F::INSTR",
            Enabled = true,
            DefaultTemperature = 25.0,
            DefaultLaserCurrent = 50.0,
        };

        var result = RunEditor("Laser controller", device, new List<FieldSpec<LaserControllerDeviceConfig>>
        {
            new("Name", d => d.Name, d => d with { Name = Ask("Name", d.Name) }),
            new("Controller type", d => d.ControllerType, d => d with { ControllerType = Ask("Controller type", d.ControllerType) }),
            new("VISA resource", d => d.VisaResource, d => d with { VisaResource = Ask("VISA resource", d.VisaResource) }),
            new("Enabled", d => YesNo(d.Enabled), d => d with { Enabled = AnsiConsole.Confirm("Enabled?", d.Enabled) }),
            new("Default temperature", d => $"{d.DefaultTemperature} C", d => d with { DefaultTemperature = AskDouble("Default temperature", d.DefaultTemperature) }),
            new("Default laser current", d => $"{d.DefaultLaserCurrent} mA", d => d with { DefaultLaserCurrent = AskDouble("Default laser current", d.DefaultLaserCurrent) }),
        }, d => NameError(d.Name, others));
        if (result == null) return;

        devices.Add(result);
        LaserControllerJsonLoader.Save(devices, _configDir);
        Saved(result.Name, "laser_controller_devices.json", devices.Count);
    }

    private void AddMeasurement()
    {
        if (PickPreset("Keithley 6482 — dual-channel picoammeter") == null) return;

        var devices = CurrentMeasurementJsonLoader.Load(_configDir).ToList();
        var others = devices.Select(d => d.Name).ToList();
        var device = new CurrentMeasurementDeviceConfig
        {
            Name = UniqueName("keithley", others),
            Type = "keithley_6482",
            VisaResource = "GPIB0::1::INSTR",
            Enabled = true,
            ChannelCount = 2,
            EnabledChannels = new List<int> { 1, 2 },
        };

        var result = RunEditor("Measurement device", device, new List<FieldSpec<CurrentMeasurementDeviceConfig>>
        {
            new("Name", d => d.Name, d => d with { Name = Ask("Name", d.Name) }),
            new("Type", d => d.Type, d => d with { Type = Ask("Type", d.Type) }),
            new("VISA resource", d => d.VisaResource, d => d with { VisaResource = Ask("VISA resource", d.VisaResource) }),
            new("Enabled", d => YesNo(d.Enabled), d => d with { Enabled = AnsiConsole.Confirm("Enabled?", d.Enabled) }),
            new("Channel count", d => d.ChannelCount.ToString(), d => d with { ChannelCount = AskInt("Channel count", d.ChannelCount) }),
            new("Enabled channels", d => string.Join(",", d.EnabledChannels), d => d with { EnabledChannels = AskIntList("Enabled channels", d.EnabledChannels) }),
        }, d => NameError(d.Name, others));
        if (result == null) return;

        devices.Add(result);
        CurrentMeasurementJsonLoader.Save(devices, _configDir);
        Saved(result.Name, "current_measurement_devices.json", devices.Count);
    }

    private void AddSafety()
    {
        if (PickPreset("Watched input (E-stop / door / sensor)") == null) return;

        var config = ConfigLoader.LoadJson<SafetyWatchConfig>(_configDir, "safety_watch") ?? new SafetyWatchConfig();
        var input = new WatchedInput
        {
            DeviceName = "IOTop",
            PinName = "Input0",
            Trigger = TriggerEdge.Rising,
            IsCritical = true,
            Enabled = true,
            AlertTitle = "Safety input triggered",
        };

        var result = RunEditor("Watched input", input, new List<FieldSpec<WatchedInput>>
        {
            new("Device", w => w.DeviceName, w => w with { DeviceName = Ask("Device", w.DeviceName) }),
            new("Pin", w => w.PinName, w => w with { PinName = Ask("Pin", w.PinName) }),
            new("Trigger", w => w.Trigger.ToString(), w => w with { Trigger = AskEnum("Trigger", w.Trigger) }),
            new("Critical", w => YesNo(w.IsCritical), w => w with { IsCritical = AnsiConsole.Confirm("Critical?", w.IsCritical) }),
            new("Enabled", w => YesNo(w.Enabled), w => w with { Enabled = AnsiConsole.Confirm("Enabled?", w.Enabled) }),
            new("Alert title", w => w.AlertTitle, w => w with { AlertTitle = Ask("Alert title", w.AlertTitle) }),
        }, w =>
            config.WatchedInputs.Any(x =>
                string.Equals(x.DeviceName, w.DeviceName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.PinName, w.PinName, StringComparison.OrdinalIgnoreCase))
                ? $"{w.DeviceName}.{w.PinName} is already watched."
                : string.IsNullOrWhiteSpace(w.PinName) ? "Pin is required." : null);
        if (result == null) return;

        var inputs = config.WatchedInputs.ToList();
        inputs.Add(result);
        ConfigLoader.SaveJson(_configDir, "safety_watch", config with { WatchedInputs = inputs });
        Saved($"{result.DeviceName}.{result.PinName}", "safety_watch.json", inputs.Count);
    }

    private void AddInterlock()
    {
        if (PickPreset("Slide-retracted-before-motion rule") == null) return;

        var config = ConfigLoader.LoadJson<InterlockConfig>(_configDir, "motion_interlocks") ?? new InterlockConfig();
        var others = config.Rules.Select(r => r.Name).ToList();
        var rule = new InterlockRule
        {
            Name = UniqueName("interlock", others),
            Enabled = true,
            TargetDevices = new List<string> { "gantry-main" },
            AxisBlock = AxisBlock.All,
            Layer = EnforcementLayer.ScriptOnly,
            Conditions = new List<InterlockCondition>
            {
                new() { Type = "allRetracted", Slides = new List<string>() },
            },
        };

        var result = RunEditor("Interlock rule", rule, new List<FieldSpec<InterlockRule>>
        {
            new("Name", r => r.Name, r => r with { Name = Ask("Name", r.Name) }),
            new("Target devices", r => string.Join(",", r.TargetDevices), r => r with { TargetDevices = AskList("Target devices", r.TargetDevices) }),
            new("Axis block", r => r.AxisBlock.ToString(), r => r with { AxisBlock = AskEnum("Axis block", r.AxisBlock) }),
            new("Layer", r => r.Layer.ToString(), r => r with { Layer = AskEnum("Layer", r.Layer) }),
            new("Retracted slides", r => string.Join(",", FirstSlides(r)), r => r with
            {
                Conditions = new List<InterlockCondition>
                {
                    new() { Type = "allRetracted", Slides = AskList("Retracted slides", FirstSlides(r)) },
                },
            }),
        }, r => NameError(r.Name, others));
        if (result == null) return;

        var rules = config.Rules.ToList();
        rules.Add(result);
        ConfigLoader.SaveJson(_configDir, "motion_interlocks", config with { Rules = rules });
        Saved(result.Name, "motion_interlocks.json", rules.Count);
    }

    private void AddLighting()
    {
        if (PickPreset("CCS_PD3 LED controller") == null) return;

        var controllers = LightingJsonLoader.Load(_configDir).ToList();
        var others = controllers.Select(c => c.DeviceId).ToList();
        var controller = new LightingControllerConfig
        {
            DeviceId = UniqueName("led-controller", others),
            DeviceName = "LED Controller",
            ControllerType = "CCS_PD3",
            IpAddress = "192.168.0.2",
            Port = 40001,
            Enabled = true,
            Channels = new(),
        };

        var result = RunEditor("Lighting controller", controller, new List<FieldSpec<LightingControllerConfig>>
        {
            new("Device id", c => c.DeviceId, c => c with { DeviceId = Ask("Device id", c.DeviceId) }),
            new("Display name", c => c.DeviceName, c => c with { DeviceName = Ask("Display name", c.DeviceName) }),
            new("Controller type", c => c.ControllerType, c => c with { ControllerType = Ask("Controller type", c.ControllerType) }),
            new("IP address", c => c.IpAddress, c => c with { IpAddress = Ask("IP address", c.IpAddress) }),
            new("Port", c => c.Port.ToString(), c => c with { Port = AskInt("Port", c.Port) }),
            new("Enabled", c => YesNo(c.Enabled), c => c with { Enabled = AnsiConsole.Confirm("Enabled?", c.Enabled) }),
        }, c => NameError(c.DeviceId, others));
        if (result == null) return;

        controllers.Add(result);
        LightingJsonLoader.Save(controllers, _configDir);
        AnsiConsole.MarkupLine("[grey]Channels can be filled in once edit lands (Phase 3).[/]");
        Saved(result.DeviceId, "lighting_config.json", controllers.Count);
    }

    private void AddTec()
    {
        var preset = PickPreset("Thorlabs CLD101x (laser + TEC)", "Thorlabs TED4015 (TEC only)");
        if (preset == null) return;

        var devices = TecJsonLoader.Load(_configDir).ToList();
        var others = devices.Select(d => d.Name).ToList();
        var isTed = preset.Contains("TED4015");
        var device = new TecDeviceConfig
        {
            Name = UniqueName("tec", others),
            ControllerType = isTed ? "TED4015_Direct" : "CLD101x_Direct",
            VisaResource = isTed ? "USB0::0x1313::0x8048::INSTR" : "USB0::0x1313::0x804F::INSTR",
            Enabled = true,
            DefaultTemperature = 25.0,
            DefaultLaserCurrent = isTed ? 0.0 : 50.0,
        };

        var result = RunEditor("TEC device", device, new List<FieldSpec<TecDeviceConfig>>
        {
            new("Name", d => d.Name, d => d with { Name = Ask("Name", d.Name) }),
            new("Controller type", d => d.ControllerType, d => d with { ControllerType = Ask("Controller type", d.ControllerType) }),
            new("VISA resource", d => d.VisaResource, d => d with { VisaResource = Ask("VISA resource", d.VisaResource) }),
            new("IP address", d => d.IpAddress, d => d with { IpAddress = Ask("IP address", d.IpAddress) }),
            new("Port", d => d.Port.ToString(), d => d with { Port = AskInt("Port", d.Port) }),
            new("Enabled", d => YesNo(d.Enabled), d => d with { Enabled = AnsiConsole.Confirm("Enabled?", d.Enabled) }),
            new("Default temperature", d => $"{d.DefaultTemperature} C", d => d with { DefaultTemperature = AskDouble("Default temperature", d.DefaultTemperature) }),
            new("Default laser current", d => $"{d.DefaultLaserCurrent} mA", d => d with { DefaultLaserCurrent = AskDouble("Default laser current", d.DefaultLaserCurrent) }),
        }, d => NameError(d.Name, others));
        if (result == null) return;

        devices.Add(result);
        TecJsonLoader.Save(devices, _configDir);
        Saved(result.Name, "tec_devices.json", devices.Count);
    }

    private void AddSmu()
    {
        var preset = PickPreset("Keithley 6482 — dual picoammeter", "Keithley 2400 — SourceMeter");
        if (preset == null) return;

        var devices = SmuJsonLoader.Load(_configDir).ToList();
        var others = devices.Select(d => d.Name).ToList();
        var is2400 = preset.Contains("2400");
        var device = new SmuDeviceConfig
        {
            Name = UniqueName("keithley", others),
            Type = is2400 ? "keithley_2400" : "keithley_6482",
            VisaResource = is2400 ? "GPIB1::24::INSTR" : "GPIB0::1::INSTR",
            Enabled = true,
            ChannelCount = is2400 ? 1 : 2,
            EnabledChannels = is2400 ? new List<int> { 1 } : new List<int> { 1, 2 },
            MeasurementNplc = is2400 ? 1.0 : 5.0,
            AutoRange = true,
            DefaultCompliance = is2400 ? 0.001 : 0.0,
        };

        var result = RunEditor("SMU device", device, new List<FieldSpec<SmuDeviceConfig>>
        {
            new("Name", d => d.Name, d => d with { Name = Ask("Name", d.Name) }),
            new("Type", d => d.Type, d => d with { Type = Ask("Type", d.Type) }),
            new("VISA resource", d => d.VisaResource, d => d with { VisaResource = Ask("VISA resource", d.VisaResource) }),
            new("Enabled", d => YesNo(d.Enabled), d => d with { Enabled = AnsiConsole.Confirm("Enabled?", d.Enabled) }),
            new("Channel count", d => d.ChannelCount.ToString(), d => d with { ChannelCount = AskInt("Channel count", d.ChannelCount) }),
            new("Enabled channels", d => string.Join(",", d.EnabledChannels), d => d with { EnabledChannels = AskIntList("Enabled channels", d.EnabledChannels) }),
            new("Measurement NPLC", d => d.MeasurementNplc.ToString(), d => d with { MeasurementNplc = AskDouble("Measurement NPLC", d.MeasurementNplc) }),
            new("Auto range", d => YesNo(d.AutoRange), d => d with { AutoRange = AnsiConsole.Confirm("Auto range?", d.AutoRange) }),
            new("Fixed range", d => d.FixedRange.ToString(), d => d with { FixedRange = AskDouble("Fixed range", d.FixedRange) }),
            new("Default compliance", d => d.DefaultCompliance.ToString(), d => d with { DefaultCompliance = AskDouble("Default compliance", d.DefaultCompliance) }),
        }, d => NameError(d.Name, others));
        if (result == null) return;

        devices.Add(result);
        SmuJsonLoader.Save(devices, _configDir);
        Saved(result.Name, "smu_devices.json", devices.Count);
    }

    private void AddPowerSupply()
    {
        var preset = PickPreset("Keysight E36441A — 4-channel", "Keysight E36103B — single-channel");
        if (preset == null) return;

        var devices = PowerSupplyJsonLoader.Load(_configDir).ToList();
        var others = devices.Select(d => d.Name).ToList();
        var quad = preset.Contains("E36441A");
        var device = new PowerSupplyConfig
        {
            Name = UniqueName("psu", others),
            Type = quad ? "keysight_e36441a_direct" : "keysight_e36103b_direct",
            Ip = quad ? "192.168.0.32" : "192.168.0.33",
            Port = 5025,
            Channels = quad ? 4 : 1,
        };

        var result = RunEditor("Power supply", device, new List<FieldSpec<PowerSupplyConfig>>
        {
            new("Name", d => d.Name, d => d with { Name = Ask("Name", d.Name) }),
            new("Type", d => d.Type, d => d with { Type = Ask("Type", d.Type) }),
            new("IP address", d => d.Ip, d => d with { Ip = Ask("IP address", d.Ip) }),
            new("Port", d => d.Port.ToString(), d => d with { Port = AskInt("Port", d.Port) }),
            new("Channels", d => d.Channels.ToString(), d => d with { Channels = AskInt("Channels", d.Channels) }),
        }, d => NameError(d.Name, others));
        if (result == null) return;

        devices.Add(result);
        PowerSupplyJsonLoader.Save(devices, _configDir);
        Saved(result.Name, "power_supply_config.json", devices.Count);
    }

    private void AddTcp()
    {
        var preset = PickPreset("Single-channel client", "Multi-channel client");
        if (preset == null) return;

        var clients = TcpClientJsonLoader.Load(_configDir).ToList();
        var others = clients.Select(c => c.Id).ToList();
        var isMulti = preset.StartsWith("Multi");
        var client = new TcpClientConfig
        {
            Id = UniqueName("tcp-client", others),
            Name = "TCP Client",
            Host = "127.0.0.1",
            Port = isMulti ? 65432 : 8888,
            ProtocolType = isMulti ? "multi" : "single",
            AutoConnect = isMulti,
            AutoReconnect = isMulti,
        };

        var result = RunEditor("TCP client", client, new List<FieldSpec<TcpClientConfig>>
        {
            new("Id", c => c.Id, c => c with { Id = Ask("Id", c.Id) }),
            new("Name", c => c.Name, c => c with { Name = Ask("Name", c.Name) }),
            new("Host", c => c.Host, c => c with { Host = Ask("Host", c.Host) }),
            new("Port", c => c.Port.ToString(), c => c with { Port = AskInt("Port", c.Port) }),
            new("Protocol", c => c.ProtocolType, c => c with { ProtocolType = Ask("Protocol (single/multi)", c.ProtocolType) }),
            new("Data channel", c => c.DataChannel, c => c with { DataChannel = Ask("Data channel", c.DataChannel) }),
            new("Data channels", c => string.Join(",", c.DataChannels), c => c with { DataChannels = AskList("Data channels", c.DataChannels) }),
            new("Auto connect", c => YesNo(c.AutoConnect), c => c with { AutoConnect = AnsiConsole.Confirm("Auto connect?", c.AutoConnect) }),
            new("Auto reconnect", c => YesNo(c.AutoReconnect), c => c with { AutoReconnect = AnsiConsole.Confirm("Auto reconnect?", c.AutoReconnect) }),
        }, c => NameError(c.Id, others));
        if (result == null) return;

        clients.Add(result);
        TcpClientJsonLoader.Save(clients, _configDir);
        Saved(result.Id, "tcp_clients.json", clients.Count);
    }

    // --- generic field editor ---

    /// <summary>One editable field: how to show it, and how to edit it.</summary>
    private sealed record FieldSpec<T>(string Name, Func<T, string> Show, Func<T, T> Edit);

    /// <summary>
    /// Shows an entry's whole config, lets the user edit any field, then Save
    /// (validated) or Cancel. Returns the edited entry, or null if cancelled.
    /// </summary>
    private static T? RunEditor<T>(string title, T item, List<FieldSpec<T>> fields, Func<T, string?> validate)
        where T : class
    {
        while (true)
        {
            ShowConfig(title, fields.Select(f => (f.Name, f.Show(item))).ToArray());
            var choice = PickField(fields.Select(f => f.Name).ToArray());

            if (choice == Cancel) return null;
            if (choice == Save)
            {
                var error = validate(item);
                if (error == null) return item;
                AnsiConsole.MarkupLineInterpolated($"[red]{error}[/]");
                continue;
            }
            item = fields.First(f => f.Name == choice).Edit(item);
        }
    }

    private static List<string> FirstSlides(InterlockRule rule)
        => rule.Conditions.FirstOrDefault()?.Slides ?? new List<string>();

    private static string? NameError(string name, IEnumerable<string> others)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Name is required.";
        if (others.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
            return $"'{name}' already exists.";
        return null;
    }

    // --- helpers ---

    private static Table NewTable(string title, params string[] columns)
    {
        var table = new Table().Border(TableBorder.Rounded).Title($"[aqua]{title}[/]");
        foreach (var c in columns)
            table.AddColumn(c);
        return table;
    }

    private static void ShowConfig(string title, params (string Field, string Value)[] fields)
    {
        var table = new Table().Border(TableBorder.Rounded).Title($"[aqua]{title}[/]");
        table.AddColumn("Field");
        table.AddColumn("Value");
        foreach (var (field, value) in fields)
            table.AddRow(Markup.Escape(field), value.Length == 0 ? "[grey]—[/]" : Markup.Escape(value));
        AnsiConsole.Write(table);
    }

    private static void Empty(string file)
        => AnsiConsole.MarkupLineInterpolated($"[yellow]No {file} (or no entries) in this directory.[/]");

    private static string YesNo(bool value) => value ? "yes" : "no";

    private static string Pin(IOPinReference reference)
        => string.IsNullOrEmpty(reference.PinName)
            ? "[grey]—[/]"
            : Markup.Escape($"{reference.DeviceName}.{reference.PinName}");

    private static string? PickPreset(params string[] presets)
    {
        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>().Title("Select a preset:").AddChoices(presets.Append("Back")));
        return choice == "Back" ? null : choice;
    }

    private static string PickField(params string[] fields)
        => AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Edit a field, or:")
                .AddChoices(fields.Append(Save).Append(Cancel)));

    private static string UniqueName(string baseName, IEnumerable<string> existing)
    {
        var taken = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(baseName)) return baseName;
        for (int i = 2; ; i++)
            if (!taken.Contains($"{baseName}-{i}")) return $"{baseName}-{i}";
    }

    private static string Ask(string label)
        => AnsiConsole.Prompt(new TextPrompt<string>($"{label}:")).Trim();

    private static string Ask(string label, string defaultValue)
        => AnsiConsole.Prompt(new TextPrompt<string>($"{label}:").DefaultValue(defaultValue)).Trim();

    private static int AskInt(string label, int defaultValue)
        => AnsiConsole.Prompt(new TextPrompt<int>($"{label}:").DefaultValue(defaultValue));

    private static double AskDouble(string label, double defaultValue)
        => AnsiConsole.Prompt(new TextPrompt<double>($"{label}:").DefaultValue(defaultValue));

    private static TEnum AskEnum<TEnum>(string label, TEnum current) where TEnum : struct, Enum
        => AnsiConsole.Prompt(
            new SelectionPrompt<TEnum>().Title($"{label} (current: {current}):").AddChoices(Enum.GetValues<TEnum>()));

    private static List<string> AskList(string label, List<string> current)
        => Ask(label, string.Join(",", current))
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    private static List<int> AskIntList(string label, List<int> current)
        => Ask(label, string.Join(",", current))
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var n) ? n : 0)
            .Where(n => n > 0)
            .ToList();

    private static void Saved(string name, string file, int count)
        => AnsiConsole.MarkupLineInterpolated(
            $"[green]Added[/] '{name}' — {file} now has {count} entr{(count == 1 ? "y" : "ies")}.");
}
