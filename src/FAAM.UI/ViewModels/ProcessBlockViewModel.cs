using System.Collections.ObjectModel;
using System.Windows;
using FAAM.UI.Mvvm;
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

    public ProcessBlockViewModel(EngineHost host, OpSpec spec, string id)
    {
        _host = host;
        Spec = spec;
        Id = id;

        foreach (var p in spec.Params)
            Params.Add(new BlockParamViewModel(p));

        EditPositionCommand = new RelayCommand(EditPosition, () => CanEditPosition());

        // The Move block's recall preview tracks the chosen position name.
        if (PositionParam is { } posParam)
            posParam.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(BlockParamViewModel.StringValue)) return;
                RefreshPositionPreview();
                EditPositionCommand.RaiseCanExecuteChanged();
            };

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

    public static string NewId() => "s" + Guid.NewGuid().ToString("N")[..8];

    public string Id { get; }
    public OpSpec Spec { get; }
    public string OpKey => Spec.OpKey;
    public string Display => Spec.Display;

    public string Name
    {
        get => _name;
        set { if (SetField(ref _name, value)) OnPropertyChanged(nameof(Summary)); }
    }

    /// <summary>Caption shown in the flow box.</summary>
    public string Summary => string.IsNullOrWhiteSpace(_name) ? Display : $"{_name} · {Display}";

    public bool ShowDevice => Spec.DeviceKind != DeviceKind.None;

    /// <summary>Device drop-down source, resolved from the op's <see cref="DeviceKind"/>.</summary>
    public IReadOnlyList<string> DeviceOptions => Spec.DeviceKind switch
    {
        DeviceKind.Motion => _host.Engine.Motion.DeviceNames,
        DeviceKind.Io => _host.Engine.IO.DeviceNames,
        DeviceKind.Pneumatic => _host.Engine.Pneumatic.SlideNames,
        DeviceKind.Camera => _host.Engine.Camera.CameraIds,
        _ => Array.Empty<string>(),
    };

    public string? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (!SetField(ref _selectedDevice, value)) return;
            RefreshPositionOptions();
            RefreshPositionPreview();
            EditPositionCommand.RaiseCanExecuteChanged();
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

    // --- Move-to-Position recall settings (live on the NamedPosition, not the step) ---

    /// <summary>Read-only summary of the selected position's stored velocity / camera / exposure.</summary>
    public string PositionPreview { get => _positionPreview; private set { if (SetField(ref _positionPreview, value)) OnPropertyChanged(nameof(HasPositionPreview)); } }
    public bool HasPositionPreview => !string.IsNullOrEmpty(_positionPreview);

    public RelayCommand EditPositionCommand { get; }

    /// <summary>The position-name parameter, if this op has one (Move to Position).</summary>
    private BlockParamViewModel? PositionParam =>
        Params.FirstOrDefault(p => p.Kind == ParamKind.PositionName);

    private void RefreshPositionOptions()
    {
        var pos = PositionParam;
        if (pos == null) return;
        var names = string.IsNullOrEmpty(_selectedDevice)
            ? Array.Empty<string>()
            : _host.Process.GetPositionNames(_selectedDevice);
        pos.SetOptions(names);
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
    };
}
