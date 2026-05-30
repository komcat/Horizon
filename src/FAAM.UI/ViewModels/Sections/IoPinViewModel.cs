using FAAM.UI.Mvvm;

namespace FAAM.UI.ViewModels.Sections;

/// <summary>
/// One digital IO pin in the Machine-page IO section. Input pins are read-only;
/// output pins carry a <see cref="ToggleCommand"/> that flips the hardware pin.
/// </summary>
public sealed class IoPinViewModel : ObservableObject
{
    private readonly Func<bool, Task>? _setState;
    private bool _isOn;

    /// <param name="setState">Drives an output pin; null for an input pin.</param>
    public IoPinViewModel(int pin, string name, Func<bool, Task>? setState)
    {
        Pin = pin;
        DisplayName = string.IsNullOrWhiteSpace(name) ? $"Pin {pin}" : name;
        _setState = setState;
        if (setState != null)
            ToggleCommand = new AsyncRelayCommand(ToggleAsync);
    }

    public int Pin { get; }
    public string DisplayName { get; }

    /// <summary>Live pin state from the latest poll.</summary>
    public bool IsOn
    {
        get => _isOn;
        private set => SetField(ref _isOn, value);
    }

    /// <summary>Flips an output pin; null on input pins.</summary>
    public AsyncRelayCommand? ToggleCommand { get; }

    /// <summary>Applies a freshly polled state — called each sampler tick.</summary>
    public void Apply(bool state) => IsOn = state;

    private Task ToggleAsync() => _setState!(!_isOn);
}
