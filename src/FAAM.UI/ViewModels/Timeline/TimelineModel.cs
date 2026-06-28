using System.Collections.ObjectModel;
using FAAM.UI.Mvvm;

namespace FAAM.UI.ViewModels.Timeline;

/// <summary>
/// Hardware domain a lane belongs to — drives grouping in the left column and which
/// palette ops may drop onto the lane. <see cref="Signal"/> carries the device-less
/// read-out block; <see cref="Control"/> carries the other device-less flow ops
/// (wait / log / comment).
/// </summary>
public enum LaneDomain { Motion, Io, Pneumatic, PowerSupply, Smu, Tec, Vision, Signal, Control }

/// <summary>
/// Which conditional branch a stage's cells fall in, for the timeline tint:
/// <see cref="Then"/> (between an If and its Else/End If) shows light pink,
/// <see cref="Else"/> (between Else and End If) light yellow, <see cref="None"/> untinted.
/// </summary>
public enum ConditionRegion { None, Then, Else }

/// <summary>
/// One device row in the timeline grid. A lane is fixed (built from the engine
/// facets); its <see cref="Cells"/> hold the blocks scheduled on this device, one
/// cell per stage (index-aligned with the editor's stage list).
/// </summary>
public sealed class LaneViewModel : ObservableObject
{
    public LaneViewModel(string id, string display, LaneDomain domain, string domainLabel, bool isGroupStart)
    {
        Id = id;
        Display = display;
        Domain = domain;
        DomainLabel = domainLabel;
        IsGroupStart = isGroupStart;
    }

    /// <summary>Device id (the lane's <c>WorkflowStep.Device</c>), or <c>control</c> for the flow lane.</summary>
    public string Id { get; }

    /// <summary>Row label — the device name, or "Flow / Control".</summary>
    public string Display { get; }

    public LaneDomain Domain { get; }

    /// <summary>Domain caption shown once at the first lane of each group.</summary>
    public string DomainLabel { get; }

    /// <summary>True for the first lane of its domain — the view shows the group caption.</summary>
    public bool IsGroupStart { get; }

    private bool _isVisible = true;

    /// <summary>
    /// Whether this lane (and its column of cells) is shown. The editor hides lanes
    /// that carry no blocks unless "Show all lanes" is on — so a loaded program shows
    /// only the devices it actually uses. Bound by the header and every cell column.
    /// </summary>
    public bool IsVisible { get => _isVisible; set => SetField(ref _isVisible, value); }

    /// <summary>One cell per stage, index-aligned with the editor's <c>Stages</c>.</summary>
    public ObservableCollection<CellViewModel> Cells { get; } = new();

    /// <summary>
    /// Whether a palette op may be dropped on this lane. Device ops match by their
    /// <see cref="DeviceKind"/>; the two device-less lanes split on category — the
    /// read-out block (category "Signal") goes to the Signal lane, every other
    /// device-less op to the Control lane.
    /// </summary>
    public bool Accepts(OpSpec spec) => Domain switch
    {
        LaneDomain.Motion => spec.DeviceKind == DeviceKind.Motion,
        LaneDomain.Io => spec.DeviceKind == DeviceKind.Io,
        LaneDomain.Pneumatic => spec.DeviceKind == DeviceKind.Pneumatic,
        LaneDomain.Vision => spec.DeviceKind == DeviceKind.Camera,
        LaneDomain.Signal => spec.DeviceKind == DeviceKind.None && IsSignalOp(spec),
        LaneDomain.Control => spec.DeviceKind == DeviceKind.None && !IsSignalOp(spec),
        _ => false,
    };

    /// <summary>True for the read-out op(s) that belong on the Signal lane.</summary>
    internal static bool IsSignalOp(OpSpec spec)
        => spec.Category.Equals("Signal", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// One (lane × stage) slot. Holds the blocks scheduled on this lane in this stage —
/// usually one, but a lane may run a short sub-sequence within a stage.
/// </summary>
public sealed class CellViewModel : ObservableObject
{
    public CellViewModel(LaneViewModel lane)
    {
        Lane = lane;
        Blocks.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
    }

    public LaneViewModel Lane { get; }

    public ObservableCollection<ProcessBlockViewModel> Blocks { get; } = new();

    public bool IsEmpty => Blocks.Count == 0;

    private bool _isDropTarget;

    /// <summary>True while a drag hovers this cell and the lane accepts it — drives the drop highlight.</summary>
    public bool IsDropTarget { get => _isDropTarget; set => SetField(ref _isDropTarget, value); }

    private ConditionRegion _region;

    /// <summary>
    /// Which If/Else branch this cell's stage is in — set by the editor from the Control
    /// lane's if/else/endif nesting. Drives the light pink / yellow stage tint.
    /// </summary>
    public ConditionRegion Region { get => _region; set => SetField(ref _region, value); }
}

/// <summary>A timeline column — a parallel group ended by a barrier. Purely ordering.</summary>
public sealed class StageViewModel : ObservableObject
{
    private string _title = string.Empty;

    /// <summary>
    /// Stable id for this stage — used as the compiled <c>flow.parallel</c> step's id
    /// when the stage runs more than one lane, so the parallel group is trackable
    /// across save/load. Seeded from the saved step id on load.
    /// </summary>
    public string Uid { get; set; } = "stg-" + Guid.NewGuid().ToString("N");

    public string Title { get => _title; set => SetField(ref _title, value); }

    /// <summary>
    /// This stage's cells, one per lane, index-aligned with the editor's <c>Lanes</c> —
    /// the SAME <see cref="CellViewModel"/> objects held in <c>LaneViewModel.Cells</c>,
    /// exposed stage-major so the vertical timeline can render one row per stage. The
    /// lane-major <c>LaneViewModel.Cells</c> stays the source of truth for drag/drop and
    /// compile; this is just a second view of the same objects.
    /// </summary>
    public ObservableCollection<CellViewModel> Cells { get; } = new();

    private bool _isSelected;

    /// <summary>True when this stage is picked as the "run only this stage" selection — its header highlights.</summary>
    public bool IsSelected { get => _isSelected; set => SetField(ref _isSelected, value); }
}

/// <summary>Builds the fixed lane set from the engine facets, grouped by domain.</summary>
public static class LaneBuilder
{
    public static List<LaneViewModel> Build(EngineHost host)
    {
        var lanes = new List<LaneViewModel>();

        void AddGroup(LaneDomain domain, string label, IReadOnlyList<string> devices)
        {
            for (int i = 0; i < devices.Count; i++)
                lanes.Add(new LaneViewModel(devices[i], devices[i], domain, label, isGroupStart: i == 0));
        }

        AddGroup(LaneDomain.Motion, "Motion", host.Engine.Motion.DeviceNames);
        AddGroup(LaneDomain.Io, "IO", host.Engine.IO.DeviceNames);
        AddGroup(LaneDomain.Pneumatic, "Pneumatic", host.Engine.Pneumatic.SlideNames);
        // Visible-but-empty scaffolding lanes — no catalog ops target them yet (Phase 2).
        AddGroup(LaneDomain.PowerSupply, "PSU", host.Engine.PowerSupply.DeviceNames);
        AddGroup(LaneDomain.Smu, "SMU", host.Engine.Smu.DeviceNames);
        AddGroup(LaneDomain.Tec, "TEC", host.Engine.Tec.DeviceNames);
        AddGroup(LaneDomain.Vision, "Vision", host.Engine.Camera.CameraIds);

        // The signal lane is always present (signals span devices + product sources) —
        // it carries the device-less read-out block.
        lanes.Add(new LaneViewModel(SignalLaneId, "Signal", LaneDomain.Signal, "Signal", isGroupStart: true));

        // The flow lane is always present, even with no hardware — it carries the
        // device-less control ops (wait / log / comment).
        lanes.Add(new LaneViewModel(ControlLaneId, "Flow / Control", LaneDomain.Control, "Control", isGroupStart: true));
        return lanes;
    }

    /// <summary>Lane id for the device-less read-out (signal.read) block.</summary>
    public const string SignalLaneId = "signal";

    /// <summary>Lane id for device-less flow ops.</summary>
    public const string ControlLaneId = "control";
}
