using System.Reflection;
using Horizon.Process.Interfaces;

namespace Horizon.Process;

public sealed class ProcessPluginRegistry
{
    private readonly Dictionary<string, IProcessPlugin> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<IProcessPlugin>> _byCategory = new(StringComparer.OrdinalIgnoreCase);

    public void Register(IProcessPlugin plugin)
    {
        _byId[plugin.Id] = plugin;

        if (!_byCategory.TryGetValue(plugin.Category, out var list))
        {
            list = new List<IProcessPlugin>();
            _byCategory[plugin.Category] = list;
        }

        // Replace if already exists, otherwise add
        var existing = list.FindIndex(p => string.Equals(p.Id, plugin.Id, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
            list[existing] = plugin;
        else
            list.Add(plugin);
    }

    public IProcessPlugin? GetById(string id)
    {
        return _byId.TryGetValue(id, out var plugin) ? plugin : null;
    }

    public Dictionary<string, List<IProcessPlugin>> GetByCategory()
    {
        return new Dictionary<string, List<IProcessPlugin>>(_byCategory, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<IProcessPlugin> GetAll()
    {
        return _byId.Values.ToList();
    }

    public void DiscoverPlugins(params Assembly[] assemblies)
    {
        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.GetTypes())
            {
                if (type.IsAbstract || type.IsInterface)
                    continue;

                if (!typeof(IProcessPlugin).IsAssignableFrom(type))
                    continue;

                var ctor = type.GetConstructor(Type.EmptyTypes);
                if (ctor == null)
                    continue;

                if (Activator.CreateInstance(type) is IProcessPlugin plugin)
                    Register(plugin);
            }
        }
    }
}
