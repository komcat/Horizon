using System.Windows.Media;
using Serilog.Events;

namespace Horizon.App.Logging;

internal sealed class LogEntry
{
    // Frozen brushes shared across all entries (zero GC per entry)
    private static readonly SolidColorBrush InfoBrush;
    private static readonly SolidColorBrush WarningBrush;
    private static readonly SolidColorBrush ErrorBrush;
    private static readonly SolidColorBrush TimestampBrush;

    static LogEntry()
    {
        InfoBrush = new SolidColorBrush(Color.FromRgb(0xF1, 0xF1, 0xF1));
        InfoBrush.Freeze();
        WarningBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xB9, 0x00));
        WarningBrush.Freeze();
        ErrorBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x44, 0x44));
        ErrorBrush.Freeze();
        TimestampBrush = new SolidColorBrush(Color.FromRgb(0x65, 0x65, 0x65));
        TimestampBrush.Freeze();
    }

    public string Timestamp { get; }
    public LogEventLevel Level { get; }
    public string Message { get; }
    public Brush Foreground { get; }

    public static Brush TimestampForeground => TimestampBrush;

    public LogEntry(LogEventLevel level, string message, DateTimeOffset timestamp)
    {
        Timestamp = timestamp.ToString("HH:mm:ss.fff");
        Level = level;
        Message = level switch
        {
            LogEventLevel.Warning => $"[WARN] {message}",
            LogEventLevel.Error => $"[ERROR] {message}",
            LogEventLevel.Fatal => $"[FATAL] {message}",
            _ => message
        };
        Foreground = level switch
        {
            LogEventLevel.Warning => WarningBrush,
            LogEventLevel.Error or LogEventLevel.Fatal => ErrorBrush,
            _ => InfoBrush
        };
    }
}
