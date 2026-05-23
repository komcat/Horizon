using Horizon.Common;
using Horizon.Engine;
using Horizon.Workflow.Recipe;

namespace Horizon.Workflow;

/// <summary>
/// Layer 2 facade — the product / process module on top of
/// <see cref="IHorizonEngine"/>. Consumed by Layer 3 front-ends.
///
/// Phase 1 exposes product management. Teach points / named positions, process
/// execution, and recipe execution are added by later bring-up phases.
/// </summary>
public interface IProcessService
{
    /// <summary>All configured product names.</summary>
    IReadOnlyList<string> Products { get; }

    /// <summary>The currently loaded product, or null if none.</summary>
    string? ActiveProduct { get; }

    /// <summary>Creates a new, empty product.</summary>
    Result CreateProduct(string name);

    /// <summary>Deletes a product and all its data.</summary>
    Result DeleteProduct(string name);

    /// <summary>Loads (activates) a product.</summary>
    Result LoadProduct(string name);

    /// <summary>Clears the active product.</summary>
    void UnloadProduct();

    // --- Teach points / named positions (operate on the active product) ---

    /// <summary>Named-position names for a device.</summary>
    IReadOnlyList<string> GetPositionNames(string device);

    /// <summary>Resolves a named position (coordinates + velocity), or null if unknown.</summary>
    NamedPosition? GetPosition(string device, string name);

    /// <summary>Removes a named position from the active product.</summary>
    Result RemovePosition(string device, string name);

    /// <summary>
    /// Stores a named position from explicit coordinates (manual entry).
    /// <paramref name="velocity"/> of 0 = no velocity applied on recall.
    /// </summary>
    Result SetPosition(string device, string name, MotionPosition position, double velocity);

    /// <summary>
    /// Captures the device's current position and stores it under a name.
    /// <paramref name="velocity"/> of 0 = no velocity applied on recall.
    /// </summary>
    Task<Result> TeachPositionAsync(string device, string name, double velocity);

    /// <summary>Resolves a named position and moves the device to it.</summary>
    Task<Result> MoveToNamedPositionAsync(string device, string name, CancellationToken ct = default);

    // --- Process programs (operate on the active product) ---

    /// <summary>Process program names in the active product.</summary>
    IReadOnlyList<string> ListPrograms();

    /// <summary>Loads a process program by name.</summary>
    Result<WorkflowProgram> LoadProgram(string name);

    /// <summary>Saves a process program into the active product.</summary>
    Result SaveProgram(WorkflowProgram program);

    /// <summary>
    /// Front-end-supplied operator-prompt handler — the bidirectional callback
    /// the runner blocks on for <c>flow.askUser</c> steps. Null when no
    /// front-end is registered: a program containing an <c>askUser</c> step
    /// then cannot run (the step fails). See <see cref="IOperatorPrompt"/>.
    /// </summary>
    IOperatorPrompt? OperatorPrompt { get; set; }

    /// <summary>
    /// Runs a process program against the engine. When <paramref name="serial"/>
    /// is supplied, the run is recorded into that DUT's test record (see
    /// <see cref="ListDutRecords"/>); otherwise <c>dut.*</c> steps no-op.
    /// </summary>
    Task<Result> RunAsync(WorkflowProgram program, string? serial = null, CancellationToken ct = default);

    // --- Recipes (.hrec scripts, operate on the active product) ---

    /// <summary>Recipe names in the active product.</summary>
    IReadOnlyList<string> ListRecipes();

    /// <summary>Parses a recipe by name; the returned program carries any parse errors.</summary>
    Result<RecipeProgram> LoadRecipe(string name);

    /// <summary>Saves recipe source text into the active product.</summary>
    Result SaveRecipe(string name, string source);

    /// <summary>Runs a recipe script against the engine.</summary>
    Task<Result> RunRecipeAsync(string name, CancellationToken ct = default);

    // --- Decks (ordered program chains, operate on the active product) ---

    /// <summary>Deck names in the active product.</summary>
    IReadOnlyList<string> ListDecks();

    /// <summary>Name of the last-used deck in the active product — auto-loaded by the editor; null if none.</summary>
    string? ActiveDeck { get; }

    /// <summary>Loads a deck by name.</summary>
    Result<WorkflowDeck> LoadDeck(string name);

    /// <summary>Saves a deck into the active product.</summary>
    Result SaveDeck(WorkflowDeck deck);

    /// <summary>
    /// Runs a deck — its programs in order, sharing one context, stopping on the
    /// first program failure or first false transition condition. When
    /// <paramref name="serial"/> is supplied, the whole deck run is recorded into
    /// that DUT's test record.
    /// </summary>
    Task<Result> RunDeck(WorkflowDeck deck, string? serial = null, CancellationToken ct = default);

    /// <summary>Raised before and after each deck entry while a deck runs.</summary>
    event Action<DeckProgress>? DeckStepEvent;

    // --- DUT test records (operate on the active product) ---

    /// <summary>Serial numbers that have a DUT test record in the active product.</summary>
    IReadOnlyList<string> ListDutRecords();

    // --- Interactive execution (pause / step) ---

    /// <summary>Pauses the running program before its next step (no-op if idle).</summary>
    void Pause();

    /// <summary>Resumes a paused program.</summary>
    void Resume();

    /// <summary>Runs exactly one step of a paused program, then pauses again.</summary>
    void Step();

    /// <summary>True while a running program is paused.</summary>
    bool IsPaused { get; }

    /// <summary>Raised when the running program's paused state changes (true = paused).</summary>
    event Action<bool>? PausedChanged;

    /// <summary>Raised before and after each executed step of a running program.</summary>
    event Action<WorkflowStepEvent>? StepEvent;

    /// <summary>Raised for every workflow log line. The Layer 2 logging surface.</summary>
    event Action<LogEntry>? LogEmitted;
}
