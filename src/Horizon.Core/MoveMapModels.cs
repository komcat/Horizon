using System.Text.Json.Serialization;

namespace Horizon.Core;

public record MoveMapNodeLayout
{
    [JsonPropertyName("positionName")]
    public string PositionName { get; init; } = string.Empty;

    [JsonPropertyName("canvasX")]
    public double CanvasX { get; init; }

    [JsonPropertyName("canvasY")]
    public double CanvasY { get; init; }
}

public record MoveMapEdge
{
    [JsonPropertyName("from")]
    public string From { get; init; } = string.Empty;

    [JsonPropertyName("to")]
    public string To { get; init; } = string.Empty;

    [JsonPropertyName("bidirectional")]
    public bool Bidirectional { get; init; } = true;
}

public record MoveMapDeviceGraph
{
    [JsonPropertyName("deviceName")]
    public string DeviceName { get; init; } = string.Empty;

    [JsonPropertyName("nodeLayouts")]
    public List<MoveMapNodeLayout> NodeLayouts { get; init; } = new();

    [JsonPropertyName("edges")]
    public List<MoveMapEdge> Edges { get; init; } = new();
}

public record MoveMapData
{
    [JsonPropertyName("version")]
    public int Version { get; init; } = 1;

    [JsonPropertyName("devices")]
    public List<MoveMapDeviceGraph> Devices { get; init; } = new();
}
