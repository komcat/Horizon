using Horizon.Common;
using Spectre.Console;

namespace Horizon.ConfigTool;

/// <summary>
/// Hardware configuration authoring utility. Drafts and edits the config JSON
/// files Horizon consumes — preset-driven, validated. Phase 1: load + view.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        AnsiConsole.Write(new FigletText("Horizon Config").Color(Color.Aqua));
        AnsiConsole.MarkupLine("[grey]Hardware configuration authoring utility[/]");
        AnsiConsole.WriteLine();

        // Config directory: first arg, else the standard %APPDATA%\Horizon\config.
        var configDir = args.Length > 0 && !string.IsNullOrWhiteSpace(args[0])
            ? args[0]
            : ConfigLoader.GetConfigDirectory();

        try
        {
            new ConfigToolApp(configDir).Run();
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Fatal: {ex.Message}[/]");
            return 1;
        }
    }
}
