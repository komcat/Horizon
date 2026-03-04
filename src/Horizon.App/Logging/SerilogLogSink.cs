using Serilog;

namespace Horizon.App.Logging;

/// <summary>
/// Bridges Horizon.Core.ILogSink to Serilog so AppContext messages
/// flow through the Serilog pipeline.
/// </summary>
internal sealed class SerilogLogSink : Horizon.Core.ILogSink
{
    public void Log(Horizon.Core.LogLevel level, string message)
    {
        switch (level)
        {
            case Horizon.Core.LogLevel.Debug:
                Serilog.Log.Debug(message);
                break;
            case Horizon.Core.LogLevel.Info:
                Serilog.Log.Information(message);
                break;
            case Horizon.Core.LogLevel.Warning:
                Serilog.Log.Warning(message);
                break;
            case Horizon.Core.LogLevel.Error:
                Serilog.Log.Error(message);
                break;
        }
    }
}
