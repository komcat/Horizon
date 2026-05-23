using System.Text.Json;
using Horizon.Common;
using Horizon.Engine;

namespace Horizon.Workflow;

/// <summary>
/// A registered spatial offset between two motion devices —
/// <c>Offset = linked position − primary position</c> at registration time.
/// </summary>
public sealed record DeviceLink(string Primary, string Linked, MotionPosition Offset);

/// <summary>
/// Machine-global store of device links, persisted to <c>device_links.json</c>
/// in the config directory. Links are registered at runtime by the
/// <c>motion.registerDeviceLink</c> step and replayed by
/// <c>motion.compensateDeviceLink</c> — see <c>docs/recipe-migration.md</c>.
/// </summary>
internal sealed class DeviceLinkStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;
    private readonly List<DeviceLink> _links;

    public DeviceLinkStore()
    {
        _path = Path.Combine(ConfigLoader.GetConfigDirectory(), "device_links.json");
        _links = Load();
    }

    /// <summary>The registered link for a device pair, or null if none.</summary>
    public DeviceLink? Find(string primary, string linked)
        => _links.FirstOrDefault(l =>
            l.Primary.Equals(primary, StringComparison.OrdinalIgnoreCase) &&
            l.Linked.Equals(linked, StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds or replaces the link for its device pair and persists the store.</summary>
    public void Register(DeviceLink link)
    {
        _links.RemoveAll(l =>
            l.Primary.Equals(link.Primary, StringComparison.OrdinalIgnoreCase) &&
            l.Linked.Equals(link.Linked, StringComparison.OrdinalIgnoreCase));
        _links.Add(link);
        Save();
    }

    private List<DeviceLink> Load()
    {
        if (!File.Exists(_path)) return new();
        try
        {
            return JsonSerializer.Deserialize<List<DeviceLink>>(File.ReadAllText(_path), JsonOptions) ?? new();
        }
        catch
        {
            return new();
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(_links, JsonOptions));
    }
}
