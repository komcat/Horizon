using System.Collections.ObjectModel;
using System.Windows;
using FAAM.UI.Process;
using FAAM.UI.Views;
using Horizon.Engine;
using Horizon.Workflow;

namespace FAAM.UI.ViewModels;

/// <summary>
/// One block of the visual sequence — a single Layer 2 <see cref="WorkflowStep"/>
/// rendered as a flow-chart box with a typed parameter panel. The op is fixed at
/// creation (chosen from the palette); the operator edits its device, parameters,
/// pre/post delays and enabled flag.
/// </summary>
public sealed class ProcessBlockViewModel : ObservableObject
{
    private readonly EngineHost _host;
    private string _name = string.Empty;
    private string? _selectedDevice;
    private int _delayBeforeMs;
    private int _delayAfterMs;
    private bool _enabled = true;
    private string _runStatus = "—";
    private string _positionPreview = string.Empty;
    // Concurrent branches of a flow.parallel step. The flat editor can't edit them,
    // but it must preserve them across load→run so the step keeps its branches.
    private List<WorkflowBranch> _branches = new();

    public ProcessBlockViewModel(EngineHost host, OpSpec spec, string id)
    {
        _host = host;
        Spec = spec;
        Id = id;

        foreach (var p in spec.Params)
            Params.Add(new BlockParamViewModel(p));

        // The tile caption reflects live parameter edits, so a misconfigured no-op
        // (e.g. a Move Relative with distance 0) is visible on the block itself.
        foreach (var bp in Params)
            bp.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(BlockParamViewModel.StringValue))
                    OnPropertyChanged(nameof(ParamSummary));
            };

        EditPositionCommand = new RelayCommand(EditPosition, () => CanEditPosition());

        // Position lists are re-read from the product each time the drop-down opens, so a
        // position taught / deleted elsewhere (Machine page, a slowApproach run) shows up.
        foreach (var bp in Params.Where(p => p.Kind == ParamKind.PositionName))
        {
            var param = bp;
            bp.OptionsProvider = () => PositionNamesFor(param);
            // Pinned pickers (the align.* points on gantry-main) get Go / Teach here beside them.
            if (bp.PositionDevice != null)
                bp.SetPositionActions(() => GoToPositionAsync(param), () => TeachHereAsync(param));
        }

        // The Move block's recall preview tracks the chosen position name.
        if (PositionParam is { } posParam)
            posParam.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(BlockParamViewModel.StringValue)) return;
                RefreshPositionPreview();
                EditPositionCommand.RaiseCanExecuteChanged();
            };

        // Device-independent combo options (signal catalog, motion-device picker) — the
        // key/param carries its own device, so they're filled once here. The
        // device-change path below never fires for a device-less op like signal.read.
        FillGlobalOptions();

        // Default the device to the first of the right kind, which also seeds
        // position options + the preview through the setter.
        SelectedDevice = DeviceOptions.Count > 0 ? DeviceOptions[0] : null;
    }

    /// <summary>Rebuilds a block from a loaded step (op already matched to <paramref name="spec"/>).</summary>
    public static ProcessBlockViewModel FromStep(EngineHost host, OpSpec spec, WorkflowStep step)
    {
        var vm = new ProcessBlockViewModel(host, spec, string.IsNullOrEmpty(step.Id) ? NewId() : step.Id)
        {
            _name = step.Name,
            _delayBeforeMs = step.DelayBeforeMs,
            _delayAfterMs = step.DelayAfterMs,
            _enabled = step.Enabled,
            _branches = step.Branches,   // preserved as-is (the flat editor doesn't edit branches)
        };
        // Seed parameter values from the loaded step.
        foreach (var p in vm.Params)
            if (step.Parameters.TryGetValue(p.Key, out var v))
                p.StringValue = v;
        // Set device last so position options/preview resolve against the real device.
        vm.SelectedDevice = string.IsNullOrEmpty(step.Device)
            ? (vm.DeviceOptions.Count > 0 ? vm.DeviceOptions[0] : null)
            : step.Device;
        return vm;
    }

    /// <summary>
    /// A fresh, globally-unique block id. Assigned once at creation and preserved
    /// across save/load (see <see cref="FromStep"/>), so a block stays trackable
    /// through parameter edits — the basis for program history / traceability.
    /// </summary>
    public static string NewId() => "blk-" + Guid.NewGuid().ToString("N");

    public string Id { get; }
    public OpSpec Spec { get; }
    public string OpKey => Spec.OpKey;
    public string Display => Spec.Display;

    /// <summary>One-line command help shown atop the parameter editor (from <see cref="OpCatalogHelp"/>); null when none authored.</summary>
    public string? HelpSummary => OpCatalogHelp.For(OpKey)?.Summary;
    public bool HasHelp => !string.IsNullOrWhiteSpace(HelpSummary);

    public string Name
    {
        get => _name;
        set { if (SetField(ref _name, value)) OnPropertyChanged(nameof(Summary)); }
    }

    /// <summary>Caption shown in the flow box.</summary>
    public string Summary => string.IsNullOrWhiteSpace(_name) ? Display : $"{_name} · {Display}";

    /// <summary>
    /// A live, parameter-derived caption for the timeline tile — shows what the block
    /// actually does (e.g. "X 0 mm") so a no-op / misconfigured block is obvious.
    /// </summary>
    public string ParamSummary
    {
        get
        {
            string P(string key) => Params
                .FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase))?.StringValue ?? string.Empty;
            static string Or(string v, string fallback) => string.IsNullOrWhiteSpace(v) ? fallback : v;
            static bool Truthy(string v) => v.Trim().ToLowerInvariant() is "true" or "1" or "on" or "yes";

            return Spec.OpKey.ToLowerInvariant() switch
            {
                "motion.moverelative" => $"{Or(P("axis"), "?")} {Or(P("distance"), "?")} mm",
                "motion.movetoposition" => Or(P("position"), "(no position)"),
                "motion.setvelocity" => $"{Or(P("speed"), "?")} mm/s",
                "motion.slowapproach" => $"{Or(P("position"), "(no position)")} @ {Or(P("speed"), "0.2")} mm/s",
                "io.setoutputbyname" => $"{Or(P("pin"), "(pin?)")} → {(Truthy(P("state")) ? "ON" : "OFF")}",
                "io.alertbuzzer" => $"alert \"{Or(P("title"), "?")}\" · {Or(P("pin"), "no buzzer")}",
                "flow.wait" => $"wait {Or(P("ms"), "?")} ms",
                "flow.waitpopup" => $"countdown {Or(P("ms"), "?")} ms",
                "flow.ifsignal" => $"if {Or(P("signal"), "?")} {Or(P("op"), "?")} {Or(P("value"), "?")}",
                "flow.ifposition" => $"if {Or(P("device"), "?")} {Or(P("axis"), "?")} {Or(P("op"), "?")} {Or(P("value"), "?")}",
                "signal.read" => $"{Or(P("signal"), "?")} → {Or(P("storeAs"), P("signal"))}",
                "dut.promptserial" => "ask DUT serial",
                "dut.prompttext" => $"ask → {Or(P("key"), "note")}",
                "script.run" => string.IsNullOrWhiteSpace(P("args"))
                    ? Or(P("script"), "(no script)")
                    : $"{Or(P("script"), "(no script)")} {P("args").Trim()}",
                "flow.log" => Or(P("message"), "(message)"),
                "pneumatic.extend" => "extend",
                "pneumatic.retract" => "retract",
                "align.ucorrection" => $"U ≤ {Or(P("maxUDeg"), "2")}°",
                "align.placefau" => $"gap {Or(P("gapUm"), "panel")} µm @ {Or(P("angleDeg"), "panel")}°",
                "fau.gridscan" => $"{Or(P("axes"), "X,Y")} ±{Or(P("lineRangeUm"), "50")}/{Or(P("lineStepUm"), "10")} µm",
                "fau.powerclimb" => Truthy(Or(P("exit"), "true"))
                    ? $"{Or(P("mode"), "coarse")}{(string.IsNullOrWhiteSpace(P("exitV")) ? "" : $" > {P("exitV")} V")}"
                    : $"{Or(P("mode"), "coarse")} · no exit",
                "fau.woptimize" => $"W ±{Or(P("wStep"), "0.15")}° ≤ {Or(P("wLimitDeg"), "1.5")}°",
                _ => Display,
            };
        }
    }

    public bool ShowDevice => Spec.DeviceKind != DeviceKind.None;

    /// <summary>Device drop-down source, resolved from the op's <see cref="DeviceKind"/>.</summary>
    public IReadOnlyList<string> DeviceOptions => Spec.DeviceKind switch
    {
        DeviceKind.Motion => _host.Engine.Motion.DeviceNames,
        DeviceKind.Io => _host.Engine.IO.DeviceNames,
        DeviceKind.Pneumatic => _host.Engine.Pneumatic.SlideNames,
        DeviceKind.Camera => _host.Engine.Camera.CameraIds,
        DeviceKind.Lighting => _host.Engine.Lighting.DeviceNames,
        DeviceKind.PowerSupply => _host.Engine.PowerSupply.DeviceNames,
        _ => Array.Empty<string>(),
    };

    public string? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (!SetField(ref _selectedDevice, value)) return;
            RefreshDeviceParamOptions();
            RefreshPositionPreview();
            EditPositionCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(VelocityHint));
            OnPropertyChanged(nameof(ShowVelocityHint));
        }
    }

    /// <summary>True when this op has a velocity field and a motion device is chosen.</summary>
    public bool ShowVelocityHint
        => Params.Any(p => string.Equals(p.Key, "velocity", StringComparison.OrdinalIgnoreCase))
           && Spec.DeviceKind == DeviceKind.Motion && !string.IsNullOrEmpty(_selectedDevice);

    /// <summary>The allowed velocity range for the selected device's controller type.</summary>
    public string VelocityHint
    {
        get
        {
            if (!ShowVelocityHint) return string.Empty;
            var type = _host.Engine.Motion.GetDevice(_selectedDevice!)?.ControllerType;
            var (min, max) = MotionVelocityLimits.For(type);
            var family = string.Equals(type, "PI", StringComparison.OrdinalIgnoreCase) ? "PI" : "ACS";
            // Move-to-Position falls back to the position's stored velocity at 0; a
            // relative move has no stored position, so 0 = the device's current velocity.
            var zeroMeans = Spec.OpKey.Equals("motion.moveToPosition", StringComparison.OrdinalIgnoreCase)
                ? "0 = use the position's stored velocity. Approach applies when both its distance and velocity are > 0; the velocity is restored on arrival"
                : "0 = current velocity";
            return $"Allowed {min:0.##}–{max:0.##} mm/s ({family}); {zeroMeans}.";
        }
    }

    public ObservableCollection<BlockParamViewModel> Params { get; } = new();

    /// <summary>"inbound delay" — ms the runner waits before this step.</summary>
    public int DelayBeforeMs { get => _delayBeforeMs; set => SetField(ref _delayBeforeMs, value); }

    /// <summary>"debounce delay" — ms the runner waits after this step succeeds.</summary>
    public int DelayAfterMs { get => _delayAfterMs; set => SetField(ref _delayAfterMs, value); }

    public bool Enabled { get => _enabled; set => SetField(ref _enabled, value); }

    /// <summary>Live run state: — / Running / Passed / Failed.</summary>
    public string RunStatus { get => _runStatus; set => SetField(ref _runStatus, value); }

    private bool _isSelected;
    /// <summary>Selection highlight in the timeline grid (managed by the editor view-model).</summary>
    public bool IsSelected { get => _isSelected; set => SetField(ref _isSelected, value); }

    // --- Move-to-Position recall settings (live on the NamedPosition, not the step) ---

    /// <summary>Read-only summary of the selected position's stored velocity / camera / exposure.</summary>
    public string PositionPreview { get => _positionPreview; private set { if (SetField(ref _positionPreview, value)) OnPropertyChanged(nameof(HasPositionPreview)); } }
    public bool HasPositionPreview => !string.IsNullOrEmpty(_positionPreview);

    public RelayCommand EditPositionCommand { get; }

    /// <summary>The position-name parameter on the block's own device, if this op has one (Move to Position).</summary>
    private BlockParamViewModel? PositionParam =>
        Params.FirstOrDefault(p => p.Kind == ParamKind.PositionName && p.PositionDevice == null);

    /// <summary>
    /// Go: moves the picker's pinned device to the named position (refused while a program,
    /// Run-page step or alignment is running). Holds the alignment gate for the move so the
    /// Run-page process/alignment Start buttons stay blocked until it ends.
    /// </summary>
    private async Task GoToPositionAsync(BlockParamViewModel p)
    {
        var device = p.PositionDevice;
        var name = p.StringValue.Trim();
        if (device == null || name.Length == 0 || !CanDriveManually("Go")) return;

        if (_host.Process.GetPosition(device, name) == null)
        {
            Warn("Go", $"'{name}' is not taught on {device} yet — jog there and press Teach here first.");
            return;
        }

        using var gate = _host.Activity.Begin(ActivityKind.Alignment, $"Engineer: go to {name}");
        var r = await _host.Process.MoveToNamedPositionAsync(device, name);
        if (r.IsSuccess) Log(LogLevel.Info, $"Go: {device} moved to '{name}'.");
        else Warn("Go", $"Move {device} to '{name}' failed — {r.Message}");
    }

    /// <summary>
    /// Teach here: after a confirm showing the current pose (and the old one, when overwriting),
    /// saves the pinned device's current position under the chosen name. Keeps the position's
    /// stored velocity. Refused while anything is running — a run may be using these points.
    /// </summary>
    private async Task TeachHereAsync(BlockParamViewModel p)
    {
        var device = p.PositionDevice;
        var name = p.StringValue.Trim();
        if (device == null || name.Length == 0 || !CanDriveManually("Teach here")) return;

        var now = await _host.Engine.Motion.GetPositionAsync(device);
        if (!now.IsSuccess)
        {
            Warn("Teach here", $"Can't read {device}'s position — {now.Message}");
            return;
        }

        var existing = _host.Process.GetPosition(device, name);
        string Pose(MotionPosition m) => $"X={m.X:F4}  Y={m.Y:F4}  Z={m.Z:F4}";
        var prompt = existing == null
            ? $"Create '{name}' on {device} at the current position?\n\n{Pose(now.Value)}"
            : $"Overwrite '{name}' on {device} with the current position?\n\nNow: {Pose(now.Value)}\nWas: {Pose(existing.Coordinates)}";
        if (MessageBox.Show(prompt, "Teach here", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        var r = await _host.Process.TeachPositionAsync(device, name, existing?.Velocity ?? 0);
        if (!r.IsSuccess)
        {
            Warn("Teach here", $"Teach '{name}' on {device} failed — {r.Message}");
            return;
        }
        Log(LogLevel.Info, $"Teach here: '{name}' on {device} = {Pose(now.Value)}.");
        p.RefreshOptions();   // a newly created name shows up in the drop-down
    }

    /// <summary>False (with a message) while a program, a Run-page step or an alignment is running.</summary>
    private bool CanDriveManually(string action)
    {
        string? why = _host.Process.IsRunning ? "a program / deck is running"
            : _host.Activity.ProcessBusy ? "a Run-page process step is running"
            : _host.Activity.AlignmentBusy ? "an alignment or another move is running"
            : null;
        if (why == null) return true;
        Warn(action, $"Not now — {why}.");
        return false;
    }

    private void Warn(string title, string message)
    {
        Log(LogLevel.Warning, $"{title}: {message}");
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void Log(LogLevel level, string message)
        => _host.Log.Add(new LogEntry(level, DateTimeOffset.Now, "Engineer", message));

    /// <summary>Taught positions for a picker — its fixed device if pinned, else the block's device.</summary>
    private IEnumerable<string> PositionNamesFor(BlockParamViewModel p)
    {
        var device = p.PositionDevice ?? _selectedDevice;
        return string.IsNullOrEmpty(device) ? Array.Empty<string>() : _host.Process.GetPositionNames(device);
    }

    /// <summary>
    /// Refreshes the option lists of device-dependent combo params for the current
    /// device: PositionName ← the device's taught positions; OutputPin ← the IO
    /// device's configured output-pin names. Called whenever the device changes.
    /// </summary>
    private void RefreshDeviceParamOptions()
    {
        var device = _selectedDevice;
        foreach (var p in Params)
        {
            switch (p.Kind)
            {
                case ParamKind.PositionName:
                    p.SetOptions(PositionNamesFor(p));
                    break;
                case ParamKind.OutputPin:
                    var pins = string.IsNullOrEmpty(device)
                        ? Enumerable.Empty<string>()
                        : (_host.Engine.IO.GetDevice(device)?.OutputPins ?? Array.Empty<IoPin>())
                            .Select(op => op.Name)
                            .Where(n => !string.IsNullOrWhiteSpace(n));
                    p.SetOptions(pins);
                    break;
                case ParamKind.CameraName:
                    p.SetOptions(_host.Engine.Camera.CameraIds);   // global, device-independent
                    break;
                case ParamKind.LightName:
                    // On a lighting block: the chosen controller's lights. On any other
                    // block (e.g. Move to Position's lighting override): every controller's
                    // lights — the step resolves the owning controller itself.
                    p.SetOptions(Spec.DeviceKind == DeviceKind.Lighting
                        ? (string.IsNullOrEmpty(device)
                            ? Array.Empty<string>()
                            : _host.Engine.Lighting.GetDevice(device)?.Lights ?? Array.Empty<string>())
                        : _host.Engine.Lighting.DeviceNames
                            .SelectMany(d => _host.Engine.Lighting.GetDevice(d)?.Lights ?? Array.Empty<string>())
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList());
                    break;
            }
        }
    }

    /// <summary>
    /// Fills the device-independent combos resolved once at construction (not on device
    /// change): <see cref="ParamKind.SignalName"/> ← the unified signal catalog (engine
    /// signals + product sources such as SiPhOG); <see cref="ParamKind.MotionDeviceName"/>
    /// ← all motion devices (for the guided If-Position block, which has no lane device).
    /// </summary>
    private void FillGlobalOptions()
    {
        List<string>? signalKeys = null;
        foreach (var p in Params)
            switch (p.Kind)
            {
                case ParamKind.SignalName:
                    signalKeys ??= _host.Process.DescribeSignals().Select(d => d.Key).ToList();
                    p.SetOptions(signalKeys);
                    break;
                case ParamKind.MotionDeviceName:
                    p.SetOptions(_host.Engine.Motion.DeviceNames);
                    break;
                case ParamKind.DispenseRoutineName:
                    p.SetOptions(_host.Process.ListDispenseRoutines());
                    break;
                case ParamKind.ScriptName:
                    p.SetOptions(_host.Process.ListScripts());
                    break;
                case ParamKind.PositionName when p.PositionDevice != null:
                    p.SetOptions(PositionNamesFor(p));   // pinned device — the block's device never changes it
                    break;
            }
    }

    /// <summary>Call when the chosen position name changes — refreshes the recall preview.</summary>
    public void RefreshPositionPreview()
    {
        var pos = PositionParam;
        if (pos == null || string.IsNullOrEmpty(_selectedDevice) || string.IsNullOrWhiteSpace(pos.StringValue))
        {
            PositionPreview = string.Empty;
            return;
        }
        var np = _host.Process.GetPosition(_selectedDevice, pos.StringValue);
        if (np == null)
        {
            PositionPreview = "(not taught)";
            return;
        }
        var parts = new List<string> { $"vel {np.Velocity:0.###} mm/s" };
        if (!string.IsNullOrWhiteSpace(np.Camera))
            parts.Add($"cam {np.Camera}");
        if (np.ExposureUs is { } ex)
            parts.Add($"{ex:0.###} µs");
        PositionPreview = string.Join(" · ", parts);
    }

    private bool CanEditPosition()
        => Spec.OpKey.Equals("motion.moveToPosition", StringComparison.OrdinalIgnoreCase)
           && !string.IsNullOrEmpty(_selectedDevice)
           && !string.IsNullOrWhiteSpace(PositionParam?.StringValue);

    private void EditPosition()
    {
        var device = _selectedDevice;
        var name = PositionParam?.StringValue;
        if (device == null || string.IsNullOrWhiteSpace(name)) return;

        var info = _host.Engine.Motion.GetDevice(device);
        if (info == null) return;

        var np = _host.Process.GetPosition(device, name);
        var ctx = new PositionEditContext
        {
            Position = name,
            Device = info.Name,
            ControllerType = info.ControllerType,
            Axes = info.Axes,
            Velocity = np?.Velocity ?? 0,
            Pose = np?.Coordinates ?? MotionPosition.Zero,
            Cameras = _host.Engine.Camera.CameraIds,
            Camera = np?.Camera,
            ExposureUs = np?.ExposureUs,
        };

        new PositionEditorWindow(ctx, ApplyPositionEditAsync)
        {
            Owner = Application.Current?.MainWindow,
        }.Show();
    }

    private Task ApplyPositionEditAsync(PositionEditResult r)
    {
        _host.Process.SetPositionFull(r.Device, r.Position, r.Pose, r.Velocity, r.Camera, r.ExposureUs);
        RefreshPositionPreview();
        return Task.CompletedTask;
    }

    /// <summary>Builds the persisted step from this block.</summary>
    public WorkflowStep ToStep() => new()
    {
        Id = Id,
        Name = _name,
        Op = Spec.OpKey,
        Device = _selectedDevice ?? string.Empty,
        Enabled = _enabled,
        DelayBeforeMs = _delayBeforeMs,
        DelayAfterMs = _delayAfterMs,
        Parameters = Params
            .Where(p => !string.IsNullOrWhiteSpace(p.StringValue))
            .ToDictionary(p => p.Key, p => p.StringValue, StringComparer.Ordinal),
        Branches = _branches,
    };
}
