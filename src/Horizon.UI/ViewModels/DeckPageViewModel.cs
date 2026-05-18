using System.Collections.ObjectModel;
using System.Windows.Threading;
using Horizon.Engine;
using Horizon.UI.Mvvm;
using Horizon.Workflow;

namespace Horizon.UI.ViewModels;

/// <summary>
/// The Process Sequence page — a deck editor and runner. A deck is a linear
/// chain of process programs; each entry has an optional transition condition.
/// Runs via <see cref="IProcessService.RunDeck"/>, showing per-program status
/// from <see cref="IProcessService.DeckStepEvent"/>.
/// </summary>
public sealed class DeckPageViewModel : PageViewModel
{
    private readonly EngineHost _host;
    private readonly Dispatcher _dispatcher;

    private string? _selectedProduct;
    private string? _selectedDeck;
    private string _deckName = "New Deck";
    private bool _isRunning;
    private string _status = "—";
    private CancellationTokenSource? _runCts;
    private IReadOnlyList<string> _programs = Array.Empty<string>();

    public DeckPageViewModel(EngineHost host)
    {
        _host = host;
        _dispatcher = Dispatcher.CurrentDispatcher;
        NewDeckCommand = new RelayCommand(NewDeck);
        SaveDeckCommand = new RelayCommand(SaveDeck);
        AddEntryCommand = new RelayCommand(AddEntry);
        RunCommand = new AsyncRelayCommand(RunAsync, () => Entries.Count > 0);
        StopCommand = new RelayCommand(() => _runCts?.Cancel(), () => _isRunning);
    }

    public override string Title => "Process Sequence";

    /// <summary>All configured products — the toolbar picker loads the chosen one.</summary>
    public IReadOnlyList<string> Products => _host.Process.Products;

    /// <summary>The loaded product whose decks / programs are shown.</summary>
    public string? SelectedProduct
    {
        get => _selectedProduct;
        set
        {
            if (!SetField(ref _selectedProduct, value) || value == null) return;
            var result = _host.Process.LoadProduct(value);
            Status = result.IsSuccess ? $"Product loaded: {value}" : result.Message;
            RefreshForProduct();
        }
    }

    /// <summary>Saved deck names in the active product.</summary>
    public ObservableCollection<string> Decks { get; } = new();

    /// <summary>The editable entries of the current deck.</summary>
    public ObservableCollection<DeckEntryViewModel> Entries { get; } = new();

    /// <summary>Live execution log — workflow lines while a deck runs.</summary>
    public ObservableCollection<string> Log { get; } = new();

    public string? SelectedDeck
    {
        get => _selectedDeck;
        set { if (SetField(ref _selectedDeck, value) && value != null) LoadDeck(value); }
    }

    /// <summary>Name the deck saves under.</summary>
    public string DeckName { get => _deckName; set => SetField(ref _deckName, value); }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!SetField(ref _isRunning, value)) return;
            RunCommand.RaiseCanExecuteChanged();
            StopCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Last action result, shown in the toolbar.</summary>
    public string Status { get => _status; private set => SetField(ref _status, value); }

    public RelayCommand NewDeckCommand { get; }
    public RelayCommand SaveDeckCommand { get; }
    public RelayCommand AddEntryCommand { get; }
    public AsyncRelayCommand RunCommand { get; }
    public RelayCommand StopCommand { get; }

    public override void OnActivated()
    {
        OnPropertyChanged(nameof(Products));
        // The active product was already loaded by ProcessService — reflect it
        // in the picker without re-triggering a load.
        _selectedProduct = _host.Process.ActiveProduct;
        OnPropertyChanged(nameof(SelectedProduct));
        _programs = _host.Process.ListPrograms();
        RefreshDecks();
        _host.Process.DeckStepEvent += OnDeckStep;
        _host.Process.LogEmitted += OnLog;
    }

    public override void OnDeactivated()
    {
        _host.Process.DeckStepEvent -= OnDeckStep;
        _host.Process.LogEmitted -= OnLog;
    }

    /// <summary>Reloads decks + programs after the picked product changes.</summary>
    private void RefreshForProduct()
    {
        _programs = _host.Process.ListPrograms();
        RefreshDecks();
        Entries.Clear();
        _selectedDeck = null;
        OnPropertyChanged(nameof(SelectedDeck));
        RunCommand.RaiseCanExecuteChanged();
    }

    private void RefreshDecks()
    {
        Decks.Clear();
        foreach (var name in _host.Process.ListDecks())
            Decks.Add(name);
    }

    private void LoadDeck(string name)
    {
        var result = _host.Process.LoadDeck(name);
        if (!result.IsSuccess) { Status = result.Message; return; }

        DeckName = result.Value.Name;
        Entries.Clear();
        foreach (var entry in result.Value.Entries)
            Entries.Add(NewEntry(entry.Program, entry.Enabled, entry.Condition));
        Status = $"Loaded deck '{name}' — {Entries.Count} program(s).";
        RunCommand.RaiseCanExecuteChanged();
    }

    private DeckEntryViewModel NewEntry(string program, bool enabled, string condition)
        => new(_programs, RemoveEntry, MoveEntryUp, MoveEntryDown)
        {
            Program = string.IsNullOrEmpty(program) ? null : program,
            Enabled = enabled,
            Condition = condition,
        };

    private void NewDeck()
    {
        _selectedDeck = null;
        OnPropertyChanged(nameof(SelectedDeck));
        DeckName = "New Deck";
        Entries.Clear();
        Status = "New deck.";
        RunCommand.RaiseCanExecuteChanged();
    }

    private void AddEntry()
    {
        Entries.Add(NewEntry(_programs.Count > 0 ? _programs[0] : string.Empty, true, string.Empty));
        RunCommand.RaiseCanExecuteChanged();
    }

    private void RemoveEntry(DeckEntryViewModel entry)
    {
        Entries.Remove(entry);
        RunCommand.RaiseCanExecuteChanged();
    }

    private void MoveEntryUp(DeckEntryViewModel entry)
    {
        var i = Entries.IndexOf(entry);
        if (i > 0) Entries.Move(i, i - 1);
    }

    private void MoveEntryDown(DeckEntryViewModel entry)
    {
        var i = Entries.IndexOf(entry);
        if (i >= 0 && i < Entries.Count - 1) Entries.Move(i, i + 1);
    }

    private void SaveDeck()
    {
        if (string.IsNullOrWhiteSpace(DeckName)) { Status = "Deck name is required."; return; }

        var deck = BuildDeck();
        var result = _host.Process.SaveDeck(deck);
        Status = result.IsSuccess ? $"Saved deck '{deck.Name}'." : result.Message;
        if (!result.IsSuccess) return;

        RefreshDecks();
        _selectedDeck = deck.Name;
        OnPropertyChanged(nameof(SelectedDeck));
    }

    private WorkflowDeck BuildDeck()
        => new()
        {
            Name = DeckName.Trim(),
            Entries = Entries.Select(e => new DeckEntry
            {
                Program = e.Program ?? string.Empty,
                Enabled = e.Enabled,
                Condition = e.Condition ?? string.Empty,
            }).ToList(),
        };

    private async Task RunAsync()
    {
        foreach (var entry in Entries)
            entry.RunStatus = "—";
        Log.Clear();

        var deck = BuildDeck();
        _runCts = new CancellationTokenSource();
        IsRunning = true;
        Status = $"Running deck '{deck.Name}'…";
        try
        {
            var result = await _host.Process.RunDeck(deck, _runCts.Token).ConfigureAwait(true);
            Status = !result.IsSuccess
                ? $"Deck stopped — {result.Message}"
                : string.IsNullOrEmpty(result.Message)
                    ? $"Deck completed: {deck.Name}"
                    : result.Message;   // controlled stop (a transition condition was false)
        }
        catch (Exception ex)
        {
            Status = $"Deck error: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    private void OnDeckStep(DeckProgress progress)
    {
        if (_dispatcher.CheckAccess())
            Apply(progress);
        else
            _dispatcher.BeginInvoke(() => Apply(progress));

        void Apply(DeckProgress p)
        {
            if (p.Index < 0 || p.Index >= Entries.Count) return;
            Entries[p.Index].RunStatus = !p.Completed
                ? "Running"
                : p.Success ? "Passed" : "Failed";
        }
    }

    private const int MaxLogLines = 500;

    private void OnLog(LogEntry entry)
    {
        if (_dispatcher.CheckAccess())
            Append(entry);
        else
            _dispatcher.BeginInvoke(() => Append(entry));

        void Append(LogEntry e)
        {
            Log.Add($"{e.Timestamp:HH:mm:ss}  {e.Message}");
            while (Log.Count > MaxLogLines)
                Log.RemoveAt(0);
        }
    }
}
