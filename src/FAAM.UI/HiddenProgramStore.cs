using Horizon.Common;

namespace FAAM.UI;

/// <summary>
/// Which programs the Engineer page's program list hides, per product — a view preference
/// only (a hidden program still exists, loads and runs). Saved in the config dir as
/// <c>faam_hidden_programs.json</c>: <c>{ "&lt;product&gt;": ["old_test", ...] }</c>.
/// Read fresh at every lookup; an unreadable file means nothing is hidden.
/// </summary>
public static class HiddenProgramStore
{
    private const string FileName = "faam_hidden_programs";

    /// <summary>The hidden program names for <paramref name="product"/> (case-insensitive set).</summary>
    public static HashSet<string> Load(string? product)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(product)) return set;
        try
        {
            var all = ConfigLoader.LoadJson<Dictionary<string, List<string>>>(ConfigLoader.GetConfigDirectory(), FileName);
            var key = all?.Keys.FirstOrDefault(k => string.Equals(k, product, StringComparison.OrdinalIgnoreCase));
            if (key != null && all![key] is { } names) set.UnionWith(names);
        }
        catch
        {
            // Cosmetic preference — an unreadable file just shows everything.
        }
        return set;
    }

    /// <summary>Replaces the hidden set for <paramref name="product"/>. Throws on I/O failure.</summary>
    public static void Save(string product, IEnumerable<string> hidden)
    {
        var dir = ConfigLoader.GetConfigDirectory();
        Dictionary<string, List<string>> all;
        try { all = ConfigLoader.LoadJson<Dictionary<string, List<string>>>(dir, FileName) ?? new(); }
        catch { all = new(); }

        var key = all.Keys.FirstOrDefault(k => string.Equals(k, product, StringComparison.OrdinalIgnoreCase)) ?? product;
        var list = hidden.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0) all.Remove(key);
        else all[key] = list;
        ConfigLoader.SaveJson(dir, FileName, all);
    }
}
