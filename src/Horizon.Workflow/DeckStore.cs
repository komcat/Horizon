using System.Text.Json;

namespace Horizon.Workflow;

/// <summary>Loads / saves <c>.hwd</c> decks in a product's <c>decks\</c> directory.</summary>
internal sealed class DeckStore
{
    private const string Extension = ".hwd";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _dir;

    public DeckStore(string productDir)
    {
        _dir = Path.Combine(productDir, "decks");
        Directory.CreateDirectory(_dir);
    }

    public IReadOnlyList<string> List()
        => Directory.Exists(_dir)
            ? Directory.GetFiles(_dir, "*" + Extension)
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => !string.IsNullOrEmpty(n))
                .Cast<string>()
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : Array.Empty<string>();

    public WorkflowDeck? Load(string name)
    {
        var path = Path.Combine(_dir, name + Extension);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<WorkflowDeck>(File.ReadAllText(path), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public void Save(WorkflowDeck deck)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, deck.Name + Extension);
        File.WriteAllText(path, JsonSerializer.Serialize(deck, JsonOptions));
    }
}
