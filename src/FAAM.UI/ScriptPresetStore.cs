using System;
using System.Collections.Generic;
using System.Linq;
using Horizon.Common;

namespace FAAM.UI;

/// <summary>
/// A named way to run a <c>.moon</c> script: the script (active product) plus the argument
/// text it receives as its global <c>args</c> table — e.g. "FAU climb - fine" =
/// <c>FAU_PowerClimb</c> with <c>{ sets = {{2,0.5},{1,0.5},{0.5,0.25}} }</c>. One script file,
/// several configurations.
/// </summary>
public sealed class ScriptPreset
{
    public string Name { get; set; } = "";
    public string Script { get; set; } = "";
    /// <summary>Lua table literal (outer braces optional); blank = the script's own defaults.</summary>
    public string Args { get; set; } = "";

    /// <summary>
    /// Run with no early-exit threshold: <see cref="EffectiveArgs"/> adds <c>exit = false</c>
    /// (scripts that support it, e.g. FAU_PowerClimb, then climb every set). Shown as an orange
    /// button on the Run page.
    /// </summary>
    public bool NoExitThreshold { get; set; }

    /// <summary>The argument text actually passed to the script: <see cref="Args"/>, plus
    /// <c>exit = false</c> when <see cref="NoExitThreshold"/> is set.</summary>
    public string EffectiveArgs()
    {
        if (!NoExitThreshold) return Args;
        var inner = (Args ?? "").Trim();
        if (inner.StartsWith('{') && inner.EndsWith('}')) inner = inner[1..^1];
        inner = inner.Trim().TrimEnd(',', ';').TrimEnd();
        // The newline before the added key keeps a trailing '--' comment from swallowing it.
        return inner.Length == 0 ? "{ exit = false }" : $"{{ {inner}\n, exit = false }}";
    }
}

/// <summary>The whole <c>faam_script_presets.json</c> file.</summary>
public sealed class ScriptPresetSettings
{
    public List<ScriptPreset> Presets { get; set; } = new();
}

/// <summary>
/// Machine-global script presets in <c>faam_script_presets.json</c>, edited on Machine › Settings
/// and picked by the Run-page Scanning script buttons. Script names resolve in the active
/// product. Before the file exists the FAU climb + grid scan + W optimize presets are offered as defaults (written on
/// the first Settings save).
/// </summary>
public static class ScriptPresetStore
{
    private const string FileName = "faam_script_presets";

    /// <summary>Raised after every successful <see cref="Save"/> (on the saving thread).</summary>
    public static event Action? Changed;

    /// <summary>The saved presets — trimmed, blank names dropped, first of any duplicate name kept.</summary>
    public static List<ScriptPreset> Load()
    {
        ScriptPresetSettings? s;
        try
        {
            s = ConfigLoader.LoadJson<ScriptPresetSettings>(ConfigLoader.GetConfigDirectory(), FileName);
        }
        catch
        {
            s = null;   // unreadable → defaults
        }
        return Normalize(s?.Presets ?? Defaults());
    }

    /// <summary>The preset with this name (case-insensitive), or null.</summary>
    public static ScriptPreset? Find(string name)
        => string.IsNullOrWhiteSpace(name)
            ? null
            : Load().FirstOrDefault(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Overwrites the file and notifies listeners. Throws on I/O failure.</summary>
    public static void Save(IEnumerable<ScriptPreset> presets)
    {
        ConfigLoader.SaveJson(ConfigLoader.GetConfigDirectory(), FileName,
            new ScriptPresetSettings { Presets = Normalize(presets) });
        Changed?.Invoke();
    }

    private static List<ScriptPreset> Normalize(IEnumerable<ScriptPreset?> presets)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<ScriptPreset>();
        foreach (var p in presets)
        {
            var name = (p?.Name ?? "").Trim();
            if (p == null || name.Length == 0 || !seen.Add(name)) continue;
            list.Add(new ScriptPreset
            {
                Name = name, Script = (p.Script ?? "").Trim(), Args = (p.Args ?? "").Trim(),
                NoExitThreshold = p.NoExitThreshold,
            });
        }
        return list;
    }

    private static List<ScriptPreset> Defaults() => new()
    {
        new() { Name = "FAU climb - coarse", Script = "FAU_PowerClimb",
                Args = "{ sets = {{10,1},{5,1},{2,0.5},{1,0.5},{0.5,0.25}} }" },
        new() { Name = "FAU climb - fine", Script = "FAU_PowerClimb",
                Args = "{ sets = {{2,0.5},{1,0.5},{0.5,0.25}} }" },
        new() { Name = "FAU XY only", Script = "FAU_PowerClimb",
                Args = "{ z = false, sets = {{0,0.5},{0,0.25}} }" },
        new() { Name = "FAU grid scan", Script = "FAU_GridScan", Args = "" },
        new() { Name = "FAU W optimize", Script = "FAU_WOptimize", Args = "" },
    };
}
