using Horizon.Engine;
using Horizon.Workflow;
using Spectre.Console;

namespace Horizon.WorkflowConsole;

/// <summary>
/// Layer 2 test host — drives <see cref="IProcessService"/> the way a Layer 3
/// front-end will. Phase 1: product management.
/// </summary>
internal static class Program
{
    private static int Main()
    {
        AnsiConsole.Write(new FigletText("Horizon WF").Color(Color.Aqua));
        AnsiConsole.MarkupLine("[grey]Layer 2 process module — console test host[/]");
        AnsiConsole.WriteLine();

        var service = new ProcessService();
        service.LogEmitted += OnLog;

        try
        {
            new ConsoleApp(service).Run();
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Fatal: {ex.Message}[/]");
            return 1;
        }
        finally
        {
            service.LogEmitted -= OnLog;
        }
    }

    private static void OnLog(LogEntry entry)
    {
        var color = entry.Level switch
        {
            LogLevel.Debug => "grey",
            LogLevel.Warning => "yellow",
            LogLevel.Error => "red",
            _ => "white"
        };

        var source = Markup.Escape(entry.Source);
        var message = Markup.Escape(entry.Message);
        AnsiConsole.MarkupLine(
            $"[grey]{entry.Timestamp:HH:mm:ss}[/] [{color}]{entry.Level,-7}[/] [grey]{source}[/] {message}");
    }
}
