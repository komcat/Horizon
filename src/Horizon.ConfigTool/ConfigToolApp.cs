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
using System.Diagnostics;
using System.Text.Json.Nodes;
using Spectre.Console;

namespace Horizon.ConfigTool;

/// <summary>
/// Spectre.Console menu over the hardware config files. Each domain offers
/// view, preset-driven add, edit, and remove. Add and edit share one generic
/// field editor (<see cref="RunEditor{T}"/>) driven by per-domain field lists.
/// </summary>
internal sealed class ConfigToolApp
{
    private const string Motion = "Motion";
    private const string IO = "IO";
    private const string IoWatch = "IO watch";
    private const string Pneumatic = "Pneumatic";
    private const string Laser = "Laser";
    private const string Measurement = "Measurement";
    private const string Camera = "Camera";
    private const string Safety = "Safety";
    private const string Interlocks = "Interlocks";
    private const string Lighting = "Lighting";
    private const string Tec = "TEC";
    private const string Smu = "SMU";
    private const string PowerSupply = "Power supply";
    private const string TcpClients = "TCP clients";
    private const string ViewAllDomains = "View all";
    private const string ExportBundle = "Export config bundle";
    private const string ImportBundle = "Import config bundle";
    private const string Validate = "Validate all";
    private const string OpenFolder = "Open config folder";
    private const string ChangeDir = "Change config directory";
    private const string Exit = "Exit";
    private const string Save = "Save";
    private const string Cancel = "Cancel";
    private const string Back = "Back";

    private string _configDir;

    /// <summary>
    /// Where preset catalogs live (<c>&lt;domain&gt;_presets.json</c>). Ships next
    /// to the executable; edit the JSON to add/change presets without recompiling.
    /// </summary>
    private readonly string _presetDir = Path.Combine(AppContext.BaseDirectory, "presets");

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
                    .AddChoices(Motion, IO, IoWatch, Pneumatic, Laser, Measurement, Camera, Safety, Interlocks,
                                Lighting, Tec, Smu, PowerSupply, TcpClients, ViewAllDomains,
                                ExportBundle, ImportBundle, Validate, OpenFolder, ChangeDir, Exit));

            switch (choice)
            {
                case Motion:      DomainMenu(Motion, "motor_devices.json", ViewMotion, AddMotion, EditMotion, RemoveMotion); break;
                case IO:          DomainMenu(IO, "io_devices.json", ViewIO, AddIO, EditIO, RemoveIO,
                                      ("Apply pinout preset", ApplyIoPinout)); break;
                case IoWatch:     DomainMenu(IoWatch, "io_watch.json", ViewIoWatch, AddIoWatch, EditIoWatch, RemoveIoWatch); break;
                case Pneumatic:   DomainMenu(Pneumatic, "pneumatic_slides.json", ViewPneumatic, AddPneumatic, EditPneumatic, RemovePneumatic); break;
                case Laser:       DomainMenu(Laser, "laser_controller_devices.json", ViewLaser, AddLaser, EditLaser, RemoveLaser); break;
                case Measurement: DomainMenu(Measurement, "current_measurement_devices.json", ViewMeasurement, AddMeasurement, EditMeasurement, RemoveMeasurement); break;
                case Camera:      DomainMenu(Camera, "camera-config.json", ViewCamera, AddCamera, EditCamera, RemoveCamera); break;
                case Safety:      DomainMenu(Safety, "safety_watch.json", ViewSafety, AddSafety, EditSafety, RemoveSafety); break;
                case Interlocks:  DomainMenu(Interlocks, "motion_interlocks.json", ViewInterlocks, AddInterlock, EditInterlock, RemoveInterlock); break;
                case Lighting:    DomainMenu(Lighting, "lighting_devices.json", ViewLighting, AddLighting, EditLighting, RemoveLighting); break;
                case Tec:         DomainMenu(Tec, "tec_devices.json", ViewTec, AddTec, EditTec, RemoveTec); break;
                case Smu:         DomainMenu(Smu, "smu_devices.json", ViewSmu, AddSmu, EditSmu, RemoveSmu); break;
                case PowerSupply: DomainMenu(PowerSupply, "power_supply_devices.json", ViewPowerSupply, AddPowerSupply, EditPowerSupply, RemovePowerSupply); break;
                case TcpClients:  DomainMenu(TcpClients, "tcp_clients.json", ViewTcp, AddTcp, EditTcp, RemoveTcp); break;
                case ViewAllDomains: ViewAll(); break;
                case ExportBundle: ExportBundleFlow(); break;
                case ImportBundle: ImportBundleFlow(); break;
                case Validate:    ValidateAll(); break;
                case OpenFolder:  OpenConfigFolder(); break;
                case ChangeDir:   _configDir = AnsiConsole.Prompt(
                                      new TextPrompt<string>("New config directory:").DefaultValue(_configDir)); break;
                case Exit:        return;
            }
        }
    }

    private void DomainMenu(string title, string file, Action view, Action add, Action edit, Action remove,
        params (string Label, Action Action)[] extra)
    {
        while (true)
        {
            var choices = new List<string> { "View", "Add (from preset)", "Edit", "Remove" };
            choices.AddRange(extra.Select(e => e.Label));
            choices.Add(Back);

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>().Title($"\n[aqua]{title}[/] — {file}").AddChoices(choices));

            switch (choice)
            {
                case "View":              view();   break;
                case "Add (from preset)": add();    break;
                case "Edit":              edit();   break;
                case "Remove":            remove(); break;
                case Back:                return;
                default:                  extra.FirstOrDefault(e => e.Label == choice).Action?.Invoke(); break;
            }
        }
    }

    /// <summary>Prints every domain's config in one pass — a full snapshot / printout.</summary>
    private void ViewAll()
    {
        var sections = new (string Title, Action View)[]
        {
            (Motion, ViewMotion), (IO, () => { ViewIO(); ViewIoPinout(); }), (IoWatch, ViewIoWatch),
            (Pneumatic, ViewPneumatic), (Laser, ViewLaser),
            (Measurement, ViewMeasurement), (Camera, ViewCamera), (Safety, ViewSafety),
            (Interlocks, ViewInterlocks), (Lighting, ViewLighting), (Tec, ViewTec), (Smu, ViewSmu),
            (PowerSupply, ViewPowerSupply), (TcpClients, ViewTcp),
        };

        AnsiConsole.MarkupLineInterpolated($"\nConfig directory: [aqua]{_configDir}[/]");
        foreach (var (title, view) in sections)
        {
            AnsiConsole.MarkupLineInterpolated($"\n[grey]══ {title} ══[/]");
            view();
        }
    }

    /// <summary>Runs the cross-reference validator over the whole config set.</summary>
    private void ValidateAll() => RenderIssues(new ConfigValidator(_configDir).Validate());

    /// <summary>Renders a validation result as a table (or a green all-clear line).</summary>
    private static void RenderIssues(List<ValidationIssue> issues)
    {
        if (issues.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]No issues — names are unique and all cross-references resolve.[/]");
            return;
        }

        var errors = issues.Count(i => i.Severity == IssueSeverity.Error);
        var table = NewTable($"Validation — {errors} error(s), {issues.Count - errors} warning(s)",
            "Severity", "Domain", "Message");
        foreach (var i in issues.OrderByDescending(i => i.Severity == IssueSeverity.Error))
            table.AddRow(
                i.Severity == IssueSeverity.Error ? "[red]ERROR[/]" : "[yellow]warn[/]",
                Markup.Escape(i.Domain), Markup.Escape(i.Message));
        AnsiConsole.Write(table);
    }

    /// <summary>Opens the current config directory in the OS file explorer (creating it if needed).</summary>
    private void OpenConfigFolder()
    {
        try
        {
            Directory.CreateDirectory(_configDir);
            Process.Start(new ProcessStartInfo { FileName = _configDir, UseShellExecute = true });
            AnsiConsole.MarkupLineInterpolated($"[green]Opened[/] {_configDir}");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Could not open folder: {ex.Message}[/]");
        }
    }

    // --- config bundle export / import ---

    /// <summary>Bundles the whole config set into a single transferable JSON file.</summary>
    private void ExportBundleFlow()
    {
        var profile = AnsiConsole.Prompt(new TextPrompt<string>("Profile label (optional):").AllowEmpty());
        var description = AnsiConsole.Prompt(new TextPrompt<string>("Description (optional):").AllowEmpty());

        var label = string.IsNullOrWhiteSpace(profile) ? "config" : profile.Trim();
        var defaultPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            $"horizon-config-{label}-{DateTime.Now:yyyyMMdd}.json");
        var outputPath = AnsiConsole.Prompt(new TextPrompt<string>("Output file:").DefaultValue(defaultPath));

        ConfigBundleFile bundle;
        try
        {
            bundle = ConfigBundle.Build(_configDir, profile, description, DateTime.UtcNow.ToString("o")).Bundle;
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            ConfigBundle.Write(bundle, outputPath);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Export failed: {ex.Message}[/]");
            return;
        }

        AnsiConsole.MarkupLineInterpolated($"[green]Exported[/] {bundle.Files.Count} file(s) to {outputPath}");
        var table = NewTable("Export summary", "Domain", "Status");
        foreach (var e in ConfigBundle.Manifest)
            table.AddRow(Markup.Escape(e.Id),
                bundle.Files.ContainsKey(e.Id) ? "[green]included[/]" : "[grey]skipped (missing)[/]");
        AnsiConsole.Write(table);
    }

    /// <summary>Imports a config bundle: preview → back up overwritten files → write → validate.</summary>
    private void ImportBundleFlow()
    {
        var path = AnsiConsole.Prompt(new TextPrompt<string>("Bundle file to import:"));
        if (!File.Exists(path))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]File not found: {path}[/]");
            return;
        }

        ConfigBundleFile bundle;
        try { bundle = ConfigBundle.Read(path); }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Cannot import: {ex.Message}[/]");
            return;
        }

        // Resolve writable entries against the manifest; collect unknown ids.
        var planned = new List<(string Id, string FileName, JsonNode? Node)>();
        var ignored = new List<string>();
        foreach (var (id, node) in bundle.Files)
        {
            var fileName = ConfigBundle.FileNameFor(id);
            if (fileName == null) { ignored.Add(id); continue; }
            planned.Add((id, fileName, node));
        }

        if (planned.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Bundle has no recognized config files — nothing to import.[/]");
            return;
        }

        AnsiConsole.MarkupLineInterpolated(
            $"\nBundle: [aqua]{Markup.Escape(bundle.Profile ?? "(no profile)")}[/]  created {Markup.Escape(bundle.CreatedUtc)}  v{bundle.FormatVersion}");
        if (!string.IsNullOrWhiteSpace(bundle.Description))
            AnsiConsole.MarkupLineInterpolated($"[grey]{Markup.Escape(bundle.Description)}[/]");

        var table = NewTable("Import preview", "Domain", "File", "Entries", "Action");
        foreach (var p in planned)
        {
            var target = Path.Combine(_configDir, p.FileName);
            table.AddRow(Markup.Escape(p.Id), Markup.Escape(p.FileName), EntryCount(p.Node),
                File.Exists(target) ? "[yellow]overwrite[/]" : "[green]new[/]");
        }
        AnsiConsole.Write(table);

        if (ignored.Count > 0)
            AnsiConsole.MarkupLineInterpolated(
                $"[grey]Ignored (unknown ids): {Markup.Escape(string.Join(", ", ignored))}[/]");

        if (!AnsiConsole.Confirm(
                $"Import {planned.Count} file(s) into {Markup.Escape(_configDir)}? Existing files are backed up first.", false))
            return;

        var backupDir = Path.Combine(_configDir, "_backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var backed = 0;
        try
        {
            Directory.CreateDirectory(_configDir);

            // Back up every file we're about to overwrite, then write all.
            foreach (var p in planned)
            {
                var target = Path.Combine(_configDir, p.FileName);
                if (!File.Exists(target)) continue;
                Directory.CreateDirectory(backupDir);
                File.Copy(target, Path.Combine(backupDir, p.FileName), overwrite: true);
                backed++;
            }

            foreach (var p in planned)
                File.WriteAllText(Path.Combine(_configDir, p.FileName), ConfigBundle.SerializeNode(p.Node));
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Import failed: {ex.Message}[/]");
            if (backed > 0)
                AnsiConsole.MarkupLineInterpolated($"[yellow]Pre-import copies are in {backupDir}.[/]");
            return;
        }

        var backupNote = backed > 0 ? $" Backed up {backed} existing file(s) to {backupDir}." : "";
        AnsiConsole.MarkupLineInterpolated($"[green]Imported[/] {planned.Count} file(s).{backupNote}");

        AnsiConsole.MarkupLine("\n[aqua]Validating imported config…[/]");
        RenderIssues(new ConfigValidator(_configDir).Validate());
    }

    /// <summary>Best-effort entry count for the import preview (array length, or the first array property).</summary>
    private static string EntryCount(JsonNode? node) => node switch
    {
        JsonArray arr => arr.Count.ToString(),
        JsonObject obj when obj.FirstOrDefault(kv => kv.Value is JsonArray).Value is JsonArray a => a.Count.ToString(),
        _ => "—",
    };

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

    /// <summary>Prints the input/output pin name → pin number map for each IO device.</summary>
    private void ViewIoPinout()
    {
        foreach (var d in EziIOJsonLoader.Load(_configDir))
        {
            if (d.InputPins.Count == 0 && d.OutputPins.Count == 0) continue;

            var table = NewTable($"{d.Device.Name} pinout", "Dir", "Pin", "Name");
            foreach (var p in d.InputPins.OrderBy(p => p.Pin))
                table.AddRow("in", p.Pin.ToString(), Markup.Escape(p.Name));
            foreach (var p in d.OutputPins.OrderBy(p => p.Pin))
                table.AddRow("out", p.Pin.ToString(), Markup.Escape(p.Name));
            AnsiConsole.Write(table);
        }
    }

    private void ViewIoWatch()
    {
        var config = ConfigLoader.LoadJson<IoWatchConfig>(_configDir, "io_watch");
        if (config == null) { Empty("io_watch.json"); return; }

        AnsiConsole.MarkupLineInterpolated(
            $"Enabled: [aqua]{config.Enabled}[/]   Poll interval: [aqua]{config.PollingIntervalMs} ms[/]");
        var table = NewTable("IO watch bindings", "Name", "Input", "Output", "Trigger", "Debounce", "Enabled");
        if (config.Bindings.Count == 0)
            table.AddRow("[grey]none[/]", "", "", "", "", "");
        else
            foreach (var b in config.Bindings)
                table.AddRow(Markup.Escape(b.Name),
                    Markup.Escape($"{b.Input.Device}.{b.Input.PinName}"),
                    Markup.Escape($"{b.Output.Device}.{b.Output.PinName}"),
                    b.Trigger.ToString(), $"{b.DebounceMs} ms", YesNo(b.Enabled));
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

    private void ViewCamera()
    {
        var cameras = CameraConfigJson.Load(_configDir);
        if (cameras.Count == 0) { Empty("camera-config.json"); return; }

        var table = NewTable("Cameras",
            "Name", "SDK", "Connection", "Serial", "IP", "W×H", "mm/px", "Mag");
        foreach (var c in cameras)
            table.AddRow(Markup.Escape(c.Name), c.SdkType.ToString(), Markup.Escape(c.ConnectionType),
                Markup.Escape(c.SerialNumber), Markup.Escape(c.IpAddress),
                $"{c.Width}×{c.Height}", c.MmPerPixel.ToString(), c.Magnification.ToString());
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
        if (controllers.Count == 0) { Empty("lighting_devices.json"); return; }

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
        if (devices.Count == 0) { Empty("power_supply_devices.json"); return; }

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

    // --- per-domain field lists (shared by add + edit) ---

    private static List<FieldSpec<MotionDevice>> MotionFields() => new()
    {
        new("Name", d => d.Name, d => d with { Name = Ask("Name", d.Name) }),
        new("IP address", d => d.IpAddress, d => d with { IpAddress = Ask("IP address", d.IpAddress) }),
        new("Port", d => d.Port.ToString(), d => d with { Port = AskInt("Port", d.Port) }),
        new("Controller", d => d.ControllerType, d => d with { ControllerType = Ask("Controller", d.ControllerType) }),
        new("Axes", d => string.Join(",", d.InstalledAxes), d => d with { InstalledAxes = AskList("Axes", d.InstalledAxes) }),
        new("Enabled", d => YesNo(d.Enabled), d => d with { Enabled = AnsiConsole.Confirm("Enabled?", d.Enabled) }),
    };

    private static List<FieldSpec<IODeviceConfig>> IoFields() => new()
    {
        new("Name", d => d.Device.Name, d => d with { Device = d.Device with { Name = Ask("Name", d.Device.Name) } }),
        new("IP address", d => d.Device.IpAddress, d => d with { Device = d.Device with { IpAddress = Ask("IP address", d.Device.IpAddress) } }),
        new("Input count", d => d.Device.InputCount.ToString(), d => d with { Device = d.Device with { InputCount = AskInt("Input count", d.Device.InputCount) } }),
        new("Output count", d => d.Device.OutputCount.ToString(), d => d with { Device = d.Device with { OutputCount = AskInt("Output count", d.Device.OutputCount) } }),
        new("Type", d => d.Device.DeviceType.ToString(), d => d with { Device = d.Device with { DeviceType = AskEnum("Type", d.Device.DeviceType) } }),
        new("Enabled", d => YesNo(d.Device.Enabled), d => d with { Device = d.Device with { Enabled = AnsiConsole.Confirm("Enabled?", d.Device.Enabled) } }),
    };

    private static List<FieldSpec<PneumaticConfig>> PneumaticFields() => new()
    {
        new("Name", s => s.Name, s => s with { Name = Ask("Name", s.Name) }),
        new("Output device", s => s.Output.DeviceName, s => s with { Output = s.Output with { DeviceName = Ask("Output device", s.Output.DeviceName) } }),
        new("Output pin", s => s.Output.PinName, s => s with { Output = s.Output with { PinName = Ask("Output pin", s.Output.PinName) } }),
        new("Extended sensor device", s => s.ExtendedSensor.DeviceName, s => s with { ExtendedSensor = s.ExtendedSensor with { DeviceName = Ask("Extended sensor device", s.ExtendedSensor.DeviceName) } }),
        new("Extended sensor pin", s => s.ExtendedSensor.PinName, s => s with { ExtendedSensor = s.ExtendedSensor with { PinName = Ask("Extended sensor pin", s.ExtendedSensor.PinName) } }),
        new("Retracted sensor device", s => s.RetractedSensor.DeviceName, s => s with { RetractedSensor = s.RetractedSensor with { DeviceName = Ask("Retracted sensor device", s.RetractedSensor.DeviceName) } }),
        new("Retracted sensor pin", s => s.RetractedSensor.PinName, s => s with { RetractedSensor = s.RetractedSensor with { PinName = Ask("Retracted sensor pin", s.RetractedSensor.PinName) } }),
        new("Timeout ms", s => s.TimeoutMs.ToString(), s => s with { TimeoutMs = AskInt("Timeout ms", s.TimeoutMs) }),
    };

    private static List<FieldSpec<LaserControllerDeviceConfig>> LaserFields() => new()
    {
        new("Name", d => d.Name, d => d with { Name = Ask("Name", d.Name) }),
        new("Controller type", d => d.ControllerType, d => d with { ControllerType = Ask("Controller type", d.ControllerType) }),
        new("VISA resource", d => d.VisaResource, d => d with { VisaResource = Ask("VISA resource", d.VisaResource) }),
        new("Enabled", d => YesNo(d.Enabled), d => d with { Enabled = AnsiConsole.Confirm("Enabled?", d.Enabled) }),
        new("Default temperature", d => $"{d.DefaultTemperature} C", d => d with { DefaultTemperature = AskDouble("Default temperature", d.DefaultTemperature) }),
        new("Default laser current", d => $"{d.DefaultLaserCurrent} mA", d => d with { DefaultLaserCurrent = AskDouble("Default laser current", d.DefaultLaserCurrent) }),
    };

    private static List<FieldSpec<CurrentMeasurementDeviceConfig>> MeasurementFields() => new()
    {
        new("Name", d => d.Name, d => d with { Name = Ask("Name", d.Name) }),
        new("Type", d => d.Type, d => d with { Type = Ask("Type", d.Type) }),
        new("VISA resource", d => d.VisaResource, d => d with { VisaResource = Ask("VISA resource", d.VisaResource) }),
        new("Enabled", d => YesNo(d.Enabled), d => d with { Enabled = AnsiConsole.Confirm("Enabled?", d.Enabled) }),
        new("Channel count", d => d.ChannelCount.ToString(), d => d with { ChannelCount = AskInt("Channel count", d.ChannelCount) }),
        new("Enabled channels", d => string.Join(",", d.EnabledChannels), d => d with { EnabledChannels = AskIntList("Enabled channels", d.EnabledChannels) }),
    };

    private static List<FieldSpec<CameraConfigEntry>> CameraFields() => new()
    {
        new("Name", c => c.Name, c => c with { Name = Ask("Name", c.Name) }),
        new("SDK type", c => c.SdkType.ToString(), c => c with { SdkType = AskEnum("SDK type", c.SdkType) }),
        new("Connection type", c => c.ConnectionType, c => c with { ConnectionType = Ask("Connection type (USB | IP)", c.ConnectionType) }),
        new("Serial number", c => c.SerialNumber, c => c with { SerialNumber = Ask("Serial number", c.SerialNumber) }),
        new("IP address", c => c.IpAddress, c => c with { IpAddress = Ask("IP address", c.IpAddress) }),
        new("Width", c => c.Width.ToString(), c => c with { Width = AskInt("Width (0 = auto)", c.Width) }),
        new("Height", c => c.Height.ToString(), c => c with { Height = AskInt("Height (0 = auto)", c.Height) }),
        new("mm per pixel", c => c.MmPerPixel.ToString(), c => c with { MmPerPixel = AskDouble("mm per pixel (0 = uncalibrated)", c.MmPerPixel) }),
        new("Magnification", c => c.Magnification.ToString(), c => c with { Magnification = AskDouble("Magnification", c.Magnification) }),
    };

    private static List<FieldSpec<IoBinding>> IoWatchFields() => new()
    {
        new("Name", b => b.Name, b => b with { Name = Ask("Name", b.Name) }),
        new("Input device", b => b.Input.Device, b => b with { Input = b.Input with { Device = Ask("Input device", b.Input.Device) } }),
        new("Input pin", b => b.Input.PinName, b => b with { Input = b.Input with { PinName = Ask("Input pin", b.Input.PinName) } }),
        new("Output device", b => b.Output.Device, b => b with { Output = b.Output with { Device = Ask("Output device", b.Output.Device) } }),
        new("Output pin", b => b.Output.PinName, b => b with { Output = b.Output with { PinName = Ask("Output pin", b.Output.PinName) } }),
        new("Trigger", b => b.Trigger.ToString(), b => b with { Trigger = AskEnum("Trigger", b.Trigger) }),
        new("Debounce ms", b => b.DebounceMs.ToString(), b => b with { DebounceMs = AskInt("Debounce ms", b.DebounceMs) }),
        new("Enabled", b => YesNo(b.Enabled), b => b with { Enabled = AnsiConsole.Confirm("Enabled?", b.Enabled) }),
    };

    private static List<FieldSpec<WatchedInput>> SafetyFields() => new()
    {
        new("Device", w => w.DeviceName, w => w with { DeviceName = Ask("Device", w.DeviceName) }),
        new("Pin", w => w.PinName, w => w with { PinName = Ask("Pin", w.PinName) }),
        new("Trigger", w => w.Trigger.ToString(), w => w with { Trigger = AskEnum("Trigger", w.Trigger) }),
        new("Critical", w => YesNo(w.IsCritical), w => w with { IsCritical = AnsiConsole.Confirm("Critical?", w.IsCritical) }),
        new("Enabled", w => YesNo(w.Enabled), w => w with { Enabled = AnsiConsole.Confirm("Enabled?", w.Enabled) }),
        new("Alert title", w => w.AlertTitle, w => w with { AlertTitle = Ask("Alert title", w.AlertTitle) }),
    };

    private static List<FieldSpec<InterlockRule>> InterlockFields() => new()
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
    };

    private static List<FieldSpec<LightingControllerConfig>> LightingFields() => new()
    {
        new("Device id", c => c.DeviceId, c => c with { DeviceId = Ask("Device id", c.DeviceId) }),
        new("Display name", c => c.DeviceName, c => c with { DeviceName = Ask("Display name", c.DeviceName) }),
        new("Controller type", c => c.ControllerType, c => c with { ControllerType = Ask("Controller type", c.ControllerType) }),
        new("IP address", c => c.IpAddress, c => c with { IpAddress = Ask("IP address", c.IpAddress) }),
        new("Port", c => c.Port.ToString(), c => c with { Port = AskInt("Port", c.Port) }),
        new("Enabled", c => YesNo(c.Enabled), c => c with { Enabled = AnsiConsole.Confirm("Enabled?", c.Enabled) }),
    };

    private static List<FieldSpec<TecDeviceConfig>> TecFields() => new()
    {
        new("Name", d => d.Name, d => d with { Name = Ask("Name", d.Name) }),
        new("Controller type", d => d.ControllerType, d => d with { ControllerType = Ask("Controller type", d.ControllerType) }),
        new("VISA resource", d => d.VisaResource, d => d with { VisaResource = Ask("VISA resource", d.VisaResource) }),
        new("IP address", d => d.IpAddress, d => d with { IpAddress = Ask("IP address", d.IpAddress) }),
        new("Port", d => d.Port.ToString(), d => d with { Port = AskInt("Port", d.Port) }),
        new("Serial number", d => d.SerialNumber, d => d with { SerialNumber = Ask("Serial number (TTC-001)", d.SerialNumber) }),
        new("Enabled", d => YesNo(d.Enabled), d => d with { Enabled = AnsiConsole.Confirm("Enabled?", d.Enabled) }),
        new("Default temperature", d => $"{d.DefaultTemperature} C", d => d with { DefaultTemperature = AskDouble("Default temperature", d.DefaultTemperature) }),
        new("Default laser current", d => $"{d.DefaultLaserCurrent} mA", d => d with { DefaultLaserCurrent = AskDouble("Default laser current", d.DefaultLaserCurrent) }),
        new("Sensor mode", d => d.SensorMode, d => d with { SensorMode = Ask("Sensor mode (transducer | thermistor_20k | thermistor_200k)", d.SensorMode) }),
        new("Thermistor beta", d => d.ThermistorBeta.ToString(), d => d with { ThermistorBeta = AskDouble("Thermistor beta (K)", d.ThermistorBeta) }),
        new("Thermistor R0", d => $"{d.ThermistorR0Ohms} Ohm", d => d with { ThermistorR0Ohms = AskDouble("Thermistor R0 (Ohm)", d.ThermistorR0Ohms) }),
        new("Thermistor T0", d => $"{d.ThermistorT0Celsius} C", d => d with { ThermistorT0Celsius = AskDouble("Thermistor T0 (C)", d.ThermistorT0Celsius) }),
    };

    private static List<FieldSpec<SmuDeviceConfig>> SmuFields() => new()
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
    };

    private static List<FieldSpec<PowerSupplyConfig>> PowerSupplyFields() => new()
    {
        new("Name", d => d.Name, d => d with { Name = Ask("Name", d.Name) }),
        new("Type", d => d.Type, d => d with { Type = Ask("Type", d.Type) }),
        new("IP address", d => d.Ip, d => d with { Ip = Ask("IP address", d.Ip) }),
        new("Port", d => d.Port.ToString(), d => d with { Port = AskInt("Port", d.Port) }),
        new("VISA resource", d => d.VisaResource, d => d with { VisaResource = Ask("VISA resource (USB/USBTMC)", d.VisaResource) }),
        new("Channels", d => d.Channels.ToString(), d => d with { Channels = AskInt("Channels", d.Channels) }),
    };

    private static List<FieldSpec<TcpClientConfig>> TcpFields() => new()
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
    };

    // --- add flows: materialize from preset → field editor → save ---

    private void AddMotion()
    {
        var preset = PickPresetFrom<MotionDevice>("motion_presets", d => d.Name);
        if (preset == null) return;

        var devices = MotionJsonLoader.Load(_configDir).ToList();
        // Keep the preset's name unless it collides with an existing device.
        var device = preset with { Name = UniqueName(preset.Name, devices.Select(d => d.Name)) };

        AddEntry("Motion device", device, devices, MotionFields(), d => d.Name,
            "motor_devices.json", list => MotionJsonLoader.Save(list, _configDir));
    }

    private void AddIO()
    {
        var preset = PickPresetFrom<IODeviceConfig>("io_presets", d => d.Device.Name);
        if (preset == null) return;

        var devices = EziIOJsonLoader.Load(_configDir).ToList();
        var device = preset with
        {
            Device = preset.Device with { Name = UniqueName(preset.Device.Name, devices.Select(d => d.Device.Name)) },
        };
        AddEntry("IO device", device, devices, IoFields(), d => d.Device.Name,
            "io_devices.json", list => EziIOJsonLoader.Save(list, _configDir));
    }

    /// <summary>
    /// Applies a named pinout preset (e.g. "UAA") onto the configured IO devices,
    /// matching by device name and overwriting their input/output pin names.
    /// </summary>
    private void ApplyIoPinout()
    {
        var pinout = PickPresetFrom<IoPinoutSet>("io_pinout_presets", p => p.Name);
        if (pinout == null) return;

        var devices = EziIOJsonLoader.Load(_configDir).ToList();
        if (devices.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No IO devices configured yet — add the devices first, then apply a pinout.[/]");
            return;
        }

        var matches = pinout.Devices
            .Where(p => devices.Any(d => NameEq(d.Device.Name, p.DeviceName)))
            .Select(p => p.DeviceName).ToList();
        var missing = pinout.Devices.Select(p => p.DeviceName).Except(matches, StringComparer.OrdinalIgnoreCase).ToList();

        if (matches.Count == 0)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]No device names in pinout '{pinout.Name}' match io_devices.json — nothing to apply.[/]");
            return;
        }

        if (!AnsiConsole.Confirm(
                $"Apply pinout '{pinout.Name}' to {matches.Count} device(s) ({Markup.Escape(string.Join(", ", matches))})? This overwrites their pin names.", false))
            return;

        for (int i = 0; i < devices.Count; i++)
        {
            var p = pinout.Devices.FirstOrDefault(x => NameEq(x.DeviceName, devices[i].Device.Name));
            if (p == null) continue;
            devices[i] = devices[i] with { InputPins = p.InputPins.ToList(), OutputPins = p.OutputPins.ToList() };
        }

        EziIOJsonLoader.Save(devices, _configDir);
        AnsiConsole.MarkupLineInterpolated($"[green]Applied[/] pinout '{pinout.Name}' to {matches.Count} device(s).");
        if (missing.Count > 0)
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]Skipped (no such device in io_devices.json):[/] {Markup.Escape(string.Join(", ", missing))}");
    }

    private void AddPneumatic()
    {
        var preset = PickPresetFrom<PneumaticConfig>("pneumatic_presets", s => s.Name);
        if (preset == null) return;

        var slides = PneumaticJsonLoader.Load(_configDir).ToList();
        var slide = preset with { Name = UniqueName(preset.Name, slides.Select(s => s.Name)) };
        AddEntry("Pneumatic slide", slide, slides, PneumaticFields(), s => s.Name,
            "pneumatic_slides.json", list => PneumaticJsonLoader.Save(list, _configDir));
    }

    private void AddLaser()
    {
        var preset = PickPresetFrom<LaserControllerDeviceConfig>("laser_presets", d => d.Name);
        if (preset == null) return;

        var devices = LaserControllerJsonLoader.Load(_configDir).ToList();
        var device = preset with { Name = UniqueName(preset.Name, devices.Select(d => d.Name)) };
        AddEntry("Laser controller", device, devices, LaserFields(), d => d.Name,
            "laser_controller_devices.json", list => LaserControllerJsonLoader.Save(list, _configDir));
    }

    private void AddMeasurement()
    {
        var preset = PickPresetFrom<CurrentMeasurementDeviceConfig>("measurement_presets", d => d.Name);
        if (preset == null) return;

        var devices = CurrentMeasurementJsonLoader.Load(_configDir).ToList();
        var device = preset with { Name = UniqueName(preset.Name, devices.Select(d => d.Name)) };
        AddEntry("Measurement device", device, devices, MeasurementFields(), d => d.Name,
            "current_measurement_devices.json", list => CurrentMeasurementJsonLoader.Save(list, _configDir));
    }

    private void AddIoWatch()
    {
        var preset = PickPresetFrom<IoBinding>("io_watch_presets", b => b.Name);
        if (preset == null) return;

        var config = ConfigLoader.LoadJson<IoWatchConfig>(_configDir, "io_watch") ?? new IoWatchConfig();
        var bindings = config.Bindings.ToList();
        var binding = preset with { Name = UniqueName(preset.Name, bindings.Select(b => b.Name)) };
        AddEntry("IO binding", binding, bindings, IoWatchFields(), b => b.Name,
            "io_watch.json", list => ConfigLoader.SaveJson(_configDir, "io_watch", config with { Bindings = list }));
    }

    private void AddCamera()
    {
        var preset = PickPresetFrom<CameraConfigEntry>("camera_presets", c => c.Name);
        if (preset == null) return;

        var cameras = CameraConfigJson.Load(_configDir);
        var camera = preset with { Name = UniqueName(preset.Name, cameras.Select(c => c.Name)) };
        AddEntry("Camera", camera, cameras, CameraFields(), c => c.Name,
            "camera-config.json", list => CameraConfigJson.Save(list, _configDir));
    }

    private void AddSafety()
    {
        var preset = PickPresetFrom<WatchedInput>("safety_presets", WatchedKey);
        if (preset == null) return;

        var config = ConfigLoader.LoadJson<SafetyWatchConfig>(_configDir, "safety_watch") ?? new SafetyWatchConfig();
        var inputs = config.WatchedInputs.ToList();
        AddEntry("Watched input", preset, inputs, SafetyFields(), WatchedKey,
            "safety_watch.json", list => ConfigLoader.SaveJson(_configDir, "safety_watch", config with { WatchedInputs = list }));
    }

    private void AddInterlock()
    {
        var preset = PickPresetFrom<InterlockRule>("interlock_presets", r => r.Name);
        if (preset == null) return;

        var config = ConfigLoader.LoadJson<InterlockConfig>(_configDir, "motion_interlocks") ?? new InterlockConfig();
        var rules = config.Rules.ToList();
        var rule = preset with { Name = UniqueName(preset.Name, rules.Select(r => r.Name)) };
        AddEntry("Interlock rule", rule, rules, InterlockFields(), r => r.Name,
            "motion_interlocks.json", list => ConfigLoader.SaveJson(_configDir, "motion_interlocks", config with { Rules = list }));
    }

    private void AddLighting()
    {
        var preset = PickPresetFrom<LightingControllerConfig>("lighting_presets", c => c.DeviceId);
        if (preset == null) return;

        var controllers = LightingJsonLoader.Load(_configDir).ToList();
        var controller = preset with { DeviceId = UniqueName(preset.DeviceId, controllers.Select(c => c.DeviceId)) };
        AddEntry("Lighting controller", controller, controllers, LightingFields(), c => c.DeviceId,
            "lighting_devices.json", list => LightingJsonLoader.Save(list, _configDir));
    }

    private void AddTec()
    {
        var preset = PickPresetFrom<TecDeviceConfig>("tec_presets", d => d.Name);
        if (preset == null) return;

        var devices = TecJsonLoader.Load(_configDir).ToList();
        var device = preset with { Name = UniqueName(preset.Name, devices.Select(d => d.Name)) };
        AddEntry("TEC device", device, devices, TecFields(), d => d.Name,
            "tec_devices.json", list => TecJsonLoader.Save(list, _configDir));
    }

    private void AddSmu()
    {
        var preset = PickPresetFrom<SmuDeviceConfig>("smu_presets", d => d.Name);
        if (preset == null) return;

        var devices = SmuJsonLoader.Load(_configDir).ToList();
        var device = preset with { Name = UniqueName(preset.Name, devices.Select(d => d.Name)) };
        AddEntry("SMU device", device, devices, SmuFields(), d => d.Name,
            "smu_devices.json", list => SmuJsonLoader.Save(list, _configDir));
    }

    private void AddPowerSupply()
    {
        var preset = PickPresetFrom<PowerSupplyConfig>("power_supply_presets", d => d.Name);
        if (preset == null) return;

        var devices = PowerSupplyJsonLoader.Load(_configDir).ToList();
        var device = preset with { Name = UniqueName(preset.Name, devices.Select(d => d.Name)) };
        AddEntry("Power supply", device, devices, PowerSupplyFields(), d => d.Name,
            "power_supply_devices.json", list => PowerSupplyJsonLoader.Save(list, _configDir));
    }

    private void AddTcp()
    {
        var preset = PickPresetFrom<TcpClientConfig>("tcp_presets", c => c.Id);
        if (preset == null) return;

        var clients = TcpClientJsonLoader.Load(_configDir).ToList();
        var client = preset with { Id = UniqueName(preset.Id, clients.Select(c => c.Id)) };
        AddEntry("TCP client", client, clients, TcpFields(), c => c.Id,
            "tcp_clients.json", list => TcpClientJsonLoader.Save(list, _configDir));
    }

    // --- edit flows ---

    private void EditMotion()
        => EditEntry("Motion device", MotionJsonLoader.Load(_configDir).ToList(),
            d => d.Name, MotionFields(), list => MotionJsonLoader.Save(list, _configDir));

    private void EditIO()
        => EditEntry("IO device", EziIOJsonLoader.Load(_configDir).ToList(),
            d => d.Device.Name, IoFields(), list => EziIOJsonLoader.Save(list, _configDir));

    private void EditPneumatic()
        => EditEntry("Pneumatic slide", PneumaticJsonLoader.Load(_configDir).ToList(),
            s => s.Name, PneumaticFields(), list => PneumaticJsonLoader.Save(list, _configDir));

    private void EditLaser()
        => EditEntry("Laser controller", LaserControllerJsonLoader.Load(_configDir).ToList(),
            d => d.Name, LaserFields(), list => LaserControllerJsonLoader.Save(list, _configDir));

    private void EditMeasurement()
        => EditEntry("Measurement device", CurrentMeasurementJsonLoader.Load(_configDir).ToList(),
            d => d.Name, MeasurementFields(), list => CurrentMeasurementJsonLoader.Save(list, _configDir));

    private void EditIoWatch()
    {
        var config = ConfigLoader.LoadJson<IoWatchConfig>(_configDir, "io_watch") ?? new IoWatchConfig();
        EditEntry("IO binding", config.Bindings.ToList(), b => b.Name, IoWatchFields(),
            list => ConfigLoader.SaveJson(_configDir, "io_watch", config with { Bindings = list }));
    }

    private void EditCamera()
        => EditEntry("Camera", CameraConfigJson.Load(_configDir),
            c => c.Name, CameraFields(), list => CameraConfigJson.Save(list, _configDir));

    private void EditSafety()
    {
        var config = ConfigLoader.LoadJson<SafetyWatchConfig>(_configDir, "safety_watch") ?? new SafetyWatchConfig();
        EditEntry("Watched input", config.WatchedInputs.ToList(), WatchedKey, SafetyFields(),
            list => ConfigLoader.SaveJson(_configDir, "safety_watch", config with { WatchedInputs = list }));
    }

    private void EditInterlock()
    {
        var config = ConfigLoader.LoadJson<InterlockConfig>(_configDir, "motion_interlocks") ?? new InterlockConfig();
        EditEntry("Interlock rule", config.Rules.ToList(), r => r.Name, InterlockFields(),
            list => ConfigLoader.SaveJson(_configDir, "motion_interlocks", config with { Rules = list }));
    }

    private void EditLighting()
        => EditEntry("Lighting controller", LightingJsonLoader.Load(_configDir).ToList(),
            c => c.DeviceId, LightingFields(), list => LightingJsonLoader.Save(list, _configDir));

    private void EditTec()
        => EditEntry("TEC device", TecJsonLoader.Load(_configDir).ToList(),
            d => d.Name, TecFields(), list => TecJsonLoader.Save(list, _configDir));

    private void EditSmu()
        => EditEntry("SMU device", SmuJsonLoader.Load(_configDir).ToList(),
            d => d.Name, SmuFields(), list => SmuJsonLoader.Save(list, _configDir));

    private void EditPowerSupply()
        => EditEntry("Power supply", PowerSupplyJsonLoader.Load(_configDir).ToList(),
            d => d.Name, PowerSupplyFields(), list => PowerSupplyJsonLoader.Save(list, _configDir));

    private void EditTcp()
        => EditEntry("TCP client", TcpClientJsonLoader.Load(_configDir).ToList(),
            c => c.Id, TcpFields(), list => TcpClientJsonLoader.Save(list, _configDir));

    // --- remove flows ---

    private void RemoveMotion()
        => RemoveEntry("Motion device", MotionJsonLoader.Load(_configDir).ToList(),
            d => d.Name, list => MotionJsonLoader.Save(list, _configDir));

    private void RemoveIO()
        => RemoveEntry("IO device", EziIOJsonLoader.Load(_configDir).ToList(),
            d => d.Device.Name, list => EziIOJsonLoader.Save(list, _configDir));

    private void RemovePneumatic()
        => RemoveEntry("Pneumatic slide", PneumaticJsonLoader.Load(_configDir).ToList(),
            s => s.Name, list => PneumaticJsonLoader.Save(list, _configDir));

    private void RemoveLaser()
        => RemoveEntry("Laser controller", LaserControllerJsonLoader.Load(_configDir).ToList(),
            d => d.Name, list => LaserControllerJsonLoader.Save(list, _configDir));

    private void RemoveMeasurement()
        => RemoveEntry("Measurement device", CurrentMeasurementJsonLoader.Load(_configDir).ToList(),
            d => d.Name, list => CurrentMeasurementJsonLoader.Save(list, _configDir));

    private void RemoveIoWatch()
    {
        var config = ConfigLoader.LoadJson<IoWatchConfig>(_configDir, "io_watch") ?? new IoWatchConfig();
        RemoveEntry("IO binding", config.Bindings.ToList(), b => b.Name,
            list => ConfigLoader.SaveJson(_configDir, "io_watch", config with { Bindings = list }));
    }

    private void RemoveCamera()
        => RemoveEntry("Camera", CameraConfigJson.Load(_configDir),
            c => c.Name, list => CameraConfigJson.Save(list, _configDir));

    private void RemoveSafety()
    {
        var config = ConfigLoader.LoadJson<SafetyWatchConfig>(_configDir, "safety_watch") ?? new SafetyWatchConfig();
        RemoveEntry("Watched input", config.WatchedInputs.ToList(), WatchedKey,
            list => ConfigLoader.SaveJson(_configDir, "safety_watch", config with { WatchedInputs = list }));
    }

    private void RemoveInterlock()
    {
        var config = ConfigLoader.LoadJson<InterlockConfig>(_configDir, "motion_interlocks") ?? new InterlockConfig();
        RemoveEntry("Interlock rule", config.Rules.ToList(), r => r.Name,
            list => ConfigLoader.SaveJson(_configDir, "motion_interlocks", config with { Rules = list }));
    }

    private void RemoveLighting()
        => RemoveEntry("Lighting controller", LightingJsonLoader.Load(_configDir).ToList(),
            c => c.DeviceId, list => LightingJsonLoader.Save(list, _configDir));

    private void RemoveTec()
        => RemoveEntry("TEC device", TecJsonLoader.Load(_configDir).ToList(),
            d => d.Name, list => TecJsonLoader.Save(list, _configDir));

    private void RemoveSmu()
        => RemoveEntry("SMU device", SmuJsonLoader.Load(_configDir).ToList(),
            d => d.Name, list => SmuJsonLoader.Save(list, _configDir));

    private void RemovePowerSupply()
        => RemoveEntry("Power supply", PowerSupplyJsonLoader.Load(_configDir).ToList(),
            d => d.Name, list => PowerSupplyJsonLoader.Save(list, _configDir));

    private void RemoveTcp()
        => RemoveEntry("TCP client", TcpClientJsonLoader.Load(_configDir).ToList(),
            c => c.Id, list => TcpClientJsonLoader.Save(list, _configDir));

    // --- generic add / edit / remove ---

    /// <summary>One editable field: how to show it, and how to edit it.</summary>
    private sealed record FieldSpec<T>(string Name, Func<T, string> Show, Func<T, T> Edit);

    private static void AddEntry<T>(string title, T entry, List<T> all, List<FieldSpec<T>> fields,
        Func<T, string> nameOf, string file, Action<List<T>> save) where T : class
    {
        var others = all.Select(nameOf).ToList();
        var result = RunEditor(title, entry, fields, x => NameError(nameOf(x), others));
        if (result == null) return;
        all.Add(result);
        save(all);
        AnsiConsole.MarkupLineInterpolated(
            $"[green]Added[/] '{nameOf(result)}' — {file} now has {all.Count} entr{Plural(all.Count)}.");
    }

    private static void EditEntry<T>(string title, List<T> all, Func<T, string> nameOf,
        List<FieldSpec<T>> fields, Action<List<T>> save) where T : class
    {
        if (all.Count == 0) { AnsiConsole.MarkupLine("[yellow]Nothing to edit.[/]"); return; }

        var labels = all.Select(nameOf).ToList();
        var pick = AnsiConsole.Prompt(
            new SelectionPrompt<string>().Title($"[aqua]{title}[/] — edit which?").AddChoices(labels.Append(Back)));
        if (pick == Back) return;

        int index = labels.IndexOf(pick);
        var others = all.Where((_, i) => i != index).Select(nameOf).ToList();
        var result = RunEditor(title, all[index], fields, x => NameError(nameOf(x), others));
        if (result == null) return;

        all[index] = result;
        save(all);
        AnsiConsole.MarkupLineInterpolated($"[green]Saved[/] '{nameOf(result)}'.");
    }

    private static void RemoveEntry<T>(string title, List<T> all, Func<T, string> nameOf, Action<List<T>> save)
    {
        if (all.Count == 0) { AnsiConsole.MarkupLine("[yellow]Nothing to remove.[/]"); return; }

        var labels = all.Select(nameOf).ToList();
        var pick = AnsiConsole.Prompt(
            new SelectionPrompt<string>().Title($"[aqua]{title}[/] — remove which?").AddChoices(labels.Append(Back)));
        if (pick == Back) return;

        if (!AnsiConsole.Confirm($"Remove '{Markup.Escape(pick)}'?", false)) return;
        all.RemoveAt(labels.IndexOf(pick));
        save(all);
        AnsiConsole.MarkupLineInterpolated($"[green]Removed[/] '{pick}'.");
    }

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

    private static string WatchedKey(WatchedInput w) => $"{w.DeviceName}.{w.PinName}";

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

    private static string Plural(int count) => count == 1 ? "y" : "ies";

    private static string Pin(IOPinReference reference)
        => string.IsNullOrEmpty(reference.PinName)
            ? "[grey]—[/]"
            : Markup.Escape($"{reference.DeviceName}.{reference.PinName}");

    /// <summary>A preset catalog file: a list of fully-formed entries of type T.</summary>
    private sealed record PresetFile<T>
    {
        public List<T> Presets { get; init; } = new();
    }

    /// <summary>A named IO pin map covering one or more devices (matched by name).</summary>
    private sealed record IoPinoutSet
    {
        public string Name { get; init; } = string.Empty;
        public List<IoDevicePinout> Devices { get; init; } = new();
    }

    private sealed record IoDevicePinout
    {
        public string DeviceName { get; init; } = string.Empty;
        public List<IOPinConfig> InputPins { get; init; } = new();
        public List<IOPinConfig> OutputPins { get; init; } = new();
    }

    /// <summary>
    /// Loads the <paramref name="file"/> preset catalog (e.g. <c>motion_presets.json</c>)
    /// from <see cref="_presetDir"/> and lets the user pick one by its label.
    /// Returns the chosen entry, or null if cancelled or the catalog is empty.
    /// </summary>
    private T? PickPresetFrom<T>(string file, Func<T, string> labelOf) where T : class
    {
        var presets = ConfigLoader.LoadJson<PresetFile<T>>(_presetDir, file)?.Presets ?? new List<T>();
        if (presets.Count == 0)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]No presets — expected {file}.json in {_presetDir}.[/]");
            return null;
        }

        var labels = presets.Select(labelOf).ToList();
        var pick = AnsiConsole.Prompt(
            new SelectionPrompt<string>().Title("Select a preset:").AddChoices(labels.Append(Back)));
        return pick == Back ? null : presets[labels.IndexOf(pick)];
    }

    private static string PickField(params string[] fields)
        => AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Edit a field, or:")
                .AddChoices(fields.Append(Save).Append(Cancel)));

    private static bool NameEq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

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
}
