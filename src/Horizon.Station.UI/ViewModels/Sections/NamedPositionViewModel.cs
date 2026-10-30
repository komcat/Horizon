using System.Windows;
using Horizon.Station.UI.Process;
using Horizon.Station.UI.Views;

namespace Horizon.Station.UI.ViewModels.Sections;

/// <summary>
/// One row of the Machine-page Motion section's position grid — a named position's
/// stored per-axis values (<see cref="Values"/>, one grid column per installed axis) and
/// its <b>Map</b> flag. Move / Update act on the grid's selected row from the section
/// toolbar (<see cref="MotionSectionViewModel"/>); <see cref="Edit"/> opens the
/// per-position editor so each axis value (plus velocity / camera / exposure) can be
/// typed by hand instead of jogged to — no motion is commanded.
/// </summary>
public sealed class NamedPositionViewModel : ObservableObject
{
    private readonly EngineHost _host;
    private readonly string _device;
    private readonly IReadOnlyList<string> _axes;
    private readonly PositionMapViewModel _map;
    private string _coordinates = string.Empty;
    private IReadOnlyDictionary<string, string> _values = new Dictionary<string, string>();

    public NamedPositionViewModel(EngineHost host, string device, string name, IReadOnlyList<string> axes,
                                  PositionMapViewModel map)
    {
        _host = host;
        _device = device;
        _axes = axes;
        _map = map;
        Name = name;
        RefreshCoordinates();
    }

    public string Name { get; }

    /// <summary>Compact display of the stored coordinates (installed axes only).</summary>
    public string Coordinates
    {
        get => _coordinates;
        private set => SetField(ref _coordinates, value);
    }

    /// <summary>Stored value per installed axis, formatted for the grid (bound as <c>Values[X]</c>).</summary>
    public IReadOnlyDictionary<string, string> Values
    {
        get => _values;
        private set => SetField(ref _values, value);
    }

    /// <summary>Whether this position has a node on the position map — setting it adds / removes the node.</summary>
    public bool IsOnMap
    {
        get => _map.Contains(Name);
        set
        {
            if (value) _map.AddNode(Name);
            else _map.RemoveNode(Name);
            OnPropertyChanged();
        }
    }

    /// <summary>Re-reads the stored values (after a re-teach / edit).</summary>
    public void RefreshCoordinates()
    {
        var c = _host.Process.GetPosition(_device, Name)?.Coordinates;
        double Comp(string a) => c == null ? 0 : a.ToUpperInvariant() switch
        {
            "X" => c.X, "Y" => c.Y, "Z" => c.Z, "U" => c.U, "V" => c.V, "W" => c.W, _ => 0,
        };
        Values = _axes.ToDictionary(a => a, a => c == null ? string.Empty : Comp(a).ToString("F3"));
        Coordinates = c == null ? string.Empty : string.Join("  ", _axes.Select(a => $"{a} {Comp(a):F3}"));
    }

    /// <summary>The map changed this position's node from elsewhere (canvas remove / drop) — refresh the toggle.</summary>
    public void NotifyOnMapChanged() => OnPropertyChanged(nameof(IsOnMap));

    /// <summary>
    /// Opens the per-position editor seeded with this position's stored values.
    /// Save persists via Layer 2 and refreshes the row; Cancel discards.
    /// </summary>
    public void Edit()
    {
        var info = _host.Engine.Motion.GetDevice(_device);
        if (info == null) return;

        var existing = _host.Process.GetPosition(_device, Name);

        var ctx = new PositionEditContext
        {
            Position = Name,
            Device = info.Name,
            ControllerType = info.ControllerType,
            Axes = info.Axes,
            Velocity = existing?.Velocity ?? 0,
            Pose = existing?.Coordinates ?? _host.Engine.Motion.GetState(_device).Position,
            Cameras = _host.Engine.Camera.CameraIds,
            Camera = existing?.Camera,
            ExposureUs = existing?.ExposureUs,
        };

        new PositionEditorWindow(ctx, ApplyEditAsync)
        {
            Owner = Application.Current?.MainWindow,
        }.Show();
    }

    private Task ApplyEditAsync(PositionEditResult r)
    {
        var saved = _host.Process.SetPositionFull(
            r.Device, r.Position, r.Pose, r.Velocity, r.Camera, r.ExposureUs);
        if (saved.IsSuccess)
            RefreshCoordinates();
        else
            throw new InvalidOperationException(saved.Message);   // surfaced in the editor's status line
        return Task.CompletedTask;
    }
}
