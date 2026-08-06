using Horizon.Common;
using Horizon.Engine;
using Horizon.Workflow.Calibration;
using Horizon.Workflow.Dispensing;
using Horizon.Workflow.FauPreAlign;
using Horizon.Workflow.HillClimb;
using Horizon.Workflow.Recipe;
using Horizon.Workflow.Scripting;
using Horizon.Workflow.Signals;
using Horizon.Workflow.UCorrection;

namespace Horizon.Workflow;

/// <summary>
/// Default in-process implementation of <see cref="IProcessService"/> — the
/// Layer 2 module on top of <see cref="IHorizonEngine"/>.
/// Phase 1: product management. Phase 2: teach points / named positions.
/// </summary>
public sealed class ProcessService : IProcessService
{
    private readonly IHorizonEngine _engine;
    private readonly ProductStore _products = new();
    private PositionStore? _positions;   // null when no product is active
    private ProgramStore? _programs;     // null when no product is active
    private RecipeStore? _recipes;       // null when no product is active
    private ScriptStore? _scripts;       // .moon Lua scripts; null when no product is active
    private DeckStore? _decks;           // null when no product is active
    private VisionProfileStore? _visionProfiles;   // null when no product is active
    private DispenseStore? _dispense;    // per-product dispense routines; null when no product is active
    private TaskCompletionSource<bool>? _deckGate;   // set while a deck waits for operator activation
    private DutStore? _dut;              // null when no product is active
    private readonly ProgramHistoryDb _history = new();   // machine-global audit store (history.db)
    private readonly DeviceLinkStore _deviceLinks = new();   // machine-global, not per-product
    private readonly CameraNeedleCalibrationStore _cameraNeedle = new();   // machine-global camera->needle calibration
    private readonly AppSettingsStore _appSettings = new();   // machine-global app_settings.json (read-only here)
    private PauseController? _pause;     // non-null only while a program runs
    private OperatorContext? _operator;  // current operator, stamped onto DUT runs
    private (DutRecord Record, DutRun Run)? _interactiveDut;   // open operator-driven DUT run
    private (DutRecord Record, DutRun Run)? _runDut;   // the running program/deck's DUT (up-front serial, or set mid-run by dut.promptSerial)
    private Func<IHillClimbFeedback?>? _hillClimbFeedbackProvider;   // Layer 3 (SiPhOG) wires the hill-climb signal source
    private IUtilityAppHost? _utilityAppHost;   // Layer 3 Run-page springboard, driven by app.* steps
    private ISiphogSheetSink? _siphogSheetSink;   // Layer 3 SiPhOG -> Google Sheet writer, driven by siphog.updateSheet steps
    private readonly SignalRegistry _signals;   // built-in engine signals + any product source a head registers

    public ProcessService(IHorizonEngine engine)
    {
        _engine = engine;
        _signals = new SignalRegistry(new EngineSignalSource(engine));

        // Load stores for a product that was already active from a prior run.
        var dir = _products.ActiveProductDir;
        if (dir != null)
        {
            _positions = new PositionStore(dir);
            _programs = new ProgramStore(dir);
            _recipes = new RecipeStore(dir);
            _scripts = new ScriptStore(dir);
            _decks = new DeckStore(dir);
            _visionProfiles = new VisionProfileStore(dir);
            _dispense = new DispenseStore(dir);
            _dut = new DutStore(dir, _products.ActiveProduct!);
        }
    }

    public IReadOnlyList<string> Products => _products.GetProductNames();

    public string? ActiveProduct => _products.ActiveProduct;

    public ProductInfo? ActiveProductInfo
    {
        get
        {
            var active = _products.ActiveProduct;
            return active == null ? null : _products.ReadInfo(active);
        }
    }

    public ProductInfo? GetProductInfo(string name) => _products.ReadInfo(name);

    public event Action<WorkflowStepEvent>? StepEvent;
    public event Action<DeckProgress>? DeckStepEvent;

    /// <summary>
    /// Raised when a deck pauses on a manual (auto-off) transition and is waiting
    /// for the operator to activate the next program. Resume with
    /// <see cref="ActivateNextDeckProgram"/>.
    /// </summary>
    public event Action<DeckProgress>? DeckAwaitingActivation;
    public event Action<LogEntry>? LogEmitted;
    public event Action<bool>? PausedChanged;
    public event Action<string?>? ProductChanged;
    public event Action<string?>? ActiveSerialChanged;

    public string? ActiveSerial { get; private set; }

    /// <summary>Updates <see cref="ActiveSerial"/> and notifies the front-end (no-op if unchanged).</summary>
    private void SetActiveSerial(string? serial)
    {
        if (string.Equals(ActiveSerial, serial, StringComparison.Ordinal)) return;
        ActiveSerial = serial;
        ActiveSerialChanged?.Invoke(serial);
    }

    public bool ClearActiveSerial()
    {
        if (HasActiveDutRun) return false;   // a run owns the serial — don't clear mid-run
        if (ActiveSerial == null) return false;
        SetActiveSerial(null);
        return true;
    }

    public IOperatorPrompt? OperatorPrompt { get; set; }

    public bool IsPaused => _pause?.IsPaused ?? false;

    public void Pause() => _pause?.Pause();
    public void Resume() => _pause?.Resume();
    public void Step() => _pause?.Step();

    // --- Products ---

    public Result CreateProduct(string name)
    {
        if (!_products.IsValidName(name))
            return Result.Fail(ErrorCode.InvalidParameter, $"Invalid product name: '{name}'.");
        if (_products.Exists(name))
            return Result.Fail(ErrorCode.OperationFailed, $"Product '{name}' already exists.");

        _products.Create(name);
        Log(LogLevel.Info, $"Product created: {name}");
        return Result.Ok();
    }

    public Result DeleteProduct(string name)
    {
        if (!_products.Exists(name))
            return Result.Fail(ErrorCode.FileNotFound, $"Product '{name}' not found.");

        var wasActive = string.Equals(_products.ActiveProduct, name, StringComparison.OrdinalIgnoreCase);
        if (wasActive && !CanSwitchProduct(out var why))
            return Result.Fail(ErrorCode.OperationFailed, $"Cannot delete the active product: {why}");

        _products.Delete(name);
        if (wasActive)
        {
            _products.SetActive(null);
            _positions = null;
            _programs = null;
            _recipes = null;
            _scripts = null;
            _decks = null;
            _dispense = null;
            _dut = null;
            SetActiveSerial(null);
            ProductChanged?.Invoke(null);
        }

        Log(LogLevel.Info, $"Product deleted: {name}");
        return Result.Ok();
    }

    public Result CloneProduct(string source, string newName)
    {
        if (!_products.Exists(source))
            return Result.Fail(ErrorCode.FileNotFound, $"Source product '{source}' not found.");
        if (!_products.IsValidName(newName))
            return Result.Fail(ErrorCode.InvalidParameter, $"Invalid product name: '{newName}'.");
        if (_products.Exists(newName))
            return Result.Fail(ErrorCode.OperationFailed, $"Product '{newName}' already exists.");

        _products.Clone(source, newName);
        Log(LogLevel.Info, $"Product cloned: {source} -> {newName}");
        return Result.Ok();
    }

    public Result LoadProduct(string name)
    {
        if (!_products.Exists(name))
            return Result.Fail(ErrorCode.FileNotFound, $"Product '{name}' not found.");
        if (!CanSwitchProduct(out var why))
            return Result.Fail(ErrorCode.OperationFailed, $"Cannot switch product: {why}");

        _products.SetActive(name);
        var dir = _products.ProductDir(name);
        _positions = new PositionStore(dir);
        _programs = new ProgramStore(dir);
        _recipes = new RecipeStore(dir);
        _scripts = new ScriptStore(dir);
        _decks = new DeckStore(dir);
        _visionProfiles = new VisionProfileStore(dir);
        _dispense = new DispenseStore(dir);
        _dut = new DutStore(dir, name);
        SetActiveSerial(null);   // a serial belongs to a product — drop it on switch
        Log(LogLevel.Info, $"Product loaded: {name}");
        ProductChanged?.Invoke(name);
        return Result.Ok();
    }

    public void UnloadProduct()
    {
        if (!CanSwitchProduct(out var why))
        {
            Log(LogLevel.Warning, $"Product not unloaded: {why}");
            return;
        }
        _products.SetActive(null);
        _positions = null;
        _programs = null;
        _recipes = null;
        _scripts = null;
        _decks = null;
        _visionProfiles = null;
        _dispense = null;
        _dut = null;
        SetActiveSerial(null);
        Log(LogLevel.Info, "Product unloaded.");
        ProductChanged?.Invoke(null);
    }

    /// <summary>
    /// Guards a product switch: refused while a program / recipe / deck is running
    /// or an interactive DUT run is open, since either would split a unit's data
    /// across two products.
    /// </summary>
    private bool CanSwitchProduct(out string reason)
    {
        if (_pause != null) { reason = "a process is running."; return false; }
        if (_interactiveDut != null) { reason = "a DUT run is open."; return false; }
        reason = string.Empty;
        return true;
    }

    // --- Teach points / named positions ---

    public IReadOnlyList<string> GetPositionNames(string device)
        => _positions?.GetNames(device) ?? Array.Empty<string>();

    public NamedPosition? GetPosition(string device, string name)
        => _positions?.Get(device, name);

    public Result RemovePosition(string device, string name)
    {
        if (_positions == null)
            return Result.Fail(ErrorCode.OperationFailed, "No active product — load one first.");
        if (!_positions.Remove(device, name))
            return Result.Fail(ErrorCode.FileNotFound, $"Position '{name}' not found for '{device}'.");

        Log(LogLevel.Info, $"Position removed: {device}.{name}");
        return Result.Ok();
    }

    public Result SetPosition(string device, string name, MotionPosition position, double velocity)
    {
        if (_positions == null)
            return Result.Fail(ErrorCode.OperationFailed, "No active product — load one first.");
        if (string.IsNullOrWhiteSpace(name))
            return Result.Fail(ErrorCode.InvalidParameter, "Position name is required.");

        _positions.Set(device, name, position, velocity);
        Log(LogLevel.Info, $"Position set (manual): {device}.{name} (velocity {velocity})");
        return Result.Ok();
    }

    public Result SetPositionFull(string device, string name, MotionPosition position, double velocity,
        string? camera, double? exposureUs)
    {
        if (_positions == null)
            return Result.Fail(ErrorCode.OperationFailed, "No active product — load one first.");
        if (string.IsNullOrWhiteSpace(name))
            return Result.Fail(ErrorCode.InvalidParameter, "Position name is required.");

        _positions.SetFull(device, name, position, velocity, camera, exposureUs);
        Log(LogLevel.Info,
            $"Position set: {device}.{name} (velocity {velocity}" +
            (string.IsNullOrWhiteSpace(camera) || exposureUs == null ? ")" : $", {camera} @ {exposureUs}µs)"));
        return Result.Ok();
    }

    public async Task<Result> TeachPositionAsync(string device, string name, double velocity)
    {
        if (_positions == null)
            return Result.Fail(ErrorCode.OperationFailed, "No active product — load one first.");
        if (string.IsNullOrWhiteSpace(name))
            return Result.Fail(ErrorCode.InvalidParameter, "Position name is required.");

        var read = await _engine.Motion.GetPositionAsync(device).ConfigureAwait(false);
        if (!read.IsSuccess)
            return Result.Fail(read.Error, $"Teach '{name}': {read.Message}");

        _positions.Set(device, name, read.Value, velocity);
        Log(LogLevel.Info, $"Position taught: {device}.{name} (velocity {velocity})");
        return Result.Ok();
    }

    public Task<Result> MoveToNamedPositionAsync(string device, string name, CancellationToken ct = default)
        => MoveToNamedPositionCoreAsync(device, name, new MoveOverrides(), ct);

    public Task<Result> MoveToNamedPositionAsync(string device, string name, double velocityOverride, CancellationToken ct = default)
        => MoveToNamedPositionCoreAsync(device, name, new MoveOverrides(velocityOverride), ct);

    /// <summary>
    /// Recalls a named position, applying the step's <see cref="MoveOverrides"/> at the
    /// start of the move: a velocity / camera / exposure given on the step takes
    /// precedence over the position's stored values; anything left default falls back
    /// to the position's. The camera exposure is applied while the stage is already
    /// travelling. This is the back-end for the move ops' velocity / camera fields.
    /// </summary>
    private async Task<Result> MoveToNamedPositionCoreAsync(
        string device, string name, MoveOverrides overrides, CancellationToken ct)
    {
        if (_positions == null)
            return Result.Fail(ErrorCode.OperationFailed, "No active product — load one first.");

        var np = _positions.Get(device, name);
        if (np == null)
            return Result.Fail(ErrorCode.FileNotFound, $"Position '{name}' not found for '{device}'.");

        // 1) Apply velocity BEFORE moving — the step override wins over the position's
        //    stored velocity; 0 leaves the device's current velocity unchanged.
        var velocity = MotionVelocityLimits.Clamp(
            overrides.Velocity > 0 ? overrides.Velocity : np.Velocity,
            _engine.Motion.GetDevice(device)?.ControllerType);
        if (velocity > 0)
        {
            var vel = await _engine.Motion.SetDeviceVelocityAsync(device, velocity).ConfigureAwait(false);
            if (!vel.IsSuccess)
                return Result.Fail(vel.Error, $"Move {device} -> '{name}': velocity — {vel.Message}");
            Log(LogLevel.Info, $"Velocity set: {device} = {velocity}");
        }

        // 2) Start the move (do not await yet) so the camera exposure can be applied
        //    AFTER the move has started — i.e. while the stage travels.
        Log(LogLevel.Info, $"Move {device} -> '{name}'");
        var moveTask = _engine.Motion.MoveToPositionAsync(device, np.Coordinates, ct);

        // 3) While moving, apply the camera exposure — step override wins, else the
        //    position's pinned camera / exposure. Never fails the move.
        var camera = string.IsNullOrWhiteSpace(overrides.Camera) ? np.Camera : overrides.Camera;
        var exposure = overrides.ExposureUs ?? np.ExposureUs;
        await MoveExposure.ApplyAsync(_engine.Camera, camera, exposure, Log).ConfigureAwait(false);

        // 4) Wait for the move to settle and return its result.
        return await moveTask.ConfigureAwait(false);
    }

    // --- Vision profiles ---

    public IReadOnlyList<string> ListVisionProfiles()
        => _visionProfiles?.List() ?? Array.Empty<string>();

    public string? ActiveVisionProfile => _visionProfiles?.ActiveProfile;

    public VisionProfile? LoadVisionProfile(string name)
    {
        var profile = _visionProfiles?.Load(name);
        if (profile != null) _visionProfiles!.SetActive(name);
        return profile;
    }

    /// <summary>
    /// Saves a vision profile (assigning a UID if it has none), prunes a superseded
    /// template's binaries, and records an operator-stamped revision for traceability.
    /// </summary>
    public Result<VisionProfile> SaveVisionProfile(VisionProfile profile)
    {
        if (_visionProfiles == null)
            return Result<VisionProfile>.Fail(ErrorCode.OperationFailed, "No active product — load one first.");
        if (string.IsNullOrWhiteSpace(profile.Name))
            return Result<VisionProfile>.Fail(ErrorCode.InvalidParameter, "Vision profile needs a name.");

        if (string.IsNullOrEmpty(profile.Uid))
            profile = profile with { Uid = "vp-" + Guid.NewGuid().ToString("N") };

        // Drop the previously-saved template binaries if the template changed.
        var previous = _visionProfiles.Load(profile.Name);
        var oldTpl = previous?.Template?.Uid;
        if (!string.IsNullOrEmpty(oldTpl) && !string.Equals(oldTpl, profile.Template?.Uid, StringComparison.Ordinal))
            _visionProfiles.DeleteTemplate(oldTpl);

        _visionProfiles.Save(profile);
        _visionProfiles.SetActive(profile.Name);
        Log(LogLevel.Info, $"Vision profile saved: {profile.Name}");

        try
        {
            var product = _products.ActiveProduct;
            if (product != null)
            {
                var version = _history.AppendVisionProfile(product, profile, _operator);
                Log(LogLevel.Info,
                    $"History: vision {profile.Name} v{version.Version}" +
                    (string.IsNullOrEmpty(version.OperatorName) ? "" : $" by {version.OperatorName}"));
            }
        }
        catch (Exception ex)
        {
            Log(LogLevel.Warning, $"Vision history not recorded for '{profile.Name}': {ex.Message}");
        }
        return Result<VisionProfile>.Ok(profile);
    }

    public Result DeleteVisionProfile(string name)
    {
        if (_visionProfiles == null)
            return Result.Fail(ErrorCode.OperationFailed, "No active product — load one first.");
        _visionProfiles.Delete(name);
        Log(LogLevel.Info, $"Vision profile deleted: {name}");
        return Result.Ok();
    }

    public VisionProfileHistory? GetVisionProfileHistory(string profileUid)
    {
        var product = _products.ActiveProduct;
        return product == null ? null : _history.LoadVisionHistory(product, profileUid);
    }

    public string? GetTemplateImagePath(string templateUid)
    {
        if (_visionProfiles == null || string.IsNullOrEmpty(templateUid)) return null;
        var path = _visionProfiles.ImagePath(templateUid);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Captures a new NCC template from <paramref name="roi"/> on <paramref name="frame"/>,
    /// minting a fresh template UID and writing the model + image under the product's
    /// <c>vision\templates\</c>. Returns the new <see cref="TemplateRef"/> — assign it to
    /// the profile and <see cref="SaveVisionProfile"/> to persist.
    /// </summary>
    public async Task<Result<TemplateRef>> CaptureTemplateAsync(VisionProfile profile, CameraFrame frame, RoiSpec roi)
    {
        if (_visionProfiles == null)
            return Result<TemplateRef>.Fail(ErrorCode.OperationFailed, "No active product — load one first.");

        var uid = VisionProfileStore.NewTemplateUid();
        _visionProfiles.EnsureTemplateDir(uid);
        var modelPath = _visionProfiles.ModelPath(uid);
        var imagePath = _visionProfiles.ImagePath(uid);

        var r = await _engine.Vision
            .CreateNccModelAsync(frame, roi, profile.Filters, modelPath, imagePath, profile.NccAngleRange)
            .ConfigureAwait(false);
        if (!r.IsSuccess)
        {
            _visionProfiles.DeleteTemplate(uid);   // no orphan dir if the model failed
            return Result<TemplateRef>.Fail(r.Error, r.Message);
        }

        var info = r.Value;
        var tref = new TemplateRef
        {
            Uid = uid, Roi = roi,
            AnchorX = info.AnchorX, AnchorY = info.AnchorY,
            Width = info.Width, Height = info.Height,
        };
        Log(LogLevel.Info, $"Vision template captured: {uid} ({info.Width}×{info.Height})");
        return Result<TemplateRef>.Ok(tref);
    }

    /// <summary>
    /// Runs a profile's detection against <paramref name="frame"/> using the profile's
    /// method (NCC / circle / line), filter chain and search ROI. Returns a unified
    /// key→value result (centre, score, method-specific fields) plus a crop.
    /// </summary>
    public Task<Result<VisionDetectResult>> RunVisionProfileAsync(VisionProfile profile, CameraFrame frame)
        => RunDetectionAsync(profile, frame, cameraId: null);

    /// <summary>
    /// Loads a vision profile by name and runs its detection against the latest cached
    /// frame from the profile's camera (the camera must be grabbing). Back-end for the
    /// <c>vision.runProfile</c> workflow step; does not change the active profile.
    /// </summary>
    /// <summary>
    /// Registers the hill-climb signal source used by <c>hillclimb.start</c> steps. The
    /// engine itself has no SiPhOG facet, so a Layer 3 head (FAAM.UI) supplies one backed by
    /// its live SiPhOG client. Pass null to unregister.
    /// </summary>
    public void SetHillClimbFeedbackProvider(Func<IHillClimbFeedback?>? provider)
        => _hillClimbFeedbackProvider = provider;

    public IHillClimbFeedback? GetHillClimbFeedback()
        => _hillClimbFeedbackProvider?.Invoke();

    public void SetUtilityAppHost(IUtilityAppHost? host)
        => _utilityAppHost = host;

    public void SetSiphogSheetSink(ISiphogSheetSink? sink)
        => _siphogSheetSink = sink;

    /// <summary>
    /// Registers a product-specific signal source (e.g. FAAM's SiPhOG) so its values
    /// join the unified <c>signal.read</c> catalog. The built-in engine signals
    /// (SMU / PSU / Measurement / TEC / Laser) are always present; this adds to them.
    /// </summary>
    public void AddSignalSource(ISignalSource source) => _signals.Add(source);

    /// <summary>
    /// The signals currently available to a <c>signal.read</c> block — the union of the
    /// built-in engine catalog and every registered product source. The editor calls
    /// this to fill the read-out drop-down; rebuilt on each call so newly-connected
    /// hardware appears.
    /// </summary>
    public IReadOnlyList<SignalDescriptor> DescribeSignals() => _signals.Describe();

    public event Action<VisionProfileDetection>? VisionProfileRan;

    public async Task<Result<VisionDetectResult>> RunVisionProfileByNameAsync(string profileName)
    {
        if (_visionProfiles == null)
            return Result<VisionDetectResult>.Fail(ErrorCode.OperationFailed, "No active product — load one first.");
        var profile = _visionProfiles.Load(profileName);
        if (profile == null)
            return Result<VisionDetectResult>.Fail(ErrorCode.FileNotFound, $"Vision profile '{profileName}' not found.");

        var r = await RunDetectionAsync(profile, frame: null, cameraId: profile.CameraId).ConfigureAwait(false);
        if (r.IsSuccess)
            VisionProfileRan?.Invoke(new VisionProfileDetection(profile.CameraId, profile.Name, r.Value));
        return r;
    }

    /// <summary>
    /// Measures the vision-based U correction: drives the gantry through the four reference points,
    /// runs each point's saved vision profile (a Line detection reporting the facet angle), and
    /// returns the PIC/FAU angle averages + the resulting U delta. Applies <b>no</b> move — the
    /// caller inspects <see cref="VisionUCorrectionResult"/> and decides whether to apply
    /// (via <see cref="ApplyHexUCorrectionAsync"/>) or fall back. Stops at the first undetected facet
    /// (<see cref="VisionUCorrectionResult.AllFound"/> false, <c>FailedPoint</c> set).
    /// </summary>
    public async Task<Result<VisionUCorrectionResult>> MeasureVisionUCorrectionAsync(
        VisionUCorrectionOptions o, CancellationToken ct = default)
    {
        if (_visionProfiles == null || _positions == null)
            return Result<VisionUCorrectionResult>.Fail(ErrorCode.OperationFailed, "No active product — load one first.");

        var points = new[]
        {
            (o.P1Position, o.P1Profile),
            (o.P2Position, o.P2Profile),
            (o.F1Position, o.F1Profile),
            (o.F2Position, o.F2Profile),
        };
        var angles = new double[4];
        string? failedPoint = null;

        for (int i = 0; i < points.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (position, profile) = points[i];

            var mv = await MoveToNamedPositionCoreAsync(o.GantryDevice, position, new MoveOverrides(), ct).ConfigureAwait(false);
            if (!mv.IsSuccess)
                return Result<VisionUCorrectionResult>.Fail(mv.Error, $"Vision U: move to {position} failed — {mv.Message}");

            if (o.MoveSettleMs > 0)
                await Task.Delay(o.MoveSettleMs, ct).ConfigureAwait(false);

            var det = await RunVisionProfileByNameAsync(profile).ConfigureAwait(false);
            if (!det.IsSuccess)
                return Result<VisionUCorrectionResult>.Fail(det.Error, $"Vision U: profile '{profile}' at {position} failed — {det.Message}");

            var angleVal = det.Value.Found
                ? det.Value.Values.FirstOrDefault(v => string.Equals(v.Key, "angle", StringComparison.OrdinalIgnoreCase))
                : null;
            if (angleVal == null)
            {
                // Not detected (or the profile isn't a Line profile) → stop; caller falls back.
                failedPoint = position;
                break;
            }
            angles[i] = angleVal.Value;
            Log(LogLevel.Info, $"Vision U: {position} [{profile}] angle = {angles[i]:F3}°");
        }

        bool allFound = failedPoint == null;
        double picAvg = (angles[0] + angles[1]) / 2.0;
        double fauAvg = (angles[2] + angles[3]) / 2.0;
        double delta = o.Sign * (picAvg - fauAvg);

        return Result<VisionUCorrectionResult>.Ok(new VisionUCorrectionResult(
            allFound, failedPoint,
            angles[0], angles[1], angles[2], angles[3],
            picAvg, fauAvg, delta));
    }

    /// <summary>Hard cap (deg) on a single applied vision U correction — a fail-safe against a bad measurement.</summary>
    private const double VisionUHardLimitDeg = 5.0;

    /// <summary>
    /// Applies a vision U correction as a relative U move on the hexapod. Refuses a rotation larger
    /// than <see cref="VisionUHardLimitDeg"/> (fail-safe) and treats a near-zero delta as a no-op.
    /// </summary>
    public async Task<Result> ApplyHexUCorrectionAsync(string hexDevice, double deltaDeg, CancellationToken ct = default)
    {
        if (double.IsNaN(deltaDeg))
            return Result.Fail(ErrorCode.InvalidParameter, "Vision U: delta is NaN.");
        if (Math.Abs(deltaDeg) > VisionUHardLimitDeg)
            return Result.Fail(ErrorCode.InvalidParameter,
                $"Vision U: correction {deltaDeg:F3}° exceeds the {VisionUHardLimitDeg:F1}° hard limit.");
        if (Math.Abs(deltaDeg) < 1e-4)
        {
            Log(LogLevel.Info, "Vision U: delta ≈ 0°, no move applied.");
            return Result.Ok();
        }
        return await _engine.Motion.MoveRelativeAsync(hexDevice, "U", deltaDeg, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Shared detection dispatch. Exactly one of <paramref name="frame"/> (UI, a specific
    /// frame) or <paramref name="cameraId"/> (workflow, latest cached frame) is supplied.
    /// </summary>
    private async Task<Result<VisionDetectResult>> RunDetectionAsync(
        VisionProfile profile, CameraFrame? frame, string? cameraId)
    {
        if (_visionProfiles == null)
            return Result<VisionDetectResult>.Fail(ErrorCode.OperationFailed, "No active product — load one first.");

        // When running against the live camera (workflow / script path), reproduce the
        // exposure the profile was saved under, then wait for a frame captured with it —
        // so detection sees the same condition the profile was tuned on.
        if (cameraId != null)
            await ApplyProfileExposureAsync(profile, cameraId).ConfigureAwait(false);

        var vision = _engine.Vision;
        switch ((profile.DetectionMethod ?? "ncc").ToLowerInvariant())
        {
            case "circle":
                return frame != null
                    ? await vision.FindCircleAsync(frame, profile.Filters, profile.SearchRoi, profile.Circle).ConfigureAwait(false)
                    : await vision.FindCircleAsync(cameraId!, profile.Filters, profile.SearchRoi, profile.Circle).ConfigureAwait(false);

            case "line":
                return frame != null
                    ? await vision.FindLineAsync(frame, profile.Filters, profile.SearchRoi, profile.Line).ConfigureAwait(false)
                    : await vision.FindLineAsync(cameraId!, profile.Filters, profile.SearchRoi, profile.Line).ConfigureAwait(false);

            case "corner":
                return frame != null
                    ? await vision.FindCornerAsync(frame, profile.Filters, profile.SearchRoi, profile.Corner).ConfigureAwait(false)
                    : await vision.FindCornerAsync(cameraId!, profile.Filters, profile.SearchRoi, profile.Corner).ConfigureAwait(false);

            case "edgereg":
                return frame != null
                    ? await vision.FindEdgeRegAsync(frame, profile.Filters, profile.SearchRoi, profile.EdgeReg).ConfigureAwait(false)
                    : await vision.FindEdgeRegAsync(cameraId!, profile.Filters, profile.SearchRoi, profile.EdgeReg).ConfigureAwait(false);

            default: // ncc
            {
                if (profile.Template is not { } t || string.IsNullOrEmpty(t.Uid))
                    return Result<VisionDetectResult>.Fail(ErrorCode.OperationFailed, "Profile has no template — teach one first.");
                var modelPath = _visionProfiles.ModelPath(t.Uid);
                int cropSize = Math.Max(t.Width, t.Height);
                var r = frame != null
                    ? await vision.FindNccModelAsync(frame, modelPath, profile.Filters, profile.MinScore, cropSize, profile.SearchRoi, profile.NccAngleRange).ConfigureAwait(false)
                    : await vision.FindNccModelAsync(cameraId!, modelPath, profile.Filters, profile.MinScore, cropSize, profile.SearchRoi, profile.NccAngleRange).ConfigureAwait(false);
                return r.IsSuccess
                    ? Result<VisionDetectResult>.Ok(NccToResult(profile, r.Value))
                    : Result<VisionDetectResult>.Fail(r.Error, r.Message);
            }
        }
    }

    /// <summary>
    /// Pushes the profile's saved exposure (auto flag + µs) to its camera and waits for a
    /// frame captured under it. No-op if the camera is disconnected or the exposure already
    /// matches. Best-effort: a failed apply is logged, not fatal — detection still runs on
    /// whatever frame is cached.
    /// </summary>
    private async Task ApplyProfileExposureAsync(VisionProfile profile, string cameraId)
    {
        if (!_engine.Camera.IsConnected(cameraId)) return;

        var cur = _engine.Camera.GetState(cameraId).Exposure;
        bool already = cur.AutoExposure == profile.AutoExposure &&
            (profile.AutoExposure || Math.Abs(cur.ExposureUs - profile.ExposureUs) < 1.0);
        if (already) return;

        var exp = cur with { AutoExposure = profile.AutoExposure, ExposureUs = profile.ExposureUs };
        var r = await _engine.Camera.ApplyExposureAsync(cameraId, exp).ConfigureAwait(false);
        if (!r.IsSuccess)
        {
            Log(LogLevel.Warning, $"Vision '{profile.Name}': exposure apply failed ({r.Message}).");
            return;
        }

        // Give the sensor time to complete the exposure change, then wait for a frame
        // captured after the settle — so detection never runs on a mid-transition frame.
        // Settle duration is operator-configurable (app_settings.json).
        int settleMs = _appSettings.Current.VisionExposureSettleMs;
        if (settleMs > 0)
            await Task.Delay(settleMs).ConfigureAwait(false);
        await WaitForFreshFrameAsync(cameraId).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits until the camera streams a frame newer than the one currently cached (so a
    /// just-changed exposure has taken effect), bounded by a timeout. Requires the camera
    /// to be grabbing; returns immediately once a newer frame arrives or the timeout lapses.
    /// </summary>
    private async Task WaitForFreshFrameAsync(string cameraId, int freshFrames = 2, int timeoutMs = 1500)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int seen = 0;
        void OnFrame(CameraFrame f)
        {
            if (!string.Equals(f.Camera, cameraId, StringComparison.OrdinalIgnoreCase)) return;
            if (Interlocked.Increment(ref seen) >= freshFrames) tcs.TrySetResult(true);
        }

        _engine.Camera.FrameReady += OnFrame;
        try
        {
            await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)).ConfigureAwait(false);
        }
        finally
        {
            _engine.Camera.FrameReady -= OnFrame;
        }
    }

    /// <summary>
    /// Maps an NCC match to the unified key→value result, applying the taught anchor
    /// offset (anchor − template-centre) so the reported anchor is in frame coordinates.
    /// </summary>
    private static VisionDetectResult NccToResult(VisionProfile profile, NccFindResult result)
    {
        var m = result.Match;
        double offX = 0, offY = 0, rotation = 0;
        if (profile.Template is { } t)
        {
            offX = t.AnchorX - t.Width / 2.0;
            offY = t.AnchorY - t.Height / 2.0;
            rotation = t.Rotation;
        }
        var values = new[]
        {
            new VisionResultValue("x", m.CenterX),
            new VisionResultValue("y", m.CenterY),
            new VisionResultValue("anchorX", m.CenterX + offX),
            new VisionResultValue("anchorY", m.CenterY + offY),
            // matched instance rotation + the taught anchor reference orientation
            new VisionResultValue("angle", m.Angle + rotation),
            new VisionResultValue("score", m.Score),
        };
        return new VisionDetectResult(true, values, result.Crop);
    }

    // --- Process programs ---

    public IReadOnlyList<string> ListPrograms()
        => _programs?.List() ?? Array.Empty<string>();

    public Result<WorkflowProgram> LoadProgram(string name)
    {
        if (_programs == null)
            return Result<WorkflowProgram>.Fail(ErrorCode.OperationFailed, "No active product — load one first.");

        var program = _programs.Load(name);
        return program == null
            ? Result<WorkflowProgram>.Fail(ErrorCode.FileNotFound, $"Program '{name}' not found.")
            : Result<WorkflowProgram>.Ok(program);
    }

    public Result SaveProgram(WorkflowProgram program)
    {
        if (_programs == null)
            return Result.Fail(ErrorCode.OperationFailed, "No active product — load one first.");

        // Save writes the working file only — it does NOT create a version. A version is
        // created explicitly via CommitProgram (git-style: working copy vs commits).
        _programs.Save(program);
        Log(LogLevel.Info, $"Program saved (working copy): {program.Name}");
        WarnMissingPositions(program);
        return Result.Ok();
    }

    public Result CommitProgram(WorkflowProgram program, string? note)
    {
        // Commit = save the working file, then record an operator-stamped, numbered
        // revision (with the optional note) into the version repository.
        var saved = SaveProgram(program);
        if (!saved.IsSuccess)
            return saved;

        try
        {
            var product = _products.ActiveProduct;
            if (product != null)
            {
                var version = _history.Append(product, program, _operator, NormalizeNote(note));
                Log(LogLevel.Info,
                    $"Committed: {program.Name} v{version.Version}" +
                    (string.IsNullOrEmpty(version.OperatorName) ? "" : $" by {version.OperatorName}") +
                    (string.IsNullOrEmpty(version.Note) ? "" : $" — \"{version.Note}\""));
            }
        }
        catch (Exception ex)
        {
            Log(LogLevel.Warning, $"Commit not recorded for '{program.Name}': {ex.Message}");
        }
        return Result.Ok();
    }

    public Result RestoreProgramVersion(string program, int version)
    {
        if (_programs == null)
            return Result.Fail(ErrorCode.OperationFailed, "No active product — load one first.");

        var product = _products.ActiveProduct;
        if (product == null)
            return Result.Fail(ErrorCode.OperationFailed, "No active product — load one first.");

        var snapshot = _history.LoadVersion(product, program, version);
        if (snapshot == null)
            return Result.Fail(ErrorCode.FileNotFound, $"Version v{version} of program '{program}' not found in history.");

        // Checkout: set the working file to the chosen version's content. It does NOT
        // create a new version — the operator commits afterwards if they want to keep it.
        var saved = SaveProgram(snapshot.Snapshot);
        if (saved.IsSuccess)
            Log(LogLevel.Info, $"Checked out v{version} of program '{program}' into the working copy.");
        return saved;
    }

    public ProgramHistory? GetProgramHistory(string name)
    {
        var product = _products.ActiveProduct;
        return product == null ? null : _history.Load(product, name);
    }

    /// <summary>Trims a commit note to null when blank (so the DB stores NULL, not an empty string).</summary>
    private static string? NormalizeNote(string? note)
        => string.IsNullOrWhiteSpace(note) ? null : note.Trim();

    public IReadOnlyList<HistoryRow> QueryHistory(HistoryQuery query) => _history.Query(query);

    /// <summary>
    /// Logs (non-fatally) any taught-position reference in the program that does not
    /// resolve in the active product. A program is the product's process and is
    /// inseparable from the positions it moves to — they live in the same product
    /// and travel together on switch, so a dangling reference is worth surfacing.
    /// Mirrors ConfigTool's cross-reference checks. Expression-valued names
    /// (containing <c>{</c>) are skipped — they resolve at run time.
    /// </summary>
    private void WarnMissingPositions(WorkflowProgram program)
    {
        if (_positions == null) return;

        void Check(string device, string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Contains('{')) return;
            if (_positions!.Get(device, name) == null)
                Log(LogLevel.Warning,
                    $"Program '{program.Name}' references position '{name}' on '{device}' " +
                    $"not taught in product '{ActiveProduct}'.");
        }

        void Walk(IEnumerable<WorkflowStep> steps)
        {
            foreach (var step in steps)
            {
                if (!step.Enabled) continue;
                switch (step.Op.ToLowerInvariant())
                {
                    case "motion.movetoposition":
                        Check(step.Device, step.Parameters.GetValueOrDefault("position", string.Empty));
                        break;
                    case "motion.parallelmove":
                        for (int i = 1; step.Parameters.TryGetValue($"device{i}", out var dev); i++)
                            Check(dev, step.Parameters.GetValueOrDefault($"position{i}", string.Empty));
                        break;
                    case "flow.parallel":
                        foreach (var branch in step.Branches)
                            Walk(branch.Steps);   // recurse into concurrent branches
                        break;
                }
            }
        }

        Walk(program.Steps);
    }

    public async Task<Result> RunAsync(
        WorkflowProgram program, string? serial = null, CancellationToken ct = default)
    {
        var pause = new PauseController();
        pause.PausedChanged += OnPausedChanged;
        _pause = pause;

        var ctx = new WorkflowContext();

        var runner = new WorkflowRunner(
            _engine,
            MoveToNamedPositionCoreAsync,
            name => _programs?.Load(name),
            (device, name) => _positions?.Get(device, name),
            SaveNamedPosition,
            RunVisionProfileByNameAsync,
            RunDispenseRoutineAsync,
            RunNeedleApproachAsync,
            () => _hillClimbFeedbackProvider?.Invoke(),
            () => _utilityAppHost,
            () => _siphogSheetSink,
            _signals,
            _deviceLinks,
            OperatorPrompt,
            // dut.promptSerial opens the DUT record mid-run into _runDut, which the
            // finally block persists with the run's verdict.
            (s, c) => SetRunDutSerial(s, program.Name, c),
            CloseRunDut,   // dut.close finalises + persists the DUT mid-run
            pause,
            e => StepEvent?.Invoke(e),
            Log);

        var dutSerial = OpenRunDut(serial, program.Name, ctx);

        Log(LogLevel.Info, $"Program started: {program.Name}" + (dutSerial != null ? $" [DUT {dutSerial}]" : ""));
        var verdict = DutVerdict.Fail;
        try
        {
            var result = await runner.RunAsync(program, ctx, ct).ConfigureAwait(false);
            verdict = result.IsSuccess ? DutVerdict.Pass : DutVerdict.Fail;
            var summary = $"Program {(result.IsSuccess ? "completed" : "failed")}: {program.Name}";
            if (!string.IsNullOrEmpty(result.Message))
                summary += $" — {result.Message}";
            Log(result.IsSuccess ? LogLevel.Info : LogLevel.Error, summary);
            return result;
        }
        catch (OperationCanceledException)
        {
            verdict = DutVerdict.Aborted;
            Log(LogLevel.Warning, $"Program cancelled: {program.Name}");
            return Result.Fail(ErrorCode.OperationFailed, "Program cancelled.");
        }
        finally
        {
            pause.PausedChanged -= OnPausedChanged;
            _pause = null;
            EndDut(_runDut, verdict);
            _runDut = null;
            PersistSessionIfUsed(ctx);
        }
    }

    /// <summary>
    /// When a run recorded into an open operator DUT session (the fallback path,
    /// not its own owned run), persists that session now so its entries — e.g. a
    /// <c>Read All Signals</c> snapshot — survive even before the operator presses
    /// Pass/Fail. No-op when the run owned its DUT or used none.
    /// </summary>
    private void PersistSessionIfUsed(WorkflowContext ctx)
    {
        if (_interactiveDut != null && ReferenceEquals(ctx.Dut, _interactiveDut.Value.Run))
            TrySaveDut(_interactiveDut.Value.Record, "session updated after run");
    }

    /// <summary>
    /// Back-end for the <c>dut.promptSerial</c> step: opens the DUT record for a
    /// serial entered mid-run and attaches it to both the run context and
    /// <see cref="_runDut"/> (so the run's verdict is persisted against it).
    /// Refused if no product is active, the serial is blank, or a serial is
    /// already set for this run.
    /// </summary>
    private Result SetRunDutSerial(string serial, string source, WorkflowContext ctx)
    {
        if (_dut == null)
            return Result.Fail(ErrorCode.OperationFailed, "No active product — cannot set DUT serial.");
        if (string.IsNullOrWhiteSpace(serial))
            return Result.Fail(ErrorCode.InvalidParameter, "Serial number is required.");
        // A serial this run already OWNS (up-front serial, or an earlier prompt) is
        // fixed; a fallback DUT (open session / active serial) may be overridden so a
        // deck's own DUT Entry block still works when a unit happens to be active.
        if (ctx.Dut != null && !ctx.DutIsFallback)
            return Result.Fail(ErrorCode.OperationFailed, "A DUT serial is already set for this run.");

        var begun = BeginDut(serial, source, ctx);   // creates the run, sets ctx.Dut
        if (begun == null)
            return Result.Fail(ErrorCode.OperationFailed, "Could not open the DUT record.");
        _runDut = begun;
        ctx.DutIsFallback = false;
        return Result.Ok();
    }

    /// <summary>
    /// Back-end for the <c>dut.close</c> step: finalises the DUT this run <em>owns</em>
    /// (<see cref="_runDut"/> — an up-front serial or a <c>dut.promptSerial</c> step) with
    /// <paramref name="verdict"/>, persists the record, and detaches it from
    /// <paramref name="ctx"/> so later <c>dut.*</c> steps skip and the program's own
    /// end-of-run close (the <c>RunAsync</c> finally) doesn't double-record. An open
    /// operator session (<see cref="_interactiveDut"/>) keeps its own lifecycle — its
    /// Pass/Fail belongs to the Run page — so a fallback DUT is detached, not ended.
    /// No-op when no DUT is attached.
    /// </summary>
    private Result CloseRunDut(DutVerdict verdict, WorkflowContext ctx)
    {
        if (ctx.Dut == null) return Result.Ok();

        if (_runDut != null && ReferenceEquals(ctx.Dut, _runDut.Value.Run))
        {
            EndDut(_runDut, verdict);   // adds the run to the record + persists
            _runDut = null;
        }
        else
        {
            // An adopted operator session (or sticky-serial fallback the Run page owns):
            // persist its entries so far, but leave the session open for its own Pass/Fail.
            if (_interactiveDut != null && ReferenceEquals(ctx.Dut, _interactiveDut.Value.Run))
                TrySaveDut(_interactiveDut.Value.Record, "dut.close (session left open)");
            Log(LogLevel.Info, $"dut.close: DUT {ctx.DutSerial} is operator-owned — detached, not ended.");
        }

        ctx.Dut = null;
        ctx.DutSerial = null;
        ctx.DutIsFallback = false;
        return Result.Ok();
    }

    private void OnPausedChanged(bool paused) => PausedChanged?.Invoke(paused);

    /// <summary>
    /// Writes a named position into the active product — the back-end for the
    /// <c>motion.updatePosition</c> / <c>updatePositionAxis</c> steps.
    /// </summary>
    private Result SaveNamedPosition(string device, string name, MotionPosition coordinates, double velocity)
    {
        if (_positions == null)
            return Result.Fail(ErrorCode.OperationFailed, "No active product — cannot update positions.");
        _positions.Set(device, name, coordinates, velocity);
        return Result.Ok();
    }

    // --- DUT test records ---

    public IReadOnlyList<string> ListDutRecords() => _dut?.List() ?? Array.Empty<string>();

    public DutRecord? GetDutRecord(string serial)
    {
        if (_dut == null || string.IsNullOrWhiteSpace(serial)) return null;
        // Only return a record that actually exists on disk — never materialise a
        // phantom for a serial that was never run.
        return _dut.List().Contains(serial.Trim(), StringComparer.OrdinalIgnoreCase)
            ? _dut.LoadOrCreate(serial.Trim())
            : null;
    }

    // --- Operator identity (traceability) ---

    public OperatorContext? Operator => _operator;

    public void SetOperator(OperatorContext? op)
    {
        _operator = op;
        Log(LogLevel.Info, op == null
            ? "Operator cleared."
            : $"Operator set: {op.DisplayName} ({op.UserId}) @ station {op.StationId}.");
    }

    // --- Interactive DUT runs (operator-driven, no WorkflowProgram) ---

    /// <summary>
    /// Guards the interactive DUT session (append / flush / finalise). Layer 3 mirrors
    /// operator activity from background threads (the jog pump, a debounced flush timer)
    /// while the UI thread may end the run, so these must not tear the record mid-write.
    /// </summary>
    private readonly object _interactiveDutGate = new();

    public bool HasActiveDutRun => _interactiveDut != null;

    public Result BeginDutRun(string serial, string source)
    {
        if (_dut == null)
            return Result.Fail(ErrorCode.OperationFailed, "No active product — load one first.");
        if (_interactiveDut != null)
            return Result.Fail(ErrorCode.OperationFailed,
                $"A DUT run is already open for '{_interactiveDut.Value.Record.Serial}'.");
        if (string.IsNullOrWhiteSpace(serial))
            return Result.Fail(ErrorCode.InvalidParameter, "Serial number is required.");

        var record = _dut.LoadOrCreate(serial.Trim());
        var run = new DutRun { StartedUtc = DateTime.UtcNow, Source = source, Verdict = DutVerdict.InProgress };
        StampOperator(run);
        record.Runs.Add(run);            // persist the open run now so the file exists from Start
        lock (_interactiveDutGate)
        {
            _interactiveDut = (record, run);
            TrySaveDut(record, "run started");
        }
        SetActiveSerial(record.Serial);
        Log(LogLevel.Info, $"DUT run started: {record.Serial} [{source}]" +
            (_operator != null ? $" by {_operator.DisplayName}" : ""));
        return Result.Ok();
    }

    public void RecordDutValue(string key, string value, DutValueType type = DutValueType.String)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        lock (_interactiveDutGate)
        {
            if (_interactiveDut == null) return;
            _interactiveDut.Value.Run.Entries.Add(new DutEntry(DateTime.UtcNow, key, value, type));
        }
    }

    public void FlushDutRun()
    {
        lock (_interactiveDutGate)
        {
            if (_interactiveDut == null || _dut == null) return;
            try { _dut.Save(_interactiveDut.Value.Record); }
            catch (Exception ex) { Log(LogLevel.Warning, $"DUT flush failed: {ex.Message}"); }
        }
    }

    public Result EndDutRun(DutVerdict verdict)
    {
        // Serialised against RecordDutValue / FlushDutRun so a background flush cannot save a
        // torn record while this finalises and clears the session.
        lock (_interactiveDutGate)
        {
            if (_interactiveDut == null)
                return Result.Fail(ErrorCode.OperationFailed, "No DUT run is open.");

            var (record, run) = _interactiveDut.Value;
            run.EndedUtc = DateTime.UtcNow;
            run.Verdict = verdict;
            // The run was added to record.Runs and persisted at Start (BeginDutRun); here we
            // just finalise it in place — do NOT add it again.
            _interactiveDut = null;

            if (_dut == null)
            {
                Log(LogLevel.Warning, $"DUT record for {record.Serial} not saved — product unloaded mid-run.");
                return Result.Fail(ErrorCode.OperationFailed, "Product unloaded mid-run.");
            }
            try
            {
                _dut.Save(record);
                Log(LogLevel.Info,
                    $"DUT record saved: {record.Serial} — run {verdict}, {run.Entries.Count} entry(ies).");
                return Result.Ok();
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, $"DUT record save failed for {record.Serial}: {ex.Message}");
                return Result.Fail(ErrorCode.OperationFailed, ex.Message);
            }
        }
    }

    /// <summary>
    /// Opens (or creates) the DUT record for a run and attaches a fresh
    /// <see cref="DutRun"/> to <paramref name="ctx"/>. Returns null when no
    /// serial was given or no product is active — the run then records nothing.
    /// </summary>
    /// <summary>
    /// Resolves the DUT a program/deck run records into and wires it to
    /// <paramref name="ctx"/>. Precedence:
    /// <list type="number">
    ///   <item>an explicit up-front <paramref name="serial"/> — owns the run;</item>
    ///   <item>an open operator DUT session (<see cref="_interactiveDut"/>) —
    ///         <c>dut.*</c> steps append to it; the session keeps its own lifecycle
    ///         (this run does not close it), recorded as a <em>fallback</em>;</item>
    ///   <item>the sticky <see cref="ActiveSerial"/> — a fresh run on the last-set
    ///         unit, recorded as a <em>fallback</em>;</item>
    ///   <item>none — <c>ctx.Dut</c> stays null (steps skip, or a
    ///         <c>dut.promptSerial</c> step may set one mid-run).</item>
    /// </list>
    /// A fallback (2/3) is marked <see cref="WorkflowContext.DutIsFallback"/> so a
    /// <c>dut.promptSerial</c> step can still override it. Sets <see cref="_runDut"/>
    /// only for cases this run owns and must close (1 and 3). Returns the serial for
    /// logging, or null when none was resolved.
    /// </summary>
    private string? OpenRunDut(string? serial, string source, WorkflowContext ctx)
    {
        if (!string.IsNullOrWhiteSpace(serial))
        {
            _runDut = BeginDut(serial, source, ctx);
            return _runDut?.Record.Serial;
        }
        if (_interactiveDut != null)
        {
            ctx.Dut = _interactiveDut.Value.Run;
            ctx.DutSerial = _interactiveDut.Value.Record.Serial;
            ctx.DutIsFallback = true;
            return _interactiveDut.Value.Record.Serial;
        }
        _runDut = BeginDut(ActiveSerial, source, ctx);
        if (_runDut != null) ctx.DutIsFallback = true;
        return _runDut?.Record.Serial;
    }

    private (DutRecord Record, DutRun Run)? BeginDut(string? serial, string source, WorkflowContext ctx)
    {
        if (string.IsNullOrWhiteSpace(serial) || _dut == null)
            return null;

        var record = _dut.LoadOrCreate(serial.Trim());
        var run = new DutRun { StartedUtc = DateTime.UtcNow, Source = source };
        StampOperator(run);
        ctx.Dut = run;
        ctx.DutSerial = record.Serial;
        SetActiveSerial(record.Serial);
        return (record, run);
    }

    /// <summary>Copies the current operator + station onto a DUT run for traceability.</summary>
    private void StampOperator(DutRun run)
    {
        if (_operator == null) return;
        run.OperatorId = _operator.UserId;
        run.OperatorName = _operator.DisplayName;
        run.StationId = _operator.StationId;
    }

    /// <summary>Finalises a DUT run with its verdict and persists the record.</summary>
    private void EndDut((DutRecord Record, DutRun Run)? dut, DutVerdict verdict)
    {
        if (dut == null) return;

        var (record, run) = dut.Value;
        run.EndedUtc = DateTime.UtcNow;
        run.Verdict = verdict;
        record.Runs.Add(run);

        if (_dut == null)
        {
            Log(LogLevel.Warning, $"DUT record for {record.Serial} not saved — product unloaded mid-run.");
            return;
        }
        try
        {
            _dut.Save(record);
            Log(LogLevel.Info,
                $"DUT record saved: {record.Serial} — run {verdict}, {run.Entries.Count} entry(ies).");
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"DUT record save failed for {record.Serial}: {ex.Message}");
        }
    }

    /// <summary>Persists a DUT record, logging (not throwing) on failure. No-op if no product is loaded.</summary>
    private void TrySaveDut(DutRecord record, string note)
    {
        if (_dut == null) return;
        try
        {
            _dut.Save(record);
            Log(LogLevel.Debug, $"DUT record saved: {record.Serial} ({note}).");
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"DUT record save failed for {record.Serial}: {ex.Message}");
        }
    }

    // --- U correction ---

    public Task<Result<UCorrectionResult>> RunUCorrectionAsync(
        UCorrectionOptions options, CancellationToken ct = default)
        => RunUCorrectionCoreAsync(options, OperatorPrompt, ct);

    public Task<Result<UCorrectionResult>> RunUCorrectionAsync(
        UCorrectionOptions options, IOperatorPrompt prompt, CancellationToken ct = default)
        => RunUCorrectionCoreAsync(options, prompt, ct);

    private async Task<Result<UCorrectionResult>> RunUCorrectionCoreAsync(
        UCorrectionOptions options, IOperatorPrompt? prompt, CancellationToken ct)
    {
        if (_positions == null)
            return Result<UCorrectionResult>.Fail(ErrorCode.OperationFailed, "No active product — load one first.");

        // Mirror WorkflowRunner's wiring: the routine owns no stores — it gets the
        // recall-move + position read/save delegates from the service. The prompt is
        // injectable so a non-modal front-end (the Run page's inline Capture/Apply
        // panel) can drive the per-point refine without a blocking modal dialog.
        var routine = new UCorrectionRoutine(
            _engine,
            MoveToNamedPositionCoreAsync,
            (device, name) => _positions?.Get(device, name),
            SaveNamedPosition,
            prompt,
            Log);

        Log(LogLevel.Info, $"U correction started: gantry '{options.GantryDevice}', hexapod '{options.HexapodDevice}'.");
        return await routine.RunAsync(options, ct).ConfigureAwait(false);
    }

    // --- FAU pre-align ---

    public Task<Result<FauPreAlignResult>> RunFauPreAlignAsync(
        FauPreAlignOptions options, CancellationToken ct = default)
        => RunFauPreAlignCoreAsync(options, OperatorPrompt, ct);

    public Task<Result<FauPreAlignResult>> RunFauPreAlignAsync(
        FauPreAlignOptions options, IOperatorPrompt prompt, CancellationToken ct = default)
        => RunFauPreAlignCoreAsync(options, prompt, ct);

    private async Task<Result<FauPreAlignResult>> RunFauPreAlignCoreAsync(
        FauPreAlignOptions options, IOperatorPrompt? prompt, CancellationToken ct)
    {
        if (_positions == null)
            return Result<FauPreAlignResult>.Fail(ErrorCode.OperationFailed, "No active product — load one first.");

        var routine = new FauPreAlignRoutine(
            _engine,
            MoveToNamedPositionCoreAsync,
            (device, name) => _positions?.Get(device, name),
            SaveNamedPosition,
            prompt,
            Log);

        Log(LogLevel.Info, $"FAU pre-align started: gantry '{options.GantryDevice}', hexapod '{options.HexapodDevice}'.");
        return await routine.RunAsync(options, ct).ConfigureAwait(false);
    }

    // --- Calibration: camera <-> needle ---

    public CameraNeedleCalibrationData GetCameraNeedleCalibration() => _cameraNeedle.Current;

    public void SetCameraNeedleSettings(
        double vcamneedleX, double vcamneedleY, double approachMm,
        double touchZAdjustMm, int dwellMs, int outputDebounceMs)
    {
        var cfg = _cameraNeedle.Current;
        _cameraNeedle.Update(cfg with
        {
            VcamneedleX = vcamneedleX,
            VcamneedleY = vcamneedleY,
            ApproachMm = approachMm,
            TouchZAdjustMm = touchZAdjustMm,
            DwellMs = dwellMs,
            OutputDebounceMs = outputDebounceMs,
            SavedBy = _operator?.DisplayName,
            SavedAt = DateTimeOffset.Now.ToString("o"),
        });
        Log(LogLevel.Info,
            $"Camera-needle settings set: offset ({vcamneedleX:F4}, {vcamneedleY:F4}) mm, approach {approachMm:F2} mm, " +
            $"touch-Z adjust {touchZAdjustMm:F3} mm, dwell {dwellMs} ms, debounce {outputDebounceMs} ms.");
    }

    public Task<Result<CameraNeedleResult>> RunCameraNeedleCalibrationAsync(CancellationToken ct = default)
        => RunCameraNeedleCoreAsync(OperatorPrompt, ct);

    public Task<Result<CameraNeedleResult>> RunCameraNeedleCalibrationAsync(
        IOperatorPrompt prompt, CancellationToken ct = default)
        => RunCameraNeedleCoreAsync(prompt, ct);

    private async Task<Result<CameraNeedleResult>> RunCameraNeedleCoreAsync(
        IOperatorPrompt? prompt, CancellationToken ct)
    {
        var cfg = _cameraNeedle.Current;
        var options = new CameraNeedleOptions(
            cfg.GantryDevice, cfg.DispenserSlide, cfg.ShotIoDevice, cfg.ShotPin, cfg.OutputDebounceMs,
            cfg.DwellMs, cfg.LiftoffMm, cfg.ApproachMm, cfg.SlowZVelocity, cfg.TouchZAdjustMm,
            cfg.CalPosition, cfg.VcamneedleX, cfg.VcamneedleY, cfg.FocusToNeedleZmm);

        var routine = new CameraNeedleCalibrationRoutine(_engine, prompt, Log);
        Log(LogLevel.Info, $"Camera-needle calibration started: gantry '{cfg.GantryDevice}', dispenser '{cfg.DispenserSlide}'.");

        var result = await routine.RunAsync(options, ct).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            var r = result.Value;
            _cameraNeedle.Update(cfg with
            {
                CalPosition = r.CalPosition,
                VcamneedleX = r.VcamneedleX,
                VcamneedleY = r.VcamneedleY,
                FocusToNeedleZmm = r.FocusToNeedleZmm,
                SavedBy = _operator?.DisplayName,
                SavedAt = DateTimeOffset.Now.ToString("o"),
            });
            Log(LogLevel.Info, "Camera-needle calibration saved.");
        }
        return result;
    }

    // --- Dispensing routines (per-product) ---

    public IReadOnlyList<string> ListDispenseRoutines()
        => _dispense?.List() ?? Array.Empty<string>();

    public Result<DispenseRoutine> LoadDispenseRoutine(string name)
    {
        if (_dispense == null)
            return Result<DispenseRoutine>.Fail(ErrorCode.OperationFailed, "No active product — load one first.");
        var routine = _dispense.Load(name);
        return routine == null
            ? Result<DispenseRoutine>.Fail(ErrorCode.FileNotFound, $"Dispense routine '{name}' not found.")
            : Result<DispenseRoutine>.Ok(routine);
    }

    public Result SaveDispenseRoutine(DispenseRoutine routine)
    {
        if (_dispense == null)
            return Result.Fail(ErrorCode.OperationFailed, "No active product — load one first.");
        if (string.IsNullOrWhiteSpace(routine.Name))
            return Result.Fail(ErrorCode.InvalidParameter, "Dispense routine needs a name.");
        _dispense.Save(routine with { SavedBy = _operator?.DisplayName, SavedAt = DateTimeOffset.Now.ToString("o") });
        Log(LogLevel.Info, $"Dispense routine saved: {routine.Name}");
        return Result.Ok();
    }

    public Result DeleteDispenseRoutine(string name)
    {
        if (_dispense == null)
            return Result.Fail(ErrorCode.OperationFailed, "No active product — load one first.");
        if (!_dispense.Delete(name))
            return Result.Fail(ErrorCode.FileNotFound, $"Dispense routine '{name}' not found.");
        Log(LogLevel.Info, $"Dispense routine deleted: {name}");
        return Result.Ok();
    }

    public DispenseRoutine NewDispenseRoutine(string name)
    {
        // Seed the shot params from the machine camera→needle calibration so a fresh routine
        // dispenses with the already-tuned timing / motion.
        var cal = _cameraNeedle.Current;
        return new DispenseRoutine
        {
            Name = name,
            GantryDevice = cal.GantryDevice,
            DispenserSlide = cal.DispenserSlide,
            Shot = new DispenseShot
            {
                DwellMs = cal.DwellMs,
                OutputDebounceMs = cal.OutputDebounceMs,
                LiftoffMm = cal.LiftoffMm,
                ApproachMm = cal.ApproachMm,
                SlowZVelocity = cal.SlowZVelocity,
                TouchZAdjustMm = cal.TouchZAdjustMm,
                ShotIoDevice = cal.ShotIoDevice,
                ShotPin = cal.ShotPin,
            },
        };
    }

    public async Task<Result> RunDispenseRoutineAsync(string name, CancellationToken ct = default)
    {
        var load = LoadDispenseRoutine(name);
        if (!load.IsSuccess) return Result.Fail(load.Error, load.Message);

        var cal = _cameraNeedle.Current;
        var runner = new DispenseRoutineRunner(_engine, Log);
        Log(LogLevel.Info, $"Dispense routine started: {name}");
        return await runner
            .RunAsync(load.Value, cal.VcamneedleX, cal.VcamneedleY, cal.FocusToNeedleZmm, ct)
            .ConfigureAwait(false);
    }

    public async Task<Result> RunNeedleApproachAsync(double approachDistanceMm, double velocity, CancellationToken ct = default)
    {
        var cal = _cameraNeedle.Current;
        var g = cal.GantryDevice;
        var head = new DispenseHead(_engine, Log, "Needle approach");
        Log(LogLevel.Info,
            $"Needle approach: gantry '{g}' move needle to camera point + extend '{cal.DispenserSlide}', " +
            $"approach {approachDistanceMm:F2} mm @ {velocity:F2} mm/s.");
        try
        {
            // 1) Move the needle onto the camera-pointed location (gantry += vcamneedle).
            if (Math.Abs(cal.VcamneedleX) > 1e-6)
            {
                var rx = await _engine.Motion.MoveRelativeAsync(g, "X", cal.VcamneedleX, ct).ConfigureAwait(false);
                if (!rx.IsSuccess) return rx;
            }
            if (Math.Abs(cal.VcamneedleY) > 1e-6)
            {
                var ry = await _engine.Motion.MoveRelativeAsync(g, "Y", cal.VcamneedleY, ct).ConfigureAwait(false);
                if (!ry.IsSuccess) return ry;
            }

            // 2) Extend the dispenser head.
            var ext = await head.ExtendAsync(cal.DispenserSlide, ct).ConfigureAwait(false);
            if (!ext.IsSuccess) return ext;

            // 3) Approach the surface by approachDistance at velocity (toward-surface direction
            //    from the calibration; defaults to +Z when the touch relationship is unset).
            if (Math.Abs(approachDistanceMm) > 1e-6)
            {
                double downSign = Math.Sign(-cal.FocusToNeedleZmm);
                if (downSign == 0) downSign = 1;
                double dz = downSign * Math.Abs(approachDistanceMm);
                var z = velocity > 0
                    ? await head.SlowZMoveAsync(g, dz, velocity, ct).ConfigureAwait(false)
                    : await _engine.Motion.MoveRelativeAsync(g, "Z", dz, ct).ConfigureAwait(false);
                if (!z.IsSuccess) return z;
            }
            return Result.Ok();
        }
        catch (OperationCanceledException)
        {
            await head.RetractAsync(cal.DispenserSlide, CancellationToken.None).ConfigureAwait(false);
            return Result.Fail(ErrorCode.OperationFailed, "Needle approach cancelled.");
        }
    }

    public async Task<Result> JogCameraNeedleOffsetAsync(bool toNeedle, CancellationToken ct = default)
    {
        var cal = _cameraNeedle.Current;
        double sign = toNeedle ? 1.0 : -1.0;
        double dx = sign * cal.VcamneedleX, dy = sign * cal.VcamneedleY;
        Log(LogLevel.Info, $"Move to {(toNeedle ? "needle" : "camera")}: gantry '{cal.GantryDevice}' dX={dx:F3}, dY={dy:F3} mm.");
        if (Math.Abs(dx) > 1e-6)
        {
            var rx = await _engine.Motion.MoveRelativeAsync(cal.GantryDevice, "X", dx, ct).ConfigureAwait(false);
            if (!rx.IsSuccess) return rx;
        }
        if (Math.Abs(dy) > 1e-6)
        {
            var ry = await _engine.Motion.MoveRelativeAsync(cal.GantryDevice, "Y", dy, ct).ConfigureAwait(false);
            if (!ry.IsSuccess) return ry;
        }
        return Result.Ok();
    }

    // --- Recipes ---

    public IReadOnlyList<string> ListRecipes()
        => _recipes?.List() ?? Array.Empty<string>();

    public Result<RecipeProgram> LoadRecipe(string name)
    {
        if (_recipes == null)
            return Result<RecipeProgram>.Fail(ErrorCode.OperationFailed, "No active product — load one first.");

        var source = _recipes.Load(name);
        return source == null
            ? Result<RecipeProgram>.Fail(ErrorCode.FileNotFound, $"Recipe '{name}' not found.")
            : Result<RecipeProgram>.Ok(RecipeParser.Parse(name, source));
    }

    public Result SaveRecipe(string name, string source)
    {
        if (_recipes == null)
            return Result.Fail(ErrorCode.OperationFailed, "No active product — load one first.");
        if (string.IsNullOrWhiteSpace(name))
            return Result.Fail(ErrorCode.InvalidParameter, "Recipe name is required.");

        _recipes.Save(name, source);
        Log(LogLevel.Info, $"Recipe saved: {name}");
        return Result.Ok();
    }

    public async Task<Result> RunRecipeAsync(string name, CancellationToken ct = default)
    {
        if (_recipes == null)
            return Result.Fail(ErrorCode.OperationFailed, "No active product — load one first.");

        var source = _recipes.Load(name);
        if (source == null)
            return Result.Fail(ErrorCode.FileNotFound, $"Recipe '{name}' not found.");

        var program = RecipeParser.Parse(name, source);
        if (program.HasErrors)
        {
            foreach (var error in program.Errors)
                Log(LogLevel.Error, $"Recipe '{name}' parse error — {error}");
            return Result.Fail(ErrorCode.InvalidParameter,
                $"Recipe '{name}' has {program.Errors.Count} parse error(s).");
        }

        var pause = new PauseController();
        pause.PausedChanged += OnPausedChanged;
        _pause = pause;

        var registry = new RecipeMethodRegistry(_engine, MoveToNamedPositionCoreAsync, OperatorPrompt, Log);
        var interpreter = new RecipeInterpreter(registry, pause, Log);

        Log(LogLevel.Info, $"Recipe started: {name}");
        try
        {
            var result = await interpreter.RunAsync(program, ct).ConfigureAwait(false);
            var summary = $"Recipe {(result.IsSuccess ? "completed" : "failed")}: {name}";
            if (!string.IsNullOrEmpty(result.Message))
                summary += $" — {result.Message}";
            Log(result.IsSuccess ? LogLevel.Info : LogLevel.Error, summary);
            return result;
        }
        catch (OperationCanceledException)
        {
            Log(LogLevel.Warning, $"Recipe cancelled: {name}");
            return Result.Fail(ErrorCode.OperationFailed, "Recipe cancelled.");
        }
        finally
        {
            pause.PausedChanged -= OnPausedChanged;
            _pause = null;
        }
    }

    // --- Scripts (.moon Lua) ---

    public IReadOnlyList<string> ListScripts()
        => _scripts?.List() ?? Array.Empty<string>();

    public string? LoadScript(string name) => _scripts?.Load(name);

    public Result SaveScript(string name, string source)
    {
        if (_scripts == null)
            return Result.Fail(ErrorCode.OperationFailed, "No active product — load one first.");
        if (string.IsNullOrWhiteSpace(name))
            return Result.Fail(ErrorCode.InvalidParameter, "Script name is required.");

        _scripts.Save(name, source);
        Log(LogLevel.Info, $"Script saved: {name}");
        return Result.Ok();
    }

    /// <summary>
    /// Runs raw <c>.moon</c> source against the engine. The Lua <c>machine</c> API
    /// reaches every facet plus named-position <c>moveTo</c>, unified
    /// <c>signal.read</c>, and operator prompts (all wired to this active product).
    /// Each emitted line is logged and streamed to <paramref name="onOutput"/>.
    /// </summary>
    public async Task<ScriptRunResult> RunScriptAsync(
        string source, Action<string>? onOutput = null, CancellationToken ct = default)
    {
        var script = new MoonScriptEngine(
            _engine,
            namedPositionMover: (device, position, token) =>
                MoveToNamedPositionCoreAsync(device, position, new MoveOverrides(), token),
            signalReader: (key, token) => _signals.ReadAsync(key, token),
            visionRunner: RunVisionProfileByNameAsync,
            operatorPrompt: OperatorPrompt);

        void Sink(string line)
        {
            Log(LogLevel.Info, line);
            onOutput?.Invoke(line);
        }

        Log(LogLevel.Info, "Script started.");
        var result = await script.RunAsync(source, Sink, ct).ConfigureAwait(false);

        if (result.Success)
            Log(LogLevel.Info, "Script completed.");
        else if (result.Cancelled)
            Log(LogLevel.Warning, "Script cancelled.");
        else
            Log(LogLevel.Error, $"Script error: {result.Error}");

        return result;
    }

    // --- Decks ---

    public IReadOnlyList<string> ListDecks()
        => _decks?.List() ?? Array.Empty<string>();

    public string? ActiveDeck => _decks?.ActiveDeck;

    public Result<WorkflowDeck> LoadDeck(string name)
    {
        if (_decks == null)
            return Result<WorkflowDeck>.Fail(ErrorCode.OperationFailed, "No active product — load one first.");

        var deck = _decks.Load(name);
        if (deck == null)
            return Result<WorkflowDeck>.Fail(ErrorCode.FileNotFound, $"Deck '{name}' not found.");

        _decks.SetActive(name);   // loading a deck makes it the auto-loaded one
        return Result<WorkflowDeck>.Ok(deck);
    }

    /// <summary>Maximum number of decks a product may hold.</summary>
    public const int MaxDecks = 100;

    public Result SaveDeck(WorkflowDeck deck)
    {
        if (_decks == null)
            return Result.Fail(ErrorCode.OperationFailed, "No active product — load one first.");
        if (string.IsNullOrWhiteSpace(deck.Name))
            return Result.Fail(ErrorCode.InvalidParameter, "Deck name is required.");

        // Only count toward the limit when this is a new deck name.
        var existing = _decks.List();
        bool isNew = !existing.Contains(deck.Name, StringComparer.OrdinalIgnoreCase);
        if (isNew && existing.Count >= MaxDecks)
            return Result.Fail(ErrorCode.OperationFailed, $"Deck limit ({MaxDecks}) reached — delete one first.");

        // Save writes the working file (and activates the deck) only — it does NOT create
        // a version. A version is created explicitly via CommitDeck.
        _decks.Save(deck);
        _decks.SetActive(deck.Name);
        Log(LogLevel.Info, $"Deck saved (working copy): {deck.Name}");
        return Result.Ok();
    }

    public Result CommitDeck(WorkflowDeck deck, string? note)
    {
        // Commit = save the working deck, then record an operator-stamped, numbered
        // revision (UID-keyed, with the optional note) into the version repository.
        var saved = SaveDeck(deck);
        if (!saved.IsSuccess)
            return saved;

        try
        {
            var product = _products.ActiveProduct;
            if (product != null && !string.IsNullOrEmpty(deck.Uid))
            {
                var version = _history.AppendDeck(product, deck, _operator, NormalizeNote(note));
                Log(LogLevel.Info,
                    $"Committed deck: {deck.Name} v{version.Version}" +
                    (string.IsNullOrEmpty(version.OperatorName) ? "" : $" by {version.OperatorName}") +
                    (string.IsNullOrEmpty(version.Note) ? "" : $" — \"{version.Note}\""));
            }
        }
        catch (Exception ex)
        {
            Log(LogLevel.Warning, $"Deck commit not recorded for '{deck.Name}': {ex.Message}");
        }
        return Result.Ok();
    }

    /// <summary>
    /// Restores a past deck revision (by UID + version) into the working copy: writes the
    /// stored snapshot back as the current deck and re-activates it. It does NOT create a
    /// new version — the operator commits afterwards if they want to keep it.
    /// </summary>
    public Result RestoreDeckVersion(string deckUid, int version)
    {
        if (_decks == null)
            return Result.Fail(ErrorCode.OperationFailed, "No active product — load one first.");

        var product = _products.ActiveProduct;
        if (product == null)
            return Result.Fail(ErrorCode.OperationFailed, "No active product — load one first.");
        if (string.IsNullOrEmpty(deckUid))
            return Result.Fail(ErrorCode.InvalidParameter, "Deck has no UID — it has no version history to restore.");

        var snapshot = _history.LoadDeckVersion(product, deckUid, version);
        if (snapshot == null)
            return Result.Fail(ErrorCode.FileNotFound, $"Version v{version} of this deck not found in history.");

        var saved = SaveDeck(snapshot.Snapshot);
        if (saved.IsSuccess)
            Log(LogLevel.Info, $"Checked out v{version} of deck '{snapshot.Snapshot.Name}' into the working copy.");
        return saved;
    }

    /// <summary>
    /// The stable UID of a deck by name, read <b>without</b> activating it (unlike
    /// <see cref="LoadDeck"/>) — so the version repository can look up a deck's history
    /// while browsing without disturbing the active deck. Null if the deck / product is
    /// missing or the deck has no UID (legacy).
    /// </summary>
    public string? GetDeckUid(string name)
    {
        var uid = _decks?.Load(name)?.Uid;
        return string.IsNullOrEmpty(uid) ? null : uid;
    }

    /// <summary>Revision history of a deck by UID (operator + timestamp + snapshot per save), or null.</summary>
    public DeckHistory? GetDeckHistory(string deckUid)
    {
        var product = _products.ActiveProduct;
        return product == null ? null : _history.LoadDeckHistory(product, deckUid);
    }

    /// <summary>
    /// Cross-deck history query (machine-global) — filter saved deck entries by deck UID /
    /// entry UID / program / operator / product for traceability. Newest first.
    /// </summary>
    public IReadOnlyList<DeckHistoryRow> QueryDeckHistory(DeckHistoryQuery query) => _history.QueryDecks(query);

    public Result DeleteDeck(string name)
    {
        if (_decks == null)
            return Result.Fail(ErrorCode.OperationFailed, "No active product — load one first.");
        if (string.IsNullOrWhiteSpace(name))
            return Result.Fail(ErrorCode.InvalidParameter, "Deck name is required.");

        _decks.Delete(name);
        Log(LogLevel.Info, $"Deck deleted: {name}");
        return Result.Ok();
    }

    public IReadOnlyList<string> ListDecksOf(string product)
        => _products.Exists(product)
            ? new DeckStore(_products.ProductDir(product)).List()
            : Array.Empty<string>();

    public Result ImportDeck(string sourceProduct, string deckName, string newDeckName)
    {
        if (_decks == null || _programs == null)
            return Result.Fail(ErrorCode.OperationFailed, "No active product — load one first.");
        if (!_products.Exists(sourceProduct))
            return Result.Fail(ErrorCode.FileNotFound, $"Source product '{sourceProduct}' not found.");
        if (string.Equals(sourceProduct, _products.ActiveProduct, StringComparison.OrdinalIgnoreCase))
            return Result.Fail(ErrorCode.InvalidParameter, "Source is the active product — use Save As instead.");

        newDeckName = (newDeckName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(newDeckName))
            return Result.Fail(ErrorCode.InvalidParameter, "New deck name is required.");
        if (_decks.List().Contains(newDeckName, StringComparer.OrdinalIgnoreCase))
            return Result.Fail(ErrorCode.OperationFailed, $"Deck '{newDeckName}' already exists here — pick a new name.");

        var srcDir = _products.ProductDir(sourceProduct);
        var deck = new DeckStore(srcDir).Load(deckName);
        if (deck == null)
            return Result.Fail(ErrorCode.FileNotFound, $"Deck '{deckName}' not found in '{sourceProduct}'.");

        // Copy the deck's referenced programs into the active product (only those
        // missing here), so the imported deck is runnable.
        var srcPrograms = new ProgramStore(srcDir);
        var have = _programs.List();
        int copied = 0;
        foreach (var prog in deck.Entries.Select(e => e.Program)
                     .Where(p => !string.IsNullOrWhiteSpace(p))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (have.Contains(prog, StringComparer.OrdinalIgnoreCase)) continue;
            if (srcPrograms.Load(prog) is { } p) { _programs.Save(p); copied++; }
        }

        // Save under the new name with a fresh identity so its history doesn't fork the source's.
        var imported = deck with
        {
            Uid = "deck-" + Guid.NewGuid().ToString("N"),
            Name = newDeckName,
            Entries = deck.Entries.Select(e => e with { Uid = "dge-" + Guid.NewGuid().ToString("N") }).ToList(),
        };
        var save = SaveDeck(imported);   // working copy + sets active (untracked until first commit)
        if (!save.IsSuccess) return save;

        Log(LogLevel.Info,
            $"Imported deck '{deckName}' from '{sourceProduct}' as '{newDeckName}' ({copied} program(s) copied).");
        return Result.Ok();
    }

    /// <summary>
    /// Releases a deck that is paused on a manual (auto-off) transition, letting it
    /// advance into the next program. No-op when no deck is waiting.
    /// </summary>
    public void ActivateNextDeckProgram() => _deckGate?.TrySetResult(true);

    public async Task<Result> RunDeck(
        WorkflowDeck deck, string? serial = null, CancellationToken ct = default)
    {
        if (_programs == null)
            return Result.Fail(ErrorCode.OperationFailed, "No active product — load one first.");

        var pause = new PauseController();
        pause.PausedChanged += OnPausedChanged;
        _pause = pause;

        // One context for the whole deck — also carries the DUT run, so every
        // program in the deck records into the same per-unit run.
        var ctx = new WorkflowContext();

        // One runner for the whole deck — every program runs through it, so
        // pause / step works across the deck just as it does within a program.
        var runner = new WorkflowRunner(
            _engine,
            MoveToNamedPositionCoreAsync,
            name => _programs?.Load(name),
            (device, name) => _positions?.Get(device, name),
            SaveNamedPosition,
            RunVisionProfileByNameAsync,
            RunDispenseRoutineAsync,
            RunNeedleApproachAsync,
            () => _hillClimbFeedbackProvider?.Invoke(),
            () => _utilityAppHost,
            () => _siphogSheetSink,
            _signals,
            _deviceLinks,
            OperatorPrompt,
            (s, c) => SetRunDutSerial(s, deck.Name, c),
            CloseRunDut,   // dut.close finalises + persists the DUT mid-run
            pause,
            e => StepEvent?.Invoke(e),
            Log);

        var deckRunner = new DeckRunner(
            loadProgram: name => _programs.Load(name),
            runProgram: (program, context, token) => runner.RunAsync(program, context, token),
            onProgress: p => DeckStepEvent?.Invoke(p),
            awaitActivation: async (index, label, token) =>
            {
                // Block until the operator activates the next program (or the run is cancelled).
                var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _deckGate = gate;
                DeckAwaitingActivation?.Invoke(
                    new DeckProgress(index, deck.Entries.Count, label, false, false, "awaiting operator activation"));
                using (token.Register(() => gate.TrySetCanceled(token)))
                    await gate.Task.ConfigureAwait(false);
                _deckGate = null;
            },
            log: Log);

        var dutSerial = OpenRunDut(serial, deck.Name, ctx);

        Log(LogLevel.Info, $"Deck started: {deck.Name}" + (dutSerial != null ? $" [DUT {dutSerial}]" : ""));
        var verdict = DutVerdict.Fail;
        try
        {
            var result = await deckRunner.RunAsync(deck, ctx, ct).ConfigureAwait(false);
            verdict = result.IsSuccess ? DutVerdict.Pass : DutVerdict.Fail;
            var summary = $"Deck {(result.IsSuccess ? "completed" : "stopped")}: {deck.Name}";
            if (!string.IsNullOrEmpty(result.Message))
                summary += $" — {result.Message}";
            Log(result.IsSuccess ? LogLevel.Info : LogLevel.Error, summary);
            return result;
        }
        catch (OperationCanceledException)
        {
            verdict = DutVerdict.Aborted;
            Log(LogLevel.Warning, $"Deck cancelled: {deck.Name}");
            return Result.Fail(ErrorCode.OperationFailed, "Deck cancelled.");
        }
        finally
        {
            pause.PausedChanged -= OnPausedChanged;
            _pause = null;
            _deckGate = null;
            EndDut(_runDut, verdict);
            _runDut = null;
            PersistSessionIfUsed(ctx);
        }
    }

    private void Log(LogLevel level, string message)
        => LogEmitted?.Invoke(new LogEntry(level, DateTimeOffset.Now, "Workflow", message));
}
