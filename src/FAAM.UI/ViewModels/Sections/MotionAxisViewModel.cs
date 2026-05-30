using FAAM.UI.Mvvm;

namespace FAAM.UI.ViewModels.Sections;

/// <summary>
/// One axis in the Machine-page Motion section — live position plus jog −/+
/// commands. The jog distance is the section-shared step (see
/// <see cref="MotionSectionViewModel.JogStep"/>).
/// </summary>
public sealed class MotionAxisViewModel : ObservableObject
{
    private string _position = "—";

    public MotionAxisViewModel(EngineHost host, string device, string axis, Func<double> jogStep)
    {
        Name = axis;
        JogNegativeCommand = new AsyncRelayCommand(
            () => host.Engine.Motion.MoveRelativeAsync(device, axis, -jogStep()));
        JogPositiveCommand = new AsyncRelayCommand(
            () => host.Engine.Motion.MoveRelativeAsync(device, axis, jogStep()));
    }

    public string Name { get; }

    /// <summary>Live axis position from the latest poll, or "—" before the first.</summary>
    public string Position
    {
        get => _position;
        private set => SetField(ref _position, value);
    }

    public AsyncRelayCommand JogNegativeCommand { get; }
    public AsyncRelayCommand JogPositiveCommand { get; }

    /// <summary>Applies a freshly polled position — called each sampler tick.</summary>
    public void Apply(double position) => Position = position.ToString("F4");
}
