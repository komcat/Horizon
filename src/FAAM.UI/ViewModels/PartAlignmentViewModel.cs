using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using FAAM.UI.ViewModels.Sections;
using Horizon.Engine;
using Horizon.Workflow.Registration;

namespace FAAM.UI.ViewModels;

/// <summary>One taught point of a local object as edited on the page (local frame + the camera position it is seen from).</summary>
public sealed class PartObjectPointRow : ObservableObject
{
    private PartObjectPoint? _point;
    private string _profile = "";

    public PartObjectPointRow(int index) => Index = index;

    public int Index { get; }
    public string Label => $"P{Index}";

    public PartObjectPoint? Point
    {
        get => _point;
        set { if (SetField(ref _point, value)) OnPropertyChanged(nameof(Text)); }
    }

    /// <summary>Vision profile that auto-marks this point (empty = manual).</summary>
    public string Profile { get => _profile; set => SetField(ref _profile, value ?? ""); }

    public string Text => _point == null
        ? "not taught"
        : $"local ({_point.LocalX:0.0000}, {_point.LocalY:0.0000})  cam local ({_point.CamLocalX:0.000}, {_point.CamLocalY:0.000}) Z {_point.CamZ:0.000}  [{_point.Source}]";
}

/// <summary>A local object (point / line) as listed and edited on the page.</summary>
public sealed class PartObjectRow : ObservableObject
{
    private string _name;
    private string _offsetX = "0", _offsetY = "0";

    public PartObjectRow(string name, bool isLine)
    {
        _name = name;
        IsLine = isLine;
        Points = isLine ? new[] { new PartObjectPointRow(1), new PartObjectPointRow(2) } : new[] { new PartObjectPointRow(1) };
    }

    public string Name { get => _name; set { if (SetField(ref _name, value)) OnPropertyChanged(nameof(Summary)); } }
    public bool IsLine { get; }
    public string Kind => IsLine ? "line" : "point";
    public PartObjectPointRow[] Points { get; }
    public string OffsetX { get => _offsetX; set => SetField(ref _offsetX, value); }
    public string OffsetY { get => _offsetY; set => SetField(ref _offsetY, value); }
    public bool IsComplete => Points.All(p => p.Point != null);
    public string Summary => $"{_name}  ·  {Kind}  ·  {(IsComplete ? "taught" : "incomplete")}";

    public void RefreshSummary() => OnPropertyChanged(nameof(Summary));

    public PartObject ToModel()
    {
        double.TryParse(_offsetX, NumberStyles.Float, CultureInfo.InvariantCulture, out var ox);
        double.TryParse(_offsetY, NumberStyles.Float, CultureInfo.InvariantCulture, out var oy);
        return new PartObject
        {
            Name = _name.Trim(),
            Kind = Kind,
            Points = Points.Select(p => p.Point == null ? null : p.Point with { VisionProfile = p.Profile }).Where(p => p != null).Select(p => p!).ToArray(),
            OffsetX = ox,
            OffsetY = oy,
        };
    }

    public static PartObjectRow FromModel(PartObject o)
    {
        var row = new PartObjectRow(o.Name, o.IsLine)
        {
            OffsetX = o.OffsetX.ToString("0.####", CultureInfo.InvariantCulture),
            OffsetY = o.OffsetY.ToString("0.####", CultureInfo.InvariantCulture),
        };
        for (int i = 0; i < row.Points.Length && i < o.Points.Length; i++)
        {
            row.Points[i].Point = o.Points[i];
            row.Points[i].Profile = o.Points[i].VisionProfile;
        }
        return row;
    }
}

/// <summary>A saved local object as listed under Camera moves: its live state and a button that sends the camera to it.</summary>
public sealed class PartMoveRow : ObservableObject
{
    private string _state = "";

    public PartMoveRow(string name, bool isLine, Func<string, Task> move)
    {
        Name = name;
        Kind = isLine ? "line" : "point";
        MoveCommand = new AsyncRelayCommand(() => move(name));
    }

    public string Name { get; }
    public string Kind { get; }
    public string State { get => _state; set => SetField(ref _state, value); }
    public AsyncRelayCommand MoveCommand { get; }
}

/// <summary>
/// The <b>Part Alignment</b> tab: teach the two fiducials (E1 = origin, E1 → E2 = local +X) and
/// the local objects (points / lines with an XY offset) that locate the physical part on the
/// fixture, save the profile into the product, and run the procedure that re-finds it all. The
/// geometry and moves live in Layer 2 (<c>PartAlignmentRoutine</c>); this model owns the edit
/// state and the feed-click routing (one <see cref="MarkTarget"/> at a time).
/// </summary>
public sealed class PartAlignmentViewModel : ObservableObject, IDisposable
{
    private readonly EngineHost _host;
    private string _name = "Part";
    private string _cameraDevice = "gantry-main";
    private string _exposureUs = "10000", _gain = "0";
    private bool _autoExposure = true;
    private string _settleMs = "100";
    private MotionPosition? _e1Cam, _e2Cam;
    private PartPoint? _e1, _e2;
    private string _e1Profile = "", _e2Profile = "";
    private PartObjectRow? _selectedObject;
    private string _markTarget = "";      // "", "E1", "E2", "P1", "P2" (Pn = selected object's point n)
    private bool _applyOffset = true;
    private string _status = "—";
    private bool _statusWarn;
    private string _liveText = "—";
    private bool _busy;
    private bool _active;
    private string? _leasedCamera;
    private bool _disposed;
    private TaskCompletionSource<PartPoint?>? _manualMark;
    private CancellationTokenSource? _procCts;

    public PartAlignmentViewModel(EngineHost host)
    {
        _host = host;
        Camera = new CameraSectionViewModel(host);
        _cameraDevice = host.Settings.Current.MoveClickDevice is { Length: > 0 } d ? d : "gantry-main";

        ReadExposureCommand = new RelayCommand(ReadExposure, () => Camera.SelectedCamera != null);
        ApplyExposureCommand = new AsyncRelayCommand(ApplyExposureAsync, () => !_busy && Camera.SelectedCamera != null);
        TeachE1CamCommand = new AsyncRelayCommand(() => TeachFiducialCamAsync(1), () => !_busy);
        TeachE2CamCommand = new AsyncRelayCommand(() => TeachFiducialCamAsync(2), () => !_busy);
        MoveE1Command = new AsyncRelayCommand(() => MoveToFiducialAsync(1), () => !_busy && (_e1 != null || _e1Cam != null));
        MoveE2Command = new AsyncRelayCommand(() => MoveToFiducialAsync(2), () => !_busy && (_e2 != null || _e2Cam != null));
        RunE1Command = new AsyncRelayCommand(() => RunFiducialVisionAsync(1), () => !_busy && _e1Profile.Length > 0);
        RunE2Command = new AsyncRelayCommand(() => RunFiducialVisionAsync(2), () => !_busy && _e2Profile.Length > 0);
        MarkE1Command = new RelayCommand(() => MarkTarget = "E1", () => !_busy);
        MarkE2Command = new RelayCommand(() => MarkTarget = "E2", () => !_busy);
        AddPointCommand = new RelayCommand(() => AddObject(false));
        AddLineCommand = new RelayCommand(() => AddObject(true));
        RemoveObjectCommand = new RelayCommand(RemoveObject, () => _selectedObject != null);
        CloseObjectCommand = new RelayCommand(() => SelectedObject = null);
        TeachP1CamCommand = new AsyncRelayCommand(() => TeachObjectCamAsync(1), () => !_busy && _selectedObject != null && Frame != null);
        TeachP2CamCommand = new AsyncRelayCommand(() => TeachObjectCamAsync(2), () => !_busy && _selectedObject is { IsLine: true } && Frame != null);
        MoveP1Command = new AsyncRelayCommand(() => MoveToObjectPointAsync(1), () => !_busy && _selectedObject?.Points[0].Point != null);
        MoveP2Command = new AsyncRelayCommand(() => MoveToObjectPointAsync(2), () => !_busy && _selectedObject is { IsLine: true } && _selectedObject.Points[1].Point != null);
        RunP1Command = new AsyncRelayCommand(() => RunObjectVisionAsync(1), () => !_busy && _selectedObject != null && Frame != null && _selectedObject.Points[0].Profile.Length > 0);
        RunP2Command = new AsyncRelayCommand(() => RunObjectVisionAsync(2), () => !_busy && _selectedObject is { IsLine: true } && Frame != null && _selectedObject.Points[1].Profile.Length > 0);
        MarkP1Command = new RelayCommand(() => MarkTarget = "P1", () => !_busy && _selectedObject != null && Frame != null);
        MarkP2Command = new RelayCommand(() => MarkTarget = "P2", () => !_busy && _selectedObject is { IsLine: true } && Frame != null);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !_busy && !string.IsNullOrWhiteSpace(_name));
        SaveAndBackCommand = new AsyncRelayCommand(async () => { await SaveAsync().ConfigureAwait(true); if (!StatusWarn) SelectedObject = null; },
            () => !_busy && !string.IsNullOrWhiteSpace(_name));
        DeleteCommand = new RelayCommand(Delete, () => IsSaved);
        ReloadCommand = new RelayCommand(LoadExisting);
        RunProcedureCommand = new AsyncRelayCommand(RunProcedureAsync, () => !_busy && IsSaved);
        CancelProcedureCommand = new RelayCommand(CancelProcedure, () => _procCts != null);
        CameraToE1Command = new AsyncRelayCommand(() => CameraToFiducialAsync(1), () => !_busy && IsSaved);
        CameraToE2Command = new AsyncRelayCommand(() => CameraToFiducialAsync(2), () => !_busy && IsSaved);
        CameraToObjectCommand = new AsyncRelayCommand(CameraToObjectAsync, () => !_busy && IsSaved && _selectedObject != null);

        Camera.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CameraSectionViewModel.SelectedCamera)) { UpdateLease(); ReadExposureCommand.RaiseCanExecuteChanged(); }
        };
        _host.Process.PartAlignmentsChanged += OnPartsChanged;
        _host.Process.ProductChanged += _ => OnPartsChanged();

        RefreshLists();
        LoadExisting();
    }

    public EngineHost Host => _host;
    public CameraSectionViewModel Camera { get; }
    public ObservableCollection<string> PartNames { get; } = new();
    public ObservableCollection<string> VisionProfiles { get; } = new();
    public ObservableCollection<PartObjectRow> Objects { get; } = new();

    /// <summary>The saved part's objects for the Camera moves list (measured / taught state per row).</summary>
    public ObservableCollection<PartMoveRow> MoveTargets { get; } = new();

    public string Name
    {
        get => _name;
        set { if (SetField(ref _name, value)) { OnPropertyChanged(nameof(IsSaved)); RaiseCans(); } }
    }

    public string? SelectedPartName
    {
        get => null;
        set { if (!string.IsNullOrEmpty(value)) { Name = value; LoadExisting(); } OnPropertyChanged(); }
    }

    public string CameraDevice { get => _cameraDevice; private set => SetField(ref _cameraDevice, value); }
    public string ExposureUs { get => _exposureUs; set => SetField(ref _exposureUs, value); }
    public string Gain { get => _gain; set => SetField(ref _gain, value); }
    public bool AutoExposure { get => _autoExposure; set => SetField(ref _autoExposure, value); }
    public string SettleMs { get => _settleMs; set => SetField(ref _settleMs, value); }

    public string E1Profile { get => _e1Profile; set { if (SetField(ref _e1Profile, value ?? "")) RaiseCans(); } }
    public string E2Profile { get => _e2Profile; set { if (SetField(ref _e2Profile, value ?? "")) RaiseCans(); } }
    public string E1CamText => CamText(_e1Cam);
    public string E2CamText => CamText(_e2Cam);
    public string E1Text => PointText(_e1);
    public string E2Text => PointText(_e2);

    /// <summary>The frame from the taught fiducials (for teaching objects), or null.</summary>
    public PartFrame? Frame => _e1 != null && _e2 != null ? PartFrame.From(_e1.X, _e1.Y, _e2.X, _e2.Y) : null;
    public string FrameText => Frame is { } f
        ? $"origin ({f.OriginX:0.0000}, {f.OriginY:0.0000})  X axis {f.AngleDeg:0.000}°  |E1E2| {Math.Sqrt((_e2!.X - _e1!.X) * (_e2.X - _e1.X) + (_e2.Y - _e1.Y) * (_e2.Y - _e1.Y)):0.0000} mm"
        : "— (needs E1 and E2)";

    /// <summary>The object being edited; the panel swaps to the object editor while one is selected.</summary>
    public PartObjectRow? SelectedObject
    {
        get => _selectedObject;
        set
        {
            if (!SetField(ref _selectedObject, value)) return;
            if (_markTarget.StartsWith('P')) MarkTarget = "";
            OnPropertyChanged(nameof(EditingObject));
            RaiseCans();
        }
    }
    public bool EditingObject => _selectedObject != null;

    /// <summary>What the next feed click marks: E1 / E2 / P1 / P2 (of the selected object), or empty.</summary>
    public string MarkTarget
    {
        get => _markTarget;
        set { if (SetField(ref _markTarget, value ?? "")) { OnPropertyChanged(nameof(MarkArmed)); OnPropertyChanged(nameof(MarkText)); } }
    }
    public bool MarkArmed
    {
        get => _markTarget.Length > 0;
        set { if (!value) MarkTarget = ""; }
    }
    public string MarkText => _markTarget.Length == 0 ? "Mark" : $"Marking {_markTarget} — click the feed";

    public bool ApplyOffset { get => _applyOffset; set => SetField(ref _applyOffset, value); }
    public string Status { get => _status; private set => SetField(ref _status, value); }
    public bool StatusWarn { get => _statusWarn; private set => SetField(ref _statusWarn, value); }
    public string LiveText { get => _liveText; private set => SetField(ref _liveText, value); }
    public bool IsSaved => _host.Process.GetPartAlignment(_name) != null;
    public bool ProcedureRunning => _procCts != null;

    public RelayCommand ReadExposureCommand { get; }
    public AsyncRelayCommand ApplyExposureCommand { get; }
    public AsyncRelayCommand TeachE1CamCommand { get; }
    public AsyncRelayCommand TeachE2CamCommand { get; }
    public AsyncRelayCommand MoveE1Command { get; }
    public AsyncRelayCommand MoveE2Command { get; }
    public AsyncRelayCommand RunE1Command { get; }
    public AsyncRelayCommand RunE2Command { get; }
    public RelayCommand MarkE1Command { get; }
    public RelayCommand MarkE2Command { get; }
    public RelayCommand AddPointCommand { get; }
    public RelayCommand AddLineCommand { get; }
    public RelayCommand RemoveObjectCommand { get; }
    public RelayCommand CloseObjectCommand { get; }
    public AsyncRelayCommand TeachP1CamCommand { get; }
    public AsyncRelayCommand TeachP2CamCommand { get; }
    public AsyncRelayCommand MoveP1Command { get; }
    public AsyncRelayCommand MoveP2Command { get; }
    public AsyncRelayCommand RunP1Command { get; }
    public AsyncRelayCommand RunP2Command { get; }
    public RelayCommand MarkP1Command { get; }
    public RelayCommand MarkP2Command { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand SaveAndBackCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand ReloadCommand { get; }
    public AsyncRelayCommand RunProcedureCommand { get; }
    public RelayCommand CancelProcedureCommand { get; }
    public AsyncRelayCommand CameraToE1Command { get; }
    public AsyncRelayCommand CameraToE2Command { get; }
    public AsyncRelayCommand CameraToObjectCommand { get; }

    /// <summary>Raised on each refresh tick so the view redraws its markers.</summary>
    public event Action? Ticked;

    // --- lifecycle ---

    public void SetActive(bool active)
    {
        _active = active;
        Camera.SetActive(active);
        if (!active) MarkTarget = "";
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

    public void Refresh()
    {
        if (_host.Engine.State == EngineState.ShuttingDown) return;
        Camera.Refresh();
        if (IsSaved)
        {
            var snap = _host.Process.GetPartSnapshot(_name);
            if (snap.IsSuccess && snap.Value.Frame is { } f)
            {
                int measured = snap.Value.Objects.Count(o => o.Measured);
                LiveText = $"{_name}: origin ({f.OriginX:0.0000}, {f.OriginY:0.0000})  X axis {f.AngleDeg:0.000}°  " +
                           $"{(snap.Value.FromMeasurement ? $"measured — {measured}/{snap.Value.Objects.Count} object(s) detected" : "taught (not yet run)")}";
            }
            else LiveText = snap.IsSuccess ? $"{_name}: no frame yet" : snap.Message;
            SyncMoveTargets(snap.IsSuccess ? snap.Value : null);
        }
        else { LiveText = "not saved"; SyncMoveTargets(null); }
        Ticked?.Invoke();
    }

    // --- exposure ---

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

    // --- fiducials ---

    private async Task TeachFiducialCamAsync(int i)
    {
        var r = await _host.Engine.Motion.GetPositionAsync(_cameraDevice).ConfigureAwait(true);
        if (!r.IsSuccess) { SetStatus($"Read {_cameraDevice} failed: {r.Message}", true); return; }
        if (i == 1) _e1Cam = r.Value; else _e2Cam = r.Value;
        OnPropertyChanged(i == 1 ? nameof(E1CamText) : nameof(E2CamText));
        RaiseCans();
        SetStatus($"Camera position for E{i} taught. Now mark E{i}: Mark and click it, or run its vision profile.");
    }

    private async Task MoveToFiducialAsync(int i)
    {
        var cam = i == 1 ? _e1Cam : _e2Cam;
        var pt = i == 1 ? _e1 : _e2;
        await RunBusyAsync($"Camera → E{i}", async () =>
        {
            var cur = await _host.Engine.Motion.GetPositionAsync(_cameraDevice).ConfigureAwait(true);
            if (!cur.IsSuccess) { SetStatus(cur.Message, true); return; }
            // Centre the camera on the marked point when there is one, else go to the taught camera position.
            var target = pt != null && cam != null ? cam with { X = pt.X, Y = pt.Y } : cam;
            if (target == null) { SetStatus($"E{i} has no camera position.", true); return; }
            var r = await _host.Engine.Motion.MoveToPositionAsync(_cameraDevice, target).ConfigureAwait(true);
            if (r.IsSuccess) await ApplyExposureAsync().ConfigureAwait(true);
            SetStatus(r.IsSuccess ? $"Camera on E{i}." : r.Message, !r.IsSuccess);
        });
    }

    private async Task RunFiducialVisionAsync(int i)
    {
        var profile = i == 1 ? _e1Profile : _e2Profile;
        var cam = i == 1 ? _e1Cam : _e2Cam;
        await RunBusyAsync($"Vision E{i}", async () =>
        {
            if (cam != null)
            {
                var mv = await _host.Engine.Motion.MoveToPositionAsync(_cameraDevice, cam).ConfigureAwait(true);
                if (!mv.IsSuccess) { SetStatus($"Camera → E{i}: {mv.Message}", true); return; }
                await ApplyExposureAsync().ConfigureAwait(true);
                await SettleAsync(CancellationToken.None).ConfigureAwait(true);
            }
            var r = await DetectAsync(profile).ConfigureAwait(true);
            if (!r.IsSuccess) { SetStatus($"E{i}: {r.Message}", true); return; }
            SetFiducial(i, r.Value);
            SetStatus($"E{i} detected by '{profile}' at ({r.Value.X:0.0000}, {r.Value.Y:0.0000}).");
        });
    }

    private void SetFiducial(int i, PartPoint p)
    {
        if (i == 1) { _e1 = p; _e1Cam = p.CameraPose; } else { _e2 = p; _e2Cam = p.CameraPose; }
        OnPropertyChanged(nameof(E1Text)); OnPropertyChanged(nameof(E2Text));
        OnPropertyChanged(nameof(E1CamText)); OnPropertyChanged(nameof(E2CamText));
        OnPropertyChanged(nameof(Frame)); OnPropertyChanged(nameof(FrameText));
        foreach (var o in Objects) o.RefreshSummary();
        RaiseCans();
    }

    /// <summary>Detect with a profile on the live camera; goes through Layer 2 so the conversion matches the run's.</summary>
    private Task<Horizon.Common.Result<PartPoint>> DetectAsync(string profile)
        => IsSaved
            ? _host.Process.DetectPartPointAsync(_name, profile)
            : DetectUnsavedAsync(profile);

    /// <summary>Before the first save the profile has no stored record; detect against a transient one.</summary>
    private async Task<Horizon.Common.Result<PartPoint>> DetectUnsavedAsync(string profile)
    {
        // Save a minimal shell so Layer 2 can resolve camera ids, then detect. Harmless: Save is the next step anyway.
        var r = _host.Process.SavePartAlignment(BuildModel());
        if (!r.IsSuccess) return Horizon.Common.Result<PartPoint>.Fail(r.Error, r.Message);
        OnPropertyChanged(nameof(IsSaved));
        return await _host.Process.DetectPartPointAsync(_name, profile).ConfigureAwait(true);
    }

    // --- objects ---

    private void AddObject(bool line)
    {
        int n = Objects.Count + 1;
        string name = line ? $"Line{n}" : $"Point{n}";
        while (Objects.Any(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase))) name += "_";
        var row = new PartObjectRow(name, line);
        Objects.Add(row);
        SelectedObject = row;
        SetStatus(Frame == null
            ? $"{name} added — teach E1 and E2 first; object points are stored in the part frame."
            : $"{name} added — teach its camera position(s) and mark the point(s).");
    }

    private void RemoveObject()
    {
        if (_selectedObject == null) return;
        Objects.Remove(_selectedObject);
        SelectedObject = null;
        SetStatus("Object removed (save to persist).");
    }

    private async Task TeachObjectCamAsync(int i)
    {
        var obj = _selectedObject;
        var frame = Frame;
        if (obj == null || frame == null) return;
        var r = await _host.Engine.Motion.GetPositionAsync(_cameraDevice).ConfigureAwait(true);
        if (!r.IsSuccess) { SetStatus($"Read {_cameraDevice} failed: {r.Message}", true); return; }
        var row = obj.Points[i - 1];
        var (clx, cly) = frame.ToLocal(r.Value.X, r.Value.Y);
        row.Point = (row.Point ?? new PartObjectPoint(0, 0, 0, 0, 0, row.Profile, "", 0, 0)) with { CamLocalX = clx, CamLocalY = cly, CamZ = r.Value.Z };
        obj.RefreshSummary();
        RaiseCans();
        SetStatus($"{obj.Name} P{i}: camera position taught (local {clx:0.000}, {cly:0.000}). Now mark P{i} or run its profile.");
    }

    private async Task MoveToObjectPointAsync(int i)
    {
        var obj = _selectedObject;
        var frame = Frame;
        var pt = obj?.Points[i - 1].Point;
        if (obj == null || frame == null || pt == null) return;
        await RunBusyAsync($"Camera → {obj.Name} P{i}", async () =>
        {
            var cur = await _host.Engine.Motion.GetPositionAsync(_cameraDevice).ConfigureAwait(true);
            if (!cur.IsSuccess) { SetStatus(cur.Message, true); return; }
            var (gx, gy) = frame.ToGlobal(pt.CamLocalX, pt.CamLocalY);
            var r = await _host.Engine.Motion.MoveToPositionAsync(_cameraDevice, cur.Value with { X = gx, Y = gy, Z = pt.CamZ }).ConfigureAwait(true);
            if (r.IsSuccess) await ApplyExposureAsync().ConfigureAwait(true);
            SetStatus(r.IsSuccess ? $"Camera at {obj.Name} P{i} position." : r.Message, !r.IsSuccess);
        });
    }

    private async Task RunObjectVisionAsync(int i)
    {
        var obj = _selectedObject;
        var frame = Frame;
        if (obj == null || frame == null) return;
        var row = obj.Points[i - 1];
        await RunBusyAsync($"Vision {obj.Name} P{i}", async () =>
        {
            if (row.Point != null)
            {
                var cur = await _host.Engine.Motion.GetPositionAsync(_cameraDevice).ConfigureAwait(true);
                if (!cur.IsSuccess) { SetStatus(cur.Message, true); return; }
                var (gx, gy) = frame.ToGlobal(row.Point.CamLocalX, row.Point.CamLocalY);
                var mv = await _host.Engine.Motion.MoveToPositionAsync(_cameraDevice, cur.Value with { X = gx, Y = gy, Z = row.Point.CamZ }).ConfigureAwait(true);
                if (!mv.IsSuccess) { SetStatus($"Camera → {obj.Name} P{i}: {mv.Message}", true); return; }
                await ApplyExposureAsync().ConfigureAwait(true);
                await SettleAsync(CancellationToken.None).ConfigureAwait(true);
            }
            var r = await DetectAsync(row.Profile).ConfigureAwait(true);
            if (!r.IsSuccess) { SetStatus($"{obj.Name} P{i}: {r.Message}", true); return; }
            SetObjectPoint(obj, i, r.Value);
            SetStatus($"{obj.Name} P{i} detected by '{row.Profile}'.");
        });
    }

    private void SetObjectPoint(PartObjectRow obj, int i, PartPoint p)
    {
        var frame = Frame;
        if (frame == null) return;
        var row = obj.Points[i - 1];
        var (lx, ly) = frame.ToLocal(p.X, p.Y);
        var (clx, cly) = frame.ToLocal(p.CameraPose.X, p.CameraPose.Y);
        row.Point = new PartObjectPoint(lx, ly, clx, cly, p.CameraPose.Z, row.Profile, p.Source, p.PixelX, p.PixelY);
        obj.RefreshSummary();
        RaiseCans();
    }

    // --- marking (feed click) ---

    /// <summary>A feed click at source px while a mark target is armed.</summary>
    public async Task MarkAsync(double srcPxX, double srcPxY, int srcW, int srcH)
    {
        var target = _markTarget;
        if (target.Length == 0) return;
        var pose = await _host.Engine.Motion.GetPositionAsync(_cameraDevice).ConfigureAwait(true);
        if (!pose.IsSuccess) { SetStatus($"Read {_cameraDevice} failed: {pose.Message}", true); return; }
        var pt = _host.Process.ToPartPoint(pose.Value, srcPxX, srcPxY, srcW, srcH, "manual");

        if (_manualMark is { } waiting)
        {
            // The run procedure asked for this click — hand the point to it.
            MarkTarget = "";
            waiting.TrySetResult(pt);
            return;
        }

        switch (target)
        {
            case "E1": SetFiducial(1, pt); break;
            case "E2": SetFiducial(2, pt); break;
            case "P1": if (_selectedObject != null) SetObjectPoint(_selectedObject, 1, pt); break;
            case "P2": if (_selectedObject != null) SetObjectPoint(_selectedObject, 2, pt); break;
        }
        MarkTarget = "";
        SetStatus($"{target} marked at ({pt.X:0.0000}, {pt.Y:0.0000}).");
    }

    // --- save / delete / load ---

    private PartAlignment BuildModel()
    {
        TryNum(_exposureUs, out var us); TryNum(_gain, out var gain);
        var existing = _host.Process.GetPartAlignment(_name);
        return new PartAlignment
        {
            Name = _name.Trim(),
            CameraDevice = _cameraDevice,
            CameraId = Camera.SelectedCamera ?? "main_camera",
            AutoExposure = _autoExposure,
            ExposureUs = us,
            Gain = gain,
            SettleMs = TryNum(_settleMs, out var st) && st >= 0 ? (int)st : 100,
            E1 = new PartFiducial { VisionProfile = _e1Profile, CameraPose = _e1Cam, Point = _e1 },
            E2 = new PartFiducial { VisionProfile = _e2Profile, CameraPose = _e2Cam, Point = _e2 },
            Objects = Objects.Select(o => o.ToModel()).ToArray(),
            Measured = existing?.Measured,   // a manual re-save keeps the last run until the next run replaces it
        };
    }

    private async Task SaveAsync()
    {
        var r = _host.Process.SavePartAlignment(BuildModel());
        SetStatus(r.IsSuccess ? $"'{_name}' saved into product {_host.Process.ActiveProduct}." : r.Message, !r.IsSuccess);
        await Task.CompletedTask;
        RefreshLists();
        OnPropertyChanged(nameof(IsSaved));
        RaiseCans();
    }

    private void Delete()
    {
        if (MessageBox.Show($"Delete part alignment '{_name}' — fiducials, objects and the last measurement?", "Part alignment",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var r = _host.Process.RemovePartAlignment(_name);
        SetStatus(r.IsSuccess ? $"'{_name}' deleted." : r.Message, !r.IsSuccess);
        RefreshLists();
        ResetFields();
        OnPropertyChanged(nameof(IsSaved));
        RaiseCans();
    }

    private void LoadExisting()
    {
        var part = _host.Process.GetPartAlignment(_name);
        if (part == null)
        {
            ResetFields();
            SetStatus($"'{_name}' is not taught yet in product {_host.Process.ActiveProduct} — teach E1 and E2 first.");
        }
        else
        {
            CameraDevice = part.CameraDevice;
            if (Camera.Cameras.Contains(part.CameraId, StringComparer.OrdinalIgnoreCase)) Camera.SelectedCamera = part.CameraId;
            AutoExposure = part.AutoExposure;
            ExposureUs = part.ExposureUs.ToString("0", CultureInfo.InvariantCulture);
            Gain = part.Gain.ToString("0.##", CultureInfo.InvariantCulture);
            SettleMs = part.SettleMs.ToString(CultureInfo.InvariantCulture);
            _e1Profile = part.E1.VisionProfile; _e2Profile = part.E2.VisionProfile;
            _e1Cam = part.E1.CameraPose; _e2Cam = part.E2.CameraPose;
            _e1 = part.E1.Point; _e2 = part.E2.Point;
            OnPropertyChanged(nameof(E1Profile)); OnPropertyChanged(nameof(E2Profile));
            Objects.Clear();
            foreach (var o in part.Objects) Objects.Add(PartObjectRow.FromModel(o));
            SelectedObject = null;
            OnPropertyChanged(nameof(E1Text)); OnPropertyChanged(nameof(E2Text));
            OnPropertyChanged(nameof(E1CamText)); OnPropertyChanged(nameof(E2CamText));
            OnPropertyChanged(nameof(Frame)); OnPropertyChanged(nameof(FrameText));
            SetStatus($"Loaded '{part.Name}' (by {part.SavedBy ?? "?"} at {part.SavedAt ?? "?"}){(part.Measured != null ? $", last measured {part.Measured.At}" : "")}.");
        }
        OnPropertyChanged(nameof(IsSaved));
        RaiseCans();
    }

    private void ResetFields()
    {
        _e1 = _e2 = null;
        _e1Cam = _e2Cam = null;
        _e1Profile = _e2Profile = "";
        OnPropertyChanged(nameof(E1Profile)); OnPropertyChanged(nameof(E2Profile));
        Objects.Clear();
        SelectedObject = null;
        OnPropertyChanged(nameof(E1Text)); OnPropertyChanged(nameof(E2Text));
        OnPropertyChanged(nameof(E1CamText)); OnPropertyChanged(nameof(E2CamText));
        OnPropertyChanged(nameof(Frame)); OnPropertyChanged(nameof(FrameText));
    }

    private void RefreshLists()
    {
        Sync(PartNames, _host.Process.GetPartAlignments().Select(p => p.Name).ToList());
        Sync(VisionProfiles, _host.Process.ListVisionProfiles().ToList());

        static void Sync(ObservableCollection<string> target, List<string> items)
        {
            if (target.SequenceEqual(items, StringComparer.OrdinalIgnoreCase)) return;
            target.Clear();
            foreach (var i in items) target.Add(i);
        }
    }

    private void OnPartsChanged()
    {
        if (!Application.Current.Dispatcher.CheckAccess()) { Application.Current.Dispatcher.BeginInvoke(OnPartsChanged); return; }
        RefreshLists();
        OnPropertyChanged(nameof(IsSaved));
        RaiseCans();
    }

    // --- run procedure + moves (Layer 2) ---

    private async Task RunProcedureAsync()
    {
        if (!IsSaved) return;
        _procCts = new CancellationTokenSource();
        OnPropertyChanged(nameof(ProcedureRunning));
        var ct = _procCts.Token;
        await RunBusyAsync("Part alignment run", async () =>
        {
            try
            {
                SetStatus("Run: locating E1 / E2, then the objects…");
                var r = await _host.Process.RunPartAlignmentAsync(_name, ManualMarkAsync, ct).ConfigureAwait(true);
                if (!r.IsSuccess) { SetStatus($"Run: {r.Message}", true); return; }
                int measured = r.Value.Objects.Count(o => o.Measured);
                var missed = r.Value.Objects.Where(o => !o.Measured).Select(o => o.Name).ToList();
                SetStatus(missed.Count == 0
                    ? $"Run done — frame measured, {measured}/{r.Value.Objects.Length} object(s) detected."
                    : $"Run done — frame measured; not detected (taught positions shown): {string.Join(", ", missed)}.", missed.Count > 0);
            }
            catch (OperationCanceledException) { SetStatus("Run cancelled.", true); }
            finally
            {
                _manualMark = null;
                MarkTarget = "";
                _procCts?.Dispose();
                _procCts = null;
                OnPropertyChanged(nameof(ProcedureRunning));
            }
        });
    }

    /// <summary>Layer 2 asks for an operator click: arm the mark, wait for <see cref="MarkAsync"/>.</summary>
    private async Task<PartPoint?> ManualMarkAsync(string label, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<PartPoint?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            _manualMark = tcs;
            MarkTarget = label;
            SetStatus($"Run: click {label} on the feed…");
        });
        using (ct.Register(() => tcs.TrySetResult(null)))
        {
            var pt = await tcs.Task.ConfigureAwait(false);
            _manualMark = null;
            return pt;
        }
    }

    public void CancelProcedure()
    {
        try { _procCts?.Cancel(); } catch (ObjectDisposedException) { }
        _manualMark?.TrySetResult(null);
    }

    private Task CameraToFiducialAsync(int i)
        => RunBusyAsync($"Camera → E{i}", async () =>
        {
            var r = await _host.Process.MoveCameraToPartFiducialAsync(_name, i).ConfigureAwait(true);
            SetStatus(r.IsSuccess ? $"Camera on E{i}." : r.Message, !r.IsSuccess);
        });

    private Task CameraToObjectAsync()
        => _selectedObject == null ? Task.CompletedTask : CameraToNamedObjectAsync(_selectedObject.Name);

    /// <summary>Camera → a saved object by name (+ offset when <see cref="ApplyOffset"/>); used by the Camera moves list.</summary>
    private Task CameraToNamedObjectAsync(string objectName)
        => RunBusyAsync($"Camera → {objectName}", async () =>
        {
            var r = await _host.Process.MoveCameraToPartObjectAsync(_name, objectName, _applyOffset).ConfigureAwait(true);
            SetStatus(r.IsSuccess ? $"Camera on {objectName}{(_applyOffset ? " + offset" : "")}." : r.Message, !r.IsSuccess);
        });

    /// <summary>Keeps the Camera moves list in step with the saved part's snapshot (rows are reused by name).</summary>
    private void SyncMoveTargets(PartSnapshot? snap)
    {
        var wanted = snap?.Objects ?? Array.Empty<PartSnapshotObject>();
        if (MoveTargets.Count != wanted.Count || !MoveTargets.Select(r => r.Name).SequenceEqual(wanted.Select(o => o.Name), StringComparer.OrdinalIgnoreCase))
        {
            MoveTargets.Clear();
            foreach (var o in wanted) MoveTargets.Add(new PartMoveRow(o.Name, o.IsLine, CameraToNamedObjectAsync));
        }
        for (int i = 0; i < wanted.Count; i++)
        {
            var o = wanted[i];
            MoveTargets[i].State = o.Measured ? "measured"
                : snap!.FromMeasurement ? "not detected — taught" : "taught";
        }
    }

    /// <summary>
    /// The unsaved edit markers for the view: E1 / E2 as taught here (gantry frame) and the
    /// selected object's points through the taught frame. Saved parts are drawn by the overlay.
    /// </summary>
    public IEnumerable<(string Label, double X, double Y, bool Fiducial)> EditMarkers()
    {
        if (_e1 != null) yield return ("E1", _e1.X, _e1.Y, true);
        if (_e2 != null) yield return ("E2", _e2.X, _e2.Y, true);
        var frame = Frame;
        var obj = _selectedObject;
        if (frame == null || obj == null) yield break;
        for (int i = 0; i < obj.Points.Length; i++)
        {
            if (obj.Points[i].Point is not { } p) continue;
            var (gx, gy) = frame.ToGlobal(p.LocalX, p.LocalY);
            yield return ($"{obj.Name} P{i + 1}", gx, gy, false);
        }
    }

    // --- helpers ---

    private Task SettleAsync(CancellationToken ct)
    {
        int ms = TryNum(_settleMs, out var v) && v > 0 ? (int)Math.Min(v, 60_000) : 0;
        return ms > 0 ? Task.Delay(ms, ct) : Task.CompletedTask;
    }

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
        foreach (var c in new AsyncRelayCommand[]
                 {
                     ApplyExposureCommand, TeachE1CamCommand, TeachE2CamCommand, MoveE1Command, MoveE2Command,
                     RunE1Command, RunE2Command, TeachP1CamCommand, TeachP2CamCommand, MoveP1Command, MoveP2Command,
                     RunP1Command, RunP2Command, SaveCommand, SaveAndBackCommand, RunProcedureCommand, CameraToE1Command, CameraToE2Command,
                     CameraToObjectCommand,
                 })
            c.RaiseCanExecuteChanged();
        foreach (var c in new RelayCommand[]
                 {
                     ReadExposureCommand, MarkE1Command, MarkE2Command, RemoveObjectCommand, MarkP1Command, MarkP2Command,
                     DeleteCommand, CancelProcedureCommand,
                 })
            c.RaiseCanExecuteChanged();
    }

    private void SetStatus(string text, bool warn = false) { Status = text; StatusWarn = warn; }

    private static string CamText(MotionPosition? p)
        => p == null ? "camera position not taught" : $"cam X {p.X:0.0000}  Y {p.Y:0.0000}  Z {p.Z:0.0000}";

    private static string PointText(PartPoint? p)
        => p == null ? "not marked" : $"({p.X:0.0000}, {p.Y:0.0000}) mm  px ({p.PixelX:0}, {p.PixelY:0})  [{p.Source}]";

    private static bool TryNum(string s, out double v)
        => double.TryParse(s?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelProcedure();
        _host.Process.PartAlignmentsChanged -= OnPartsChanged;
        _active = false;
        UpdateLease();
        Camera.Dispose();
    }
}
