using Horizon.Common;
using Horizon.Core.Interlock;
using Horizon.CurrentMeasurement;
using Horizon.IO;
using Horizon.LaserController;
using Horizon.Motion;
using Horizon.Pneumatic;
using Horizon.Pneumatic.Models;
using Horizon.Safety.Models;
using Spectre.Console;

namespace Horizon.ConfigTool;

/// <summary>
/// Spectre.Console menu over the hardware config files. Phase 1 is a read-only
/// viewer across all domains; add / edit / validate land in later phases.
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
    private const string ChangeDir = "Change config directory";
    private const string Exit = "Exit";

    private string _configDir;

    public ConfigToolApp(string configDir) => _configDir = configDir;

    public void Run()
    {
        while (true)
        {
            var exists = Directory.Exists(_configDir);
            AnsiConsole.MarkupLineInterpolated(
                $"\nConfig directory: [aqua]{_configDir}[/]");
            if (!exists)
                AnsiConsole.MarkupLine("[red](directory does not exist — it will be created on save)[/]");

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Select a domain to view:")
                    .AddChoices(Motion, IO, Pneumatic, Laser, Measurement,
                                Safety, Interlocks, ChangeDir, Exit));

            switch (choice)
            {
                case Motion:      ViewMotion();      break;
                case IO:          ViewIO();          break;
                case Pneumatic:   ViewPneumatic();   break;
                case Laser:       ViewLaser();       break;
                case Measurement: ViewMeasurement(); break;
                case Safety:      ViewSafety();      break;
                case Interlocks:  ViewInterlocks();  break;
                case ChangeDir:   ChangeConfigDir(); break;
                case Exit:        return;
            }
        }
    }

    private void ChangeConfigDir()
        => _configDir = AnsiConsole.Prompt(
            new TextPrompt<string>("New config directory:").DefaultValue(_configDir));

    // --- domain viewers ---

    private void ViewMotion()
    {
        var devices = MotionJsonLoader.Load(_configDir);
        if (devices.Count == 0) { Empty("motor_devices.json"); return; }

        var table = NewTable("Motion devices",
            "Name", "Id", "Controller", "IP", "Port", "Axes", "Positions", "Enabled");
        foreach (var d in devices)
            table.AddRow(
                Markup.Escape(d.Name), d.Id.ToString(), Markup.Escape(d.ControllerType),
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
            table.AddRow(
                Markup.Escape(d.Device.Name), d.Device.DeviceId.ToString(),
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
            table.AddRow(
                Markup.Escape(s.Name), Pin(s.Output), Pin(s.ExtendedSensor),
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
            table.AddRow(
                Markup.Escape(d.Name), Markup.Escape(d.ControllerType),
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
            table.AddRow(
                Markup.Escape(d.Name), Markup.Escape(d.Type), Markup.Escape(d.VisaResource),
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
                table.AddRow(
                    Markup.Escape(w.DeviceName), Markup.Escape(w.PinName), w.Trigger.ToString(),
                    YesNo(w.IsCritical), YesNo(w.Enabled), Markup.Escape(w.AlertTitle));
        AnsiConsole.Write(table);

        var ea = config.EmergencyActions;
        AnsiConsole.MarkupLine(ea == null
            ? "[grey]Emergency actions: passive mode (none)[/]"
            : $"Emergency actions: stopMotion={ea.StopMotion}, retractPneumatics={ea.RetractPneumatics}, " +
              $"cancelExecution={ea.CancelExecution}, blockNewExecution={ea.BlockNewExecution}, " +
              $"killIO={ea.KillIO != null}");
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
                table.AddRow(
                    Markup.Escape(r.Name), YesNo(r.Enabled),
                    Markup.Escape(string.Join(",", r.TargetDevices)),
                    r.AxisBlock.ToString(), r.Layer.ToString(), r.Conditions.Count.ToString());
        AnsiConsole.Write(table);
    }

    // --- helpers ---

    private static Table NewTable(string title, params string[] columns)
    {
        var table = new Table().Border(TableBorder.Rounded).Title($"[aqua]{title}[/]");
        foreach (var c in columns)
            table.AddColumn(c);
        return table;
    }

    private static void Empty(string file)
        => AnsiConsole.MarkupLineInterpolated($"[yellow]No {file} (or no entries) in this directory.[/]");

    private static string YesNo(bool value) => value ? "yes" : "[grey]no[/]";

    private static string Pin(IOPinReference reference)
        => string.IsNullOrEmpty(reference.PinName)
            ? "[grey]—[/]"
            : Markup.Escape($"{reference.DeviceName}.{reference.PinName}");
}
