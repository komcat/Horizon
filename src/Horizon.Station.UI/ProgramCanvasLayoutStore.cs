using Horizon.Common;

namespace Horizon.Station.UI;

/// <summary>Where one node sits on the Engineer canvas: free X, snapped stage row.</summary>
public sealed class CanvasSpot
{
    public double X { get; set; }
    public int Row { get; set; }

    /// <summary>True when the sequence continues in a new column at this node (shown as an E1 / E2 marker pair).</summary>
    public bool ColumnBreak { get; set; }

    /// <summary>True when this If / Loop / Group opener is folded — the blocks through its closer are hidden.</summary>
    public bool Collapsed { get; set; }
}

/// <summary>
/// Node positions of the Engineer page's program canvas, per product + program, keyed by the
/// block UID (<c>start</c> for the Start node, the step id for a parallel envelope). A view
/// preference only — the program file never carries positions, so a program with no entry
/// here (older file, checked-out version, cloned product) is simply laid out top to bottom.
/// Saved in the config dir as <c>program_canvas_layout.json</c>:
/// <c>{ "&lt;product&gt;": { "&lt;program&gt;": { "&lt;uid&gt;": { "x": 70, "row": 3 } } } }</c>.
/// </summary>
public static class ProgramCanvasLayoutStore
{
    private const string FileName = "program_canvas_layout";

    /// <summary>The saved spots for a program (empty when none / unreadable).</summary>
    public static Dictionary<string, CanvasSpot> Load(string? product, string? program)
    {
        var empty = new Dictionary<string, CanvasSpot>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(product) || string.IsNullOrWhiteSpace(program)) return empty;
        try
        {
            var all = LoadAll();
            var pKey = Find(all.Keys, product);
            if (pKey == null) return empty;
            var gKey = Find(all[pKey].Keys, program);
            if (gKey == null || all[pKey][gKey] is not { } spots) return empty;
            return new Dictionary<string, CanvasSpot>(spots, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            // Cosmetic preference — an unreadable file just means the default layout.
            return empty;
        }
    }

    /// <summary>Replaces the saved spots for a program. Throws on I/O failure.</summary>
    public static void Save(string product, string program, Dictionary<string, CanvasSpot> spots)
    {
        Dictionary<string, Dictionary<string, Dictionary<string, CanvasSpot>>> all;
        try { all = LoadAll(); }
        catch { all = new(); }

        var pKey = Find(all.Keys, product) ?? product;
        if (!all.TryGetValue(pKey, out var programs) || programs == null)
            all[pKey] = programs = new();
        var gKey = Find(programs.Keys, program) ?? program;
        programs[gKey] = spots;
        ConfigLoader.SaveJson(ConfigLoader.GetConfigDirectory(), FileName, all);
    }

    private static Dictionary<string, Dictionary<string, Dictionary<string, CanvasSpot>>> LoadAll()
        => ConfigLoader.LoadJson<Dictionary<string, Dictionary<string, Dictionary<string, CanvasSpot>>>>(
               ConfigLoader.GetConfigDirectory(), FileName) ?? new();

    private static string? Find(IEnumerable<string> keys, string wanted)
        => keys.FirstOrDefault(k => string.Equals(k, wanted, StringComparison.OrdinalIgnoreCase));
}
