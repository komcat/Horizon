namespace Horizon.Process.Models;

public enum ParamType
{
    Text,
    Number,
    Axis,
    NamedPosition,
    Boolean,
    FilePath,
    ContextKey,
    Enum
}

public sealed record CommandParamInfo(
    string Name,
    ParamType Type,
    bool Required = true,
    string? Default = null,
    string[]? Options = null,
    string? UnitSuffix = null);

public sealed record CommandMethodInfo(
    string Name,
    string Description,
    CommandParamInfo[] Params);
