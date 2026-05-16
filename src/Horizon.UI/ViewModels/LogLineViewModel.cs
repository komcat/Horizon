using System.Windows.Media;
using Horizon.Engine;

namespace Horizon.UI.ViewModels;

/// <summary>A single log line, with display strings and a level colour precomputed for binding.</summary>
public sealed class LogLineViewModel
{
    private static readonly Brush DebugBrush = Frozen(0x80, 0x80, 0x88);
    private static readonly Brush InfoBrush = Frozen(0xD0, 0xD0, 0xD4);
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
