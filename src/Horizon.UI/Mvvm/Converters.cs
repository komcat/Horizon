using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Horizon.UI.Mvvm;

/// <summary>Maps a <see cref="bool"/> to one of two brushes (true → green, false → red).</summary>
public sealed class BoolToBrushConverter : IValueConverter
{
    private static readonly Brush Connected = new SolidColorBrush(Color.FromRgb(0x2E, 0xCC, 0x71));
    private static readonly Brush Disconnected = new SolidColorBrush(Color.FromRgb(0xE7, 0x4C, 0x3C));

    static BoolToBrushConverter()
    {
        Connected.Freeze();
        Disconnected.Freeze();
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Connected : Disconnected;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Maps a <see cref="bool"/> to "Connected" / "Disconnected" text.</summary>
public sealed class BoolToStatusTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "Connected" : "Disconnected";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
