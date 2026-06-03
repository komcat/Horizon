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
    private readonly string _activePath;

    public DeckStore(string productDir)
    {
        _dir = Path.Combine(productDir, "decks");
        _activePath = Path.Combine(_dir, "active.json");
        Directory.CreateDirectory(_dir);
    }

    /// <summary>Name of the last-used deck for this product, or null if none recorded.</summary>
    public string? ActiveDeck
    {
        get
        {
            if (!File.Exists(_activePath)) return null;
            try
            {
                var doc = JsonSerializer.Deserialize<ActiveDeckDoc>(File.ReadAllText(_activePath), JsonOptions);
                return string.IsNullOrEmpty(doc?.ActiveDeck) ? null : doc!.ActiveDeck;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>Records the last-used deck — auto-loaded next time the page opens.</summary>
    public void SetActive(string? name)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_activePath,
            JsonSerializer.Serialize(new ActiveDeckDoc { ActiveDeck = name ?? string.Empty }, JsonOptions));
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

    public void Delete(string name)
    {
        var path = Path.Combine(_dir, name + Extension);
        if (File.Exists(path)) File.Delete(path);
        if (string.Equals(ActiveDeck, name, StringComparison.OrdinalIgnoreCase))
            SetActive(null);
    }

    private sealed record ActiveDeckDoc
    {
        public string ActiveDeck { get; init; } = string.Empty;
    }
}
