using System.Collections.ObjectModel;
using FAAM.UI.Mvvm;

namespace FAAM.UI.ViewModels.Timeline;

/// <summary>
/// Hardware domain a lane belongs to — drives grouping in the left column and which
/// palette ops may drop onto the lane. <see cref="Control"/> carries device-less flow
/// ops (wait / log / comment).
/// </summary>
public enum LaneDomain { Motion, Io, Pneumatic, PowerSupply, Smu, Tec, Vision, Control }

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

    /// <summary>One cell per stage, index-aligned with the editor's <c>Stages</c>.</summary>
    public ObservableCollection<CellViewModel> Cells { get; } = new();

    /// <summary>Whether a palette op may be dropped on this lane.</summary>
    public bool Accepts(DeviceKind kind) => kind switch
    {
        DeviceKind.Motion => Domain == LaneDomain.Motion,
        DeviceKind.Io => Domain == LaneDomain.Io,
        DeviceKind.Pneumatic => Domain == LaneDomain.Pneumatic,
        DeviceKind.Camera => Domain == LaneDomain.Vision,
        DeviceKind.None => Domain == LaneDomain.Control,
        _ => false,
    };
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

        // The flow lane is always present, even with no hardware — it carries the
        // device-less control ops (wait / log / comment).
        lanes.Add(new LaneViewModel(ControlLaneId, "Flow / Control", LaneDomain.Control, "Control", isGroupStart: true));
        return lanes;
    }

    /// <summary>Lane id for device-less flow ops.</summary>
    public const string ControlLaneId = "control";
}
