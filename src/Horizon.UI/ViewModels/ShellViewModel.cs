using Horizon.UI.Mvvm;
using Horizon.UI.Navigation;

namespace Horizon.UI.ViewModels;

/// <summary>
/// The hub-and-spoke shell. Owns the home launcher and the five pages, and
/// swaps the one shown in <see cref="CurrentView"/>. Pages are created once and
/// kept alive — a page keeps its state across visits. The shell draws the
/// Back / page-header chrome; <see cref="CurrentView"/> is the body.
/// </summary>
public sealed class ShellViewModel : ObservableObject, IShellNavigator, IDisposable
{
    private readonly HomeViewModel _home;
    private readonly MachinePageViewModel _machine;
    private object _currentView;

    public ShellViewModel(EngineHost host)
    {
        _machine = new MachinePageViewModel(host);

        var run = new PlaceholderPageViewModel(
            "Run", "Program run UI arrives in Phase 5.");
        var trainer = new SubListPageViewModel(
            "Process Trainer",
            new[] { "Trainer system", "Dispense", "Calibration (needle)" },
            "Each trainer UI arrives in Phases 8–10.");
        var programming = new PlaceholderPageViewModel(
            "Programming", "The WPF process / recipe editor arrives in Phase 6.");
        var sequence = new SubListPageViewModel(
            "Process Sequence",
            new[] { "Sequence steps" },
            "Process-sequence content arrives in Phase 11.");

        _home = new HomeViewModel(new[]
        {
            new HomeTile("Run", "Run a process program",
                new RelayCommand(() => NavigateTo(run))),
            new HomeTile("Machine", "Hardware operation & diagnostics",
                new RelayCommand(() => NavigateTo(_machine))),
            new HomeTile("Process Trainer", "Trainer, dispense, calibration",
                new RelayCommand(() => NavigateTo(trainer))),
            new HomeTile("Programming", "Edit process programs & recipes",
                new RelayCommand(() => NavigateTo(programming))),
            new HomeTile("Process Sequence", "Process-step sequencing",
                new RelayCommand(() => NavigateTo(sequence))),
        });

        _currentView = _home;
        GoHomeCommand = new RelayCommand(GoHome);
    }

    /// <summary>The home launcher or the active page.</summary>
    public object CurrentView
    {
        get => _currentView;
        private set
        {
            if (!SetField(ref _currentView, value))
                return;
            OnPropertyChanged(nameof(ShowPageHeader));
            OnPropertyChanged(nameof(PageTitle));
        }
    }

    /// <summary>True while a page (not the home launcher) is shown.</summary>
    public bool ShowPageHeader => _currentView is PageViewModel;

    /// <summary>Header title of the active page, or null on the home launcher.</summary>
    public string? PageTitle => (_currentView as PageViewModel)?.Title;

    /// <summary>Back button — returns to the home launcher.</summary>
    public RelayCommand GoHomeCommand { get; }

    public void NavigateTo(object page)
    {
        (_currentView as PageViewModel)?.OnDeactivated();
        CurrentView = page;
        (page as PageViewModel)?.OnActivated();
    }

    public void GoHome() => NavigateTo(_home);

    public void Dispose() => _machine.Dispose();
}
