using Horizon.Common;
using Horizon.Engine;
using Horizon.Workflow.FauPreAlign;
using Horizon.Workflow.HillClimb;
using Horizon.Workflow.Recipe;
using Horizon.Workflow.Signals;
using Horizon.Workflow.UCorrection;

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

    /// <summary>Identity / metadata of the active product, or null if none is loaded.</summary>
    ProductInfo? ActiveProductInfo { get; }

    /// <summary>Reads a product's identity / metadata without loading it; null if it does not exist.</summary>
    ProductInfo? GetProductInfo(string name);

    /// <summary>
    /// Raised after the active product changes (load / create-and-load / unload /
    /// delete-active). The argument is the new active product name, or null.
    /// </summary>
    event Action<string?>? ProductChanged;

    /// <summary>Creates a new, empty product.</summary>
    Result CreateProduct(string name);

    /// <summary>Deletes a product and all its data.</summary>
    Result DeleteProduct(string name);

    /// <summary>
    /// Creates <paramref name="newName"/> as a full editable copy of an existing
    /// product's process definition — positions, vision profiles, and programs /
    /// decks / recipes — with a fresh identity and empty DUT history. Each variation
    /// owns its own copy, so command blocks can be edited independently. Does not
    /// change the active product (the caller loads the new one if desired). Fails if
    /// the source is missing, the name is invalid, or a product by that name already
    /// exists.
    /// </summary>
    Result CloneProduct(string source, string newName);

    /// <summary>
    /// Loads (activates) a product, re-scoping all process data (programs, positions,
    /// decks, DUT records) to it. Refused while a program run or interactive DUT run
    /// is in progress — switching mid-run would split a unit's data across products.
    /// </summary>
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
    /// Preserves any existing camera / exposure on the position.
    /// </summary>
    Result SetPosition(string device, string name, MotionPosition position, double velocity);

    /// <summary>
    /// Stores a named position with all recall settings — coordinates, velocity,
    /// and the camera + exposure to apply during the move. <paramref name="camera"/>
    /// null/empty or <paramref name="exposureUs"/> null = leave the camera untouched.
    /// </summary>
    Result SetPositionFull(string device, string name, MotionPosition position, double velocity,
        string? camera, double? exposureUs);

    /// <summary>
    /// Captures the device's current position and stores it under a name.
    /// <paramref name="velocity"/> of 0 = no velocity applied on recall.
    /// </summary>
    Task<Result> TeachPositionAsync(string device, string name, double velocity);

    /// <summary>Resolves a named position and moves the device to it (using the position's stored velocity).</summary>
    Task<Result> MoveToNamedPositionAsync(string device, string name, CancellationToken ct = default);

    /// <summary>
    /// As <see cref="MoveToNamedPositionAsync(string,string,CancellationToken)"/> but overrides the
    /// position's stored velocity with <paramref name="velocityOverride"/> (controller units) when it
    /// is &gt; 0 — used by the Machine page so a manual recall honours the operator's chosen velocity
    /// rather than the position's taught process velocity. 0 falls back to the stored velocity.
    /// </summary>
    Task<Result> MoveToNamedPositionAsync(string device, string name, double velocityOverride, CancellationToken ct = default);

    // --- Vision profiles (operate on the active product) ---

    /// <summary>Vision profile names in the active product.</summary>
    IReadOnlyList<string> ListVisionProfiles();

    /// <summary>Name of the last-loaded vision profile for the active product, or null.</summary>
    string? ActiveVisionProfile { get; }

    /// <summary>Loads a vision profile by name (and records it as active), or null if not found.</summary>
    VisionProfile? LoadVisionProfile(string name);

    /// <summary>
    /// Saves a vision profile into the active product (assigning a UID if it has none),
    /// pruning a superseded template and recording an operator-stamped revision. Returns
    /// the saved profile (with its assigned UID).
    /// </summary>
    Result<VisionProfile> SaveVisionProfile(VisionProfile profile);

    /// <summary>Deletes a vision profile and its template binaries from the active product.</summary>
    Result DeleteVisionProfile(string name);

    /// <summary>Revision history of a vision profile (by UID), or null when no product is active.</summary>
    VisionProfileHistory? GetVisionProfileHistory(string profileUid);

    /// <summary>Absolute path of a template's saved image, or null if it does not exist.</summary>
    string? GetTemplateImagePath(string templateUid);

    /// <summary>
    /// Captures a new NCC template from <paramref name="roi"/> on <paramref name="frame"/>
    /// (a fresh template UID + persisted model/image). Returns the new template reference;
    /// assign it to the profile and <see cref="SaveVisionProfile"/> to keep it.
    /// </summary>
    Task<Result<TemplateRef>> CaptureTemplateAsync(VisionProfile profile, CameraFrame frame, RoiSpec roi);

    /// <summary>
    /// Runs a profile's detection (NCC / circle / line) against <paramref name="frame"/>
    /// and returns a unified key→value result plus a crop, or a failure.
    /// </summary>
    Task<Result<VisionDetectResult>> RunVisionProfileAsync(VisionProfile profile, CameraFrame frame);

    /// <summary>
    /// Registers the signal source for <c>hillclimb.start</c> steps (the engine has no
    /// SiPhOG facet, so a Layer 3 head supplies one). Pass null to unregister.
    /// </summary>
    void SetHillClimbFeedbackProvider(Func<IHillClimbFeedback?>? provider);

    /// <summary>
    /// Registers the Layer 3 utility-app host (the Run-page springboard) that
    /// <c>app.open</c> / <c>app.home</c> / <c>app.start</c> / <c>app.stop</c> steps drive.
    /// Pass null to unregister; with no host registered those steps fail with a clear
    /// message rather than crash.
    /// </summary>
    void SetUtilityAppHost(IUtilityAppHost? host);

    /// <summary>
    /// Registers the Layer 3 sink that <c>siphog.updateSheet</c> steps drive — a head
    /// (FAAM.UI) writes a staged SAG-PWR measurement to the SiPhOG server's Google Sheet
    /// over its live client. Pass null to unregister; with none registered the step fails
    /// with a clear message rather than crash.
    /// </summary>
    void SetSiphogSheetSink(ISiphogSheetSink? sink);

    /// <summary>
    /// Registers a product-specific signal source (e.g. FAAM's SiPhOG) so its values
    /// join the unified <c>signal.read</c> catalog alongside the built-in engine
    /// signals (SMU / PSU / Measurement / TEC / Laser).
    /// </summary>
    void AddSignalSource(ISignalSource source);

    /// <summary>
    /// The signals currently available to a <c>signal.read</c> block — built-in engine
    /// catalog plus every registered product source. The editor calls this to populate
    /// the read-out drop-down.
    /// </summary>
    IReadOnlyList<SignalDescriptor> DescribeSignals();

    // --- Process programs (operate on the active product) ---

    /// <summary>Process program names in the active product.</summary>
    IReadOnlyList<string> ListPrograms();

    /// <summary>Loads a process program by name.</summary>
    Result<WorkflowProgram> LoadProgram(string name);

    /// <summary>
    /// Saves a process program into the active product, recording an
    /// operator-stamped revision in its history (see <see cref="GetProgramHistory"/>).
    /// </summary>
    Result SaveProgram(WorkflowProgram program);

    /// <summary>Revision history of a program (operator + timestamp + snapshot per save), or null.</summary>
    ProgramHistory? GetProgramHistory(string name);

    /// <summary>
    /// Cross-program history query (machine-global) — filter saved block parameters by
    /// UID / key / device / operator / product / program for traceability. Newest first.
    /// </summary>
    IReadOnlyList<HistoryRow> QueryHistory(HistoryQuery query);

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

    /// <summary>
    /// Runs the FAU↔PIC U-correction teach + apply against the active product:
    /// drives the gantry through the four reference points, asks the operator (via
    /// <see cref="OperatorPrompt"/>) to refine each, computes the PIC→FAU angle, and
    /// applies it as a slow relative U move on the hexapod. Headless — needs no
    /// camera. Fails if no product is active or no operator prompt is registered.
    /// </summary>
    Task<Result<UCorrectionResult>> RunUCorrectionAsync(UCorrectionOptions options, CancellationToken ct = default);

    /// <summary>
    /// As <see cref="RunUCorrectionAsync(UCorrectionOptions,CancellationToken)"/> but drives the
    /// per-point refine + apply confirmations through the supplied <paramref name="prompt"/>
    /// instead of the globally-registered <see cref="OperatorPrompt"/>. A front-end uses this to
    /// supply a <b>non-modal</b> prompt (e.g. the Run page's inline Capture/Apply buttons) so the
    /// operator can keep jogging via the live camera while a point is being refined.
    /// </summary>
    Task<Result<UCorrectionResult>> RunUCorrectionAsync(UCorrectionOptions options, IOperatorPrompt prompt, CancellationToken ct = default);

    /// <summary>
    /// Runs the FAU pre-align teach + apply against the active product: drives the gantry through the
    /// four reference points (Out1·Out2·Fiber1·Fiber2), asks the operator to refine each, computes the
    /// gantry-XY correction toward the target offset, and applies it as a slow relative Y/Z move on the
    /// hexapod. Headless — needs no camera. Fails if no product is active or no operator prompt is registered.
    /// </summary>
    Task<Result<FauPreAlignResult>> RunFauPreAlignAsync(FauPreAlignOptions options, CancellationToken ct = default);

    /// <summary>
    /// As <see cref="RunFauPreAlignAsync(FauPreAlignOptions,CancellationToken)"/> but drives the per-point
    /// refine + apply confirmations through the supplied non-modal <paramref name="prompt"/> (e.g. the Run
    /// page's inline Capture/Move-FAU buttons) instead of the globally-registered <see cref="OperatorPrompt"/>.
    /// </summary>
    Task<Result<FauPreAlignResult>> RunFauPreAlignAsync(FauPreAlignOptions options, IOperatorPrompt prompt, CancellationToken ct = default);

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

    /// <summary>Saves a deck into the active product (refused once the deck limit is reached).</summary>
    Result SaveDeck(WorkflowDeck deck);

    /// <summary>Deletes a deck from the active product.</summary>
    Result DeleteDeck(string name);

    /// <summary>Deck names in another (non-active) product — for importing a deck from it.</summary>
    IReadOnlyList<string> ListDecksOf(string product);

    /// <summary>
    /// Imports a deck from <paramref name="sourceProduct"/> into the active product
    /// under <paramref name="newDeckName"/>, copying any programs the deck references
    /// that the active product is missing (so it is runnable here). The new deck gets
    /// a fresh identity (deck + entry UIDs) and becomes the active deck. Fails if the
    /// source/deck is missing, the source is the active product, the name is empty, or
    /// a deck by that name already exists here.
    /// </summary>
    Result ImportDeck(string sourceProduct, string deckName, string newDeckName);

    /// <summary>
    /// Runs a deck — its programs in order, sharing one context. The first program
    /// runs immediately; each later entry advances per its transition (auto-off pauses
    /// for the operator via <see cref="ActivateNextDeckProgram"/>; auto-on waits a
    /// delay and an optional condition). Stops on the first program failure or first
    /// false auto condition. When <paramref name="serial"/> is supplied, the whole
    /// deck run is recorded into that DUT's test record.
    /// </summary>
    Task<Result> RunDeck(WorkflowDeck deck, string? serial = null, CancellationToken ct = default);

    /// <summary>Raised before and after each deck entry while a deck runs.</summary>
    event Action<DeckProgress>? DeckStepEvent;

    /// <summary>
    /// Raised when a deck pauses on a manual (auto-off) transition and is waiting for
    /// the operator to activate the next program. Resume with <see cref="ActivateNextDeckProgram"/>.
    /// </summary>
    event Action<DeckProgress>? DeckAwaitingActivation;

    /// <summary>Releases a deck paused on a manual transition so it advances. No-op when none is waiting.</summary>
    void ActivateNextDeckProgram();

    /// <summary>Revision history of a deck by UID (operator + timestamp + snapshot per save), or null.</summary>
    DeckHistory? GetDeckHistory(string deckUid);

    /// <summary>
    /// Cross-deck history query (machine-global) — filter saved deck entries by deck UID /
    /// entry UID / program / operator / product for traceability. Newest first.
    /// </summary>
    IReadOnlyList<DeckHistoryRow> QueryDeckHistory(DeckHistoryQuery query);

    // --- Operator identity (traceability) ---

    /// <summary>The operator currently at the station, or null if none is set.</summary>
    OperatorContext? Operator { get; }

    /// <summary>
    /// Sets (or clears) the operator stamped onto subsequent DUT runs. The front-end
    /// calls this after login and on logout. Does not affect an already-finalised run.
    /// </summary>
    void SetOperator(OperatorContext? op);

    // --- DUT test records (operate on the active product) ---

    /// <summary>Serial numbers that have a DUT test record in the active product.</summary>
    IReadOnlyList<string> ListDutRecords();

    /// <summary>Loads the full DUT record for a serial in the active product, or null if none.</summary>
    DutRecord? GetDutRecord(string serial);

    /// <summary>
    /// The current (or most recent) DUT serial for the active product — set when a
    /// program/deck run, a mid-run <c>dut.promptSerial</c> step, or an interactive
    /// run supplies one, and kept after the run ends so the front-end can keep
    /// showing "the unit we last ran". Cleared (→ null) only on a product switch /
    /// unload. Distinct from <see cref="HasActiveDutRun"/>, which tells you whether a
    /// run is actually in progress.
    /// </summary>
    string? ActiveSerial { get; }

    /// <summary>
    /// Clears the sticky <see cref="ActiveSerial"/> (→ null) without switching products —
    /// the operator dismissing "the unit we last ran" from the header. No-op while an
    /// interactive DUT run is in progress (<see cref="HasActiveDutRun"/>); returns whether
    /// the serial was cleared.
    /// </summary>
    bool ClearActiveSerial();

    /// <summary>
    /// Raised when <see cref="ActiveSerial"/> changes — a new unit starting (serial)
    /// or a product switch clearing it (null). Fired for program/deck runs (including
    /// a mid-run <c>dut.promptSerial</c> step) and interactive runs.
    /// </summary>
    event Action<string?>? ActiveSerialChanged;

    // --- Interactive DUT runs (operator-driven processes, not WorkflowProgram execution) ---

    /// <summary>True while an interactive DUT run opened by <see cref="BeginDutRun"/> is in progress.</summary>
    bool HasActiveDutRun { get; }

    /// <summary>
    /// Opens an interactive DUT run for a unit identified by <paramref name="serial"/>
    /// — used by operator-driven processes (e.g. FAAM's hand-clicked step sequence)
    /// that do not execute a <see cref="WorkflowProgram"/>. Record values with
    /// <see cref="RecordDutValue"/>, then finalise with <see cref="EndDutRun"/>.
    /// Fails if a run is already open or no product is active.
    /// </summary>
    Result BeginDutRun(string serial, string source);

    /// <summary>Appends a value to the open interactive DUT run (no-op if none is open).</summary>
    void RecordDutValue(string key, string value, DutValueType type = DutValueType.String);

    /// <summary>
    /// Finalises the open interactive DUT run with a verdict, stamps the current
    /// operator + station, and persists it. Fails if no run is open.
    /// </summary>
    Result EndDutRun(DutVerdict verdict);

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
