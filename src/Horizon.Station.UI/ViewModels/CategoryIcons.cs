using System.Windows.Media;

namespace Horizon.Station.UI.ViewModels;

/// <summary>A command category's badge: a line glyph (24 × 24 design grid, drawn in white) on a coloured tile.</summary>
public sealed record CategoryIcon(Geometry Glyph, Brush Fill);

/// <summary>
/// One icon per command category (<see cref="OpSpec.Category"/>), shown on every block on
/// the Engineer canvas and beside each palette group, so a sequence can be read at a glance:
/// the tile colour and the glyph both say what kind of command a block is. Vector, so the
/// badges stay sharp at any canvas zoom. The tiles carry their own colours (white glyph on a
/// mid-dark fill), so they read the same on a light or dark system theme.
/// A category with no entry here gets the "Other" badge.
/// </summary>
public static class CategoryIcons
{
    private static readonly Dictionary<string, CategoryIcon> Icons = new(StringComparer.OrdinalIgnoreCase)
    {
        // Four-way arrows — a stage moving.
        ["Motion"] = Make("#2F6FDE", "M12 3 V21 M3 12 H21 M9 6 L12 3 L15 6 M9 18 L12 21 L15 18 M6 9 L3 12 L6 15 M18 9 L21 12 L18 15"),
        // Toggle switch — an output pin on / off.
        ["IO"] = Make("#7A52CC", "M8 7 H16 A5 5 0 0 1 16 17 H8 A5 5 0 0 1 8 7 Z M18.2 12 A2.2 2.2 0 1 1 13.8 12 A2.2 2.2 0 1 1 18.2 12 Z"),
        // Air cylinder with its piston rod out.
        ["Pneumatic"] = Make("#2A8FC7", "M3 8 H14 V16 H3 Z M10 8 V16 M14 12 H21 M21 9 V15"),
        // Lamp bulb.
        ["Lighting"] = Make("#B8900A", "M8.5 14 A6 6 0 1 1 15.5 14 C15 15 15 16 15 17 H9 C9 16 9 15 8.5 14 Z M10 20 H14"),
        // Lightning bolt — power supply.
        ["Power"] = Make("#C9711A", "M13 2.5 L5.5 13 H11 L10 21.5 L18.5 10.5 H13 Z"),
        // Eye — camera / vision.
        ["Vision"] = Make("#D6457A", "M2 12 C6 5.5 18 5.5 22 12 C18 18.5 6 18.5 2 12 Z M15 12 A3 3 0 1 1 9 12 A3 3 0 1 1 15 12 Z"),
        // Waveform — a signal being read.
        ["Signal"] = Make("#2E9E5B", "M2 12 H6 L9 5 L13 19 L16 9 L18 12 H22"),
        // One path forking into two — sequence control.
        ["Flow"] = Make("#5F6B7A", "M12 3 V8 M12 8 C12 13 6 11 6 16 V21 M12 8 C12 13 18 11 18 16 V21 M3.5 18.5 L6 21 L8.5 18.5 M15.5 18.5 L18 21 L20.5 18.5"),
        // Cross-hair target — alignment.
        ["Align"] = Make("#D64541", "M18.5 12 A6.5 6.5 0 1 1 5.5 12 A6.5 6.5 0 1 1 18.5 12 Z M12 2 V8 M12 16 V22 M2 12 H8 M16 12 H22"),
        // Serpentine raster path — a scan.
        ["Scan"] = Make("#0E7C86", "M4 5 H20 V10 H4 V15 H20 V20 H4"),
        // Droplet — dispensing.
        ["Dispense"] = Make("#A0522D", "M12 3 C9 7.5 5.5 11.5 5.5 15 A6.5 6.5 0 0 0 18.5 15 C18.5 11.5 15 7.5 12 3 Z"),
        // Chip with pins — the device under test.
        ["DUT"] = Make("#4B5FC7", "M7 7 H17 V17 H7 Z M10 3 V7 M14 3 V7 M10 17 V21 M14 17 V21 M3 10 H7 M3 14 H7 M17 10 H21 M17 14 H21"),
        // Two-jaw gripper — the tool.
        ["Tool"] = Make("#6B8E23", "M12 3 V8 M6 8 H18 M6 8 V14 L9.5 17 V21 M18 8 V14 L14.5 17 V21"),
        // Box — the part on the fixture.
        ["Part"] = Make("#8D6E63", "M12 3 L20 7.5 V16.5 L12 21 L4 16.5 V7.5 Z M4 7.5 L12 12 L20 7.5 M12 12 V21"),
        // 2 × 2 tiles — a utility app.
        ["App"] = Make("#455A64", "M4 4 H10 V10 H4 Z M14 4 H20 V10 H14 Z M4 14 H10 V20 H4 Z M14 14 H20 V20 H14 Z"),
        // Question mark — a command this catalog does not know.
        ["Other"] = Make("#8A8F98", "M8.5 9 A3.5 3.5 0 1 1 12 12.5 V15 M12 18.5 V19"),
    };

    /// <summary>The badge for <paramref name="category"/> (the "Other" badge when it has none).</summary>
    public static CategoryIcon For(string? category)
        => category != null && Icons.TryGetValue(category, out var icon) ? icon : Icons["Other"];

    private static CategoryIcon Make(string fill, string path)
    {
        var glyph = Geometry.Parse(path);
        glyph.Freeze();
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fill));
        brush.Freeze();
        return new CategoryIcon(glyph, brush);
    }
}
