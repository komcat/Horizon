using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Horizon.Workflow.Migration;

/// <summary>How serious a single conversion finding is.</summary>
public enum ConversionSeverity
{
    /// <summary>Informational — a parameter was renamed or dropped harmlessly.</summary>
    Info,
    /// <summary>The step converted but some behaviour may differ.</summary>
    Warning,
    /// <summary>The step type has no Horizon equivalent yet — emitted as a disabled no-op.</summary>
    Unsupported,
}

/// <summary>One finding from converting a bobcat recipe or program.</summary>
public sealed record ConversionIssue(
    string Source,
    string StepId,
    string StepType,
    ConversionSeverity Severity,
    string Detail);

/// <summary>Outcome of converting one bobcat recipe into a <see cref="WorkflowProgram"/>.</summary>
public sealed record RecipeConversionResult(
    WorkflowProgram Program,
    IReadOnlyList<ConversionIssue> Issues)
{
    /// <summary>True when at least one step had no Horizon op (a no-op placeholder was emitted).</summary>
    public bool HasUnsupported => Issues.Any(i => i.Severity == ConversionSeverity.Unsupported);
}

/// <summary>Outcome of converting one bobcat program into a <see cref="WorkflowDeck"/>.</summary>
public sealed record DeckConversionResult(
    WorkflowDeck Deck,
    IReadOnlyList<ConversionIssue> Issues);

/// <summary>
/// Stage 1 of the bobcat → Horizon recipe migration (see
/// <c>docs/recipe-migration.md</c>): converts a legacy bobcat <em>recipe</em>
/// JSON into a Horizon <see cref="WorkflowProgram"/>, and a bobcat
/// <em>program</em> JSON into a <see cref="WorkflowDeck"/>.
///
/// Pure — string in, objects + issue list out, no file or hardware I/O. The
/// table below covers the Tier A step types (those whose Horizon op already
/// exists); any other type is emitted as a disabled <c>flow.comment</c>
/// placeholder and reported as <see cref="ConversionSeverity.Unsupported"/>.
/// </summary>
public sealed class BobcatRecipeConverter
{
    /// <summary>A bobcat step <c>type</c> mapped onto a Horizon step.</summary>
    private sealed record StepMap(
        string Op,
        string? DeviceKey = null,
        IReadOnlyDictionary<string, string>? Renames = null,
        IReadOnlyDictionary<string, string>? Added = null,
        /// <summary>Optional warning emitted when this step type converts (e.g. a behaviour change).</summary>
        string? Note = null);

    // Bobcat keeps a step's settle delays in the parameter bag under several
    // names; Horizon lifts them onto WorkflowStep.DelayBefore/AfterMs.
    private static readonly string[] DelayBeforeKeys = { "delay_before_ms", "inbounce_delay_ms" };
    private static readonly string[] DelayAfterKeys = { "delay_after_ms", "debounce_delay_ms", "outbounce_delay_ms" };
    // Consumed-and-dropped: Horizon's slide ops wait for completion internally.
    private static readonly string[] DropKeys = { "timeout_ms" };

    // Bobcat's imperative DUT lifecycle steps — Horizon makes the serial a run
    // parameter, so these become no-op comments (see ConvertStep).
    private static readonly HashSet<string> DutLifecycle =
        new(StringComparer.OrdinalIgnoreCase) { "DUT Create", "DUT Load", "DUT Close" };

    // Bobcat XCAN names its feedback by a DataStore channel string, which has no
    // reliable mapping to a Horizon facet — the author must set it after convert.
    private const string ScanFeedbackNote =
        "converts to a coarse-to-fine hexapod scan — set the feedback source " +
        "(feedbackKind / feedbackDevice / feedbackChannel) manually; the bobcat " +
        "DataStore channel name cannot be auto-resolved to a Horizon facet";

    private static readonly IReadOnlyDictionary<string, StepMap> Table =
        new Dictionary<string, StepMap>(StringComparer.OrdinalIgnoreCase)
        {
            // --- pneumatic ---
            ["Slide Extend"]      = new("pneumatic.extend",  "slide_name"),
            ["Slide Retract"]     = new("pneumatic.retract", "slide_name"),
            ["Slide Retract All"] = new("pneumatic.retractall"),

            // --- motion ---
            ["Move to Position"] = new("motion.movetoposition", "device",
                Renames: new Dictionary<string, string> { ["target_position"] = "position" }),
            ["Move Relative"] = new("motion.moverelative", "device"),
            ["Set Velocity"]  = new("motion.setvelocity",  "device"),
            ["Read Position"] = new("motion.readposition", "device",
                Renames: new Dictionary<string, string> { ["key_prefix"] = "prefix", ["save_uvw"] = "saveUvw" }),
            ["Update Position All"] = new("motion.updateposition", "motion_device",
                Renames: new Dictionary<string, string> { ["position_name"] = "position" }),
            ["Update Position Axis"] = new("motion.updatepositionaxis", "motion_device",
                Renames: new Dictionary<string, string>
                {
                    ["position_name"] = "position",
                    ["context_key"]   = "contextKey",
                }),
            ["Register Device Link"] = new("motion.registerdevicelink",
                Renames: new Dictionary<string, string>
                {
                    ["primary_device"] = "primary",
                    ["linked_device"]  = "linked",
                }),
            ["Compensate Device Link"] = new("motion.compensatedevicelink",
                Renames: new Dictionary<string, string>
                {
                    ["primary_device"] = "primary",
                    ["linked_device"]  = "linked",
                }),

            // --- scanning (coarse-to-fine hexapod scan) ---
            // XCAN-class adaptive hill-climb only; bobcat's Grid Scan / Axis Scan
            // / Fast Align are separate algorithms and stay unsupported.
            ["XCAN"] = new("scan.hexapod", "device",
                Renames: new Dictionary<string, string> { ["settling_time_ms"] = "settleMs" },
                Note: ScanFeedbackNote),
            ["XCAN Extended"] = new("scan.hexapod", "device",
                Renames: new Dictionary<string, string>
                {
                    ["step_sizes_um"]    = "stepSizesUm",
                    ["settling_time_ms"] = "settleMs",
                    ["capture_as"]       = "captureAs",
                },
                Note: ScanFeedbackNote),

            // --- IO ---
            ["Set Output"] = new("io.setoutput", "io_device"),
            ["Set Output Name"] = new("io.setoutputbyname", "io_device",
                Renames: new Dictionary<string, string> { ["pin_name"] = "pin" },
                Added:   new Dictionary<string, string> { ["state"] = "true" }),
            ["Clear Output Name"] = new("io.setoutputbyname", "io_device",
                Renames: new Dictionary<string, string> { ["pin_name"] = "pin" },
                Added:   new Dictionary<string, string> { ["state"] = "false" }),
            ["Toggle Output"] = new("io.toggleoutput", "io_device",
                Renames: new Dictionary<string, string>
                {
                    ["pin_name"]      = "pin",
                    ["dwell_time_ms"] = "dwellMs",
                    ["start_state"]   = "startState",
                }),

            // --- TEC / laser ---
            ["Set TEC Temp"] = new("tec.settemperature", "device",
                Renames: new Dictionary<string, string> { ["power_state"] = "power" }),
            ["Set Laser Current"] = new("laser.setcurrent", "device",
                Renames: new Dictionary<string, string> { ["current_ma"] = "current", ["power_state"] = "power" }),
            ["Read Temperature"] = new("tec.readtemperature", "tec_device"),
            ["TEC Connect"]      = new("tec.connect", "device"),

            // --- camera (all four map to the single multi-field setExposure op) ---
            ["Set Exposure"] = new("camera.setexposure", "camera_name",
                Renames: new Dictionary<string, string> { ["value"] = "exposureUs" }),
            ["Set Gain"] = new("camera.setexposure", "camera_name",
                Renames: new Dictionary<string, string> { ["value"] = "gain" }),
            ["Set Auto Exposure"] = new("camera.setexposure", "camera_name",
                Renames: new Dictionary<string, string> { ["value"] = "autoExposure" }),
            ["Set Auto Gain"] = new("camera.setexposure", "camera_name",
                Renames: new Dictionary<string, string> { ["value"] = "autoGain" }),

            // --- SMU ---
            ["SMU Connect"]    = new("smu.connect", "device"),
            ["SMU Set Voltage"] = new("smu.setsourcevoltage", "device",
                Renames: new Dictionary<string, string> { ["voltage"] = "volts" }),
            ["SMU Set Current"] = new("smu.setsourcecurrent", "device",
                Renames: new Dictionary<string, string> { ["current"] = "amps" }),
            ["SMU Set Compliance"] = new("smu.setcompliance", "device",
                Renames: new Dictionary<string, string> { ["limit"] = "value" }),
            ["SMU Output ON"]  = new("smu.outputon",  "device"),
            ["SMU Output OFF"] = new("smu.outputoff", "device"),
            ["SMU Read Current"] = new("smu.readcurrent", "device"),
            ["SMU Read Voltage"] = new("smu.readvoltage", "device"),

            // --- PSU (PSU Output On/Off is special-cased — see ConvertStep) ---
            ["PSU Connect"] = new("psu.connect", "psu_device"),
            ["PSU Set Voltage"] = new("psu.setvoltage", "psu_device",
                Renames: new Dictionary<string, string> { ["voltage"] = "volts" }),
            ["PSU Set Current"] = new("psu.setcurrentlimit", "psu_device",
                Renames: new Dictionary<string, string> { ["current"] = "amps" }),
            ["PSU Read Voltage"] = new("psu.readvoltage", "psu_device"),
            ["PSU Read Current"] = new("psu.readcurrent", "psu_device"),

            // --- lighting (bobcat addresses channels by name) ---
            ["Light: Set Intensity"] = new("lighting.setnamedchannel", "device_name"),
            ["Light: Turn On"] = new("lighting.setnamedchannel", "device_name",
                Added: new Dictionary<string, string> { ["intensity"] = "100" }),
            ["Light: Turn Off"] = new("lighting.setnamedchannel", "device_name",
                Added: new Dictionary<string, string> { ["intensity"] = "0" }),
            ["Light: All On"]  = new("lighting.setall", "device_name"),
            ["Light: All Off"] = new("lighting.setall", "device_name",
                Added: new Dictionary<string, string> { ["intensity"] = "0" }),
            ["Light: Configure"] = new("lighting.configuregroup", "device_name"),

            // --- DUT test records (Create/Load/Close are special-cased — the
            //     lifecycle is implicit; the serial is a run parameter) ---
            ["DUT Insert String"] = new("dut.record",
                Added: new Dictionary<string, string> { ["type"] = "string" }),
            ["DUT Insert Double"] = new("dut.record",
                Added: new Dictionary<string, string> { ["type"] = "double" }),
            ["DUT Insert Integer"] = new("dut.record",
                Added: new Dictionary<string, string> { ["type"] = "integer" }),
            ["DUT Insert from Context"] = new("dut.recordfromcontext",
                Renames: new Dictionary<string, string>
                {
                    ["entry_key"]   = "key",
                    ["context_key"] = "from",
                    ["data_type"]   = "type",
                }),
            ["DUT Bulk Insert from Context"] = new("dut.recordgroup",
                Renames: new Dictionary<string, string> { ["entry_name"] = "group" }),

            // --- flow / system ---
            ["Wait"] = new("flow.wait", Renames: new Dictionary<string, string> { ["duration_ms"] = "ms" }),
            ["Log Message"] = new("flow.log"),
            ["Comment"]     = new("flow.comment"),
            ["Ask User"]    = new("flow.askuser"),
            ["Loop Start"]  = new("flow.loopstart"),
            ["Loop End"]    = new("flow.loopend"),
            // Conditional If / Sub-Recipe are special-cased (see ConvertStep).
            ["Conditional Else"] = new("flow.else"),
            ["Conditional End"]  = new("flow.endif"),

            // --- system control ---
            ["Stop Sequence"] = new("flow.stop"),
            ["Group Start"]   = new("flow.groupstart",
                Renames: new Dictionary<string, string> { ["group_name"] = "name" }),
            ["Group End"]     = new("flow.groupend"),
            ["Clear Context"] = new("var.clear"),
            ["Manual Adjust"] = new("flow.askuser",
                Added: new Dictionary<string, string> { ["title"] = "Manual Adjust" },
                Note: "Manual Adjust converts to a confirm prompt — the operator jogs via the " +
                      "Machine page, then confirms; jog step-size and reference images are not carried over"),
        };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Converts one bobcat recipe JSON document into a <see cref="WorkflowProgram"/>.</summary>
    public RecipeConversionResult ConvertRecipe(string json)
    {
        var recipe = JsonSerializer.Deserialize<BobcatRecipe>(json, ReadOptions)
            ?? throw new FormatException("recipe JSON deserialized to null");

        var name = string.IsNullOrWhiteSpace(recipe.RecipeName) ? "Converted Recipe" : recipe.RecipeName;
        var issues = new List<ConversionIssue>();
        var steps = new List<WorkflowStep>();

        foreach (var src in recipe.Steps)
            steps.Add(ConvertStep(name, src, issues));

        return new RecipeConversionResult(
            new WorkflowProgram { Name = name, Steps = steps }, issues);
    }

    /// <summary>Converts one bobcat program JSON document into a <see cref="WorkflowDeck"/>.</summary>
    public DeckConversionResult ConvertProgram(string json)
    {
        var program = JsonSerializer.Deserialize<BobcatProgram>(json, ReadOptions)
            ?? throw new FormatException("program JSON deserialized to null");

        var name = string.IsNullOrWhiteSpace(program.ProgramName) ? "Converted Program" : program.ProgramName;
        var issues = new List<ConversionIssue>();
        var entries = new List<DeckEntry>();

        foreach (var r in program.Recipes)
        {
            // The deck references a program by name; bobcat's display_name is
            // that name (it matches the child recipe's recipe_name). Fall back
            // to the recipe file's stem when display_name is blank.
            var programName = !string.IsNullOrWhiteSpace(r.DisplayName)
                ? r.DisplayName
                : Path.GetFileNameWithoutExtension(r.RecipeFile);

            if (string.IsNullOrWhiteSpace(programName))
            {
                issues.Add(new ConversionIssue(name, "", "recipe-ref",
                    ConversionSeverity.Warning, "program entry has no display_name or recipe_file — skipped"));
                continue;
            }

            // Bobcat programs have no inter-recipe conditions; every link is
            // unconditional ("previous finished, nothing failed").
            entries.Add(new DeckEntry { Program = programName, Enabled = r.Enabled, Condition = "" });
        }

        return new DeckConversionResult(
            new WorkflowDeck { Name = name, Entries = entries }, issues);
    }

    private static WorkflowStep ConvertStep(string recipe, BobcatStep src, List<ConversionIssue> issues)
    {
        // PSU Output On/Off picks its op from the output_state parameter.
        if (src.Type.Equals("PSU Output On/Off", StringComparison.OrdinalIgnoreCase))
        {
            var on = ParseBool(src.Parameters.GetValueOrDefault("output_state"));
            return BuildStep(recipe, src, new StepMap(on ? "psu.outputon" : "psu.outputoff", "psu_device"), issues);
        }

        // Conditional If folds bobcat's context_key/comparison/threshold into a
        // single ConditionEvaluator expression carried on the flow.if step.
        if (src.Type.Equals("Conditional If", StringComparison.OrdinalIgnoreCase))
            return BuildConditionalIf(recipe, src, issues);

        // Sub-Recipe references a child by file path; Horizon's flow.subProgram
        // references the converted child program by name (its file stem).
        if (src.Type.Equals("Sub-Recipe", StringComparison.OrdinalIgnoreCase))
        {
            var childName = Path.GetFileNameWithoutExtension(
                src.Parameters.GetValueOrDefault("recipe_file", string.Empty));
            if (string.IsNullOrWhiteSpace(childName))
                issues.Add(new ConversionIssue(recipe, src.Id, src.Type, ConversionSeverity.Warning,
                    "Sub-Recipe has no recipe_file — the sub-program reference is empty"));
            return new WorkflowStep
            {
                Id = src.Id,
                Name = src.Nickname,
                Op = "flow.subprogram",
                Device = string.Empty,
                Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["program"] = childName,
                },
                Enabled = src.Enabled,
            };
        }

        // DUT Create/Load/Close — the lifecycle is implicit in Horizon (the
        // serial is a run parameter), so these drop to a no-op comment.
        if (DutLifecycle.Contains(src.Type))
        {
            issues.Add(new ConversionIssue(recipe, src.Id, src.Type, ConversionSeverity.Info,
                "DUT lifecycle is implicit in Horizon — the serial is a run parameter; step dropped"));
            return new WorkflowStep
            {
                Id = src.Id,
                Name = string.IsNullOrEmpty(src.Nickname) ? src.Type : src.Nickname,
                Op = "flow.comment",
                Device = string.Empty,
                Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["_bobcatType"] = src.Type,
                },
                Enabled = src.Enabled,
            };
        }

        if (Table.TryGetValue(src.Type, out var map))
            return BuildStep(recipe, src, map, issues);

        // No mapping — emit a disabled flow.comment placeholder that keeps the
        // original type + parameters so the recipe survives a round-trip, and
        // report it so it shows up in the conversion report.
        var detail = src.Type.Equals("Read DataStore", StringComparison.OrdinalIgnoreCase)
            ? "DataStore is not ported — Horizon reads measurements directly from the facet; " +
              "replace with measurement.read / smu.readCurrent / psu.readCurrent for the relevant device"
            : "no Horizon op — emitted as a disabled placeholder";
        issues.Add(new ConversionIssue(recipe, src.Id, src.Type, ConversionSeverity.Unsupported, detail));

        var preserved = new Dictionary<string, string>(src.Parameters) { ["_bobcatType"] = src.Type };
        return new WorkflowStep
        {
            Id = src.Id,
            Name = $"[UNSUPPORTED: {src.Type}] {src.Nickname}".Trim(),
            Op = "flow.comment",
            Device = string.Empty,
            Parameters = preserved,
            Enabled = false,
        };
    }

    private static WorkflowStep BuildStep(
        string recipe, BobcatStep src, StepMap map, List<ConversionIssue> issues)
    {
        var device = string.Empty;
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int delayBefore = 0, delayAfter = 0;

        foreach (var (key, value) in src.Parameters)
        {
            if (map.DeviceKey != null && key.Equals(map.DeviceKey, StringComparison.OrdinalIgnoreCase))
            {
                device = value;
                continue;
            }
            if (DelayBeforeKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                delayBefore = Math.Max(delayBefore, ParseInt(value));
                continue;
            }
            if (DelayAfterKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                delayAfter = Math.Max(delayAfter, ParseInt(value));
                continue;
            }
            if (DropKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
                continue;

            var outKey = map.Renames != null && map.Renames.TryGetValue(key, out var renamed) ? renamed : key;
            parameters[outKey] = value;
        }

        if (map.Added != null)
            foreach (var (k, v) in map.Added)
                parameters[k] = v;

        if (map.DeviceKey != null && string.IsNullOrWhiteSpace(device))
            issues.Add(new ConversionIssue(recipe, src.Id, src.Type, ConversionSeverity.Warning,
                $"expected device parameter '{map.DeviceKey}' was missing"));

        if (map.Note != null)
            issues.Add(new ConversionIssue(recipe, src.Id, src.Type, ConversionSeverity.Warning, map.Note));

        return new WorkflowStep
        {
            Id = src.Id,
            Name = src.Nickname,
            Op = map.Op,
            Device = device,
            Parameters = parameters,
            Enabled = src.Enabled,
            DelayBeforeMs = delayBefore,
            DelayAfterMs = delayAfter,
        };
    }

    /// <summary>
    /// Builds a <c>flow.if</c> step from bobcat's <c>context_key</c> /
    /// <c>comparison</c> / <c>threshold</c> parameters, translated into a
    /// <see cref="ConditionEvaluator"/> expression on the <c>condition</c> param.
    /// </summary>
    private static WorkflowStep BuildConditionalIf(string recipe, BobcatStep src, List<ConversionIssue> issues)
    {
        var key = src.Parameters.GetValueOrDefault("context_key", string.Empty).Trim();
        var cmp = src.Parameters.GetValueOrDefault("comparison", "==").Trim();
        var threshold = src.Parameters.GetValueOrDefault("threshold", string.Empty).Trim();

        if (string.IsNullOrEmpty(key))
            issues.Add(new ConversionIssue(recipe, src.Id, src.Type, ConversionSeverity.Warning,
                "Conditional If has no context_key — the condition will not evaluate as intended"));

        return new WorkflowStep
        {
            Id = src.Id,
            Name = src.Nickname,
            Op = "flow.if",
            Device = string.Empty,
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["condition"] = TranslateComparison(key, cmp, threshold),
            },
            Enabled = src.Enabled,
        };
    }

    /// <summary>
    /// Translates a bobcat comparison (<c>context_key</c>, operator, <c>threshold</c>)
    /// into a <see cref="ConditionEvaluator"/> expression. Bobcat's boolean and
    /// existence operators are expressed with the numeric/string operators the
    /// evaluator supports — note that EXISTS treats an explicitly empty value as
    /// "not present".
    /// </summary>
    private static string TranslateComparison(string key, string comparison, string threshold)
        => comparison.ToUpperInvariant() switch
        {
            "IS_TRUE"    => $"{key} == 1",
            "IS_FALSE"   => $"{key} == 0",
            "EXISTS"     => $"{key} != \"\"",
            "NOT_EXISTS" => $"{key} == \"\"",
            ">" or "<" or ">=" or "<=" or "==" or "!="
                         => $"{key} {comparison} {QuoteIfText(threshold)}",
            _            => $"{key} == {QuoteIfText(threshold)}",
        };

    /// <summary>Quotes a threshold as a string literal unless it parses as a number.</summary>
    private static string QuoteIfText(string value)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
            ? value : $"\"{value}\"";

    private static int ParseInt(string? raw)
        => double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0
            ? (int)d : 0;

    private static bool ParseBool(string? raw)
        => (raw ?? string.Empty).Trim().ToLowerInvariant() is "true" or "1" or "on" or "yes";

    // --- bobcat JSON shapes (snake_case via the naming policy) ---

    private sealed class BobcatRecipe
    {
        public string RecipeName { get; set; } = string.Empty;
        public List<BobcatStep> Steps { get; set; } = new();
    }

    private sealed class BobcatStep
    {
        public string Id { get; set; } = string.Empty;
        public string Nickname { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
        public Dictionary<string, string> Parameters { get; set; } = new();
    }

    private sealed class BobcatProgram
    {
        public string ProgramName { get; set; } = string.Empty;
        public List<BobcatProgramEntry> Recipes { get; set; } = new();
    }

    private sealed class BobcatProgramEntry
    {
        public string DisplayName { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
        public string RecipeFile { get; set; } = string.Empty;
    }
}
