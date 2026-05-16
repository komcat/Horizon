using System.Net;
using Horizon.Common;
using Horizon.Config;
using Horizon.Core.Interlock;
using Horizon.CurrentMeasurement;
using Horizon.IO;
using Horizon.LaserController;
using Horizon.Motion;
using Horizon.Pneumatic;
using Horizon.Pneumatic.Models;
using Horizon.Safety.Models;

namespace Horizon.ConfigTool;

internal enum IssueSeverity { Error, Warning }

/// <summary>One validation finding for the config set.</summary>
internal sealed record ValidationIssue(IssueSeverity Severity, string Domain, string Message);

/// <summary>
/// Cross-checks a whole config directory — the value a single-file hand-edit
/// cannot give: duplicate names, and references between files (a pneumatic
/// slide pointing at an IO pin, a safety input, an interlock's slides/devices).
/// </summary>
internal sealed class ConfigValidator
{
    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;

    private readonly string _dir;

    public ConfigValidator(string configDir) => _dir = configDir;

    public List<ValidationIssue> Validate()
    {
        var issues = new List<ValidationIssue>();

        var io = EziIOJsonLoader.Load(_dir);
        var motion = MotionJsonLoader.Load(_dir);
        var pneumatic = PneumaticJsonLoader.Load(_dir);
        var safety = ConfigLoader.LoadJson<SafetyWatchConfig>(_dir, "safety_watch");
        var interlock = ConfigLoader.LoadJson<InterlockConfig>(_dir, "motion_interlocks");
        var laser = LaserControllerJsonLoader.Load(_dir);
        var measurement = CurrentMeasurementJsonLoader.Load(_dir);
        var lighting = LightingJsonLoader.Load(_dir);
        var tec = TecJsonLoader.Load(_dir);
        var smu = SmuJsonLoader.Load(_dir);
        var psu = PowerSupplyJsonLoader.Load(_dir);
        var tcp = TcpClientJsonLoader.Load(_dir);

        // --- duplicate names within a domain ---
        Duplicates(issues, "Motion", motion.Select(d => d.Name));
        Duplicates(issues, "IO", io.Select(d => d.Device.Name));
        Duplicates(issues, "Pneumatic", pneumatic.Select(s => s.Name));
        Duplicates(issues, "Laser", laser.Select(d => d.Name));
        Duplicates(issues, "Measurement", measurement.Select(d => d.Name));
        Duplicates(issues, "Lighting", lighting.Select(c => c.DeviceId));
        Duplicates(issues, "TEC", tec.Select(d => d.Name));
        Duplicates(issues, "SMU", smu.Select(d => d.Name));
        Duplicates(issues, "Power supply", psu.Select(d => d.Name));
        Duplicates(issues, "TCP clients", tcp.Select(c => c.Id));

        // --- IO device + pin lookup ---
        var ioDevices = new HashSet<string>(io.Select(d => d.Device.Name), Ci);
        var ioPins = io.ToDictionary(
            d => d.Device.Name,
            d => new HashSet<string>(d.InputPins.Concat(d.OutputPins).Select(p => p.Name), Ci),
            Ci);

        // --- pneumatic slides → IO references ---
        foreach (var s in pneumatic)
        {
            CheckPin(issues, "Pneumatic", $"slide '{s.Name}' output", s.Output, ioDevices, ioPins);
            CheckPin(issues, "Pneumatic", $"slide '{s.Name}' extended sensor", s.ExtendedSensor, ioDevices, ioPins);
            CheckPin(issues, "Pneumatic", $"slide '{s.Name}' retracted sensor", s.RetractedSensor, ioDevices, ioPins);
        }

        // --- safety watched inputs → IO references ---
        if (safety != null)
            foreach (var w in safety.WatchedInputs)
                CheckPin(issues, "Safety", $"watched input '{w.DeviceName}.{w.PinName}'",
                    w.DeviceName, w.PinName, ioDevices, ioPins);

        // --- interlock rules → motion devices + pneumatic slides ---
        if (interlock != null)
        {
            var motionNames = new HashSet<string>(motion.Select(d => d.Name), Ci);
            var slideNames = new HashSet<string>(pneumatic.Select(s => s.Name), Ci);
            foreach (var rule in interlock.Rules)
            {
                foreach (var device in rule.TargetDevices)
                    if (!motionNames.Contains(device))
                        issues.Add(new(IssueSeverity.Error, "Interlocks",
                            $"rule '{rule.Name}' targets motion device '{device}' which does not exist."));

                foreach (var slide in rule.Conditions.SelectMany(c => c.Slides))
                    if (!slideNames.Contains(slide))
                        issues.Add(new(IssueSeverity.Error, "Interlocks",
                            $"rule '{rule.Name}' references slide '{slide}' which does not exist."));
            }
        }

        // --- IP-address sanity (warnings) ---
        CheckIps(issues, "Motion", motion.Select(d => (d.Name, d.IpAddress)));
        CheckIps(issues, "IO", io.Select(d => (d.Device.Name, d.Device.IpAddress)));
        CheckIps(issues, "Lighting", lighting.Select(c => (c.DeviceId, c.IpAddress)));
        CheckIps(issues, "Power supply", psu.Select(d => (d.Name, d.Ip)));

        return issues;
    }

    private static void Duplicates(List<ValidationIssue> issues, string domain, IEnumerable<string> names)
    {
        var seen = new HashSet<string>(Ci);
        foreach (var name in names)
            if (!string.IsNullOrWhiteSpace(name) && !seen.Add(name))
                issues.Add(new(IssueSeverity.Error, domain, $"duplicate name '{name}'."));
    }

    private static void CheckPin(List<ValidationIssue> issues, string domain, string what,
        IOPinReference reference, HashSet<string> ioDevices, Dictionary<string, HashSet<string>> ioPins)
        => CheckPin(issues, domain, what, reference.DeviceName, reference.PinName, ioDevices, ioPins);

    private static void CheckPin(List<ValidationIssue> issues, string domain, string what,
        string device, string pin, HashSet<string> ioDevices, Dictionary<string, HashSet<string>> ioPins)
    {
        if (string.IsNullOrWhiteSpace(pin))
        {
            issues.Add(new(IssueSeverity.Warning, domain, $"{what} has no pin name set."));
            return;
        }
        if (!ioDevices.Contains(device))
        {
            issues.Add(new(IssueSeverity.Error, domain,
                $"{what} references IO device '{device}' which does not exist."));
            return;
        }
        if (ioPins.TryGetValue(device, out var pins) && !pins.Contains(pin))
            issues.Add(new(IssueSeverity.Warning, domain,
                $"{what} references pin '{pin}' not named on IO device '{device}'."));
    }

    private static void CheckIps(List<ValidationIssue> issues, string domain,
        IEnumerable<(string Name, string Ip)> items)
    {
        foreach (var (name, ip) in items)
            if (!string.IsNullOrWhiteSpace(ip) && !IPAddress.TryParse(ip, out _))
                issues.Add(new(IssueSeverity.Warning, domain,
                    $"'{name}' has IP address '{ip}' which is not a valid address."));
    }
}
