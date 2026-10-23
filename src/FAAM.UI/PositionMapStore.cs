using Horizon.Common;

namespace FAAM.UI;

/// <summary>
/// The Machine › Motion position map — per product and motion device, where each
/// position's node sits on the canvas and which nodes are linked — plus the width of
/// the position list beside the canvas. Saved in the config dir as
/// <c>faam_position_map.json</c>:
/// <c>{ "listWidth": 560, "products": { "&lt;product&gt;": { "&lt;device&gt;": { "nodes": [{ "name", "x", "y" }], "edges": [{ "a", "b" }] } } } }</c>.
/// View data only — nothing here moves the machine.
/// </summary>
public static class PositionMapStore
{
    private const string FileName = "faam_position_map";

    /// <summary>Raised after a map is saved — (product, device, origin) — so other open maps of it reload.</summary>
    public static event Action<string, string, object?>? Saved;

    /// <summary>The saved map for <paramref name="product"/> / <paramref name="device"/>; empty when none or unreadable.</summary>
    public static PositionMapLayout Load(string product, string device)
    {
        try
        {
            var devices = Find(Read().Products, product);
            return (devices == null ? null : Find(devices, device)) ?? new PositionMapLayout();
        }
        catch
        {
            // Cosmetic layout — an unreadable file just shows an empty map.
            return new PositionMapLayout();
        }
    }

    /// <summary>
    /// Replaces the map for <paramref name="product"/> / <paramref name="device"/>, keeping every
    /// other product / device. Throws on I/O failure, or when the existing file can't be parsed
    /// (rather than overwriting the other maps in it). <paramref name="origin"/> is passed on to
    /// <see cref="Saved"/> so the saving map can ignore its own change.
    /// </summary>
    public static void Save(string product, string device, PositionMapLayout layout, object? origin = null)
    {
        var file = Read();
        var devices = Find(file.Products, product);
        if (devices == null)
        {
            devices = new Dictionary<string, PositionMapLayout>();
            file.Products[KeyOf(file.Products, product)] = devices;
        }
        devices[KeyOf(devices, device)] = layout;
        ConfigLoader.SaveJson(ConfigLoader.GetConfigDirectory(), FileName, file);
        Saved?.Invoke(product, device, origin);
    }

    /// <summary>Saved pixel width of the position list column; null = default.</summary>
    public static double? LoadListWidth()
    {
        try { return Read().ListWidth; }
        catch { return null; }
    }

    /// <summary>Remembers the position list column width. Throws on I/O failure.</summary>
    public static void SaveListWidth(double? width)
    {
        var file = Read();
        file.ListWidth = width;
        ConfigLoader.SaveJson(ConfigLoader.GetConfigDirectory(), FileName, file);
    }

    private static PositionMapFile Read()
    {
        var file = ConfigLoader.LoadJson<PositionMapFile>(ConfigLoader.GetConfigDirectory(), FileName) ?? new PositionMapFile();
        file.Products ??= new Dictionary<string, Dictionary<string, PositionMapLayout>>();
        return file;
    }

    private static T? Find<T>(Dictionary<string, T> map, string key) where T : class
        => map.TryGetValue(KeyOf(map, key), out var value) ? value : null;

    /// <summary>The existing key matching <paramref name="key"/> case-insensitively, else <paramref name="key"/> itself.</summary>
    private static string KeyOf<T>(Dictionary<string, T> map, string key)
        => map.Keys.FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) ?? key;
}

/// <summary>Root of <c>faam_position_map.json</c>.</summary>
public sealed class PositionMapFile
{
    public double? ListWidth { get; set; }
    public Dictionary<string, Dictionary<string, PositionMapLayout>> Products { get; set; } = new();
}

/// <summary>One device's map within one product.</summary>
public sealed class PositionMapLayout
{
    public List<PositionMapNode> Nodes { get; set; } = new();
    public List<PositionMapEdge> Edges { get; set; } = new();
}

/// <summary>A position's node — <see cref="X"/>/<see cref="Y"/> is its centre in canvas pixels.</summary>
public sealed class PositionMapNode
{
    public string Name { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
}

/// <summary>An undirected link between two positions (by name).</summary>
public sealed class PositionMapEdge
{
    public string A { get; set; } = string.Empty;
    public string B { get; set; } = string.Empty;
}
