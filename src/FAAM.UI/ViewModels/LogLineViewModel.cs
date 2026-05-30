using System.Windows;
using System.Windows.Media;
using Horizon.Engine;

namespace FAAM.UI.ViewModels;

/// <summary>A single log line, with display strings and a level colour precomputed for binding.</summary>
public sealed class LogLineViewModel
{
    // Debug/Info follow the system text colours so they stay readable in any
    // theme; Warning/Error are fixed status colours.
    private static readonly Brush DebugBrush = SystemColors.GrayTextBrush;
    private static readonly Brush InfoBrush = SystemColors.ControlTextBrush;
    private static readonly Brush WarningBrush = Frozen(0xF1, 0xC4, 0x0F);
    private static readonly Brush ErrorBrush = Frozen(0xE7, 0x4C, 0x3C);

    public LogLineViewModel(LogEntry entry)
    {
        Time = entry.Timestamp.ToString("HH:mm:ss");
        Level = entry.Level.ToString();
        Source = entry.Source;
        Message = entry.Message;
        Color = entry.Level switch
        {
            LogLevel.Debug => DebugBrush,
            LogLevel.Warning => WarningBrush,
            LogLevel.Error => ErrorBrush,
            _ => InfoBrush
        };
    }

    public string Time { get; }
    public string Level { get; }
    public string Source { get; }
    public string Message { get; }
    public Brush Color { get; }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
