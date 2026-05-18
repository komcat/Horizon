namespace Horizon.Workflow;

/// <summary>One entry of a deck — a program reference plus its transition condition.</summary>
public sealed record DeckEntry
{
    /// <summary>Name of the process program (<c>.hwf</c>) this entry runs.</summary>
    public string Program { get; init; } = string.Empty;

    /// <summary>When false, the deck skips this entry without running or gating on it.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Boolean precondition checked before this entry runs, evaluated against the
    /// deck-shared context (see <see cref="ConditionEvaluator"/>). Empty = always
    /// proceed — the default "previous program finished, nothing failed". A false
    /// condition stops the deck.
    /// </summary>
    public string Condition { get; init; } = string.Empty;
}

/// <summary>
/// A deck — an ordered, linear list of process programs run one after another
/// against a single shared <see cref="WorkflowContext"/>, so a later entry's
/// condition can read values an earlier program captured. The run stops on the
/// first program failure or the first false transition condition.
/// </summary>
public sealed record WorkflowDeck
{
    public string Name { get; init; } = string.Empty;
    public List<DeckEntry> Entries { get; init; } = new();
}

/// <summary>Progress event raised before and after each deck entry.</summary>
public sealed record DeckProgress(
    int Index,
    int Total,
    string Program,
    bool Completed,
    bool Success,
    string Message);
