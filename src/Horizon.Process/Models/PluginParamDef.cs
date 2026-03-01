namespace Horizon.Process.Models;

public enum PluginParamType
{
    Text,
    Number,
    Boolean,
    Enum,
    FilePath,
    MotionDevice,
    CameraDevice,
    NamedPosition,
    Axis
}

public sealed record PluginParamDef(
    string Name,
    string DisplayName,
    PluginParamType Type,
    string? Category = null,
    bool Required = true,
    string? Default = null,
    string[]? Options = null,
    string? UnitSuffix = null,
    double? Min = null,
    double? Max = null,
    double? Step = null,
    int? DecimalPlaces = null,
    string? DependsOnDevice = null);
