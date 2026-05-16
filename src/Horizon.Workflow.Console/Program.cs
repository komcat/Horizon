using Horizon.Engine;
using Horizon.Workflow;
using Spectre.Console;

namespace Horizon.WorkflowConsole;

/// <summary>
/// Layer 2 test host — a Layer 1 + 2 host. Creates and connects the engine,
/// then drives <see cref="IProcessService"/> the way a Layer 3 front-end will.
/// </summary>
internal static class Program
{
    private static async Task<int> Main()
    {
        AnsiConsole.Write(new FigletText("Horizon WF").Color(Color.Aqua));
        AnsiConsole.MarkupLine("[grey]Layer 2 process module — console test host[/]");
        AnsiConsole.WriteLine();

        await using var engine = new HorizonEngine();
        engine.LogEmitted += OnLog;

        var service = new ProcessService(engine);
        service.LogEmitted += OnLog;

        try
        {
            await engine.InitializeAsync();
            await AnsiConsole.Status()
                .StartAsync("Connecting hardware…", async ctx =>
                {
                    var progress = new Progress<string>(msg => ctx.Status(Markup.Escape(msg)));
                    await engine.ConnectAllAsync(progress);
                });

            await new ConsoleApp(service, engine).RunAsync();

            await engine.ShutdownAsync();
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Fatal: {ex.Message}[/]");
            return 1;
        }
        finally
        {
            engine.LogEmitted -= OnLog;
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
