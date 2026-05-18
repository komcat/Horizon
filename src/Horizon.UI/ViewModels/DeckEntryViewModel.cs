using Horizon.UI.Mvvm;

namespace Horizon.UI.ViewModels;

/// <summary>
/// One row of the deck editor — a program reference, its enabled flag, its
/// transition condition, and live run status. Reorder / remove act on the
/// parent <see cref="DeckPageViewModel"/> via injected callbacks.
/// </summary>
public sealed class DeckEntryViewModel : ObservableObject
{
    private string? _program;
    private bool _enabled = true;
    private string _condition = string.Empty;
    private string _runStatus = "—";

    public DeckEntryViewModel(
        IReadOnlyList<string> programs,
        Func<DeckEntryViewModel, Task> run,
        Action<DeckEntryViewModel> remove,
        Action<DeckEntryViewModel> moveUp,
        Action<DeckEntryViewModel> moveDown)
    {
        Programs = programs;
        RunCommand = new AsyncRelayCommand(() => run(this));
        RemoveCommand = new RelayCommand(() => remove(this));
        MoveUpCommand = new RelayCommand(() => moveUp(this));
        MoveDownCommand = new RelayCommand(() => moveDown(this));
    }

    /// <summary>Process programs available to pick for this entry.</summary>
    public IReadOnlyList<string> Programs { get; }

    public string? Program { get => _program; set => SetField(ref _program, value); }
    public bool Enabled { get => _enabled; set => SetField(ref _enabled, value); }

    /// <summary>
    /// Boolean precondition to run this entry — empty = always proceed. e.g.
    /// <c>DarkCurrent &gt; 0.0000005</c>.
    /// </summary>
    public string Condition { get => _condition; set => SetField(ref _condition, value); }

    /// <summary>Live status during a deck run: — / Running / Passed / Failed.</summary>
    public string RunStatus { get => _runStatus; set => SetField(ref _runStatus, value); }

    /// <summary>Runs just this entry's program standalone — for one-by-one testing.</summary>
    public AsyncRelayCommand RunCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }
}
