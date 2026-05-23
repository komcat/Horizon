using System.Text;
using System.Text.Json;

namespace Horizon.Workflow.Migration;

/// <summary>Tally of one migration run over a folder of bobcat files.</summary>
public sealed record MigrationSummary(
    int RecipesConverted,
    int ProgramsConverted,
    int Skipped,
    IReadOnlyList<ConversionIssue> Issues,
    string OutputDirectory,
    string ReportPath)
{
    public int UnsupportedSteps => Issues.Count(i => i.Severity == ConversionSeverity.Unsupported);
}

/// <summary>
/// File-I/O driver around <see cref="BobcatRecipeConverter"/>: reads a folder of
/// bobcat recipe/program JSON files, writes converted Horizon <c>.hwf</c> /
/// <c>.hwd</c> files into an output folder, and drops a <c>conversion-report.txt</c>
/// listing every finding.
///
/// Per <c>docs/recipe-migration.md</c> §6, this emits standalone files for
/// review — it does not write into a product's ProgramStore/DeckStore.
/// </summary>
public sealed class BobcatMigration
{
    private const string ProgramExtension = ".hwf";
    private const string DeckExtension = ".hwd";
    private const string ReportName = "conversion-report.txt";

    // PascalCase + indented — matches what ProgramStore/DeckStore read back.
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly BobcatRecipeConverter _converter = new();

    /// <summary>
    /// Converts every <c>*.json</c> directly under <paramref name="inputDir"/>.
    /// Each file is classified as a recipe or a program by its top-level keys.
    /// </summary>
    public MigrationSummary ConvertFolder(string inputDir, string outputDir)
    {
        if (!Directory.Exists(inputDir))
            throw new DirectoryNotFoundException($"input folder not found: {inputDir}");

        Directory.CreateDirectory(outputDir);

        int recipes = 0, programs = 0, skipped = 0;
        var issues = new List<ConversionIssue>();
        var report = new StringBuilder();
        report.AppendLine($"Bobcat → Horizon conversion — {DateTime.Now:yyyy-MM-dd HH:mm}");
        report.AppendLine($"Input : {inputDir}");
        report.AppendLine($"Output: {outputDir}");
        report.AppendLine();

        foreach (var path in Directory.GetFiles(inputDir, "*.json").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var file = Path.GetFileName(path);
            try
            {
                var json = File.ReadAllText(path);
                switch (Classify(json))
                {
                    case FileKind.Recipe:
                    {
                        var result = _converter.ConvertRecipe(json);
                        WriteJson(Path.Combine(outputDir, Sanitize(result.Program.Name) + ProgramExtension),
                            result.Program);
                        issues.AddRange(result.Issues);
                        recipes++;
                        report.AppendLine($"[recipe ] {file} → {result.Program.Name}{ProgramExtension}" +
                            $"  ({result.Program.Steps.Count} steps" +
                            $"{(result.HasUnsupported ? ", HAS UNSUPPORTED" : "")})");
                        break;
                    }
                    case FileKind.Program:
                    {
                        var result = _converter.ConvertProgram(json);
                        WriteJson(Path.Combine(outputDir, Sanitize(result.Deck.Name) + DeckExtension),
                            result.Deck);
                        issues.AddRange(result.Issues);
                        programs++;
                        report.AppendLine($"[program] {file} → {result.Deck.Name}{DeckExtension}" +
                            $"  ({result.Deck.Entries.Count} entries)");
                        break;
                    }
                    default:
                        skipped++;
                        report.AppendLine($"[skip   ] {file} — not a recognized recipe or program");
                        break;
                }
            }
            catch (Exception ex)
            {
                skipped++;
                report.AppendLine($"[error  ] {file} — {ex.Message}");
            }
        }

        report.AppendLine();
        report.AppendLine($"Converted: {recipes} recipe(s), {programs} program(s); skipped {skipped}.");

        if (issues.Count > 0)
        {
            report.AppendLine();
            report.AppendLine($"Findings ({issues.Count}):");
            foreach (var i in issues.OrderByDescending(i => i.Severity))
                report.AppendLine($"  {i.Severity,-11} {i.Source} :: step '{i.StepId}' [{i.StepType}] — {i.Detail}");
        }

        var reportPath = Path.Combine(outputDir, ReportName);
        File.WriteAllText(reportPath, report.ToString());

        return new MigrationSummary(recipes, programs, skipped, issues, outputDir, reportPath);
    }

    private enum FileKind { Unknown, Recipe, Program }

    /// <summary>A bobcat recipe has <c>steps</c>; a program has <c>recipes</c>.</summary>
    private static FileKind Classify(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return FileKind.Unknown;
            if (doc.RootElement.TryGetProperty("steps", out _)) return FileKind.Recipe;
            if (doc.RootElement.TryGetProperty("recipes", out _)) return FileKind.Program;
            return FileKind.Unknown;
        }
        catch
        {
            return FileKind.Unknown;
        }
    }

    private static void WriteJson<T>(string path, T value)
        => File.WriteAllText(path, JsonSerializer.Serialize(value, WriteOptions));

    /// <summary>Strips characters that are invalid in a file name from a recipe/program name.</summary>
    private static string Sanitize(string name)
    {
        var cleaned = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "converted" : cleaned.Trim();
    }
}
