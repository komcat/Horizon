using Horizon.Engine;
using FAAM.UI.Mvvm;

namespace FAAM.UI.ViewModels.Sections;

/// <summary>
/// One pneumatic slide in the Machine-page Pneumatic section — live position
/// and sensor state, plus non-blocking Extend / Retract commands.
/// </summary>
public sealed class PneumaticSlideViewModel : ObservableObject
{
    private string _position = "—";
    private bool _extendedSensor;
    private bool _retractedSensor;

    public PneumaticSlideViewModel(EngineHost host, string name)
    {
        Name = name;
        ExtendCommand = new AsyncRelayCommand(() => host.Engine.Pneumatic.ExtendAsync(name));
        RetractCommand = new AsyncRelayCommand(() => host.Engine.Pneumatic.RetractAsync(name));
    }

    public string Name { get; }

    /// <summary>Slide position name (Extended / Retracted / Extending / …), or "—" before the first poll.</summary>
    public string Position
    {
        get => _position;
        private set => SetField(ref _position, value);
    }

    public bool ExtendedSensor { get => _extendedSensor; private set => SetField(ref _extendedSensor, value); }
    public bool RetractedSensor { get => _retractedSensor; private set => SetField(ref _retractedSensor, value); }

    public AsyncRelayCommand ExtendCommand { get; }
    public AsyncRelayCommand RetractCommand { get; }

    /// <summary>Applies a freshly polled state — called each sampler tick.</summary>
    public void Apply(PneumaticSlideState state)
    {
        Position = state.Valid ? state.Position.ToString() : "—";
        ExtendedSensor = state.ExtendedSensor;
        RetractedSensor = state.RetractedSensor;
    }
}
