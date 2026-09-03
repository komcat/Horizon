using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using FAAM.UI.Mvvm;
using FAAM.UI.ViewModels.Sections;
using Horizon.Engine;
using Horizon.Workflow.Registration;

namespace FAAM.UI.ViewModels;

/// <summary>
/// The <b>Gripped FAU</b> page — teaches the object held by a registered tool (the FAU in the
/// hex-right gripper) so it tracks on every camera view, purple, next to the gripper:
/// <list type="number">
/// <item>park the gripper and <b>capture its pose</b>;</item>
/// <item>set the camera <b>exposure</b> (lighting placeholder alongside);</item>
/// <item>for each of the <b>two reference points</b>: teach the camera position, then mark
///   the point by clicking the feed or by running a vision profile;</item>
/// <item>the average is the object <b>centre</b>;</item>
/// <item>pick / type the <b>outline</b> (same as the tool outline);</item>
/// <item><b>save</b> into the product; <b>clear</b> removes the object and its outline.</item>
/// </list>
/// The geometry mirrors the Registration section: every click / detection is converted to the
/// camera device's frame at once, and the object is anchored to the tool through the tool's
/// registration axis maps (see <c>docs/device-registration-design.md</c>).
/// </summary>
public sealed class GrippedObjectViewModel : ObservableObject, IDisposable
{
    private const double PoseDriftToleranceMm = 0.002;

    private readonly EngineHost _host;
    private string _name = "FAU";
    private string? _toolDevice;
    private string _cameraDevice = "gantry-main";
    private MotionPosition? _toolPose;
    private string _exposureUs = "10000";
    private string _gain = "0";
    private bool _autoExposure = true;
    private string _lightingNote = "";
    private MotionPosition? _camPose1, _camPose2;
    private CameraOffset? _camOff1, _camOff2;
    private GrippedPoint? _pt1, _pt2;
    private TaskCompletionSource<bool>? _manualMark;   // step 7 waits on this for a manual click
    private CancellationTokenSource? _procCts;
    private string _profile1 = "", _profile2 = "";
    private int _markArmed;   // 0 = none, 1 / 2 = the point the next feed click marks
    private bool _pickOutlineArmed;
    private readonly List<(double X, double Y)> _outlineCorners = new();
    private string _outlineWidth = "2", _outlineLength = "6", _outlineOffsetX = "0", _outlineOffsetY = "0";
    private string _settleMs = "100";
    private string _toCameraZOffset = "0";
    private bool _toCameraIncludeZ = true;
    private string _status = "—";
    private bool _statusWarn;
    private string _liveText = "—";
    private bool _busy;
    private bool _active;
    private bool _loading;
    private string? _leasedCamera;
    private bool _disposed;

    public GrippedObjectViewModel(EngineHost host)
    {
        _host = host;
        Camera = new CameraSectionViewModel(host);

        CaptureToolPoseCommand = new AsyncRelayCommand(CaptureToolPoseAsync, () => !_busy && _toolDevice != null);
        MoveToPoseCommand = new AsyncRelayCommand(MoveToPoseAsync, () => !_busy && _toolDevice != null && _toolPose != null);
        CameraToToolCommand = new AsyncRelayCommand(CameraToToolAsync, () => !_busy && _toolDevice != null);
        RunProcedureCommand = new AsyncRelayCommand(RunProcedureAsync, () => !_busy && _toolDevice != null && _camOff1 != null && _camOff2 != null);
        CancelProcedureCommand = new RelayCommand(CancelProcedure, () => _procCts != null);
        ObjectToCameraCommand = new AsyncRelayCommand(ObjectToCameraAsync, () => !_busy && IsSaved);
        ReadExposureCommand = new RelayCommand(ReadExposure, () => Camera.SelectedCamera != null);
        ApplyExposureCommand = new AsyncRelayCommand(ApplyExposureAsync, () => !_busy && Camera.SelectedCamera != null);
        TeachCamera1Command = new AsyncRelayCommand(() => TeachCameraAsync(1), () => !_busy && _toolPose != null);
        TeachCamera2Command = new AsyncRelayCommand(() => TeachCameraAsync(2), () => !_busy && _toolPose != null);
        MoveCamera1Command = new AsyncRelayCommand(() => MoveCameraAsync(1), () => !_busy && (_camOff1 != null || _camPose1 != null));
        MoveCamera2Command = new AsyncRelayCommand(() => MoveCameraAsync(2), () => !_busy && (_camOff2 != null || _camPose2 != null));
        RunVision1Command = new AsyncRelayCommand(() => RunVisionAsync(1), () => !_busy && _toolPose != null && _profile1.Length > 0);
        RunVision2Command = new AsyncRelayCommand(() => RunVisionAsync(2), () => !_busy && _toolPose != null && _profile2.Length > 0);
        ClearPointsCommand = new RelayCommand(ClearPoints, () => _pt1 != null || _pt2 != null);
        ClearOutlineCommand = new RelayCommand(ClearOutline, () => _outlineCorners.Count > 0);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !_busy && CanSave);
        ClearObjectCommand = new RelayCommand(ClearObject, () => IsSaved);
        ReloadCommand = new RelayCommand(LoadExisting);

        Camera.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CameraSectionViewModel.SelectedCamera)) { UpdateLease(); ReadExposureCommand.RaiseCanExecuteChanged(); }
        };
        _host.Process.GrippedObjectsChanged += OnObjectsChanged;
        _host.Process.ToolRegistrationsChanged += OnObjectsChanged;
        _host.Process.ProductChanged += _ => OnObjectsChanged();

        RefreshLists();
        _toolDevice = Tools.Count > 0 ? Tools[0] : null;
        LoadExisting();
    }

    public EngineHost Host => _host;

    /// <summary>The live feed (frame pipeline + camera picker).</summary>
    public CameraSectionViewModel Camera { get; }

    /// <summary>Registered tool devices (a gripped object needs one to ride on).</summary>
    public ObservableCollection<string> Tools { get; } = new();

    /// <summary>Saved gripped objects of the active product (pick to load).</summary>
    public ObservableCollection<string> ObjectNames { get; } = new();

    /// <summary>Vision profiles of the active product (for auto-marking a point).</summary>
    public ObservableCollection<string> VisionProfiles { get; } = new();

    public string Name
    {
        get => _name;
        set { if (SetField(ref _name, value)) { OnPropertyChanged(nameof(IsSaved)); OnPropertyChanged(nameof(CanSave)); RaiseCans(); } }
    }

    /// <summary>Selecting a saved object's name loads it.</summary>
    public string? SelectedObjectName
    {
        get => null;
        set { if (!string.IsNullOrEmpty(value)) { Name = value; LoadExisting(); } OnPropertyChanged(); }
    }

    public string? ToolDevice
    {
        get => _toolDevice;
        set { if (SetField(ref _toolDevice, value)) { CameraDeviceFromRegistration(); if (!_loading) LoadExisting(); } }
    }

    public string CameraDevice { get => _cameraDevice; private set => SetField(ref _cameraDevice, value); }

    public MotionPosition? ToolPose
    {
        get => _toolPose;
        private set { if (SetField(ref _toolPose, value)) { OnPropertyChanged(nameof(ToolPoseText)); OnPropertyChanged(nameof(CanSave)); RaiseCans(); } }
    }

    public string ToolPoseText => _toolPose == null
        ? "not captured"
        : $"X {_toolPose.X:0.0000}  Y {_toolPose.Y:0.0000}  Z {_toolPose.Z:0.0000}  U {_toolPose.U:0.0000}  V {_toolPose.V:0.0000}  W {_toolPose.W:0.0000}";

    public string ExposureUs { get => _exposureUs; set => SetField(ref _exposureUs, value); }
    public string Gain { get => _gain; set => SetField(ref _gain, value); }
    public bool AutoExposure { get => _autoExposure; set => SetField(ref _autoExposure, value); }
    public string LightingNote { get => _lightingNote; set => SetField(ref _lightingNote, value); }

    public string Profile1 { get => _profile1; set { if (SetField(ref _profile1, value ?? "")) RaiseCans(); } }
    public string Profile2 { get => _profile2; set { if (SetField(ref _profile2, value ?? "")) RaiseCans(); } }

    public string CamPose1Text => PoseText(_camPose1, _camOff1);
    public string CamPose2Text => PoseText(_camPose2, _camOff2);

    /// <summary>True while the step-7 procedure is running.</summary>
    public bool ProcedureRunning => _procCts != null;

    /// <summary>Settle wait after each move in the procedure (ms). Saved with the object.</summary>
    public string SettleMs { get => _settleMs; set => SetField(ref _settleMs, value); }

    /// <summary>Step 8: Z offset (mm, camera frame) for object → camera. Negative = higher (hex-right X+), positive = lower (hex-right X−).</summary>
    public string ToCameraZOffset { get => _toCameraZOffset; set => SetField(ref _toCameraZOffset, value); }

    /// <summary>Step 8: whether object → camera also drives Z.</summary>
    public bool ToCameraIncludeZ { get => _toCameraIncludeZ; set => SetField(ref _toCameraIncludeZ, value); }
    public string Point1Text => PointText(_pt1);
    public string Point2Text => PointText(_pt2);

    /// <summary>Which point the next feed click marks (0 = none). Bound to the two Mark toggles.</summary>
    public bool Mark1Armed { get => _markArmed == 1; set { _markArmed = value ? 1 : (_markArmed == 1 ? 0 : _markArmed); OnPropertyChanged(); OnPropertyChanged(nameof(Mark2Armed)); } }
    public bool Mark2Armed { get => _markArmed == 2; set { _markArmed = value ? 2 : (_markArmed == 2 ? 0 : _markArmed); OnPropertyChanged(); OnPropertyChanged(nameof(Mark1Armed)); } }
    public int MarkArmed => _markArmed;

    public IReadOnlyList<GrippedPoint> Points
        => new[] { _pt1, _pt2 }.Where(p => p != null).Select(p => p!).ToList();

    /// <summary>Mean of the two points (camera device frame, mm), or null until both exist.</summary>
    public (double X, double Y)? Centre
        => _pt1 != null && _pt2 != null ? ((_pt1.CameraFrameX + _pt2.CameraFrameX) / 2, (_pt1.CameraFrameY + _pt2.CameraFrameY) / 2) : null;

    public string CentreText => Centre is { } c
        ? $"({c.X:0.0000}, {c.Y:0.0000}) mm in {_cameraDevice} frame" + (FocusZ is { } z ? $"   focus Z {z:0.0000}" : "")
        : "— (needs both points)";

    /// <summary>Camera Z of the point poses (mean), or null.</summary>
    public double? FocusZ => _pt1 != null && _pt2 != null ? (_pt1.CameraPose.Z + _pt2.CameraPose.Z) / 2 : null;

    public bool PickOutlineArmed { get => _pickOutlineArmed; set => SetField(ref _pickOutlineArmed, value); }
    public IReadOnlyList<(double X, double Y)> OutlineCorners => _outlineCorners;
    public string OutlineSummary => _outlineCorners.Count switch
    {
        0 => "no corners picked",
        1 => "1 / 2 corners — click the opposite corner",
        _ => "2 / 2 corners",
    };
    public string OutlineWidth { get => _outlineWidth; set { if (SetField(ref _outlineWidth, value)) OnPropertyChanged(nameof(CanSave)); } }
    public string OutlineLength { get => _outlineLength; set { if (SetField(ref _outlineLength, value)) OnPropertyChanged(nameof(CanSave)); } }
    public string OutlineOffsetX { get => _outlineOffsetX; set => SetField(ref _outlineOffsetX, value); }
    public string OutlineOffsetY { get => _outlineOffsetY; set => SetField(ref _outlineOffsetY, value); }

    public (double OffX, double OffY, double W, double L)? OutlineBox
        => TryNum(_outlineWidth, out var w) && w > 0 && TryNum(_outlineLength, out var l) && l > 0
           && TryNum(_outlineOffsetX, out var ox) && TryNum(_outlineOffsetY, out var oy)
            ? (ox, oy, w, l) : null;

    public string Status { get => _status; private set => SetField(ref _status, value); }
    public bool StatusWarn { get => _statusWarn; private set => SetField(ref _statusWarn, value); }
    public string LiveText { get => _liveText; private set => SetField(ref _liveText, value); }

    public bool IsSaved => _host.Process.GetGrippedObject(_name) != null;

    public bool CanSave =>
        _toolDevice != null && _toolPose != null && _pt1 != null && _pt2 != null && !string.IsNullOrWhiteSpace(_name)
        && TryNum(_outlineWidth, out var w) && w > 0 && TryNum(_outlineLength, out var l) && l > 0;

    public AsyncRelayCommand CaptureToolPoseCommand { get; }
    public AsyncRelayCommand MoveToPoseCommand { get; }
    public AsyncRelayCommand CameraToToolCommand { get; }
    public AsyncRelayCommand RunProcedureCommand { get; }
    public RelayCommand CancelProcedureCommand { get; }
    public AsyncRelayCommand ObjectToCameraCommand { get; }
    public RelayCommand ReadExposureCommand { get; }
    public AsyncRelayCommand ApplyExposureCommand { get; }
    public AsyncRelayCommand TeachCamera1Command { get; }
    public AsyncRelayCommand TeachCamera2Command { get; }
    public AsyncRelayCommand MoveCamera1Command { get; }
    public AsyncRelayCommand MoveCamera2Command { get; }
    public AsyncRelayCommand RunVision1Command { get; }
    public AsyncRelayCommand RunVision2Command { get; }
    public RelayCommand ClearPointsCommand { get; }
    public RelayCommand ClearOutlineCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public RelayCommand ClearObjectCommand { get; }
    public RelayCommand ReloadCommand { get; }

    /// <summary>Raised on each refresh tick so the view redraws its markers.</summary>
    public event Action? Ticked;

    // --- lifecycle ---

    public void SetActive(bool active)
    {
        _active = active;
        Camera.SetActive(active);
        if (!active) { _markArmed = 0; OnPropertyChanged(nameof(Mark1Armed)); OnPropertyChanged(nameof(Mark2Armed)); PickOutlineArmed = false; }
        else RefreshLists();
        UpdateLease();
    }

    private void UpdateLease()
    {
        var want = _active ? Camera.SelectedCamera : null;
        if (string.Equals(want, _leasedCamera, StringComparison.OrdinalIgnoreCase)) return;
        if (_leasedCamera != null) _ = _host.Engine.Camera.StopGrabbingAsync(_leasedCamera);
        _leasedCamera = want;
        if (want != null) _ = _host.Engine.Camera.StartGrabbingAsync(want);
    }

    /// <summary>Refresh tick (from the view's timer): live read-out + marker redraw.</summary>
    public void Refresh()
    {
        if (_host.Engine.State == EngineState.ShuttingDown) return;   // facets gone — nothing to read
        Camera.Refresh();
        if (IsSaved)
        {
            var loc = _host.Process.LocateGrippedObject(_name);
            LiveText = loc.IsSuccess
                ? $"{_name} at ({loc.Value.ToolX:0.0000}, {loc.Value.ToolY:0.0000})  camera at ({loc.Value.CameraX:0.0000}, {loc.Value.CameraY:0.0000})  " +
                  $"Δ ({loc.Value.DxMm:+0.000;-0.000}, {loc.Value.DyMm:+0.000;-0.000})  d {loc.Value.DistanceMm:0.000} mm" +
                  (loc.Value.HasZ ? (loc.Value.DzMm < 0 ? $"   {_name} higher {loc.Value.DzMm:0.00}" : $"   {_name} lower {loc.Value.DzMm:+0.00}") : "")
                : loc.Message;
        }
        else LiveText = "not saved";
        Ticked?.Invoke();
    }

    // --- step 1: tool pose ---

    private async Task CaptureToolPoseAsync()
    {
        if (_toolDevice == null) return;
        var r = await _host.Engine.Motion.GetPositionAsync(_toolDevice).ConfigureAwait(true);
        if (!r.IsSuccess) { SetStatus($"Read {_toolDevice} failed: {r.Message}", true); return; }
        ToolPose = r.Value;
        if (_pt1 != null || _pt2 != null)
        {
            _pt1 = _pt2 = null;
            PointsChanged();
            SetStatus("Tool pose captured — the points were cleared (they belong to the old pose). Teach and mark them again.");
        }
        else SetStatus("Tool pose captured. Set the exposure, then teach the camera position for point 1.");
    }

    private async Task MoveToPoseAsync()
    {
        if (_toolDevice == null || _toolPose == null) return;
        await RunBusyAsync("Tool → pose", async () =>
        {
            var p = _toolPose;
            _host.Log.Add(new LogEntry(LogLevel.Info, DateTimeOffset.Now, "Gripped",
                $"{_toolDevice} → object pose ({p.X:F4}, {p.Y:F4}, {p.Z:F4}, {p.U:F4}, {p.V:F4}, {p.W:F4})."));
            var r = await _host.Engine.Motion.MoveToPositionAsync(_toolDevice, p).ConfigureAwait(true);
            SetStatus(r.IsSuccess ? "Tool is at the object pose." : r.Message, !r.IsSuccess);
        });
    }

    // --- step 2: exposure ---

    private void ReadExposure()
    {
        var cam = Camera.SelectedCamera;
        if (cam == null) return;
        var e = _host.Engine.Camera.GetState(cam).Exposure;
        ExposureUs = e.ExposureUs.ToString("0", CultureInfo.InvariantCulture);
        Gain = e.Gain.ToString("0.##", CultureInfo.InvariantCulture);
        AutoExposure = e.AutoExposure;
        SetStatus("Exposure read from the camera.");
    }

    private async Task ApplyExposureAsync()
    {
        var cam = Camera.SelectedCamera;
        if (cam == null) return;
        if (!TryNum(_exposureUs, out var us) || !TryNum(_gain, out var gain)) { SetStatus("Exposure / gain must be numbers.", true); return; }
        var cur = _host.Engine.Camera.GetState(cam).Exposure;
        var r = await _host.Engine.Camera.ApplyExposureAsync(cam, cur with { ExposureUs = us, Gain = gain, AutoExposure = _autoExposure }).ConfigureAwait(true);
        SetStatus(r.IsSuccess ? "Exposure applied." : r.Message, !r.IsSuccess);
    }

    // --- step 3: two points ---

    private async Task TeachCameraAsync(int i)
    {
        var r = await _host.Engine.Motion.GetPositionAsync(_cameraDevice).ConfigureAwait(true);
        if (!r.IsSuccess) { SetStatus($"Read {_cameraDevice} failed: {r.Message}", true); return; }
        SetCameraPose(i, r.Value);
        RaiseCans();
        SetStatus((i == 1 ? _camOff1 : _camOff2) == null
            ? $"Camera position for point {i} taught (absolute only — the tool is not registered / located, so no offset from the tool centre)."
            : $"Camera position for point {i} taught as an offset from the tool centre. Now mark point {i}: arm Mark {i} and click it, or run its vision profile.");
    }

    /// <summary>Stores a point's camera pose both absolute and relative to the live tool centre.</summary>
    private void SetCameraPose(int i, MotionPosition pose)
    {
        var off = OffsetFromTool(pose);
        if (i == 1) { _camPose1 = pose; _camOff1 = off; } else { _camPose2 = pose; _camOff2 = off; }
        OnPropertyChanged(i == 1 ? nameof(CamPose1Text) : nameof(CamPose2Text));
    }

    /// <summary>Camera pose − tool location (from the tool registration, cached states); null if the tool cannot be located.</summary>
    private CameraOffset? OffsetFromTool(MotionPosition camPose)
    {
        if (_toolDevice == null) return null;
        var loc = _host.Process.LocateTool(_toolDevice);
        if (!loc.IsSuccess) return null;
        return new CameraOffset(camPose.X - loc.Value.ToolX, camPose.Y - loc.Value.ToolY,
            loc.Value.ToolZ is { } tz ? camPose.Z - tz : null);
    }

    /// <summary>The absolute camera target for a point right now: live tool centre + stored offset (falls back to the absolute pose).</summary>
    private MotionPosition? CameraTargetFor(int i, MotionPosition current)
    {
        var off = i == 1 ? _camOff1 : _camOff2;
        var abs = i == 1 ? _camPose1 : _camPose2;
        if (off == null || _toolDevice == null) return abs;
        var loc = _host.Process.LocateTool(_toolDevice);
        if (!loc.IsSuccess) return abs;
        var t = current with { X = loc.Value.ToolX + off.X, Y = loc.Value.ToolY + off.Y };
        if (off.Z is { } oz && loc.Value.ToolZ is { } tz) t = t with { Z = tz + oz };
        return t;
    }

    /// <summary>Camera → tool centre (the registered gripper anchor), with Z when the registration knows it.</summary>
    private async Task CameraToToolAsync()
    {
        if (_toolDevice == null) return;
        await RunBusyAsync("Camera → tool", async () =>
        {
            var reg = _host.Process.GetToolRegistration(_toolDevice);
            var r = await _host.Process.MoveCameraToToolAsync(_toolDevice, includeZ: reg?.HasZ == true).ConfigureAwait(true);
            SetStatus(r.IsSuccess ? "Camera is on the tool centre." : r.Message, !r.IsSuccess);
        });
    }

    private async Task MoveCameraAsync(int i)
    {
        await RunBusyAsync($"Camera → point {i}", async () =>
        {
            var r = await MoveCameraToPointAsync(i).ConfigureAwait(true);
            SetStatus(r.IsSuccess ? $"Camera at point {i} position (relative to the tool; exposure applied)." : r.Message, !r.IsSuccess);
        });
    }

    /// <summary>Moves the camera device to point i's position relative to the live tool centre, then applies the exposure.</summary>
    private async Task<Horizon.Common.Result> MoveCameraToPointAsync(int i)
    {
        var cur = await _host.Engine.Motion.GetPositionAsync(_cameraDevice).ConfigureAwait(true);
        if (!cur.IsSuccess) return Horizon.Common.Result.Fail(Horizon.Common.ErrorCode.OperationFailed, $"Read {_cameraDevice} failed: {cur.Message}");
        var target = CameraTargetFor(i, cur.Value);
        if (target == null) return Horizon.Common.Result.Fail(Horizon.Common.ErrorCode.InvalidParameter, $"No camera position taught for point {i}.");
        var r = await _host.Engine.Motion.MoveToPositionAsync(_cameraDevice, target).ConfigureAwait(true);
        if (r.IsSuccess) await ApplyExposureAsync().ConfigureAwait(true);
        return r;
    }

    /// <summary>Manual mark: a feed click at source px while <see cref="MarkArmed"/> is 1 or 2.</summary>
    public async Task MarkPointAsync(double srcPxX, double srcPxY, int srcW, int srcH)
    {
        int i = _markArmed;
        if (i == 0) return;
        if (_toolPose == null) { SetStatus("Capture the tool pose first (step 1).", true); return; }
        if (!await ToolIsAtPoseAsync($"marking point {i}").ConfigureAwait(true)) return;

        var pose = await _host.Engine.Motion.GetPositionAsync(_cameraDevice).ConfigureAwait(true);
        if (!pose.IsSuccess) { SetStatus($"Read {_cameraDevice} failed: {pose.Message}", true); return; }
        SetPoint(i, pose.Value, srcPxX, srcPxY, srcW, srcH, "manual");
        _markArmed = 0;
        OnPropertyChanged(nameof(Mark1Armed)); OnPropertyChanged(nameof(Mark2Armed));
        SetStatus($"Point {i} marked manually." + (Centre != null ? " Both points set — pick the outline, then Save." : ""));
        _manualMark?.TrySetResult(true);   // step 7 waiting on this click
    }

    // --- step 7: run the detect procedure from wherever the tool is ---

    /// <summary>
    /// Re-finds the object with the tool wherever it stands now: capture the tool pose, camera →
    /// tool centre, then for each point move the camera by that point's stored offset and detect
    /// it (vision profile if set, otherwise wait for a Mark click), then save — the outline
    /// (width / length / offset from the centre) is kept, so it is redrawn at the new place.
    /// </summary>
    private async Task RunProcedureAsync()
    {
        if (_toolDevice == null || _camOff1 == null || _camOff2 == null) return;
        var cam = Camera.SelectedCamera;
        if (cam == null) { SetStatus("No camera selected.", true); return; }

        _procCts = new CancellationTokenSource();
        OnPropertyChanged(nameof(ProcedureRunning));
        CancelProcedureCommand.RaiseCanExecuteChanged();
        var ct = _procCts.Token;
        await RunBusyAsync("Object detect procedure", async () =>
        {
            try
            {
                // 1. The object rides with the tool: its pose NOW is the new teach pose.
                var tp = await _host.Engine.Motion.GetPositionAsync(_toolDevice).ConfigureAwait(true);
                if (!tp.IsSuccess) { SetStatus($"Read {_toolDevice} failed: {tp.Message}", true); return; }
                ToolPose = tp.Value;
                _pt1 = _pt2 = null;
                PointsChanged();

                // 2. Camera → tool centre (bounded move; Z when the registration knows it).
                var reg = _host.Process.GetToolRegistration(_toolDevice);
                SetStatus("Procedure: camera → tool centre…");
                var c2t = await _host.Process.MoveCameraToToolAsync(_toolDevice, includeZ: reg?.HasZ == true, ct: ct).ConfigureAwait(true);
                if (!c2t.IsSuccess) { SetStatus($"Procedure: camera → tool: {c2t.Message}", true); return; }
                await SettleAsync(ct).ConfigureAwait(true);

                // 3. Each point: relative move, then detect (auto) or wait for the operator's click (manual).
                for (int i = 1; i <= 2; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    SetStatus($"Procedure: camera → point {i} (offset from the tool centre)…");
                    var mv = await MoveCameraToPointAsync(i).ConfigureAwait(true);
                    if (!mv.IsSuccess) { SetStatus($"Procedure: camera → point {i}: {mv.Message}", true); return; }
                    await SettleAsync(ct).ConfigureAwait(true);

                    var profile = i == 1 ? _profile1 : _profile2;
                    if (profile.Length > 0)
                    {
                        if (!await DetectPointAsync(i, profile, cam).ConfigureAwait(true)) return;
                    }
                    else
                    {
                        var wait = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        _manualMark = wait;
                        _markArmed = i;
                        OnPropertyChanged(nameof(Mark1Armed)); OnPropertyChanged(nameof(Mark2Armed));
                        SetStatus($"Procedure: click point {i} on the feed (Mark {i} is armed)…");
                        using (ct.Register(() => wait.TrySetResult(false)))
                        {
                            if (!await wait.Task.ConfigureAwait(true)) return;
                        }
                        _manualMark = null;
                    }
                }

                // 4. Save: centre from the new points; the outline box keeps its size + offset from the centre.
                if (!CanSave) { SetStatus("Procedure: points set but the object cannot be saved (check the outline fields).", true); return; }
                await SaveAsync().ConfigureAwait(true);
                SetStatus($"Procedure done — '{_name}' re-taught at the tool's current pose and saved; outline redrawn.");
            }
            catch (OperationCanceledException)
            {
                SetStatus("Procedure cancelled.", true);
            }
            finally
            {
                _manualMark = null;
                _markArmed = 0;
                OnPropertyChanged(nameof(Mark1Armed)); OnPropertyChanged(nameof(Mark2Armed));
                _procCts?.Dispose();
                _procCts = null;
                OnPropertyChanged(nameof(ProcedureRunning));
                CancelProcedureCommand.RaiseCanExecuteChanged();
            }
        });
    }

    /// <summary>Motion settling wait after a procedure move (the page's Settle field, ≥ 0 ms).</summary>
    private Task SettleAsync(CancellationToken ct)
    {
        int ms = TryNum(_settleMs, out var v) && v > 0 ? (int)Math.Min(v, 60_000) : 0;
        return ms > 0 ? Task.Delay(ms, ct) : Task.CompletedTask;
    }

    // --- step 8: gripped object → camera centre ---

    private async Task ObjectToCameraAsync()
    {
        if (!IsSaved) return;
        await RunBusyAsync($"{_name} → camera", async () =>
        {
            if (!TryNum(_toCameraZOffset, out var zOff)) { SetStatus("Z offset must be a number.", true); return; }
            var r = await _host.Process.MoveGrippedObjectToCameraAsync(_name, includeZ: _toCameraIncludeZ, zOffsetMm: zOff).ConfigureAwait(true);
            SetStatus(r.IsSuccess
                ? $"{_name} is under the camera centre" + (_toCameraIncludeZ ? $" (Z offset {zOff:+0.000;-0.000} mm)." : " (XY only).")
                : r.Message, !r.IsSuccess);
        });
    }

    /// <summary>Cancels a running step-7 procedure (also called when the page is left).</summary>
    public void CancelProcedure()
    {
        try { _procCts?.Cancel(); } catch (ObjectDisposedException) { /* already finished */ }
        _manualMark?.TrySetResult(false);
    }

    /// <summary>Auto mark: runs the point's vision profile on the live camera and takes its x/y.</summary>
    private async Task RunVisionAsync(int i)
    {
        var profile = i == 1 ? _profile1 : _profile2;
        var cam = Camera.SelectedCamera;
        if (profile.Length == 0 || cam == null) return;
        if (_toolPose == null) { SetStatus("Capture the tool pose first (step 1).", true); return; }
        if (!await ToolIsAtPoseAsync($"detecting point {i}").ConfigureAwait(true)) return;

        await RunBusyAsync($"Vision point {i}", async () =>
        {
            // Run = go to the taught camera position (exposure re-applied there), then detect.
            // Without a taught position the profile runs wherever the camera stands.
            if ((i == 1 ? _camOff1 : _camOff2) != null || (i == 1 ? _camPose1 : _camPose2) != null)
            {
                SetStatus($"Moving camera to point {i} position…");
                var mv = await MoveCameraToPointAsync(i).ConfigureAwait(true);
                if (!mv.IsSuccess) { SetStatus($"Camera → point {i}: {mv.Message}", true); return; }
            }

            await DetectPointAsync(i, profile, cam).ConfigureAwait(true);
        });
    }

    /// <summary>Runs the profile where the camera stands and stores the detected point (anchor preferred).</summary>
    private async Task<bool> DetectPointAsync(int i, string profile, string cam)
    {
        {
            var det = await _host.Process.RunVisionProfileByNameAsync(profile).ConfigureAwait(true);
            if (!det.IsSuccess) { SetStatus($"Profile '{profile}': {det.Message}", true); return false; }
            if (!det.Value.Found) { SetStatus($"Profile '{profile}' found nothing — adjust and retry, or mark manually.", true); return false; }
            // NCC profiles report the taught ANCHOR (anchorX/Y) as well as the match centre (x/y);
            // the anchor is the point the operator meant. Circle / corner profiles only have x/y.
            bool anchored = true;
            double? x = Value(det.Value, "anchorX"), y = Value(det.Value, "anchorY");
            if (x is null || y is null) { anchored = false; x = Value(det.Value, "x"); y = Value(det.Value, "y"); }
            if (x is null || y is null) { SetStatus($"Profile '{profile}' reports no x/y point (a line/edge profile?) — use a point-type profile or mark manually.", true); return false; }

            var state = _host.Engine.Camera.GetState(cam);
            if (state.Width <= 0 || state.Height <= 0) { SetStatus("Camera frame size unknown — is it grabbing?", true); return false; }
            var pose = await _host.Engine.Motion.GetPositionAsync(_cameraDevice).ConfigureAwait(true);
            if (!pose.IsSuccess) { SetStatus($"Read {_cameraDevice} failed: {pose.Message}", true); return false; }
            SetPoint(i, pose.Value, x.Value, y.Value, state.Width, state.Height, "vision:" + profile);
            SetStatus($"Point {i} detected by '{profile}' at px ({x:0}, {y:0}) [{(anchored ? "anchor" : "centre")}]." + (Centre != null ? " Both points set — pick the outline, then Save." : ""));
            return true;
        }

        static double? Value(VisionDetectResult r, string key)
        {
            foreach (var v in r.Values)
                if (string.Equals(v.Key, key, StringComparison.OrdinalIgnoreCase)) return v.Value;
            return null;
        }
    }

    private void SetPoint(int i, MotionPosition camPose, double px, double py, int srcW, int srcH, string source)
    {
        var (dxMm, dyMm) = ToolOverlayMath.SourcePxToMm(px - srcW / 2.0, py - srcH / 2.0, _host.Settings.Current);
        var pt = new GrippedPoint(i, camPose, px, py, camPose.X + dxMm, camPose.Y + dyMm, source);
        if (i == 1) _pt1 = pt; else _pt2 = pt;
        SetCameraPose(i, camPose);
        PointsChanged();
    }

    private void PointsChanged()
    {
        OnPropertyChanged(nameof(Point1Text)); OnPropertyChanged(nameof(Point2Text));
        OnPropertyChanged(nameof(Points)); OnPropertyChanged(nameof(CentreText)); OnPropertyChanged(nameof(CanSave));
        RecomputeOutlineFromCorners();
        RaiseCans();
    }

    private void ClearPoints()
    {
        _pt1 = _pt2 = null;
        PointsChanged();
        SetStatus("Points cleared.");
    }

    private async Task<bool> ToolIsAtPoseAsync(string what)
    {
        if (_toolDevice == null || _toolPose == null) return false;
        var now = await _host.Engine.Motion.GetPositionAsync(_toolDevice).ConfigureAwait(true);
        if (!now.IsSuccess) { SetStatus($"Read {_toolDevice} failed: {now.Message}", true); return false; }
        double drift = Math.Max(Math.Abs(now.Value.X - _toolPose.X),
                       Math.Max(Math.Abs(now.Value.Y - _toolPose.Y), Math.Abs(now.Value.Z - _toolPose.Z)));
        if (drift <= PoseDriftToleranceMm) return true;
        SetStatus($"{_toolDevice} is {drift:0.0000} mm from the object pose — press Move to pose (or recapture) before {what}.", true);
        return false;
    }

    // --- step 5: outline ---

    public async Task AddOutlineCornerAsync(double srcPxX, double srcPxY, int srcW, int srcH)
    {
        if (_outlineCorners.Count >= 2) { SetStatus("Already have 2 corners — Clear outline to re-pick.", true); return; }
        if (_toolPose == null) { SetStatus("Capture the tool pose first (step 1).", true); return; }
        if (!await ToolIsAtPoseAsync("picking outline corners").ConfigureAwait(true)) return;

        var pose = await _host.Engine.Motion.GetPositionAsync(_cameraDevice).ConfigureAwait(true);
        if (!pose.IsSuccess) { SetStatus($"Read {_cameraDevice} failed: {pose.Message}", true); return; }
        var (dxMm, dyMm) = ToolOverlayMath.SourcePxToMm(srcPxX - srcW / 2.0, srcPxY - srcH / 2.0, _host.Settings.Current);
        _outlineCorners.Add((pose.Value.X + dxMm, pose.Value.Y + dyMm));
        OnPropertyChanged(nameof(OutlineSummary));
        ClearOutlineCommand.RaiseCanExecuteChanged();

        if (_outlineCorners.Count == 2)
        {
            PickOutlineArmed = false;
            RecomputeOutlineFromCorners();
            SetStatus(Centre == null
                ? "Outline picked — width/length set; the offset fills in once both points exist."
                : "Outline picked — width, length and offset from the centre set. Adjust if needed, then Save.");
        }
        else SetStatus("Corner 1 of 2 picked — click the opposite corner.");
    }

    private void RecomputeOutlineFromCorners()
    {
        if (_outlineCorners.Count != 2) return;
        var (a, b) = (_outlineCorners[0], _outlineCorners[1]);
        double w = Math.Abs(b.X - a.X), l = Math.Abs(b.Y - a.Y);
        if (w > 0) OutlineWidth = w.ToString("0.####", CultureInfo.InvariantCulture);
        if (l > 0) OutlineLength = l.ToString("0.####", CultureInfo.InvariantCulture);
        if (Centre is { } c)
        {
            OutlineOffsetX = ((a.X + b.X) / 2 - c.X).ToString("0.####", CultureInfo.InvariantCulture);
            OutlineOffsetY = ((a.Y + b.Y) / 2 - c.Y).ToString("0.####", CultureInfo.InvariantCulture);
        }
    }

    private void ClearOutline()
    {
        _outlineCorners.Clear();
        OnPropertyChanged(nameof(OutlineSummary));
        ClearOutlineCommand.RaiseCanExecuteChanged();
        SetStatus("Outline corners cleared (fields kept — edit or re-pick).");
    }

    // --- step 6: save / clear ---

    private async Task SaveAsync()
    {
        if (_toolDevice == null || _toolPose == null || _pt1 == null || _pt2 == null || Centre is not { } c) return;
        if (!TryNum(_outlineWidth, out var w) || !TryNum(_outlineLength, out var l)
            || !TryNum(_outlineOffsetX, out var ox) || !TryNum(_outlineOffsetY, out var oy)
            || !TryNum(_exposureUs, out var us) || !TryNum(_gain, out var gain))
        {
            SetStatus("Outline / offset / exposure fields must be numbers.", true);
            return;
        }
        var obj = new GrippedObject
        {
            Name = _name.Trim(),
            ToolDevice = _toolDevice,
            CameraDevice = _cameraDevice,
            CameraId = Camera.SelectedCamera ?? "main_camera",
            ToolPose = _toolPose,
            AutoExposure = _autoExposure,
            ExposureUs = us,
            Gain = gain,
            LightingNote = _lightingNote,
            Point1VisionProfile = _profile1,
            Point2VisionProfile = _profile2,
            Point1 = _pt1,
            Point2 = _pt2,
            Point1CameraOffset = _camOff1,
            Point2CameraOffset = _camOff2,
            CentreX = c.X,
            CentreY = c.Y,
            FocusZ = FocusZ,
            OutlineWidthMm = w,
            OutlineLengthMm = l,
            OutlineOffsetXmm = ox,
            OutlineOffsetYmm = oy,
            SettleMs = TryNum(_settleMs, out var settle) && settle >= 0 ? (int)settle : 100,
            ToCameraZOffsetMm = TryNum(_toCameraZOffset, out var tz) ? tz : 0,
        };
        var r = _host.Process.SaveGrippedObject(obj);
        SetStatus(r.IsSuccess ? $"'{obj.Name}' saved into product {_host.Process.ActiveProduct}. It now tracks on the camera views." : r.Message, !r.IsSuccess);
        await Task.CompletedTask;
        RefreshLists();
        OnPropertyChanged(nameof(IsSaved));
        RaiseCans();
    }

    /// <summary>Clear = remove the saved object (points + outline) and reset the page for a fresh teach.</summary>
    private void ClearObject()
    {
        if (MessageBox.Show($"Clear the gripped object '{_name}' — its points and outline?", "Gripped object",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var r = _host.Process.RemoveGrippedObject(_name);
        SetStatus(r.IsSuccess ? $"'{_name}' cleared." : r.Message, !r.IsSuccess);
        RefreshLists();
        ResetFields();
        OnPropertyChanged(nameof(IsSaved));
        RaiseCans();
    }

    // --- load / lists ---

    private void RefreshLists()
    {
        var tools = _host.Process.GetToolRegistrations().Select(r => r.ToolDevice).ToList();
        Sync(Tools, tools);
        Sync(ObjectNames, _host.Process.GetGrippedObjects().Select(o => o.Name).ToList());
        Sync(VisionProfiles, _host.Process.ListVisionProfiles().ToList());
        if (_toolDevice != null && !tools.Contains(_toolDevice, StringComparer.OrdinalIgnoreCase))
            ToolDevice = tools.Count > 0 ? tools[0] : null;

        static void Sync(ObservableCollection<string> target, List<string> items)
        {
            if (target.SequenceEqual(items, StringComparer.OrdinalIgnoreCase)) return;
            target.Clear();
            foreach (var i in items) target.Add(i);
        }
    }

    private void CameraDeviceFromRegistration()
    {
        var reg = _toolDevice == null ? null : _host.Process.GetToolRegistration(_toolDevice);
        if (reg != null)
        {
            CameraDevice = reg.CameraDevice;
            if (Camera.Cameras.Contains(reg.CameraId, StringComparer.OrdinalIgnoreCase))
                Camera.SelectedCamera = reg.CameraId;
        }
    }

    private void LoadExisting()
    {
        _loading = true;
        try
        {
            var obj = _host.Process.GetGrippedObject(_name);
            if (obj != null)
            {
                if (Tools.Contains(obj.ToolDevice, StringComparer.OrdinalIgnoreCase)) _toolDevice = obj.ToolDevice;
                OnPropertyChanged(nameof(ToolDevice));
                CameraDeviceFromRegistration();
                if (Camera.Cameras.Contains(obj.CameraId, StringComparer.OrdinalIgnoreCase)) Camera.SelectedCamera = obj.CameraId;
                ToolPose = obj.ToolPose;
                AutoExposure = obj.AutoExposure;
                ExposureUs = obj.ExposureUs.ToString("0", CultureInfo.InvariantCulture);
                Gain = obj.Gain.ToString("0.##", CultureInfo.InvariantCulture);
                LightingNote = obj.LightingNote;
                Profile1 = obj.Point1VisionProfile; Profile2 = obj.Point2VisionProfile;
                _pt1 = obj.Point1; _pt2 = obj.Point2;
                _camPose1 = obj.Point1?.CameraPose; _camPose2 = obj.Point2?.CameraPose;
                _camOff1 = obj.Point1CameraOffset; _camOff2 = obj.Point2CameraOffset;
                _outlineCorners.Clear();
                OutlineWidth = obj.OutlineWidthMm.ToString("0.###", CultureInfo.InvariantCulture);
                OutlineLength = obj.OutlineLengthMm.ToString("0.###", CultureInfo.InvariantCulture);
                OutlineOffsetX = obj.OutlineOffsetXmm.ToString("0.####", CultureInfo.InvariantCulture);
                OutlineOffsetY = obj.OutlineOffsetYmm.ToString("0.####", CultureInfo.InvariantCulture);
                SettleMs = obj.SettleMs.ToString(CultureInfo.InvariantCulture);
                ToCameraZOffset = obj.ToCameraZOffsetMm.ToString("0.###", CultureInfo.InvariantCulture);
                OnPropertyChanged(nameof(CamPose1Text)); OnPropertyChanged(nameof(CamPose2Text));
                OnPropertyChanged(nameof(OutlineSummary));
                PointsChanged();
                SetStatus($"Loaded '{obj.Name}' (by {obj.SavedBy ?? "?"} at {obj.SavedAt ?? "?"}).");
            }
            else
            {
                ResetFields();
                CameraDeviceFromRegistration();
                SetStatus(_toolDevice == null
                    ? "No registered tool — register the gripper on the Machine page first."
                    : $"'{_name}' is not taught yet in product {_host.Process.ActiveProduct} — start with step 1.");
            }
        }
        finally { _loading = false; }
        OnPropertyChanged(nameof(IsSaved));
        RaiseCans();
    }

    private void ResetFields()
    {
        ToolPose = null;
        _pt1 = _pt2 = null;
        _camPose1 = _camPose2 = null;
        _camOff1 = _camOff2 = null;
        _outlineCorners.Clear();
        OutlineOffsetX = "0"; OutlineOffsetY = "0";
        OnPropertyChanged(nameof(CamPose1Text)); OnPropertyChanged(nameof(CamPose2Text));
        OnPropertyChanged(nameof(OutlineSummary));
        PointsChanged();
    }

    private void OnObjectsChanged()
    {
        if (!Application.Current.Dispatcher.CheckAccess()) { Application.Current.Dispatcher.BeginInvoke(OnObjectsChanged); return; }
        RefreshLists();
        OnPropertyChanged(nameof(IsSaved));
        RaiseCans();
    }

    // --- helpers ---

    private async Task RunBusyAsync(string activityName, Func<Task> body)
    {
        _busy = true;
        RaiseCans();
        using var activity = _host.Activity.Begin(ActivityKind.Alignment, activityName);
        try { await body().ConfigureAwait(true); }
        catch (Exception ex) { SetStatus(ex.Message, true); }
        finally { _busy = false; RaiseCans(); }
    }

    private void RaiseCans()
    {
        CaptureToolPoseCommand.RaiseCanExecuteChanged();
        MoveToPoseCommand.RaiseCanExecuteChanged();
        CameraToToolCommand.RaiseCanExecuteChanged();
        RunProcedureCommand.RaiseCanExecuteChanged();
        CancelProcedureCommand.RaiseCanExecuteChanged();
        ObjectToCameraCommand.RaiseCanExecuteChanged();
        ReadExposureCommand.RaiseCanExecuteChanged();
        ApplyExposureCommand.RaiseCanExecuteChanged();
        TeachCamera1Command.RaiseCanExecuteChanged();
        TeachCamera2Command.RaiseCanExecuteChanged();
        MoveCamera1Command.RaiseCanExecuteChanged();
        MoveCamera2Command.RaiseCanExecuteChanged();
        RunVision1Command.RaiseCanExecuteChanged();
        RunVision2Command.RaiseCanExecuteChanged();
        ClearPointsCommand.RaiseCanExecuteChanged();
        ClearOutlineCommand.RaiseCanExecuteChanged();
        SaveCommand.RaiseCanExecuteChanged();
        ClearObjectCommand.RaiseCanExecuteChanged();
    }

    private void SetStatus(string text, bool warn = false)
    {
        Status = text;
        StatusWarn = warn;
    }

    private static string PoseText(MotionPosition? p, CameraOffset? off)
        => p == null ? "not taught"
         : $"X {p.X:0.0000}  Y {p.Y:0.0000}  Z {p.Z:0.0000}" +
           (off == null ? "  (absolute only)" : $"   Δtool ({off.X:+0.000;-0.000}, {off.Y:+0.000;-0.000}{(off.Z is { } z ? $", {z:+0.000;-0.000}" : "")})");

    private static string PointText(GrippedPoint? p)
        => p == null ? "not marked" : $"px ({p.PixelX:0}, {p.PixelY:0}) → ({p.CameraFrameX:0.0000}, {p.CameraFrameY:0.0000}) mm  [{p.Source}]";

    private static bool TryNum(string s, out double v)
        => double.TryParse(s?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelProcedure();
        _host.Process.GrippedObjectsChanged -= OnObjectsChanged;
        _host.Process.ToolRegistrationsChanged -= OnObjectsChanged;
        _active = false;
        UpdateLease();
        Camera.Dispose();
    }
}
