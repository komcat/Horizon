using System.Text.Json.Serialization;

namespace Horizon.Core;

public record DeviceSnapshot
{
    [JsonPropertyName("deviceName")]
    public string DeviceName { get; init; } = "";

    [JsonPropertyName("x")]
    public double X { get; init; }
    [JsonPropertyName("y")]
    public double Y { get; init; }
    [JsonPropertyName("z")]
    public double Z { get; init; }
    [JsonPropertyName("u")]
    public double U { get; init; }
    [JsonPropertyName("v")]
    public double V { get; init; }
    [JsonPropertyName("w")]
    public double W { get; init; }
}

public record SecondaryTeaching
{
    [JsonPropertyName("deviceName")]
    public string DeviceName { get; init; } = "";

    [JsonPropertyName("method")]
    public string Method { get; init; } = "2-point";

    [JsonPropertyName("devicePosition")]
    public DeviceSnapshot DevicePosition { get; init; } = new();

    [JsonPropertyName("observationCorners")]
    public List<BondingCorner> ObservationCorners { get; init; } = new();

    [JsonPropertyName("observationCenterX")]
    public double ObservationCenterX { get; init; }
    [JsonPropertyName("observationCenterY")]
    public double ObservationCenterY { get; init; }
    [JsonPropertyName("observationCenterZ")]
    public double ObservationCenterZ { get; init; }

    [JsonPropertyName("centerImagePath")]
    public string? CenterImagePath { get; init; }

    [JsonPropertyName("zObj")]
    public double? ZObj { get; init; }
    [JsonPropertyName("clearanceT")]
    public double? ClearanceT { get; init; }
    [JsonPropertyName("zBond")]
    public double? ZBond { get; init; }
    [JsonPropertyName("zRel")]
    public double? ZRel { get; init; }

    [JsonIgnore]
    public bool HasXYTeaching => !string.IsNullOrEmpty(DeviceName);
    [JsonIgnore]
    public bool HasZTeaching => ZRel.HasValue;
}

public record BondingCorner
{
    [JsonPropertyName("uid")]
    public string Uid { get; init; } = Guid.NewGuid().ToString();

    [JsonPropertyName("x")]
    public double X { get; init; }

    [JsonPropertyName("y")]
    public double Y { get; init; }

    [JsonPropertyName("z")]
    public double Z { get; init; }

    [JsonPropertyName("imagePath")]
    public string? ImagePath { get; init; }
}

public record BondingPoint
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("corners")]
    public List<BondingCorner> Corners { get; init; } = new();

    [JsonPropertyName("globalX")]
    public double GlobalX { get; init; }

    [JsonPropertyName("globalY")]
    public double GlobalY { get; init; }

    [JsonPropertyName("globalZ")]
    public double GlobalZ { get; init; }

    [JsonPropertyName("localX")]
    public double LocalX { get; init; }

    [JsonPropertyName("localY")]
    public double LocalY { get; init; }

    [JsonPropertyName("centerImagePath")]
    public string? CenterImagePath { get; init; }

    [JsonPropertyName("secondaryTeaching")]
    public SecondaryTeaching? SecondaryTeaching { get; init; }

    [JsonPropertyName("valid")]
    public bool Valid { get; init; }
}

public record ReferenceCorner
{
    [JsonPropertyName("uid")]
    public string Uid { get; init; } = Guid.NewGuid().ToString();

    [JsonPropertyName("x")]
    public double X { get; init; }

    [JsonPropertyName("y")]
    public double Y { get; init; }

    [JsonPropertyName("z")]
    public double Z { get; init; }

    [JsonPropertyName("imagePath")]
    public string? ImagePath { get; init; }
}

public record ReferencePoint
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("uid")]
    public string Uid { get; init; } = Guid.NewGuid().ToString();

    [JsonPropertyName("corners")]
    public List<ReferenceCorner> Corners { get; init; } = new();

    [JsonPropertyName("globalX")]
    public double GlobalX { get; init; }

    [JsonPropertyName("globalY")]
    public double GlobalY { get; init; }

    [JsonPropertyName("globalZ")]
    public double GlobalZ { get; init; }

    [JsonPropertyName("centerImagePath")]
    public string? CenterImagePath { get; init; }

    [JsonPropertyName("imagePath")]
    public string? ImagePath { get; init; }

    [JsonPropertyName("valid")]
    public bool Valid { get; init; }
}

public record TrainingSet
{
    [JsonPropertyName("uid")]
    public string Uid { get; init; } = Guid.NewGuid().ToString();

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("deviceName")]
    public string DeviceName { get; init; } = "gantry-main";

    [JsonPropertyName("createdAt")]
    public string CreatedAt { get; init; } = DateTime.Now.ToString("o");

    [JsonPropertyName("originX")]
    public double OriginX { get; init; }

    [JsonPropertyName("originY")]
    public double OriginY { get; init; }

    [JsonPropertyName("xAxisX")]
    public double XAxisX { get; init; }

    [JsonPropertyName("xAxisY")]
    public double XAxisY { get; init; }

    [JsonPropertyName("yAxisX")]
    public double YAxisX { get; init; }

    [JsonPropertyName("yAxisY")]
    public double YAxisY { get; init; }

    [JsonPropertyName("referencePoints")]
    public List<ReferencePoint> ReferencePoints { get; init; } = new();

    [JsonPropertyName("bondingPoints")]
    public List<BondingPoint> BondingPoints { get; init; } = new();

    [JsonPropertyName("valid")]
    public bool Valid { get; init; }
}
