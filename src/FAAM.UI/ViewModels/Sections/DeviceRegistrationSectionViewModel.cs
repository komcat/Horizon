using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using Horizon.Engine;
using Horizon.Workflow.Registration;

namespace FAAM.UI.ViewModels.Sections;

/// <summary>
/// One operator-clicked sample of the tool centre, shown in the registration point list.
/// </summary>
public sealed record RegistrationPointRow(int Index, RegisteredPoint Point)
{
    public string Text => $"{Index}: px ({Point.PixelX:0}, {Point.PixelY:0})  →  ({Point.CameraFrameX:0.0000}, {Point.CameraFrameY:0.0000}) mm";
}

/// <summary>
/// The Machine page's <b>Device Registration</b> section — registers a tool carried by one
/// motion device (the hex-right gripper) to the camera carried by another (gantry-main), so
/// the process can send the camera to the tool and the tool to the camera. The procedure:
/// <list type="number">
/// <item>park the gripper anywhere and <b>capture its pose</b>;</item>
/// <item>jog the gantry (rail / MoveClick) until the camera sees the gripper;</item>
/// <item><b>pick 2 or 4 points</b> on the gripper in the live feed — their average is the tool centre;</item>
/// <item>type the gripper <b>outline</b> (width × length, mm);</item>
/// <item><b>save</b> — persisted machine-global until the hardware is changed.</item>
/// </list>
/// Each click is converted to the camera device's frame immediately (camera pose at the click
/// + image offset through mm/pixel and the MoveClick signs), so the gantry may move between
/// clicks. The view owns the pixel geometry; this model owns the state, the conversions and
/// the Layer 2 calls.
/// </summary>
public sealed class DeviceRegistrationSectionViewModel : ObservableObject, IDisposable
{
    private const double PoseDriftToleranceMm = 0.002;

    private readonly EngineHost _host;
    private readonly JogTransformStore _jogTransforms = new();
    private string? _toolDevice;
    private string? _cameraDevice;
    private string _label = "Gripper";
    private string _mapXAxis = "Z";
    private string _mapXSign = "-1";
    private string _mapYAxis = "Y";
    private string _mapYSign = "-1";
    private string _mapZAxis = "X";
    private string _mapZSign = "-1";
    private MotionPosition? _toolPose;
    private double? _cameraZ;
    private bool _testIncludeZ = true;   // Z is part of the coupling by default
    private string _zOffset = "0";
    private bool _tracking;
    private bool _trackBusy;
    private MotionPosition? _trackSnapTool;
    private MotionPosition? _trackSnapCam;

    /// <summary>Below this XYZ displacement (mm) a device is considered not to have been jogged.</summary>
    private const double TrackDeadbandMm = 0.0005;
    /// <summary>Settle wait after a follow move before re-arming, so the polled state catches up (ms).</summary>
    private const int TrackSettleMs = 500;
    private int _pointMode = 2;
    private bool _pickArmed;
    private string _outlineWidth = "2";
    private string _outlineLength = "6";
    private string _outlineOffsetX = "0";
    private string _outlineOffsetY = "0";
    private bool _pickOutlineArmed;
    private readonly List<(double X, double Y)> _outlineCorners = new();
    private string _maxMove = "10";
    private string _status = "—";
    private bool _statusWarn;
    private string _liveText = "—";
    private bool _busy;
    private bool _disposed;
    private bool _loading;
    private bool _active;
    private string? _leasedCamera;

    public DeviceRegistrationSectionViewModel(EngineHost host)
    {
        _host = host;
        Camera = new CameraSectionViewModel(host);
        MotionDevices = host.Engine.Motion.DeviceNames;

        var s = host.Settings.Current;
        _cameraDevice = Pick(MotionDevices, s.MoveClickDevice, "gantry-main");
        _toolDevice = Pick(MotionDevices, "hex-right", null);

        CaptureToolPoseCommand = new AsyncRelayCommand(CaptureToolPoseAsync, () => !_busy && _toolDevice != null);
        MoveToPoseCommand = new AsyncRelayCommand(MoveToPoseAsync, () => !_busy && _toolDevice != null && _toolPose != null);
        CaptureFocusZCommand = new AsyncRelayCommand(CaptureFocusZAsync, () => !_busy && _cameraDevice != null && _toolPose != null);
        ClearPointsCommand = new RelayCommand(ClearPoints, () => Points.Count > 0);
        ClearOutlineCommand = new RelayCommand(ClearOutline, () => _outlineCorners.Count > 0);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !_busy && CanSave);
        DeleteCommand = new RelayCommand(Delete, () => _toolDevice != null && _host.Process.GetToolRegistration(_toolDevice) != null);
        CameraToToolCommand = new AsyncRelayCommand(() => MoveAsync(true), () => !_busy && IsRegistered);
        ToolToCameraCommand = new AsyncRelayCommand(() => MoveAsync(false), () => !_busy && IsRegistered);
        ReloadCommand = new RelayCommand(LoadExisting);

        Points.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(AverageText));
            OnPropertyChanged(nameof(PointsSummary));
            OnPropertyChanged(nameof(CanSave));
            RecomputeOutlineFromCorners();   // the box offset is relative to the (new) anchor
            RaiseCans();
        };

        // Follow the camera picker: the lease (while active) and the overlay filter track it.
        Camera.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CameraSectionViewModel.SelectedCamera)) { UpdateLease(); Points.Clear(); }
        };

        _host.Process.ToolRegistrationsChanged += OnRegistrationsChanged;
        LoadExisting();
    }

    /// <summary>Label shown in the Machine-page section rail.</summary>
    public string Title => "Registration";

    public EngineHost Host => _host;

    /// <summary>The live feed (frame pipeline + camera picker) this section draws over.</summary>
    public CameraSectionViewModel Camera { get; }

    public IReadOnlyList<string> MotionDevices { get; }
    public IReadOnlyList<string> Axes { get; } = new[] { "X", "Y", "Z", "U", "V", "W" };
    public IReadOnlyList<string> Signs { get; } = new[] { "+1", "-1" };
    public IReadOnlyList<int> PointModes { get; } = new[] { 2, 4 };

    /// <summary>The device carrying the tool (the gripper). Changing it reloads its saved registration.</summary>
    public string? ToolDevice
    {
        get => _toolDevice;
        set { if (SetField(ref _toolDevice, value)) LoadExisting(); }
    }

    /// <summary>The device carrying the camera.</summary>
    public string? CameraDevice
    {
        get => _cameraDevice;
        set { if (SetField(ref _cameraDevice, value) && !_loading) SeedAxisMap(); }
    }

    public string Label { get => _label; set => SetField(ref _label, value); }
    public string MapXAxis { get => _mapXAxis; set => SetField(ref _mapXAxis, value); }
    public string MapXSign { get => _mapXSign; set => SetField(ref _mapXSign, value); }
    public string MapYAxis { get => _mapYAxis; set => SetField(ref _mapYAxis, value); }
    public string MapYSign { get => _mapYSign; set => SetField(ref _mapYSign, value); }
    public string MapZAxis { get => _mapZAxis; set => SetField(ref _mapZAxis, value); }
    public string MapZSign { get => _mapZSign; set => SetField(ref _mapZSign, value); }

    /// <summary>Camera-device Z at which the gripper is in focus (step 2), or null until captured.</summary>
    public double? CameraZ
    {
        get => _cameraZ;
        private set { if (SetField(ref _cameraZ, value)) OnPropertyChanged(nameof(CameraZText)); }
    }

    public string CameraZText => _cameraZ is { } z ? $"focus Z {z:0.0000} mm ({_cameraDevice})" : "not captured - XY-only moves";

    /// <summary>Whether the two test moves (and tracking) also drive Z (needs a captured focus Z).</summary>
    public bool TestIncludeZ { get => _testIncludeZ; set => SetField(ref _testIncludeZ, value); }

    /// <summary>
    /// Z offset (mm, camera-device frame) for tool → camera: the gripper parks at the focus plane
    /// plus this. Negative = higher (gantry Z− is up). Saved on the registration; camera → tool
    /// ignores it and always goes to the taught focus height.
    /// </summary>
    public string ZOffset { get => _zOffset; set => SetField(ref _zOffset, value); }

    /// <summary>
    /// Test-only <b>tracking arm</b>: while on, jogging either device makes the other follow so
    /// the coupling holds — jog the gantry and the gripper comes under the camera (tool → camera,
    /// with the Z offset when Z is included); jog the hexapod and the camera goes to the gripper
    /// (camera → tool). Switched off when the section is left.
    /// </summary>
    public bool Tracking
    {
        get => _tracking;
        set
        {
            if (!SetField(ref _tracking, value)) return;
            _trackSnapTool = null;
            _trackSnapCam = null;
            if (value && !IsRegistered) { _tracking = false; OnPropertyChanged(); SetStatus("Save a registration before tracking.", true); return; }
            SetStatus(value ? "Tracking on — jog either device; the other follows (bounded moves)." : "Tracking off.");
        }
    }

    /// <summary>The captured tool pose (step 1), or null.</summary>
    public MotionPosition? ToolPose
    {
        get => _toolPose;
        private set
        {
            if (SetField(ref _toolPose, value))
            {
                OnPropertyChanged(nameof(ToolPoseText));
                OnPropertyChanged(nameof(CanSave));
                RaiseCans();
            }
        }
    }

    public string ToolPoseText => _toolPose == null
        ? "not captured"
        : $"X {_toolPose.X:0.0000}  Y {_toolPose.Y:0.0000}  Z {_toolPose.Z:0.0000}  U {_toolPose.U:0.0000}  V {_toolPose.V:0.0000}  W {_toolPose.W:0.0000}";

    /// <summary>How many points the operator will click (2 or 4).</summary>
    public int PointMode
    {
        get => _pointMode;
        set { if (SetField(ref _pointMode, value)) { OnPropertyChanged(nameof(PointsSummary)); OnPropertyChanged(nameof(CanSave)); RaiseCans(); } }
    }

    /// <summary>True while the feed click adds registration points (instead of MoveClick).</summary>
    public bool PickArmed { get => _pickArmed; set => SetField(ref _pickArmed, value); }

    public ObservableCollection<RegistrationPointRow> Points { get; } = new();

    public string PointsSummary => $"{Points.Count} / {_pointMode} points";

    /// <summary>Mean of the clicked points in the camera device's frame, or null.</summary>
    public (double X, double Y)? Average
    {
        get
        {
            if (Points.Count == 0) return null;
            double sx = 0, sy = 0;
            foreach (var p in Points) { sx += p.Point.CameraFrameX; sy += p.Point.CameraFrameY; }
            return (sx / Points.Count, sy / Points.Count);
        }
    }

    public string AverageText => Average is { } a ? $"({a.X:0.0000}, {a.Y:0.0000}) mm in {_cameraDevice} frame" : "—";

    public string OutlineWidth { get => _outlineWidth; set { if (SetField(ref _outlineWidth, value)) OnPropertyChanged(nameof(CanSave)); } }
    public string OutlineLength { get => _outlineLength; set { if (SetField(ref _outlineLength, value)) OnPropertyChanged(nameof(CanSave)); } }

    /// <summary>Outline box centre − tool anchor, camera-device frame (mm). Zero = anchor at the box centre.</summary>
    public string OutlineOffsetX { get => _outlineOffsetX; set => SetField(ref _outlineOffsetX, value); }
    public string OutlineOffsetY { get => _outlineOffsetY; set => SetField(ref _outlineOffsetY, value); }

    /// <summary>True while a feed click adds an outline corner (two opposite corners define the box).</summary>
    public bool PickOutlineArmed { get => _pickOutlineArmed; set => SetField(ref _pickOutlineArmed, value); }

    /// <summary>The picked outline corners (camera-device frame mm), for the view to draw. 0..2 entries.</summary>
    public IReadOnlyList<(double X, double Y)> OutlineCorners => _outlineCorners;

    public string OutlineSummary => _outlineCorners.Count switch
    {
        0 => "no corners picked",
        1 => "1 / 2 corners — click the opposite corner",
        _ => "2 / 2 corners",
    };

    /// <summary>The outline box as (centre, size) relative to the tool anchor, parsed from the fields; null if unusable.</summary>
    public (double OffX, double OffY, double W, double L)? OutlineBox
        => TryNum(_outlineWidth, out var w) && w > 0 && TryNum(_outlineLength, out var l) && l > 0
           && TryNum(_outlineOffsetX, out var ox) && TryNum(_outlineOffsetY, out var oy)
            ? (ox, oy, w, l) : null;
    public string MaxMove { get => _maxMove; set => SetField(ref _maxMove, value); }

    public string Status { get => _status; private set => SetField(ref _status, value); }
    public bool StatusWarn { get => _statusWarn; private set => SetField(ref _statusWarn, value); }

    /// <summary>Live "where is the tool" read-out for the selected tool (from the saved registration).</summary>
    public string LiveText { get => _liveText; private set => SetField(ref _liveText, value); }

    public bool IsRegistered => _toolDevice != null && _host.Process.GetToolRegistration(_toolDevice) != null;

    public bool CanSave =>
        _toolDevice != null && _cameraDevice != null && _toolPose != null
        && Points.Count == _pointMode
        && TryNum(_outlineWidth, out var w) && w > 0 && TryNum(_outlineLength, out var l) && l > 0;

    public AsyncRelayCommand CaptureToolPoseCommand { get; }
    public AsyncRelayCommand MoveToPoseCommand { get; }
    public AsyncRelayCommand CaptureFocusZCommand { get; }
    public RelayCommand ClearPointsCommand { get; }
    public RelayCommand ClearOutlineCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public AsyncRelayCommand CameraToToolCommand { get; }
    public AsyncRelayCommand ToolToCameraCommand { get; }
    public RelayCommand ReloadCommand { get; }

    /// <summary>Raised on each sampler tick so the view can redraw its point markers.</summary>
    public event Action? Ticked;

    // --- lifecycle (driven by MachinePageViewModel) ---

    /// <summary>
    /// The section is the visible one: run the frame pipeline and hold a grab lease on the
    /// selected camera so the operator sees live video without pressing Start.
    /// </summary>
    public void SetActive(bool active)
    {
        _active = active;
        Camera.SetActive(active);
        if (!active) { PickArmed = false; PickOutlineArmed = false; Tracking = false; }
        UpdateLease();
    }

    /// <summary>Holds exactly one grab lease — on the selected camera, only while active.</summary>
    private void UpdateLease()
    {
        var want = _active ? Camera.SelectedCamera : null;
        if (string.Equals(want, _leasedCamera, StringComparison.OrdinalIgnoreCase)) return;
        if (_leasedCamera != null) _ = _host.Engine.Camera.StopGrabbingAsync(_leasedCamera);
        _leasedCamera = want;
        if (want != null) _ = _host.Engine.Camera.StartGrabbingAsync(want);
    }

    /// <summary>Sampler tick: refresh the live read-out and let the view redraw.</summary>
    public void Refresh()
    {
        if (_host.Engine.State == EngineState.ShuttingDown) return;   // facets gone — nothing to read
        Camera.Refresh();
        if (_toolDevice != null && IsRegistered)
        {
            var loc = _host.Process.LocateTool(_toolDevice);
            LiveText = loc.IsSuccess
                ? $"tool at ({loc.Value.ToolX:0.0000}, {loc.Value.ToolY:0.0000})  camera at ({loc.Value.CameraX:0.0000}, {loc.Value.CameraY:0.0000})  " +
                  $"Δ ({loc.Value.DxMm:+0.000;-0.000}, {loc.Value.DyMm:+0.000;-0.000})  d {loc.Value.DistanceMm:0.000} mm" +
                  (loc.Value.HasZ ? (loc.Value.DzMm < 0 ? $"   tool higher {loc.Value.DzMm:0.00}" : $"   tool lower {loc.Value.DzMm:+0.00}") : "")
                : loc.Message;
        }
        else LiveText = "not registered";
        Ticked?.Invoke();

        if (_tracking && !_busy && !_trackBusy && _toolDevice != null && _cameraDevice != null)
            _ = TrackTickAsync();
    }

    // --- tracking arm (test only) ---

    /// <summary>
    /// One tracking step: compare both devices' polled poses with the last settled snapshot; if
    /// one was jogged (and both are at rest) the other follows through the same bounded Layer 2
    /// moves the test buttons use, then the snapshot is re-taken after a short settle.
    /// </summary>
    private async Task TrackTickAsync()
    {
        var ts = _host.Engine.Motion.GetState(_toolDevice!);
        var cs = _host.Engine.Motion.GetState(_cameraDevice!);
        if (!ts.Valid || !cs.Valid) return;
        if (_trackSnapTool == null || _trackSnapCam == null)
        {
            _trackSnapTool = ts.Position;
            _trackSnapCam = cs.Position;
            return;
        }
        if (ts.IsMoving || cs.IsMoving) return;   // let the jog settle first

        double dTool = Dist3(ts.Position, _trackSnapTool);
        double dCam = Dist3(cs.Position, _trackSnapCam);
        if (dTool < TrackDeadbandMm && dCam < TrackDeadbandMm) return;

        bool toolLed = dTool >= dCam;   // the device that moved more is the leader
        _trackBusy = true;
        RaiseCans();
        using var activity = _host.Activity.Begin(ActivityKind.Alignment, "Tracking arm");
        try
        {
            Horizon.Common.Result r;
            if (toolLed)
            {
                r = await _host.Process.MoveCameraToToolAsync(_toolDevice!, includeZ: _testIncludeZ).ConfigureAwait(true);
            }
            else
            {
                if (!TryNum(_zOffset, out var zOff)) zOff = 0;
                r = await _host.Process.MoveToolToCameraAsync(_toolDevice!, includeZ: _testIncludeZ, zOffsetMm: zOff).ConfigureAwait(true);
            }
            SetStatus(r.IsSuccess
                ? (toolLed ? "Tracking: camera followed the tool." : "Tracking: tool followed the camera.")
                : $"Tracking: {r.Message}", !r.IsSuccess);
        }
        catch (Exception ex)
        {
            SetStatus($"Tracking: {ex.Message}", true);
        }
        finally
        {
            // Re-arm from the settled poses so the follow move itself never re-triggers.
            try { await Task.Delay(TrackSettleMs).ConfigureAwait(true); } catch (TaskCanceledException) { /* app closing */ }
            _trackSnapTool = _host.Engine.Motion.GetState(_toolDevice!).Position;
            _trackSnapCam = _host.Engine.Motion.GetState(_cameraDevice!).Position;
            _trackBusy = false;
            RaiseCans();
        }
    }

    private static double Dist3(MotionPosition a, MotionPosition b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    // --- step 1: capture the tool pose ---

    private async Task CaptureToolPoseAsync()
    {
        if (_toolDevice == null) return;
        var r = await _host.Engine.Motion.GetPositionAsync(_toolDevice).ConfigureAwait(true);
        if (!r.IsSuccess) { SetStatus($"Read {_toolDevice} failed: {r.Message}", true); return; }
        ToolPose = r.Value;
        CameraZ = null;   // the focus height belongs to the pose it was captured at
        if (Points.Count > 0)
        {
            Points.Clear();   // the points were relative to the old pose
            SetStatus("Tool pose captured — points and focus Z cleared; put the camera on the gripper, capture focus Z and pick the points again.");
        }
        else SetStatus("Tool pose captured. Now jog the camera to see the gripper, capture focus Z and pick the points.");
    }

    /// <summary>Sends the tool device back to the captured pose (through Layer 2 when it is the saved one).</summary>
    private async Task MoveToPoseAsync()
    {
        if (_toolDevice == null || _toolPose == null) return;
        _busy = true;
        RaiseCans();
        using var activity = _host.Activity.Begin(ActivityKind.Alignment, "Tool → pose");
        try
        {
            SetStatus("Moving tool to its pose…");
            var saved = _host.Process.GetToolRegistration(_toolDevice);
            Horizon.Common.Result r;
            if (saved?.ToolPose != null && saved.ToolPose == _toolPose)
            {
                r = await _host.Process.MoveToolToPoseAsync(_toolDevice).ConfigureAwait(true);
            }
            else
            {
                var p = _toolPose;
                _host.Log.Add(new LogEntry(LogLevel.Info, DateTimeOffset.Now, "Registration",
                    $"{_toolDevice} → captured pose ({p.X:F4}, {p.Y:F4}, {p.Z:F4}, {p.U:F4}, {p.V:F4}, {p.W:F4})."));
                r = await _host.Engine.Motion.MoveToPositionAsync(_toolDevice, p).ConfigureAwait(true);
            }
            SetStatus(r.IsSuccess ? "Tool is at its pose." : r.Message, !r.IsSuccess);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, true);
        }
        finally
        {
            _busy = false;
            RaiseCans();
        }
    }

    // --- step 2b: focus Z ---

    /// <summary>Records the camera device's live Z as the height at which the gripper is in focus.</summary>
    private async Task CaptureFocusZAsync()
    {
        if (_cameraDevice == null) return;
        var r = await _host.Engine.Motion.GetPositionAsync(_cameraDevice).ConfigureAwait(true);
        if (!r.IsSuccess) { SetStatus($"Read {_cameraDevice} failed: {r.Message}", true); return; }
        CameraZ = r.Value.Z;
        SetStatus($"Focus Z captured: {r.Value.Z:0.0000} mm. Camera → tool / tool → camera can now include Z.");
    }

    // --- step 3: points ---

    /// <summary>
    /// Adds a clicked point given in camera source pixels. Reads the camera device's pose live so
    /// the point is stored in the camera frame (the gantry may move between clicks).
    /// </summary>
    public async Task AddPointAsync(double srcPxX, double srcPxY, int srcW, int srcH)
    {
        if (_cameraDevice == null) { SetStatus("Pick a camera device first.", true); return; }
        if (_toolPose == null) { SetStatus("Capture the tool pose first (step 1).", true); return; }
        if (Points.Count >= _pointMode) { SetStatus($"Already have {_pointMode} points — Clear to re-pick.", true); return; }
        if (!await ToolIsAtPoseAsync("picking centre points").ConfigureAwait(true)) return;

        var pose = await _host.Engine.Motion.GetPositionAsync(_cameraDevice).ConfigureAwait(true);
        if (!pose.IsSuccess) { SetStatus($"Read {_cameraDevice} failed: {pose.Message}", true); return; }

        var (dxMm, dyMm) = ToolOverlayMath.SourcePxToMm(srcPxX - srcW / 2.0, srcPxY - srcH / 2.0, _host.Settings.Current);
        var pt = new RegisteredPoint(srcPxX, srcPxY, pose.Value.X + dxMm, pose.Value.Y + dyMm);
        Points.Add(new RegistrationPointRow(Points.Count + 1, pt));
        SetStatus(Points.Count == _pointMode
            ? $"{_pointMode} points picked — set the outline and Save."
            : $"Point {Points.Count} of {_pointMode} picked.");
        if (Points.Count == _pointMode) PickArmed = false;
    }

    /// <summary>
    /// Points and corners are only meaningful with the tool standing where its pose was captured
    /// (they describe the tool <i>at that pose</i>). Reads the tool live and refuses otherwise,
    /// pointing at Move to pose. Editing the other fields and saving needs no such check.
    /// </summary>
    private async Task<bool> ToolIsAtPoseAsync(string what)
    {
        if (_toolDevice == null || _toolPose == null) return false;
        var now = await _host.Engine.Motion.GetPositionAsync(_toolDevice).ConfigureAwait(true);
        if (!now.IsSuccess) { SetStatus($"Read {_toolDevice} failed: {now.Message}", true); return false; }
        double drift = Math.Max(Math.Abs(now.Value.X - _toolPose.X),
                       Math.Max(Math.Abs(now.Value.Y - _toolPose.Y), Math.Abs(now.Value.Z - _toolPose.Z)));
        if (drift <= PoseDriftToleranceMm) return true;
        SetStatus($"{_toolDevice} is {drift:0.0000} mm from its captured pose — press Move to pose (or recapture the pose) before {what}.", true);
        return false;
    }

    private void ClearPoints()
    {
        Points.Clear();
        SetStatus("Points cleared.");
    }

    // --- step 4: outline corners ---

    /// <summary>
    /// Adds an outline corner given in camera source pixels (camera pose read live, like the
    /// centre points). Two opposite corners define the box: width / length from their span, and
    /// the box offset from the tool anchor (average of the centre points) from their midpoint.
    /// </summary>
    public async Task AddOutlineCornerAsync(double srcPxX, double srcPxY, int srcW, int srcH)
    {
        if (_cameraDevice == null) { SetStatus("Pick a camera device first.", true); return; }
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
            SetStatus(Average == null
                ? "Outline picked — width/length set; the offset is filled in once the centre points exist."
                : "Outline picked — width, length and offset from the anchor set. Adjust the fields if needed, then Save.");
        }
        else SetStatus("Corner 1 of 2 picked — click the opposite corner of the gripper.");
    }

    /// <summary>Fills width / length (and, with an anchor, the box offset) from the two picked corners.</summary>
    private void RecomputeOutlineFromCorners()
    {
        if (_outlineCorners.Count != 2) return;
        var (a, b) = (_outlineCorners[0], _outlineCorners[1]);
        double w = Math.Abs(b.X - a.X), l = Math.Abs(b.Y - a.Y);
        if (w > 0) OutlineWidth = w.ToString("0.####", CultureInfo.InvariantCulture);
        if (l > 0) OutlineLength = l.ToString("0.####", CultureInfo.InvariantCulture);
        if (Average is { } avg)
        {
            OutlineOffsetX = ((a.X + b.X) / 2 - avg.X).ToString("0.####", CultureInfo.InvariantCulture);
            OutlineOffsetY = ((a.Y + b.Y) / 2 - avg.Y).ToString("0.####", CultureInfo.InvariantCulture);
        }
    }

    private void ClearOutline()
    {
        _outlineCorners.Clear();
        OnPropertyChanged(nameof(OutlineSummary));
        ClearOutlineCommand.RaiseCanExecuteChanged();
        SetStatus("Outline corners cleared (width / length / offset fields kept — edit or re-pick).");
    }

    // --- step 5: save / delete ---

    private async Task SaveAsync()
    {
        if (_toolDevice == null || _cameraDevice == null || _toolPose == null || Average is not { } avg) return;
        if (!TryNum(_outlineWidth, out var w) || !TryNum(_outlineLength, out var l) || !TryNum(_maxMove, out var max)
            || !TryNum(_zOffset, out var zOff) || !TryNum(_outlineOffsetX, out var offX) || !TryNum(_outlineOffsetY, out var offY))
        {
            SetStatus("Outline / offset / max move / Z offset must be numbers.", true);
            return;
        }
        if (!TryNum(_mapXSign, out var sx) || !TryNum(_mapYSign, out var sy) || !TryNum(_mapZSign, out var sz))
        {
            SetStatus("Axis signs must be +1 or -1.", true);
            return;
        }

        var camPose = await _host.Engine.Motion.GetPositionAsync(_cameraDevice).ConfigureAwait(true);
        var reg = new ToolRegistration
        {
            ToolDevice = _toolDevice,
            Label = string.IsNullOrWhiteSpace(_label) ? _toolDevice : _label.Trim(),
            CameraDevice = _cameraDevice,
            CameraId = Camera.SelectedCamera ?? "main_camera",
            ToolPose = _toolPose,
            CameraPose = camPose.IsSuccess ? camPose.Value : null,
            ToolX = avg.X,
            ToolY = avg.Y,
            CameraZ = _cameraZ,
            Points = Points.Select(p => p.Point).ToArray(),
            MmPerPixel = _host.Settings.Current.MmPerPixel,
            OutlineWidthMm = w,
            OutlineLengthMm = l,
            OutlineOffsetXmm = offX,
            OutlineOffsetYmm = offY,
            MapX = new ToolAxisMap(_mapXAxis.Trim().ToUpperInvariant(), sx),
            MapY = new ToolAxisMap(_mapYAxis.Trim().ToUpperInvariant(), sy),
            MapZ = new ToolAxisMap(_mapZAxis.Trim().ToUpperInvariant(), sz),
            MaxMoveMm = max,
            ToolToCameraZOffsetMm = zOff,
        };
        var r = _host.Process.SaveToolRegistration(reg);
        SetStatus(r.IsSuccess ? $"Registration saved for {_toolDevice}. It stays until the hardware changes." : r.Message, !r.IsSuccess);
        OnPropertyChanged(nameof(IsRegistered));
        RaiseCans();
    }

    private void Delete()
    {
        if (_toolDevice == null) return;
        if (MessageBox.Show($"Delete the registration for {_toolDevice}?", "Device registration",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var r = _host.Process.RemoveToolRegistration(_toolDevice);
        SetStatus(r.IsSuccess ? "Registration deleted." : r.Message, !r.IsSuccess);
        OnPropertyChanged(nameof(IsRegistered));
        RaiseCans();
    }

    // --- test moves (the same Layer 2 calls the recipe blocks use) ---

    private async Task MoveAsync(bool cameraToTool)
    {
        if (_toolDevice == null || _trackBusy) return;
        _busy = true;
        RaiseCans();
        using var activity = _host.Activity.Begin(ActivityKind.Alignment, cameraToTool ? "Camera → tool" : "Tool → camera");
        try
        {
            SetStatus(cameraToTool ? "Moving camera to tool…" : "Moving tool to camera…");
            var r = cameraToTool
                ? await _host.Process.MoveCameraToToolAsync(_toolDevice, includeZ: _testIncludeZ).ConfigureAwait(true)
                : await _host.Process.MoveToolToCameraAsync(_toolDevice, includeZ: _testIncludeZ,
                      zOffsetMm: TryNum(_zOffset, out var zOff) ? zOff : 0).ConfigureAwait(true);
            SetStatus(r.IsSuccess ? (cameraToTool ? "Camera is on the tool." : "Tool is under the camera.") : r.Message, !r.IsSuccess);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, true);
        }
        finally
        {
            _busy = false;
            RaiseCans();
        }
    }

    // --- load / seed ---

    /// <summary>Fills the fields from the tool's saved registration, or seeds defaults for a new one.</summary>
    private void LoadExisting()
    {
        _loading = true;
        try
        {
            Points.Clear();
            _outlineCorners.Clear();
            OnPropertyChanged(nameof(OutlineSummary));
            var reg = _toolDevice == null ? null : _host.Process.GetToolRegistration(_toolDevice);
            if (reg != null)
            {
                CameraDevice = Pick(MotionDevices, reg.CameraDevice, _cameraDevice);
                if (Camera.Cameras.Contains(reg.CameraId, StringComparer.OrdinalIgnoreCase))
                    Camera.SelectedCamera = reg.CameraId;
                Label = reg.Label;
                MapXAxis = reg.MapX.ToolAxis; MapXSign = reg.MapX.Sign < 0 ? "-1" : "+1";
                MapYAxis = reg.MapY.ToolAxis; MapYSign = reg.MapY.Sign < 0 ? "-1" : "+1";
                MapZAxis = reg.MapZ.ToolAxis; MapZSign = reg.MapZ.Sign < 0 ? "-1" : "+1";
                ToolPose = reg.ToolPose;
                CameraZ = reg.CameraZ;
                PointMode = reg.Points.Length == 4 ? 4 : 2;
                for (int i = 0; i < reg.Points.Length; i++)
                    Points.Add(new RegistrationPointRow(i + 1, reg.Points[i]));
                OutlineWidth = reg.OutlineWidthMm.ToString("0.###", CultureInfo.InvariantCulture);
                OutlineLength = reg.OutlineLengthMm.ToString("0.###", CultureInfo.InvariantCulture);
                OutlineOffsetX = reg.OutlineOffsetXmm.ToString("0.####", CultureInfo.InvariantCulture);
                OutlineOffsetY = reg.OutlineOffsetYmm.ToString("0.####", CultureInfo.InvariantCulture);
                MaxMove = reg.MaxMoveMm.ToString("0.###", CultureInfo.InvariantCulture);
                ZOffset = reg.ToolToCameraZOffsetMm.ToString("0.###", CultureInfo.InvariantCulture);
                SetStatus($"Loaded the saved registration for {_toolDevice} (by {reg.SavedBy ?? "?"} at {reg.SavedAt ?? "?"}).");
            }
            else
            {
                ToolPose = null;
                CameraZ = null;
                OutlineOffsetX = "0";
                OutlineOffsetY = "0";
                Label = "Gripper";
                SeedAxisMap();
                SetStatus(_toolDevice == null ? "Pick the tool device." : $"{_toolDevice} is not registered yet — start with step 1.");
            }
        }
        finally
        {
            _loading = false;
        }
        OnPropertyChanged(nameof(IsRegistered));
        RaiseCans();
    }

    /// <summary>
    /// Derives the camera-X / camera-Y ← tool-axis map from the jog transforms: a unit move on a
    /// tool-local axis maps to the global frame through the tool's matrix (orthonormal, so the
    /// inverse is the transpose) and into the camera device's frame through its matrix. The
    /// dominant column of each of the first two rows names the axis; its sign is the sign.
    /// </summary>
    private void SeedAxisMap()
    {
        if (_toolDevice == null || _cameraDevice == null) return;
        var mt = _jogTransforms.Get(_toolDevice);
        var mc = _jogTransforms.Get(_cameraDevice);
        double[,] t = { { mt.M11, mt.M12, mt.M13 }, { mt.M21, mt.M22, mt.M23 }, { mt.M31, mt.M32, mt.M33 } };
        double[,] c = { { mc.M11, mc.M12, mc.M13 }, { mc.M21, mc.M22, mc.M23 }, { mc.M31, mc.M32, mc.M33 } };

        // R = C · Tᵀ  — column a of R is the camera-frame direction of a unit tool-axis-a move.
        var r = new double[3, 3];
        for (int i = 0; i < 3; i++)
            for (int a = 0; a < 3; a++)
            {
                double sum = 0;
                for (int k = 0; k < 3; k++) sum += c[i, k] * t[a, k];
                r[i, a] = sum;
            }

        (MapXAxis, MapXSign) = Dominant(r, 0);
        (MapYAxis, MapYSign) = Dominant(r, 1);
        (MapZAxis, MapZSign) = Dominant(r, 2);

        static (string Axis, string Sign) Dominant(double[,] r, int row)
        {
            int best = 0;
            for (int a = 1; a < 3; a++) if (Math.Abs(r[row, a]) > Math.Abs(r[row, best])) best = a;
            if (Math.Abs(r[row, best]) < 1e-9) return ("XYZ".Substring(row, 1), "+1");
            return ("XYZ".Substring(best, 1), r[row, best] < 0 ? "-1" : "+1");
        }
    }

    private void OnRegistrationsChanged()
    {
        if (!Application.Current.Dispatcher.CheckAccess()) { Application.Current.Dispatcher.BeginInvoke(OnRegistrationsChanged); return; }
        OnPropertyChanged(nameof(IsRegistered));
        RaiseCans();
    }

    private void RaiseCans()
    {
        CaptureToolPoseCommand.RaiseCanExecuteChanged();
        MoveToPoseCommand.RaiseCanExecuteChanged();
        CaptureFocusZCommand.RaiseCanExecuteChanged();
        ClearPointsCommand.RaiseCanExecuteChanged();
        ClearOutlineCommand.RaiseCanExecuteChanged();
        SaveCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
        CameraToToolCommand.RaiseCanExecuteChanged();
        ToolToCameraCommand.RaiseCanExecuteChanged();
    }

    private void SetStatus(string text, bool warn = false)
    {
        Status = text;
        StatusWarn = warn;
    }

    private static bool TryNum(string s, out double v)
        => double.TryParse(s?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    private static string? Pick(IReadOnlyList<string> list, string? preferred, string? fallback)
    {
        if (preferred != null && list.Contains(preferred, StringComparer.OrdinalIgnoreCase))
            return list.First(d => string.Equals(d, preferred, StringComparison.OrdinalIgnoreCase));
        if (fallback != null && list.Contains(fallback, StringComparer.OrdinalIgnoreCase))
            return list.First(d => string.Equals(d, fallback, StringComparison.OrdinalIgnoreCase));
        return list.Count > 0 ? list[0] : null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _host.Process.ToolRegistrationsChanged -= OnRegistrationsChanged;
        _active = false;
        _tracking = false;
        UpdateLease();   // releases the grab lease, if held
        Camera.Dispose();
    }
}
