using System.Windows;
using FAAM.UI.Mvvm;
using FAAM.UI.Process;
using FAAM.UI.Views;

namespace FAAM.UI.ViewModels.Sections;

/// <summary>
/// One named position in the Machine-page Motion section. <b>Move</b> recalls it
/// through Layer 2 (<c>MoveToNamedPositionAsync</c>: move → camera exposure) at the
/// operator's chosen section velocity (the Apply box) rather than the position's
/// taught process velocity. <b>Save</b> re-teaches it — captures the device's
/// current pose under this name (preserving its stored velocity / camera) and
/// refreshes the displayed coordinates. <b>Edit</b> opens the per-position editor
/// so each axis value (plus velocity / camera / exposure) can be typed by hand
/// instead of jogged to — no motion is commanded either way.
/// </summary>
public sealed class NamedPositionViewModel : ObservableObject
{
    private readonly EngineHost _host;
    private readonly string _device;
    private readonly IReadOnlyList<string> _axes;
    private string _coordinates;

    public NamedPositionViewModel(EngineHost host, string device, string name, IReadOnlyList<string> axes, Func<double> velocity)
    {
        _host = host;
        _device = device;
        _axes = axes;
        Name = name;
        _coordinates = Summarize();
        // Use the Machine page's current velocity (operator's Apply box) so a manual
        // recall isn't forced to the position's slow taught process velocity.
        MoveCommand = new AsyncRelayCommand(() => host.Process.MoveToNamedPositionAsync(device, name, velocity()));
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        EditCommand = new RelayCommand(Edit);
    }

    public string Name { get; }

    /// <summary>Compact display of the stored coordinates (installed axes only).</summary>
    public string Coordinates
    {
        get => _coordinates;
        private set => SetField(ref _coordinates, value);
    }

    public AsyncRelayCommand MoveCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public RelayCommand EditCommand { get; }

    private async Task SaveAsync()
    {
        // Preserve the position's existing velocity (teach only re-captures the pose).
        var velocity = _host.Process.GetPosition(_device, Name)?.Velocity ?? 0;
        await _host.Process.TeachPositionAsync(_device, Name, velocity).ConfigureAwait(true);
        Coordinates = Summarize();
    }

    /// <summary>
    /// Opens the per-position editor seeded with this position's stored values.
    /// Save persists via Layer 2 and refreshes the row; Cancel discards.
    /// </summary>
    private void Edit()
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
            Coordinates = Summarize();
        else
            throw new InvalidOperationException(saved.Message);   // surfaced in the editor's status line
        return Task.CompletedTask;
    }

    private string Summarize()
    {
        var np = _host.Process.GetPosition(_device, Name);
        if (np == null) return string.Empty;
        var c = np.Coordinates;
        double Comp(string a) => a.ToUpperInvariant() switch
        {
            "X" => c.X, "Y" => c.Y, "Z" => c.Z, "U" => c.U, "V" => c.V, "W" => c.W, _ => 0,
        };
        return string.Join("  ", _axes.Select(a => $"{a} {Comp(a):F3}"));
    }
}
