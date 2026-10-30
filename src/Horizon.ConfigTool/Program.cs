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
        // Non-interactive FireDog import:
        //   Horizon.ConfigTool --import-firedog [<FireDog root>] [<config dir>] [--overwrite]
        if (args.Length > 0 && args[0].Equals("--import-firedog", StringComparison.OrdinalIgnoreCase))
            return ImportFireDog(args.Skip(1).ToArray());

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

    /// <summary>
    /// <c>--import-firedog</c>: translate a FireDog machine root into Horizon config files, write
    /// those that do not exist yet (all, with backups, under <c>--overwrite</c>), validate.
    /// Exit code 0 = imported, 1 = failed.
    /// </summary>
    private static int ImportFireDog(string[] args)
    {
        bool overwrite = args.Any(a => a.Equals("--overwrite", StringComparison.OrdinalIgnoreCase));
        var positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
        var root = positional.Length > 0 ? positional[0] : FireDogImport.DefaultRoot;
        var configDir = positional.Length > 1 ? positional[1] : ConfigLoader.GetConfigDirectory();

        try
        {
            var plan = FireDogImport.Build(root);
            ConfigToolApp.RenderFireDogPlan(plan, configDir);
            var result = FireDogImport.Apply(plan, configDir,
                overwrite ? ImportOverwrite.BackupAndOverwrite : ImportOverwrite.SkipExisting);
            ConfigToolApp.RenderFireDogResult(result);
            AnsiConsole.MarkupLine("\n[aqua]Validating config…[/]");
            ConfigToolApp.RenderValidation(configDir);
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]FireDog import failed: {ex.Message}[/]");
            return 1;
        }
    }
}
