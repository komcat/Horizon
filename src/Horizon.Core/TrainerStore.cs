using System.IO;
using System.Text.Json;

namespace Horizon.Core;

public sealed class TrainerStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly string _directory;

    public TrainerStore()
    {
        _directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Horizon", "trainer");
    }

    private void EnsureDirectory()
    {
        Directory.CreateDirectory(_directory);
    }

    public string DataDirectory => _directory;

    public string GetFilePath(string name) => Path.Combine(_directory, $"{name}.json");

    public string GetImageDirectory()
    {
        var dir = Path.Combine(_directory, "images");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public List<string> GetTrainingSetNames()
    {
        if (!Directory.Exists(_directory))
            return new();

        return Directory.GetFiles(_directory, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n != null)
            .Cast<string>()
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public TrainingSet? Load(string name)
    {
        var path = Path.Combine(_directory, $"{name}.json");
        if (!File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<TrainingSet>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public void Save(TrainingSet set)
    {
        EnsureDirectory();
        var path = Path.Combine(_directory, $"{set.Name}.json");
        var json = JsonSerializer.Serialize(set, JsonOptions);
        File.WriteAllText(path, json);
    }

    public void Delete(string name)
    {
        var path = Path.Combine(_directory, $"{name}.json");
        if (File.Exists(path))
            File.Delete(path);
    }
}
