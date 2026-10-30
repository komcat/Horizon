using System.Windows.Media;
using Horizon.Workflow.Oee;

namespace Horizon.Station.UI.Views;

/// <summary>One colour + label per <see cref="OeeState"/>, shared by the OEE page, its timeline and the downtime bar.</summary>
public static class OeeColors
{
    private static readonly Brush RunningBrush = Frozen(0x43, 0xA0, 0x47);
    private static readonly Brush StandbyBrush = Frozen(0xA5, 0xD6, 0xA7);
    private static readonly Brush IdleBrush = Frozen(0xFF, 0xB3, 0x00);
    private static readonly Brush DownBrush = Frozen(0xE5, 0x39, 0x35);
    private static readonly Brush OfflineBrush = Frozen(0x9E, 0x9E, 0x9E);

    public static Brush For(OeeState state) => state switch
    {
        OeeState.Running => RunningBrush,
        OeeState.Standby => StandbyBrush,
        OeeState.Idle => IdleBrush,
        OeeState.Down => DownBrush,
        _ => OfflineBrush,
    };

    /// <summary>Readable text colour on top of <see cref="For"/> — white on the dark greens / red, black on the light ones.</summary>
    public static Brush TextOn(OeeState state)
        => state is OeeState.Running or OeeState.Down ? Brushes.White : Brushes.Black;

    public static string Label(OeeState state) => state switch
    {
        OeeState.Running => "RUNNING",
        OeeState.Standby => "STANDBY",
        OeeState.Idle => "IDLE",
        OeeState.Down => "DOWN",
        _ => "OFFLINE",
    };

    /// <summary>"3h 05m" / "12m 40s" — a duration for the operator.</summary>
    public static string Duration(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" : $"{t.Minutes}m {t.Seconds:00}s";
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
