using System.Text.Json;
using System.Text.Json.Serialization;

namespace Horizon.Workflow;

/// <summary>
/// Everything a <c>vision.detectObject</c> block carries, parsed from its parameters. The block is
/// self-contained: its camera position (<see cref="PosX"/>/<see cref="PosY"/>/<see cref="PosZ"/>,
/// on the camera's motion device — never a product position) and its vision recipe
/// (<see cref="Recipe"/>, a <see cref="VisionProfile"/> as JSON) both live in the block.
/// See <c>docs/detect-object-design.md</c>.
/// </summary>
public sealed record DetectObjectRequest
{
    /// <summary>Block name, for log lines and the manual-mark window title.</summary>
    public string Label { get; init; } = "Detect Object";

    /// <summary>Internal camera position; all three set = move there before detecting.</summary>
    public double? PosX { get; init; }
    public double? PosY { get; init; }
    public double? PosZ { get; init; }

    /// <summary>The vision recipe (<see cref="DetectObjectRecipe.Serialize"/>).</summary>
    public string Recipe { get; init; } = string.Empty;

    /// <summary>Velocity (mm/s) for the block's moves; 0 = leave the device as it is.</summary>
    public double Velocity { get; init; }

    /// <summary>Wait (ms) after arriving, before the frame is taken.</summary>
    public int SettleMs { get; init; } = 300;

    /// <summary>True: no result fails the step. False: found = 0 and the program continues.</summary>
    public bool RequireMatch { get; init; } = true;

    /// <summary>On a failed detection, ask the operator to mark the location on the image.</summary>
    public bool AskOperator { get; init; }

    /// <summary>Centre the camera on the result (XY), guarded by <see cref="MaxMoveMm"/>.</summary>
    public bool MoveToResult { get; init; }

    /// <summary>Guard (mm) for the move to the result — refused, never clamped, beyond it. Capped at <see cref="HardMaxMoveMm"/>.</summary>
    public double MaxMoveMm { get; init; } = HardMaxMoveMm;

    /// <summary>After the move to the result: a further move by this much on the camera device's own axes.</summary>
    public double OffsetX { get; init; }
    public double OffsetY { get; init; }
    public double OffsetZ { get; init; }

    /// <summary>
    /// Which parts of the result the block takes from the detection. An unticked X or Y reports
    /// the camera's own axis value instead (so nothing is corrected, and the move to the result
    /// leaves that axis alone); an unticked P reports 0. E.g. a horizontal edge: Y + P, not X.
    /// </summary>
    public bool UseX { get; init; } = true;
    public bool UseY { get; init; } = true;
    public bool UseP { get; init; } = true;

    /// <summary>The largest move-to-result the block will ever make, whatever the block asks for.</summary>
    public const double HardMaxMoveMm = 5.0;

    public bool HasPosition => PosX.HasValue && PosY.HasValue && PosZ.HasValue;
    public bool HasOffset => Math.Abs(OffsetX) > 1e-9 || Math.Abs(OffsetY) > 1e-9 || Math.Abs(OffsetZ) > 1e-9;

    /// <summary>The effective guard: the block's value, never above <see cref="HardMaxMoveMm"/>; a non-positive value means the hard cap.</summary>
    public double Guard => MaxMoveMm > 0 ? Math.Min(MaxMoveMm, HardMaxMoveMm) : HardMaxMoveMm;
}

/// <summary>
/// The result of a detect-object run. <see cref="X"/>/<see cref="Y"/> are in the camera device's
/// (gantry-main) axis values, mm. <see cref="P"/> is the object angle in degrees: 0 = straight up
/// on the image, clockwise positive, −180…+180. <see cref="Px"/>/<see cref="Py"/> are the source
/// pixels the point was found at. <see cref="Manual"/> = the operator marked it by hand.
/// </summary>
public sealed record DetectedObject(
    bool Found, double X, double Y, double P, double Score, double Px, double Py, bool Manual)
{
    /// <summary>
    /// What the detector actually matched, as polylines in the camera device's axis values (mm):
    /// the matched template box (NCC), the two fitted edges (corner), the fitted line, or the
    /// circle. For display only — null for an operator mark. It stays where the feature was
    /// found even when the block reports the camera's own X or Y instead.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<OutlinePoint>>? Outline { get; init; }

    public static DetectedObject NotFound { get; } = new(false, 0, 0, 0, 0, 0, 0, false);
}

/// <summary>One vertex of a <see cref="DetectedObject.Outline"/>, in the camera device's axis values (mm).</summary>
public readonly record struct OutlinePoint(double X, double Y);

/// <summary>Request for <see cref="IOperatorPrompt.MarkLocationAsync"/> — which camera to show and what to ask.</summary>
public sealed record MarkLocationRequest(string CameraId, string Title, string Message);

/// <summary>The point the operator marked, in source pixels of a <see cref="SrcW"/>×<see cref="SrcH"/> frame.</summary>
public sealed record MarkedLocation(double Px, double Py, int SrcW, int SrcH);

/// <summary>
/// (De)serialises a block's vision recipe. The recipe is a <see cref="VisionProfile"/> written as
/// compact JSON into the block's <c>recipe</c> parameter; NCC template binaries stay on disk under
/// the product's <c>vision\templates\{uid}\</c>, referenced by <see cref="TemplateRef.Uid"/>.
/// </summary>
public static class DetectObjectRecipe
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize(VisionProfile profile) => JsonSerializer.Serialize(profile, Options);

    /// <summary>Parses a recipe; null when the text is blank or not a recipe.</summary>
    public static VisionProfile? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonSerializer.Deserialize<VisionProfile>(text, Options); }
        catch (JsonException) { return null; }
    }

    /// <summary>One-line description for the editor: method · camera · template.</summary>
    public static string Describe(string? text)
    {
        var p = Parse(text);
        if (p == null) return "no recipe";
        var method = (p.DetectionMethod ?? "ncc").ToLowerInvariant();
        var camera = string.IsNullOrEmpty(p.CameraId) ? "no camera" : p.CameraId;
        var template = method != "ncc" ? string.Empty : p.Template == null ? " · no template" : " · template set";
        return $"{method.ToUpperInvariant()} · {camera}{template}";
    }
}
