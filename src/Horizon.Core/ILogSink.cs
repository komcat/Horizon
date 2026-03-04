namespace Horizon.Core;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error
}

public interface ILogSink
{
    void Log(LogLevel level, string message);
}

public static class LogSinkExtensions
{
    public static void Debug(this ILogSink sink, string message) => sink.Log(LogLevel.Debug, message);
    public static void Info(this ILogSink sink, string message) => sink.Log(LogLevel.Info, message);
    public static void Warn(this ILogSink sink, string message) => sink.Log(LogLevel.Warning, message);
    public static void Error(this ILogSink sink, string message) => sink.Log(LogLevel.Error, message);
}
