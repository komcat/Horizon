namespace Horizon.Process.Recipe;

/// <summary>
/// Typed dispatch table mapping (device, method) to handler delegates.
/// Populated at startup by AppContext with direct manager lambdas — no reflection at runtime.
/// </summary>
public sealed class RecipeMethodRegistry
{
    /// <summary>Handler delegate: receives resolved args, context, and cancellation token. Returns success.</summary>
    public delegate Task<bool> RecipeMethodHandler(
        Dictionary<string, object> args,
        RecipeContext context,
        CancellationToken ct);

    // device name -> (method name -> handler)
    private readonly Dictionary<string, Dictionary<string, RecipeMethodHandler>> _table = new(StringComparer.OrdinalIgnoreCase);

    // device name -> manager type label (for UI/diagnostics)
    private readonly Dictionary<string, string> _deviceTypes = new(StringComparer.OrdinalIgnoreCase);

    // method type -> (method name -> parameter signatures)
    private readonly Dictionary<string, Dictionary<string, MethodParamInfo>> _paramInfo = new(StringComparer.OrdinalIgnoreCase);

    public void Register(string device, string method, RecipeMethodHandler handler)
    {
        if (!_table.TryGetValue(device, out var methods))
            _table[device] = methods = new(StringComparer.OrdinalIgnoreCase);

        methods[method] = handler;
    }

    public void RegisterDeviceType(string device, string type)
    {
        _deviceTypes[device] = type;
    }

    /// <summary>Register parameter hints for a method on a device type (e.g., "motion", "moverelative").</summary>
    public void RegisterParams(string deviceType, string method, params ParamHint[] hints)
    {
        if (!_paramInfo.TryGetValue(deviceType, out var methods))
            _paramInfo[deviceType] = methods = new(StringComparer.OrdinalIgnoreCase);

        methods[method] = new MethodParamInfo { Params = hints.ToList() };
    }

    public MethodParamInfo? GetParamInfo(string device, string method)
    {
        var deviceType = GetDeviceType(device);
        if (deviceType != null && _paramInfo.TryGetValue(deviceType, out var methods)
            && methods.TryGetValue(method, out var info))
            return info;
        return null;
    }

    public bool TryGetMethod(string device, string method, out RecipeMethodHandler? handler)
    {
        handler = null;
        return _table.TryGetValue(device, out var methods) && methods.TryGetValue(method, out handler);
    }

    public bool HasDevice(string device) => _table.ContainsKey(device);

    public string? GetDeviceType(string device) =>
        _deviceTypes.TryGetValue(device, out var type) ? type : null;

    public List<string> GetDeviceNames() =>
        _table.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

    public List<string> GetMethodNames(string device) =>
        _table.TryGetValue(device, out var methods)
            ? methods.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList()
            : new List<string>();
}

public sealed class MethodParamInfo
{
    public List<ParamHint> Params { get; init; } = new();

    public string ToSignatureString()
    {
        return string.Join(", ", Params.Select(p =>
            p.Required ? $"{p.Name}: {p.Type}" : $"[{p.Name}: {p.Type}]"));
    }
}

public sealed class ParamHint
{
    public required string Name { get; init; }
    public required string Type { get; init; }    // "string", "number", "bool"
    public bool Required { get; init; } = true;
    public string? Description { get; init; }
    public string? Default { get; init; }

    public ParamHint() { }
    public ParamHint(string name, string type, bool required = true, string? description = null, string? defaultValue = null)
    {
        Name = name;
        Type = type;
        Required = required;
        Description = description;
        Default = defaultValue;
    }
}
