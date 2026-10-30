using System.Text.Json;
using System.Text.Json.Nodes;
using Horizon.Common;
using Horizon.Config;
using Horizon.IO;
using Horizon.IO.Models;
using Horizon.Motion;
using Horizon.Motion.Models;
using Horizon.Pneumatic;
using Horizon.Pneumatic.Models;
using Horizon.Safety.Models;

namespace Horizon.ConfigTool;

/// <summary>One Horizon config file the FireDog import produces.</summary>
internal sealed record ImportedFile(string FileName, string Domain, int Entries);

/// <summary>
/// The result of translating a FireDog machine's hardware config: the Horizon files, already
/// written into <see cref="StagingDir"/> by Horizon's own loaders (so the format is exactly what
/// the engine reads), plus notes on everything that was not — or not fully — carried over.
/// Nothing touches the real config directory until <see cref="FireDogImport.Apply"/>.
/// </summary>
internal sealed record FireDogImportPlan(string SourceDir, string StagingDir, List<ImportedFile> Files, List<string> Notes);

/// <summary>How <see cref="FireDogImport.Apply"/> treats a target file that already exists.</summary>
internal enum ImportOverwrite { SkipExisting, BackupAndOverwrite }

/// <summary>The outcome of <see cref="FireDogImport.Apply"/>.</summary>
internal sealed record FireDogImportResult(List<string> Written, List<string> Skipped, string? BackupDir);

/// <summary>
/// Translates a FireDog machine root (<c>D:\ProgramData\FireDog</c>) into Horizon config files —
/// see <c>docs/dlam-ui.md</c> §2. Reads <c>config\hardware.json</c>, <c>motion_transforms.json</c>,
/// and the <c>pivot</c> / <c>tower_light</c> files (<c>.local.json</c> first); never writes to
/// FireDog's folder. Covers motion, Ezi-IO, IO watch, pneumatics, safety watch, camera, SMU
/// (Keithley), power supplies, CCS lighting, plus FAAM-format jog transforms, hexapod pivots and
/// tower light. Product data (positions, maps, vision, grips, registrations) is a later phase.
/// </summary>
internal static class FireDogImport
{
    public const string DefaultRoot = @"D:\ProgramData\FireDog";

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// Builds the plan. <paramref name="root"/> is the FireDog machine root or its <c>config</c>
    /// folder. Throws <see cref="FileNotFoundException"/> when no <c>hardware.json</c> is found.
    /// </summary>
    public static FireDogImportPlan Build(string root)
    {
        var configDir = Directory.Exists(Path.Combine(root, "config")) && File.Exists(Path.Combine(root, "config", "hardware.json"))
            ? Path.Combine(root, "config")
            : root;
        var hardwarePath = Path.Combine(configDir, "hardware.json");
        if (!File.Exists(hardwarePath))
            throw new FileNotFoundException($"No FireDog hardware.json under '{root}' (looked in '{configDir}').", hardwarePath);

        var hw = Parse(hardwarePath) as JsonObject
                 ?? throw new InvalidDataException($"{hardwarePath} is not a JSON object.");
        var staging = Path.Combine(Path.GetTempPath(), "horizon-firedog-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);

        var files = new List<ImportedFile>();
        var notes = new List<string>();

        ImportMotion(hw, staging, files, notes);
        var ioDevices = ImportIo(hw, staging, files, notes);
        ImportIoWatch(hw, staging, files);
        ImportPneumatics(hw, staging, files, notes);
        ImportSafety(hw, staging, files, notes);
        ImportCameras(hw, staging, files, notes);
        ImportInstruments(hw, staging, files, notes);
        ImportJogTransforms(configDir, staging, files, notes);
        ImportPivots(configDir, staging, files, notes);
        ImportTowerLight(configDir, staging, files, notes, ioDevices);

        return new FireDogImportPlan(configDir, staging, files, notes);
    }

    /// <summary>
    /// Copies the staged files into <paramref name="targetDir"/>. Existing files are skipped, or —
    /// with <see cref="ImportOverwrite.BackupAndOverwrite"/> — copied to a <c>_backup-&lt;stamp&gt;</c>
    /// folder first. The staging folder is deleted afterwards.
    /// </summary>
    public static FireDogImportResult Apply(FireDogImportPlan plan, string targetDir, ImportOverwrite overwrite)
    {
        var written = new List<string>();
        var skipped = new List<string>();
        string? backupDir = null;
        Directory.CreateDirectory(targetDir);
        try
        {
            foreach (var f in plan.Files)
            {
                var target = Path.Combine(targetDir, f.FileName);
                if (File.Exists(target))
                {
                    if (overwrite == ImportOverwrite.SkipExisting)
                    {
                        skipped.Add(f.FileName);
                        continue;
                    }
                    backupDir ??= Path.Combine(targetDir, "_backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                    Directory.CreateDirectory(backupDir);
                    File.Copy(target, Path.Combine(backupDir, f.FileName), overwrite: true);
                }
                File.Copy(Path.Combine(plan.StagingDir, f.FileName), target, overwrite: true);
                written.Add(f.FileName);
            }
        }
        finally
        {
            Discard(plan);
        }
        return new FireDogImportResult(written, skipped, backupDir);
    }

    /// <summary>Deletes the plan's staging folder (best-effort).</summary>
    public static void Discard(FireDogImportPlan plan)
    {
        try { if (Directory.Exists(plan.StagingDir)) Directory.Delete(plan.StagingDir, recursive: true); }
        catch (IOException) { /* temp folder — leave it for the OS */ }
        catch (UnauthorizedAccessException) { /* same */ }
    }

    // --- motion ----------------------------------------------------------------------------

    private static void ImportMotion(JsonObject hw, string staging, List<ImportedFile> files, List<string> notes)
    {
        var devices = new List<MotionDevice>();
        foreach (var d in Items(hw, "motion_devices"))
        {
            var type = Str(d, "controller_type").ToUpperInvariant();
            if (type is not ("ACS" or "PI"))
            {
                notes.Add($"Motion '{Name(d)}': controller type '{type}' not supported — skipped.");
                continue;
            }
            devices.Add(new MotionDevice
            {
                Id = Int(d, "device_id"),
                Name = Name(d),
                IpAddress = Str(d, "ip_address"),
                Port = Int(d, "port", type == "PI" ? 50000 : 701),
                ControllerType = type,
                InstalledAxes = Str(d, "axes").Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(a => a.ToUpperInvariant()).ToList(),
                Enabled = Bool(d, "enabled", true),
            });
        }
        if (devices.Count == 0) return;
        MotionJsonLoader.Save(devices, staging);
        files.Add(new ImportedFile("motor_devices.json", "Motion", devices.Count));
    }

    // --- IO ----------------------------------------------------------------------------------

    private static HashSet<string> ImportIo(JsonObject hw, string staging, List<ImportedFile> files, List<string> notes)
    {
        var devices = new List<IODeviceConfig>();
        foreach (var d in Items(hw, "io_devices"))
        {
            var type = Str(d, "type").ToLowerInvariant();
            if (type != "eziio")
            {
                notes.Add($"IO '{Name(d)}' ({type}{(Bool(d, "enabled", true) ? "" : ", disabled")}): no Horizon adapter — skipped.");
                continue;
            }
            var pins = d["pins"] as JsonObject;
            devices.Add(new IODeviceConfig
            {
                Device = new EziIODeviceConfig
                {
                    DeviceId = Int(d, "device_id"),
                    Name = Name(d),
                    IpAddress = Str(d, "ip_address"),
                    InputCount = Int(d, "input_count"),
                    OutputCount = Int(d, "output_count"),
                    Enabled = Bool(d, "enabled", true),
                    DeviceType = EziDeviceType.IOModule,
                },
                InputPins = Pins(pins, "inputs"),
                OutputPins = Pins(pins, "outputs"),
            });
        }
        if (devices.Count > 0)
        {
            EziIOJsonLoader.Save(devices, staging);
            files.Add(new ImportedFile("io_devices.json", "IO", devices.Count));
        }
        return devices.Select(d => d.Device.Name).ToHashSet(Ci);
    }

    private static List<IOPinConfig> Pins(JsonObject? pins, string key)
        => Items(pins, key).Select(p => new IOPinConfig { Name = Str(p, "name"), Pin = Int(p, "pin") }).ToList();

    private static void ImportIoWatch(JsonObject hw, string staging, List<ImportedFile> files)
    {
        if (hw["io_watch"] is not JsonObject w) return;
        var bindings = Items(w, "bindings").Select(b => new IoBinding
        {
            Name = Str(b, "name"),
            Input = Endpoint(b["input"]),
            Output = Endpoint(b["output"]),
            Indicator = b["indicator"] is JsonObject ? Endpoint(b["indicator"]) : null,
            Trigger = Str(b, "trigger").ToLowerInvariant() switch
            {
                "falling" => IoEdge.Falling,
                "both" => IoEdge.Both,
                _ => IoEdge.Rising,
            },
            DebounceMs = Int(b, "debounce_ms", 100),
            Enabled = Bool(b, "enabled", true),
        }).ToList();
        ConfigLoader.SaveJson(staging, "io_watch", new IoWatchConfig
        {
            Enabled = Bool(w, "enabled", true),
            PollingIntervalMs = Int(w, "polling_interval_ms", 50),
            Bindings = bindings,
        });
        files.Add(new ImportedFile("io_watch.json", "IO watch", bindings.Count));
    }

    private static IoEndpoint Endpoint(JsonNode? n) => new() { Device = Str(n, "device"), PinName = Str(n, "pin_name") };

    // --- pneumatics / safety -----------------------------------------------------------------

    private static void ImportPneumatics(JsonObject hw, string staging, List<ImportedFile> files, List<string> notes)
    {
        var slides = new List<PneumaticConfig>();
        foreach (var s in Items(hw, "pneumatic_slides"))
        {
            if (!Bool(s, "enabled", true))
            {
                notes.Add($"Pneumatic '{Name(s)}' is disabled in FireDog — skipped (Horizon slides have no enabled flag).");
                continue;
            }
            slides.Add(new PneumaticConfig
            {
                Name = Name(s),
                Output = PinRef(s["output"]),
                ExtendedSensor = PinRef(s["extended_input"]),
                RetractedSensor = PinRef(s["retracted_input"]),
                TimeoutMs = Int(s, "timeout_ms", 5000),
            });
        }
        if (slides.Count == 0) return;
        PneumaticJsonLoader.Save(slides, staging);
        files.Add(new ImportedFile("pneumatic_slides.json", "Pneumatic", slides.Count));
    }

    private static IOPinReference PinRef(JsonNode? n) => new() { DeviceName = Str(n, "device"), PinName = Str(n, "pin") };

    private static void ImportSafety(JsonObject hw, string staging, List<ImportedFile> files, List<string> notes)
    {
        if (hw["safety_watch"] is not JsonObject s) return;
        bool on = Bool(s, "enabled", true);
        if (!on) notes.Add("Safety watch is disabled in FireDog — imported with every input disabled.");
        var inputs = Items(s, "watched_inputs").Select(i => new WatchedInput
        {
            DeviceName = Str(i, "device"),
            PinName = Str(i, "pin_name"),
            Trigger = Str(i, "trigger").ToLowerInvariant() switch
            {
                "rising" => TriggerEdge.Rising,
                "falling" => TriggerEdge.Falling,
                _ => TriggerEdge.Both,
            },
            AlertTitle = Str(i, "alert_title"),
            AlertMessage = Str(i, "alert_message"),
            IsCritical = Bool(i, "critical", false),
            Enabled = on && Bool(i, "enabled", true),
        }).ToList();
        ConfigLoader.SaveJson(staging, "safety_watch", new SafetyWatchConfig
        {
            PollIntervalMs = Int(s, "polling_interval_ms", 50),
            WatchedInputs = inputs,
        });
        files.Add(new ImportedFile("safety_watch.json", "Safety", inputs.Count));
    }

    // --- camera --------------------------------------------------------------------------------

    private static void ImportCameras(JsonObject hw, string staging, List<ImportedFile> files, List<string> notes)
    {
        var cams = new List<CameraConfigEntry>();
        foreach (var c in Items(hw, "cameras"))
        {
            var id = Str(c, "id");
            if (id.Length == 0) id = Name(c);
            var sdk = Str(c, "camera_type").ToUpperInvariant() switch
            {
                "PYLON" => CameraSdkType.Pylon,
                var t when t.StartsWith("IDS", StringComparison.Ordinal) || t == "UEYE" => CameraSdkType.IDS,
                _ => CameraSdkType.Unknown,
            };
            // FireDog stores these as floats (0.001724999980069697); 9 decimals = 1 pm is plenty.
            double mmX = Math.Round(Dbl(c, "pixel_to_mm_x"), 9), mmY = Math.Round(Dbl(c, "pixel_to_mm_y"), 9);
            if (mmY > 0 && Math.Abs(mmX - mmY) > 1e-9)
                notes.Add($"Camera '{id}': pixel size X {mmX} ≠ Y {mmY} mm — Horizon keeps one value; X used.");
            if (Dbl(c, "exposure_us") > 0)
                notes.Add($"Camera '{id}': FireDog exposure {Dbl(c, "exposure_us")} µs not carried over (set per position / vision profile).");
            cams.Add(new CameraConfigEntry
            {
                Name = id,
                SerialNumber = Str(c, "serial_number"),
                SdkType = sdk,
                ConnectionType = Str(c, "connection_type").Equals("ip", StringComparison.OrdinalIgnoreCase) ? "IP" : "USB",
                IpAddress = Str(c, "ip_address"),
                MmPerPixel = mmX,
            });
        }
        if (cams.Count == 0) return;
        CameraConfigJson.Save(cams, staging);
        files.Add(new ImportedFile("camera-config.json", "Camera", cams.Count));
    }

    // --- instruments ---------------------------------------------------------------------------

    private static void ImportInstruments(JsonObject hw, string staging, List<ImportedFile> files, List<string> notes)
    {
        var smus = new List<SmuDeviceConfig>();
        var psus = new List<PowerSupplyConfig>();
        var lights = new List<LightingControllerConfig>();

        foreach (var i in Items(hw, "instruments"))
        {
            var type = Str(i, "type").ToLowerInvariant();
            var name = Name(i);
            bool enabled = Bool(i, "enabled", true);
            if (type.StartsWith("keithley_", StringComparison.Ordinal))
            {
                var channels = Items(i, "enabled_channels").Select(n => n.GetValue<int>()).ToList();
                smus.Add(new SmuDeviceConfig
                {
                    Name = name,
                    Type = type,
                    VisaResource = Str(i, "visa_resource"),
                    Enabled = enabled,
                    ChannelCount = Int(i, "channel_count", Math.Max(1, channels.Count)),
                    EnabledChannels = channels,
                    MeasurementNplc = Dbl(i, "measurement_nplc", 1.0),
                    AutoRange = Bool(i, "auto_range", true),
                    FixedRange = Dbl(i, "fixed_range"),
                });
                if (Dbl(i, "bias_voltage") != 0)
                    notes.Add($"SMU '{name}': FireDog bias {Dbl(i, "bias_voltage")} V is not a Horizon config field — set it in the program (smu.setSourceVoltage + smu.outputOn on every channel; a 2602B output that is off reads ~0 A).");
            }
            else if (type is "unit_t_udp3305s" or "unitt_udp3305s" or "udp3305s" or "udp3305")
            {
                if (!enabled)
                {
                    notes.Add($"Power supply '{name}' is disabled in FireDog — skipped.");
                    continue;
                }
                psus.Add(new PowerSupplyConfig
                {
                    Name = name,
                    Type = "unit_t_udp3305s",
                    Ip = Str(i, "ip_address"),
                    Port = Int(i, "port", 5025),
                    VisaResource = Str(i, "visa_resource"),
                    Channels = i["channels"] is JsonValue ? Int(i, "channels", 1) : 1,
                });
            }
            else if (type == "ccs_pd3")
            {
                if (Bool(i, "simulated", false))
                    notes.Add($"Lighting '{name}' is simulated in FireDog — imported as a real controller.");
                lights.Add(new LightingControllerConfig
                {
                    DeviceId = name,
                    DeviceName = name,
                    ControllerType = "CCS_PD3",
                    IpAddress = Str(i, "ip_address"),
                    Port = Int(i, "port", 40001),
                    Enabled = enabled,
                    Channels = Items(i, "channels").Select(ch => new LightingChannelConfig
                    {
                        Channel = Int(ch, "channel"),
                        Name = Str(ch, "name"),
                        Color = Str(ch, "color"),
                        Group = Str(ch, "group"),
                        Camera = Str(ch, "camera"),
                        Enabled = Bool(ch, "enabled", true),
                    }).ToList(),
                });
            }
            else
            {
                notes.Add($"Instrument '{name}' ({type}): not covered by the importer — add it from its ConfigTool menu.");
            }
        }

        if (smus.Count > 0)
        {
            SmuJsonLoader.Save(smus, staging);
            files.Add(new ImportedFile("smu_devices.json", "SMU", smus.Count));
        }
        if (psus.Count > 0)
        {
            PowerSupplyJsonLoader.Save(psus, staging);
            files.Add(new ImportedFile("power_supply_devices.json", "Power supply", psus.Count));
        }
        if (lights.Count > 0)
        {
            LightingJsonLoader.Save(lights, staging);
            files.Add(new ImportedFile("lighting_devices.json", "Lighting", lights.Count));
        }
    }

    // --- FAAM-format machine files -------------------------------------------------------------
    // These mirror FAAM.UI's own records (JogDeviceTransform, HexapodPivotSetting,
    // TowerLightConfig) — the ConfigTool cannot reference the WPF head.

    private sealed record JogMatrixDto(double M11, double M12, double M13, double M21, double M22, double M23, double M31, double M32, double M33);

    private sealed record JogTransformDto(string DeviceId, JogMatrixDto Matrix);

    private sealed record PivotDto(double R, double S, double T);

    private sealed record TowerLightDto(bool Enabled, string Device, string Green, string Yellow, string Red,
        string Buzzer, int SafetyBeeps, double DownBuzzerSeconds);

    private static void ImportJogTransforms(string configDir, string staging, List<ImportedFile> files, List<string> notes)
    {
        var path = Path.Combine(configDir, "motion_transforms.json");
        if (!File.Exists(path)) return;
        var list = new List<JogTransformDto>();
        bool hadRotation = false;
        foreach (var t in (Parse(path) as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            var m = Get(t, "Matrix");
            hadRotation |= Get(t, "Rotation") != null;
            list.Add(new JogTransformDto(Str(t, "DeviceId"), new JogMatrixDto(
                Dbl(m, "M11", 1), Dbl(m, "M12"), Dbl(m, "M13"),
                Dbl(m, "M21"), Dbl(m, "M22", 1), Dbl(m, "M23"),
                Dbl(m, "M31"), Dbl(m, "M32"), Dbl(m, "M33", 1))));
        }
        if (list.Count == 0) return;
        if (hadRotation)
            notes.Add("Jog transforms: FireDog's 'Rotation' matrices (its device-registration R) are not carried over — re-teach the registrations in Horizon.");
        ConfigLoader.SaveJson(staging, "jog_transforms", list);
        files.Add(new ImportedFile("jog_transforms.json", "Jog transforms", list.Count));
    }

    private static void ImportPivots(string configDir, string staging, List<ImportedFile> files, List<string> notes)
    {
        if (ParseLocal(configDir, "pivot") is not JsonObject root || root["pivots"] is not JsonObject pivots) return;
        var all = new Dictionary<string, PivotDto>(Ci);
        foreach (var (device, p) in pivots)
            all[device] = new PivotDto(Dbl(p, "x"), Dbl(p, "y"), Dbl(p, "z"));
        if (all.Count == 0) return;
        ConfigLoader.SaveJson(staging, "faam_hexapod_pivot", all);
        files.Add(new ImportedFile("faam_hexapod_pivot.json", "Hexapod pivots", all.Count));
        notes.Add("Hexapod pivots go to faam_hexapod_pivot.json (FAAM's file name) — the DLAM head will read the same format.");
    }

    private static void ImportTowerLight(string configDir, string staging, List<ImportedFile> files, List<string> notes,
        HashSet<string> ioDevices)
    {
        if (ParseLocal(configDir, "tower_light") is not JsonObject t) return;
        var dto = new TowerLightDto(
            Bool(t, "enabled", true), Str(t, "device"), Str(t, "green"), Str(t, "yellow"), Str(t, "red"),
            Str(t, "buzzer"), Int(t, "safety_beeps", 3), Dbl(t, "down_buzzer_seconds", 3));
        if (!ioDevices.Contains(dto.Device))
            notes.Add($"Tower light: IO device '{dto.Device}' is not among the imported IO devices.");
        ConfigLoader.SaveJson(staging, "faam_tower_light", dto);
        files.Add(new ImportedFile("faam_tower_light.json", "Tower light", 1));
    }

    // --- JSON helpers --------------------------------------------------------------------------

    private static JsonNode? Parse(string path) => JsonNode.Parse(File.ReadAllText(path), documentOptions: ReadOptions);

    /// <summary>FireDog's <c>name.local.json</c> (machine copy) else <c>name.json</c> (shipped default); null if neither.</summary>
    private static JsonNode? ParseLocal(string configDir, string name)
    {
        foreach (var file in new[] { name + ".local.json", name + ".json" })
        {
            var path = Path.Combine(configDir, file);
            if (File.Exists(path)) return Parse(path);
        }
        return null;
    }

    private static IEnumerable<JsonNode> Items(JsonNode? parent, string key)
        => (Get(parent, key) as JsonArray ?? new JsonArray()).Where(n => n != null).Cast<JsonNode>();

    /// <summary>Property lookup, case-insensitive (FireDog writes snake_case; the transforms file PascalCase).</summary>
    private static JsonNode? Get(JsonNode? parent, string key)
    {
        if (parent is not JsonObject o) return null;
        if (o.TryGetPropertyValue(key, out var v)) return v;
        return o.FirstOrDefault(kv => Ci.Equals(kv.Key, key)).Value;
    }

    private static string Name(JsonNode? n)
    {
        var name = Str(n, "name");
        return name.Length > 0 ? name : Str(n, "id");
    }

    private static string Str(JsonNode? n, string key)
        => Get(n, key) is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : string.Empty;

    private static int Int(JsonNode? n, string key, int fallback = 0)
        => Get(n, key) is JsonValue v ? v.TryGetValue<int>(out var i) ? i
            : v.TryGetValue<double>(out var d) ? (int)Math.Round(d) : fallback
            : fallback;

    private static double Dbl(JsonNode? n, string key, double fallback = 0)
        => Get(n, key) is JsonValue v && v.TryGetValue<double>(out var d) ? d : fallback;

    private static bool Bool(JsonNode? n, string key, bool fallback)
        => Get(n, key) is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;
}
