using Horizon.Common;
using Horizon.Engine;
using Horizon.Workflow.FauPreAlign;
using Horizon.Workflow.HillClimb;
using Horizon.Workflow.Recipe;
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
    private DeckStore? _decks;           // null when no product is active
    private VisionProfileStore? _visionProfiles;   // null when no product is active
    private TaskCompletionSource<bool>? _deckGate;   // set while a deck waits for operator activation
    private DutStore? _dut;              // null when no product is active
    private readonly ProgramHistoryDb _history = new();   // machine-global audit store (history.db)
    private readonly DeviceLinkStore _deviceLinks = new();   // machine-global, not per-product
    private PauseController? _pause;     // non-null only while a program runs
    private OperatorContext? _operator;  // current operator, stamped onto DUT runs
    private (DutRecord Record, DutRun Run)? _interactiveDut;   // open operator-driven DUT run
    private (DutRecord Record, DutRun Run)? _runDut;   // the running program/deck's DUT (up-front serial, or set mid-run by dut.promptSerial)
    private Func<IHillClimbFeedback?>? _hillClimbFeedbackProvider;   // Layer 3 (SiPhOG) wires the hill-climb signal source

    public ProcessService(IHorizonEngine engine)
    {
        _engine = engine;

        // Load stores for a product that was already active from a prior run.
        var dir = _products.ActiveProductDir;
        if (dir != null)
        {
            _positions = new PositionStore(dir);
            _programs = new ProgramStore(dir);
            _recipes = new RecipeStore(dir);
            _decks = new DeckStore(dir);
            _visionProfiles = new VisionProfileStore(dir);
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
            _decks = null;
            _dut = null;
            SetActiveSerial(null);
            ProductChanged?.Invoke(null);
        }

        Log(LogLevel.Info, $"Product deleted: {name}");
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
        _decks = new DeckStore(dir);
        _visionProfiles = new VisionProfileStore(dir);
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
        _decks = null;
        _visionProfiles = null;
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

    public Task<Result<VisionDetectResult>> RunVisionProfileByNameAsync(string profileName)
    {
        if (_visionProfiles == null)
            return Task.FromResult(Result<VisionDetectResult>.Fail(ErrorCode.OperationFailed, "No active product — load one first."));
        var profile = _visionProfiles.Load(profileName);
        if (profile == null)
            return Task.FromResult(Result<VisionDetectResult>.Fail(ErrorCode.FileNotFound, $"Vision profile '{profileName}' not found."));
        return RunDetectionAsync(profile, frame: null, cameraId: profile.CameraId);
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

        _programs.Save(program);
        Log(LogLevel.Info, $"Program saved: {program.Name}");
        WarnMissingPositions(program);

        // Record an operator-stamped revision for history / traceability.
        try
        {
            var product = _products.ActiveProduct;
            if (product != null)
            {
                var version = _history.Append(product, program, _operator);
                Log(LogLevel.Info,
                    $"History: {program.Name} v{version.Version}" +
                    (string.IsNullOrEmpty(version.OperatorName) ? "" : $" by {version.OperatorName}"));
            }
        }
        catch (Exception ex)
        {
            Log(LogLevel.Warning, $"Program history not recorded for '{program.Name}': {ex.Message}");
        }
        return Result.Ok();
    }

    public ProgramHistory? GetProgramHistory(string name)
    {
        var product = _products.ActiveProduct;
        return product == null ? null : _history.Load(product, name);
    }

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
            () => _hillClimbFeedbackProvider?.Invoke(),
            _deviceLinks,
            OperatorPrompt,
            // dut.promptSerial opens the DUT record mid-run into _runDut, which the
            // finally block persists with the run's verdict.
            (s, c) => SetRunDutSerial(s, program.Name, c),
            pause,
            e => StepEvent?.Invoke(e),
            Log);

        _runDut = BeginDut(serial, program.Name, ctx);

        Log(LogLevel.Info, $"Program started: {program.Name}" + (_runDut != null ? $" [DUT {_runDut.Value.Record.Serial}]" : ""));
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
        }
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
        if (ctx.Dut != null)
            return Result.Fail(ErrorCode.OperationFailed, "A DUT serial is already set for this run.");

        var begun = BeginDut(serial, source, ctx);   // creates the run, sets ctx.Dut
        if (begun == null)
            return Result.Fail(ErrorCode.OperationFailed, "Could not open the DUT record.");
        _runDut = begun;
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
        var run = new DutRun { StartedUtc = DateTime.UtcNow, Source = source };
        StampOperator(run);
        _interactiveDut = (record, run);
        SetActiveSerial(record.Serial);
        Log(LogLevel.Info, $"DUT run started: {record.Serial} [{source}]" +
            (_operator != null ? $" by {_operator.DisplayName}" : ""));
        return Result.Ok();
    }

    public void RecordDutValue(string key, string value, DutValueType type = DutValueType.String)
    {
        if (_interactiveDut == null || string.IsNullOrWhiteSpace(key)) return;
        _interactiveDut.Value.Run.Entries.Add(new DutEntry(DateTime.UtcNow, key, value, type));
    }

    public Result EndDutRun(DutVerdict verdict)
    {
        if (_interactiveDut == null)
            return Result.Fail(ErrorCode.OperationFailed, "No DUT run is open.");

        var (record, run) = _interactiveDut.Value;
        run.EndedUtc = DateTime.UtcNow;
        run.Verdict = verdict;
        record.Runs.Add(run);
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

    /// <summary>
    /// Opens (or creates) the DUT record for a run and attaches a fresh
    /// <see cref="DutRun"/> to <paramref name="ctx"/>. Returns null when no
    /// serial was given or no product is active — the run then records nothing.
    /// </summary>
    private (DutRecord Record, DutRun Run)? BeginDut(string? serial, string source, WorkflowContext ctx)
    {
        if (string.IsNullOrWhiteSpace(serial) || _dut == null)
            return null;

        var record = _dut.LoadOrCreate(serial.Trim());
        var run = new DutRun { StartedUtc = DateTime.UtcNow, Source = source };
        StampOperator(run);
        ctx.Dut = run;
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

        _decks.Save(deck);
        _decks.SetActive(deck.Name);
        Log(LogLevel.Info, $"Deck saved: {deck.Name}");

        // Record an operator-stamped revision for history / traceability (UID-keyed).
        try
        {
            var product = _products.ActiveProduct;
            if (product != null && !string.IsNullOrEmpty(deck.Uid))
            {
                var version = _history.AppendDeck(product, deck, _operator);
                Log(LogLevel.Info,
                    $"Deck history: {deck.Name} v{version.Version}" +
                    (string.IsNullOrEmpty(version.OperatorName) ? "" : $" by {version.OperatorName}"));
            }
        }
        catch (Exception ex)
        {
            Log(LogLevel.Warning, $"Deck history not recorded for '{deck.Name}': {ex.Message}");
        }
        return Result.Ok();
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
            () => _hillClimbFeedbackProvider?.Invoke(),
            _deviceLinks,
            OperatorPrompt,
            (s, c) => SetRunDutSerial(s, deck.Name, c),
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

        _runDut = BeginDut(serial, deck.Name, ctx);

        Log(LogLevel.Info, $"Deck started: {deck.Name}" + (_runDut != null ? $" [DUT {_runDut.Value.Record.Serial}]" : ""));
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
        }
    }

    private void Log(LogLevel level, string message)
        => LogEmitted?.Invoke(new LogEntry(level, DateTimeOffset.Now, "Workflow", message));
}
