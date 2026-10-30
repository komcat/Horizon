using System.Windows;

namespace FAAM.UI.ViewModels.Sections;

/// <summary>
/// One axis in the Machine-page Motion section — live position plus jog −/+
/// commands. The jog distance is the section-shared step (see
/// <see cref="MotionSectionViewModel.JogStep"/>). On controllers with per-axis
/// enable (ACS) it also shows the enable state and an Enable/Disable toggle.
/// </summary>
public sealed class MotionAxisViewModel : ObservableObject
{
    private readonly EngineHost _host;
    private readonly string _device;
    private string _position = "—";
    private bool? _isEnabled;
    private string _targetText = string.Empty;

    public MotionAxisViewModel(EngineHost host, string device, string axis, Func<double> jogStep,
        bool showAbsoluteMove = false)
    {
        _host = host;
        _device = device;
        Name = axis;
        ShowAbsoluteMove = showAbsoluteMove;
        JogNegativeCommand = new AsyncRelayCommand(
            () => host.Engine.Motion.MoveRelativeAsync(device, axis, -jogStep()));
        JogPositiveCommand = new AsyncRelayCommand(
            () => host.Engine.Motion.MoveRelativeAsync(device, axis, jogStep()));
        ToggleEnableCommand = new AsyncRelayCommand(ToggleEnableAsync, () => _isEnabled.HasValue);
        MoveAbsoluteCommand = new AsyncRelayCommand(MoveAbsoluteAsync);
    }

    public string Name { get; }

    /// <summary>Shows the absolute-target box + Move button (the hexapod rows).</summary>
    public bool ShowAbsoluteMove { get; }

    /// <summary>Absolute target typed by the operator for <see cref="MoveAbsoluteCommand"/>.</summary>
    public string TargetText
    {
        get => _targetText;
        set => SetField(ref _targetText, value);
    }

    public AsyncRelayCommand MoveAbsoluteCommand { get; }

    /// <summary>Live axis position from the latest poll, or "—" before the first.</summary>
    public string Position
    {
        get => _position;
        private set => SetField(ref _position, value);
    }

    /// <summary>Polled enable (servo-on) state; null when the controller doesn't report one (PI).</summary>
    public bool? IsEnabled
    {
        get => _isEnabled;
        private set
        {
            if (!SetField(ref _isEnabled, value)) return;
            OnPropertyChanged(nameof(HasEnable));
            OnPropertyChanged(nameof(EnableText));
            OnPropertyChanged(nameof(ToggleText));
            ToggleEnableCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>True when this axis reports an enable state — shows the indicator + toggle.</summary>
    public bool HasEnable => _isEnabled.HasValue;

    public string EnableText => _isEnabled == true ? "Enabled" : "Disabled";

    public string ToggleText => _isEnabled == true ? "Disable" : "Enable";

    /// <summary>Last enable/disable failure for this axis, surfaced by the section.</summary>
    public event Action<string>? CommandFailed;

    public AsyncRelayCommand JogNegativeCommand { get; }
    public AsyncRelayCommand JogPositiveCommand { get; }
    public AsyncRelayCommand ToggleEnableCommand { get; }

    /// <summary>Applies a freshly polled position — called each sampler tick.</summary>
    public void Apply(double position) => Position = position.ToString("F4");

    /// <summary>Applies the freshly polled enable state (null = not reported).</summary>
    public void ApplyEnabled(bool? enabled) => IsEnabled = enabled;

    private async Task MoveAbsoluteAsync()
    {
        if (!double.TryParse(_targetText.Trim(), out var target) || !double.IsFinite(target))
        {
            CommandFailed?.Invoke($"{Name}: enter a number to move to.");
            return;
        }

        var result = await _host.Engine.Motion.MoveAbsoluteAsync(_device, Name, target).ConfigureAwait(true);
        if (!result.IsSuccess) CommandFailed?.Invoke(result.Message);
    }

    private async Task ToggleEnableAsync()
    {
        var enable = _isEnabled != true;
        if (!enable && MessageBox.Show(
                $"Disable {_device} axis {Name}? The servo releases — an unbraked axis may drift or drop.",
                "Disable axis", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        var result = await _host.Engine.Motion.SetAxisEnabledAsync(_device, Name, enable).ConfigureAwait(true);
        if (!result.IsSuccess) CommandFailed?.Invoke(result.Message);
    }
}
