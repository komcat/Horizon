using Horizon.Engine;
using Spectre.Console;

namespace Horizon.ConsoleHost;

/// <summary>
/// Layer 1 test host. Drives <see cref="IHorizonEngine"/> through its full
/// lifecycle with no hardware attached — proves the engine boundary.
/// </summary>
internal static class Program
{
    private static async Task<int> Main()
    {
        AnsiConsole.Write(new FigletText("Horizon").Color(Color.Aqua));
        AnsiConsole.MarkupLine("[grey]Layer 1 hardware engine — console test host[/]");
        AnsiConsole.WriteLine();

        await using var engine = new HorizonEngine();
        engine.LogEmitted += OnLog;

        try
        {
            await engine.InitializeAsync();
            await ConnectAsync(engine);

            var app = new ConsoleApp(engine);
            await app.RunAsync();

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
        }
    }

    /// <summary>Runs ConnectAll under a Spectre status spinner. Shared with the Reconnect menu action.</summary>
    internal static async Task ConnectAsync(IHorizonEngine engine)
    {
        await AnsiConsole.Status()
            .StartAsync("Connecting hardware…", async ctx =>
            {
                var progress = new Progress<string>(msg => ctx.Status(Markup.Escape(msg)));
                await engine.ConnectAllAsync(progress);
            });
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
