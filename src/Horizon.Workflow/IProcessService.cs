using Horizon.Common;
using Horizon.Engine;
using Horizon.Workflow.Calibration;
using Horizon.Workflow.Dispensing;
using Horizon.Workflow.FauPreAlign;
using Horizon.Workflow.HillClimb;
using Horizon.Workflow.Oee;
using Horizon.Workflow.Recipe;
using Horizon.Workflow.Registration;
using Horizon.Workflow.Scripting;
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

    /// <summary>
    /// The active product's <c>dut\</c> folder (per-serial DUT records incl. the UV signal
    /// snapshots), or null when no product is active. For "open in Explorer" style UI only.
    /// </summary>
    string? DutDataDirectory { get; }

    /// <summary>
    /// The active product's <c>siphog_data\</c> folder (per-serial labelled SiPhOG snapshot files),
    /// or null when no product is active. May not exist on disk until the first snapshot is saved.
    /// </summary>
    string? SiphogDataDirectory { get; }

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
    /// Returns the currently-registered alignment feedback (via the provider set by
    /// <see cref="SetHillClimbFeedbackProvider"/>), or null when none is registered or the
    /// provider yields none. Lets other Layer-3 alignment surfaces (e.g. the Engineer
    /// Testing Ground spiral search) reuse the same signal source without re-plumbing the
    /// SiPhOG client.
    /// </summary>
    IHillClimbFeedback? GetHillClimbFeedback();

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
    /// Registers the Layer 3 image panel that <c>machine.ui.showImage</c> pushes a still
    /// picture into (FAAM's Run-page snapshot box). Pass null to unregister; with none
    /// registered the script call fails with a clear message rather than crash.
    /// </summary>
    void SetImageDisplaySink(IImageDisplaySink? sink);

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

    /// <summary>
    /// Commits the program: saves the working file <i>and</i> records a new numbered
    /// revision in the version repository, with an optional operator <paramref name="note"/>
    /// (blank → no note). Unlike <see cref="SaveProgram"/>, this is what bumps the version.
    /// </summary>
    Result CommitProgram(WorkflowProgram program, string? note);

    /// <summary>
    /// Checks out a past program revision into the working copy: writes the stored
    /// snapshot back as the current program. Does <b>not</b> create a new version (commit
    /// afterwards to keep it). Fails if no product is active or the version is unknown.
    /// </summary>
    Result RestoreProgramVersion(string program, int version);

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
    /// Measures the vision-based U correction: drives the gantry through the four reference points,
    /// runs each point's saved vision profile (Line detection → facet angle), and returns the PIC/FAU
    /// angle averages and the resulting U delta. Applies <b>no</b> move — the caller decides whether
    /// to apply (via <see cref="ApplyHexUCorrectionAsync"/>) based on the delta magnitude, and falls
    /// back to the manual correction if a facet isn't detected.
    /// </summary>
    Task<Result<VisionUCorrectionResult>> MeasureVisionUCorrectionAsync(VisionUCorrectionOptions options, CancellationToken ct = default);

    /// <summary>
    /// Applies a vision U correction as a relative U move on the hexapod, with a fail-safe hard limit
    /// on the rotation magnitude. Near-zero deltas are a no-op.
    /// </summary>
    Task<Result> ApplyHexUCorrectionAsync(string hexDevice, double deltaDeg, CancellationToken ct = default);

    /// <summary>
    /// Raised after a named vision profile runs (auto U-correction, <c>vision.runProfile</c>, etc.)
    /// so a live front-end can briefly flash the detection on its camera feed. May fire on a
    /// background thread — marshal to the UI before touching controls.
    /// </summary>
    event Action<VisionProfileDetection>? VisionProfileRan;

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

    // --- Calibration (machine-global; camera <-> needle is the first) ---

    /// <summary>The current camera→needle calibration (config + last measured results).</summary>
    CameraNeedleCalibrationData GetCameraNeedleCalibration();

    /// <summary>Raised (on any thread) after the camera→needle calibration file changes — a pass
    /// saved, parameters saved, or a seed typed.</summary>
    event Action? CameraNeedleCalibrationChanged;

    /// <summary>
    /// Types a rough camera→needle XY offset (a tape-measure SEED, mm, needle = cam + offset).
    /// Stored as NOT calibrated: enough to aim the first calibration imprint into the field of
    /// view, never enough to dispense through. No-op when the values are unchanged.
    /// </summary>
    void SetCameraNeedleSeed(double vcamneedleX, double vcamneedleY);

    /// <summary>
    /// Saves the calibration protocol's parameters + hardware names (shot, settle, heights,
    /// velocities, passes, tolerances, clear-first moves) from <paramref name="parameters"/>; the
    /// measured result is untouched. Refused while a run is in progress.
    /// </summary>
    Result SaveCameraNeedleParameters(CameraNeedleCalibrationData parameters);

    /// <summary>
    /// Runs the camera→needle calibration (FireDog's measure + verify procedure) using the
    /// globally-registered <see cref="OperatorPrompt"/>. Every pass is persisted to the
    /// machine-global calibration file; with 2+ passes it fails when a verify pass is outside
    /// the XY / Z tolerance (the offset is saved anyway).
    /// </summary>
    Task<Result<CameraNeedleResult>> RunCameraNeedleCalibrationAsync(CancellationToken ct = default);

    /// <summary>
    /// As <see cref="RunCameraNeedleCalibrationAsync(CancellationToken)"/> but drives the
    /// confirmations and jog steps through the supplied non-modal <paramref name="prompt"/>
    /// (the Calibration page's inline buttons + live camera).
    /// </summary>
    Task<Result<CameraNeedleResult>> RunCameraNeedleCalibrationAsync(IOperatorPrompt prompt, CancellationToken ct = default);

    // --- Dispensing routines (per-product, reuse the camera→needle calibration) ---

    /// <summary>Names of the active product's saved dispense routines (empty if no product).</summary>
    IReadOnlyList<string> ListDispenseRoutines();

    /// <summary>Loads a saved dispense routine by name.</summary>
    Result<DispenseRoutine> LoadDispenseRoutine(string name);

    /// <summary>Saves (creates / overwrites) a dispense routine in the active product.</summary>
    Result SaveDispenseRoutine(DispenseRoutine routine);

    /// <summary>Deletes a dispense routine from the active product.</summary>
    Result DeleteDispenseRoutine(string name);

    /// <summary>A new routine with the given name and the protocol's default parameters.</summary>
    DispenseRoutine NewDispenseRoutine(string name);

    /// <summary>
    /// Runs a saved dispense routine at the current camera target (FireDog's dispense
    /// procedure): uses the machine camera→needle calibration — hardware, offset and touch
    /// height — to place the needle where the camera is centred, then lays down the routine's
    /// pattern. Prompts go through <see cref="OperatorPrompt"/>. Refused when the needle has not
    /// been calibrated.
    /// </summary>
    Task<Result> RunDispenseRoutineAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Relative XY jog of the camera→needle gantry by the calibrated offset:
    /// <paramref name="toNeedle"/> = true moves the needle onto the point the camera is centred
    /// on (gantry += vcamneedle); false moves the camera back over that point (gantry −= vcamneedle).
    /// A quick manual aid to preview / return from where a dispense would land.
    /// </summary>
    Task<Result> JogCameraNeedleOffsetAsync(bool toNeedle, CancellationToken ct = default);

    /// <summary>
    /// Core "needle to point" routine: move the needle onto the camera-pointed location
    /// (gantry += vcamneedle), extend the dispenser head, then approach the surface by
    /// <paramref name="approachDistanceMm"/> at <paramref name="velocity"/> mm/s. Leaves the head
    /// extended (no shot, no retract) — a positioning primitive for manual work / sequences.
    /// Aborting retracts the head.
    /// </summary>
    Task<Result> RunNeedleApproachAsync(double approachDistanceMm, double velocity, CancellationToken ct = default);

    // --- Device registration (machine-global): a tool on one motion device registered to the
    //     camera on another, so either can be sent to the other. See docs/device-registration-design.md.

    /// <summary>Every saved tool registration (empty until the first is saved).</summary>
    IReadOnlyList<ToolRegistration> GetToolRegistrations();

    /// <summary>The registration for a tool device, or null if it has not been registered.</summary>
    ToolRegistration? GetToolRegistration(string toolDevice);

    /// <summary>
    /// Adds or replaces a tool registration and persists it (machine-global). The operator
    /// stamp is applied here. Raises <see cref="ToolRegistrationsChanged"/>.
    /// </summary>
    Result SaveToolRegistration(ToolRegistration registration);

    /// <summary>Deletes a tool device's registration. Raises <see cref="ToolRegistrationsChanged"/>.</summary>
    Result RemoveToolRegistration(string toolDevice);

    /// <summary>Raised after a registration is saved or removed (any thread).</summary>
    event Action? ToolRegistrationsChanged;

    /// <summary>
    /// Where a registered tool is right now in its camera device's frame, from the engine's
    /// cached motion states — non-blocking, any thread; drives the live camera overlay.
    /// Fails when the tool is unregistered or a device has no valid state yet.
    /// </summary>
    Result<ToolLocation> LocateTool(string toolDevice);

    /// <summary>
    /// Moves the camera device so the camera is centred on the registered tool (camera device
    /// XY; with <paramref name="includeZ"/> also to the captured focus height). Refused — not
    /// clamped — when the required travel exceeds <paramref name="maxDistanceMm"/> (0 = the
    /// registration's own bound, default 10 mm).
    /// </summary>
    Task<Result> MoveCameraToToolAsync(string toolDevice, double maxDistanceMm = 0, bool includeZ = false, CancellationToken ct = default);

    /// <summary>
    /// Moves the tool device so the tool sits where the camera is centred (the mapped tool
    /// axes only; with <paramref name="includeZ"/> also the Z-mapped axis so the tool reaches the
    /// camera's focus plane plus <paramref name="zOffsetMm"/> — null = the registration's stored
    /// offset; negative = higher on the gantry's Z convention). Same bound rule as
    /// <see cref="MoveCameraToToolAsync"/>, which itself always goes to the taught focus height.
    /// </summary>
    Task<Result> MoveToolToCameraAsync(string toolDevice, double maxDistanceMm = 0, bool includeZ = false, double? zOffsetMm = null, CancellationToken ct = default);

    /// <summary>Moves the tool device back to the pose captured in its registration (all axes, one coordinated move).</summary>
    Task<Result> MoveToolToPoseAsync(string toolDevice, CancellationToken ct = default);

    // --- Gripped objects (per-product): something held by a registered tool, e.g. the FAU in
    //     the hex-right gripper, taught by two reference points and drawn purple on the feeds.

    /// <summary>The active product's gripped objects (empty if none / no product).</summary>
    IReadOnlyList<GrippedObject> GetGrippedObjects();

    /// <summary>A gripped object by name, or null.</summary>
    GrippedObject? GetGrippedObject(string name);

    /// <summary>Adds or replaces a gripped object in the active product. Raises <see cref="GrippedObjectsChanged"/>.</summary>
    Result SaveGrippedObject(GrippedObject obj);

    /// <summary>Deletes a gripped object (its points and outline). Raises <see cref="GrippedObjectsChanged"/>.</summary>
    Result RemoveGrippedObject(string name);

    /// <summary>Raised after a gripped object is saved or removed, and on product switch (any thread).</summary>
    event Action? GrippedObjectsChanged;

    /// <summary>
    /// Where a gripped object is right now in its camera device's frame (cached motion states,
    /// non-blocking) — its taught centre carried along by the tool's move since the teach pose.
    /// Needs the tool's registration. Drives the live overlay.
    /// </summary>
    Result<ToolLocation> LocateGrippedObject(string name);

    /// <summary>
    /// Moves the tool device so a gripped object's centre sits where the camera is centred (the
    /// mapped tool axes; with <paramref name="includeZ"/> also the Z-mapped axis so the object's
    /// focus plane lands at the camera's Z + <paramref name="zOffsetMm"/>, null = the object's
    /// stored offset, negative = higher). Bounded like the tool moves — refused, never clamped.
    /// </summary>
    Task<Result> MoveGrippedObjectToCameraAsync(string name, double maxDistanceMm = 0, bool includeZ = false, double? zOffsetMm = null, CancellationToken ct = default);

    // --- Part alignment (per-product): two fiducials fix the part's local frame on the
    //     fixture; local objects (points / lines) are taught in that frame and re-measured
    //     by a run. See docs/device-registration-design.md.

    IReadOnlyList<PartAlignment> GetPartAlignments();
    PartAlignment? GetPartAlignment(string name);

    /// <summary>Adds or replaces a part alignment profile in the active product. Raises <see cref="PartAlignmentsChanged"/>.</summary>
    Result SavePartAlignment(PartAlignment part);

    /// <summary>Deletes a part alignment profile. Raises <see cref="PartAlignmentsChanged"/>.</summary>
    Result RemovePartAlignment(string name);

    /// <summary>Raised after a part alignment is saved, removed or re-measured, and on product switch (any thread).</summary>
    event Action? PartAlignmentsChanged;

    /// <summary>The part as it stands now in the gantry frame (measured after a run, taught otherwise) — drives the overlay.</summary>
    Result<PartSnapshot> GetPartSnapshot(string name);

    /// <summary>Runs a part's vision profile on the live camera and returns the point in the gantry frame (anchor preferred).</summary>
    Task<Result<PartPoint>> DetectPartPointAsync(string name, string profile);

    /// <summary>Converts an image point at a camera pose to a gantry-frame <see cref="PartPoint"/> (MoveClick semantics) — for manual marks.</summary>
    PartPoint ToPartPoint(MotionPosition camPose, double px, double py, int srcW, int srcH, string source);

    /// <summary>
    /// Re-finds E1 / E2, rebuilds the frame and re-measures every object, then stores the
    /// measurement on the profile. <paramref name="manual"/> supplies operator clicks for manual
    /// points (null = manual points fail, as from a program step).
    /// </summary>
    Task<Result<PartMeasurement>> RunPartAlignmentAsync(string name, PartManualMark? manual = null, CancellationToken ct = default);

    /// <summary>Camera → fiducial 1 or 2 (measured if a run happened, taught otherwise).</summary>
    Task<Result> MoveCameraToPartFiducialAsync(string name, int which, CancellationToken ct = default);

    /// <summary>Camera → a local object's reference point, plus its local-frame offset when <paramref name="applyOffset"/>.</summary>
    Task<Result> MoveCameraToPartObjectAsync(string name, string objectName, bool applyOffset = true, CancellationToken ct = default);

    /// <summary>Camera → one point of a local object (1-based; a line has 2) — measured after a run, taught otherwise.</summary>
    Task<Result> MoveCameraToPartObjectPointAsync(string name, string objectName, int pointIndex, CancellationToken ct = default);

    // --- Recipes (.hrec scripts, operate on the active product) ---

    /// <summary>Recipe names in the active product.</summary>
    IReadOnlyList<string> ListRecipes();

    /// <summary>Parses a recipe by name; the returned program carries any parse errors.</summary>
    Result<RecipeProgram> LoadRecipe(string name);

    /// <summary>Saves recipe source text into the active product.</summary>
    Result SaveRecipe(string name, string source);

    /// <summary>Runs a recipe script against the engine.</summary>
    Task<Result> RunRecipeAsync(string name, CancellationToken ct = default);

    // --- Scripts (.moon Lua procedural scripts, operate on the active product) ---

    /// <summary>Script names in the active product.</summary>
    IReadOnlyList<string> ListScripts();

    /// <summary>Reads a script's source text, or null if it doesn't exist / no product is active.</summary>
    string? LoadScript(string name);

    /// <summary>Saves <c>.moon</c> source text into the active product.</summary>
    Result SaveScript(string name, string source);

    /// <summary>
    /// Runs raw <c>.moon</c> source against the engine, streaming each emitted line to
    /// <paramref name="onOutput"/> (off the UI thread). The result carries success /
    /// cancel / error plus the full output. Drives the script editor's Run button.
    /// <paramref name="args"/> is optional argument text (a Lua table literal, e.g.
    /// <c>{ preset = "fine" }</c>) the script reads as the global <c>args</c>.
    /// </summary>
    Task<ScriptRunResult> RunScriptAsync(
        string source, Action<string>? onOutput = null, CancellationToken ct = default, string? args = null);

    // --- Decks (ordered program chains, operate on the active product) ---

    /// <summary>Deck names in the active product.</summary>
    IReadOnlyList<string> ListDecks();

    /// <summary>Name of the last-used deck in the active product — auto-loaded by the editor; null if none.</summary>
    string? ActiveDeck { get; }

    /// <summary>Loads a deck by name.</summary>
    Result<WorkflowDeck> LoadDeck(string name);

    /// <summary>Saves a deck into the active product (refused once the deck limit is reached).</summary>
    Result SaveDeck(WorkflowDeck deck);

    /// <summary>
    /// Commits the deck: saves the working file (and activates it) <i>and</i> records a
    /// new numbered revision (UID-keyed) in the version repository, with an optional
    /// operator <paramref name="note"/>. Unlike <see cref="SaveDeck"/>, this bumps the version.
    /// </summary>
    Result CommitDeck(WorkflowDeck deck, string? note);

    /// <summary>
    /// Checks out a past deck revision (by UID + version) into the working copy: writes
    /// the stored snapshot back as the current deck and re-activates it. Does <b>not</b>
    /// create a new version (commit afterwards to keep it). Fails if no product is active
    /// or the version is unknown.
    /// </summary>
    Result RestoreDeckVersion(string deckUid, int version);

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

    /// <summary>
    /// The stable UID of a deck by name, read <b>without</b> activating it (unlike
    /// <see cref="LoadDeck"/>). For the version repository to look up a deck's history
    /// while browsing without changing the active deck. Null if missing / legacy UID-less.
    /// </summary>
    string? GetDeckUid(string name);

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
    /// Persists the open interactive DUT run to disk in its current state (no-op if none is
    /// open). <see cref="RecordDutValue"/> mutates only in memory, so callers that append
    /// long-lived operator activity flush periodically to survive a mid-run crash. Thread-safe.
    /// </summary>
    void FlushDutRun();

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

    /// <summary>
    /// True while a program, deck or recipe run is in progress (paused or not). Cheap and
    /// thread-safe — Layer 3 polls it to lock manual controls during a run.
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// Station utilisation (OEE) — online sessions, executions, engineer-declared downtime
    /// and finished units in the machine-global <c>oee.db</c>. Programs / decks / recipes /
    /// scripts are bracketed here automatically; a head starts / ends the online session
    /// and may bracket its own executions. See <c>docs/oee-design.md</c>.
    /// </summary>
    OeeTracker Oee { get; }

    /// <summary>Raised when the running program's paused state changes (true = paused).</summary>
    event Action<bool>? PausedChanged;

    // --- SiPhOG labelled snapshots (product folder siphog_data\{serial}.json) ---

    /// <summary>
    /// Samples the six SiPhOG values for <see cref="SiphogSnapshotRequest.WindowMs"/> (mean /
    /// min / max / std), stamps label + time + operator, and appends the set to the DUT's
    /// <c>siphog_data\{serial}.json</c>. Serial defaults to <see cref="ActiveSerial"/>. Drives the
    /// <c>siphog.saveSnapshot</c> block and <c>machine.siphog.save</c>.
    /// </summary>
    Task<Result<SiphogSnapshotSet>> SaveSiphogSnapshotAsync(SiphogSnapshotRequest request, CancellationToken ct = default);

    /// <summary>Serials with a SiPhOG snapshot file in the active product.</summary>
    IReadOnlyList<string> ListSiphogSnapshotSerials();

    /// <summary>A DUT's SiPhOG snapshot file, or null when none / unreadable.</summary>
    SiphogSnapshotFile? GetSiphogSnapshots(string serial);

    /// <summary>Raised (on the saving thread) after a SiPhOG snapshot set is written; arg = the DUT serial.</summary>
    event Action<string>? SiphogSnapshotSaved;

    /// <summary>Raised before and after each executed step of a running program.</summary>
    event Action<WorkflowStepEvent>? StepEvent;

    /// <summary>
    /// Raised (off the UI thread) when a running <c>.moon</c> script reports progress via
    /// <c>machine.progress(pct, message)</c> — from a <c>script.run</c> step or the editor.
    /// </summary>
    event Action<ScriptProgress>? ScriptProgressChanged;

    /// <summary>Raised for every workflow log line. The Layer 2 logging surface.</summary>
    event Action<LogEntry>? LogEmitted;
}
