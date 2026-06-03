namespace Horizon.Workflow;

/// <summary>One entry of a deck — a program reference plus its transition condition.</summary>
public sealed record DeckEntry
{
    /// <summary>
    /// Stable per-entry id (<c>dge-…</c>), assigned once by the editor and preserved
    /// across save / load / reorder, so an entry stays trackable through edits — the
    /// basis for deck history / diff. Empty on legacy entries authored before this field.
    /// </summary>
    public string Uid { get; init; } = string.Empty;

    /// <summary>Name of the process program (<c>.hwf</c>) this entry runs.</summary>
    public string Program { get; init; } = string.Empty;

    /// <summary>When false, the deck skips this entry without running or gating on it.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Boolean precondition checked before this entry runs (only when <see cref="Auto"/>
    /// is true), evaluated against the deck-shared context (see <see cref="ConditionEvaluator"/>).
    /// Empty = always proceed — the default "previous program finished, nothing failed".
    /// A false condition stops the deck.
    /// </summary>
    public string Condition { get; init; } = string.Empty;

    /// <summary>
    /// Transition mode into this entry (ignored for the first entry, which always
    /// runs). When true the deck advances automatically — it waits
    /// <see cref="DelaySeconds"/> and, if <see cref="Condition"/> is set, gates on it.
    /// When false the deck pauses after the previous program and waits for the
    /// operator to activate this entry.
    /// <para>Defaults to true so decks saved before this field existed (and heads
    /// without an "activate next" control) keep auto-advancing; the FAAM deck editor
    /// creates new transitions with Auto = false.</para>
    /// </summary>
    public bool Auto { get; init; } = true;

    /// <summary>Seconds to wait before auto-advancing into this entry (<see cref="Auto"/> only).</summary>
    public double DelaySeconds { get; init; } = 2;

    /// <summary>
    /// When true the Run page shows a button for this program (in deck order), so the
    /// operator can run it on its own. Lets a deck double as the Run page's step list.
    /// </summary>
    public bool ShowInRun { get; init; } = true;
}

/// <summary>
/// A deck — an ordered, linear list of process programs run one after another
/// against a single shared <see cref="WorkflowContext"/>, so a later entry's
/// condition can read values an earlier program captured. The first program runs
/// immediately; each later entry's transition (see <see cref="DeckEntry.Auto"/>)
/// either pauses for the operator or auto-advances after a delay + optional
/// condition. The run stops on the first program failure or first false auto
/// condition.
/// </summary>
public sealed record WorkflowDeck
{
    /// <summary>
    /// Stable deck id (<c>deck-…</c>), assigned once by the editor and preserved across
    /// save / load / rename, so a deck's revision history stays tied to it even if it is
    /// renamed. Empty on legacy decks authored before this field (then history keys by name).
    /// </summary>
    public string Uid { get; init; } = string.Empty;

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
