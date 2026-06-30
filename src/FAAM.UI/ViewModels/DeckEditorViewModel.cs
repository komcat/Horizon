using System.Collections.ObjectModel;
using System.Windows.Threading;
using FAAM.UI.Mvvm;
using Horizon.Common;
using Horizon.Engine;
using Horizon.Workflow;

namespace FAAM.UI.ViewModels;

/// <summary>
/// The Engineer page's <b>Process Deck</b> tab — authors a deck (an ordered
/// collection of programs with a transition rule between each). Each row is a
/// program reference plus, for every row after the first, how the deck advances
/// into it: <b>Auto off</b> = pause and wait for the operator to activate it;
/// <b>Auto on</b> = wait a delay (and an optional condition) then run. Round-trips
/// a Layer 2 <see cref="WorkflowDeck"/> and runs it through <c>IProcessService</c>.
/// </summary>
public sealed class DeckEditorViewModel : ObservableObject
{
    private const int MaxLogLines = 500;

    private readonly EngineHost _host;
    private readonly Dispatcher _dispatcher;

    private string _deckUid = NewDeckId();
    private string _deckName = "New Deck";
    private string? _selectedDeck;
    private string _commitNote = string.Empty;
    private string _currentVersionText = "—";
    private string _status = "—";
    private bool _isRunning;
    private bool _isAwaiting;
    private bool _isDirty;
    private bool _suppressDirty;   // true while loading / resetting, so it doesn't count as an edit
    private bool _subscribed;
    private CancellationTokenSource? _runCts;

    /// <summary>A fresh, globally-unique deck id, the basis for deck history / diff.</summary>
    private static string NewDeckId() => "deck-" + System.Guid.NewGuid().ToString("N");

    public DeckEditorViewModel(EngineHost host)
    {
        _host = host;
        _dispatcher = Dispatcher.CurrentDispatcher;

        NewCommand = new RelayCommand(NewDeck, () => !_isRunning);
        SaveCommand = new RelayCommand(SaveDeck, () => !_isRunning);
        CommitCommand = new RelayCommand(CommitDeck, () => !_isRunning);
        DeleteCommand = new RelayCommand(DeleteDeck, () => !_isRunning && !string.IsNullOrWhiteSpace(_selectedDeck));
        AddProgramCommand = new RelayCommand(AddProgram, () => !_isRunning);
        RunCommand = new AsyncRelayCommand(RunAsync, () => !_isRunning && Entries.Count > 0);
        ActivateNextCommand = new RelayCommand(ActivateNext, () => _isRunning && _isAwaiting);
        StopCommand = new RelayCommand(() => _runCts?.Cancel(), () => _isRunning);
    }

    // --- bound state ---

    public ObservableCollection<DeckEntryRowViewModel> Entries { get; } = new();
    public ObservableCollection<string> Decks { get; } = new();
    public ObservableCollection<string> AvailablePrograms { get; } = new();
    public ObservableCollection<string> Log { get; } = new();

    public string DeckName { get => _deckName; set { if (SetField(ref _deckName, value)) MarkDirty(); } }

    /// <summary>
    /// Optional commit note recorded with the next save (the version repository shows it
    /// on the revision). Cleared after each successful save. Blank = no note.
    /// </summary>
    public string CommitNote { get => _commitNote; set => SetField(ref _commitNote, value); }

    /// <summary>Stable UID of the deck being edited (for history / diff / query).</summary>
    public string DeckUid => _deckUid;

    /// <summary>
    /// The loaded deck's current committed version, e.g. <c>v10</c> — or
    /// <c>uncommitted</c> when it has no committed revision yet. Shown next to the name.
    /// </summary>
    public string CurrentVersionText { get => _currentVersionText; private set => SetField(ref _currentVersionText, value); }

    /// <summary>True when the editor has unsaved changes vs the last save / load.</summary>
    public bool IsDirty { get => _isDirty; private set => SetField(ref _isDirty, value); }

    private void MarkDirty() { if (!_suppressDirty) IsDirty = true; }

    /// <summary>Props that change for display/run reasons, not edits — they must not mark dirty.</summary>
    private static readonly System.Collections.Generic.HashSet<string> TransientProps = new()
    {
        nameof(DeckEntryRowViewModel.RunState),
        nameof(DeckEntryRowViewModel.Order),
        nameof(DeckEntryRowViewModel.IsFirst),
        nameof(DeckEntryRowViewModel.HasTransition),
    };

    /// <summary>Subscribes a row so any non-transient edit marks the deck dirty.</summary>
    private void Track(DeckEntryRowViewModel row)
        => row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == null || !TransientProps.Contains(e.PropertyName))
                MarkDirty();
        };

    public string? SelectedDeck
    {
        get => _selectedDeck;
        set
        {
            if (!SetField(ref _selectedDeck, value)) return;
            DeleteCommand.RaiseCanExecuteChanged();
            if (value != null) LoadDeck(value);
        }
    }

    public string Status { get => _status; private set => SetField(ref _status, value); }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!SetField(ref _isRunning, value)) return;
            NewCommand.RaiseCanExecuteChanged();
            SaveCommand.RaiseCanExecuteChanged();
            CommitCommand.RaiseCanExecuteChanged();
            DeleteCommand.RaiseCanExecuteChanged();
            AddProgramCommand.RaiseCanExecuteChanged();
            RunCommand.RaiseCanExecuteChanged();
            ActivateNextCommand.RaiseCanExecuteChanged();
            StopCommand.RaiseCanExecuteChanged();
            foreach (var r in Entries) r.RunProgramCommand.RaiseCanExecuteChanged();
        }
    }

    // Setter-only helper (callers read the backing field _isAwaiting directly): updates
    // the flag and refreshes ActivateNext's CanExecute. A method, not a write-only property.
    private void SetAwaiting(bool value)
    {
        if (SetField(ref _isAwaiting, value)) ActivateNextCommand.RaiseCanExecuteChanged();
    }

    public RelayCommand NewCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand CommitCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand AddProgramCommand { get; }
    public AsyncRelayCommand RunCommand { get; }
    public RelayCommand ActivateNextCommand { get; }
    public RelayCommand StopCommand { get; }

    // --- page lifecycle ---

    public void Activate()
    {
        RefreshPrograms();
        RefreshDecks();
        RefreshVersionText();
        if (!_subscribed)
        {
            _host.Process.DeckStepEvent += OnDeckStep;
            _host.Process.DeckAwaitingActivation += OnAwaiting;
            _host.Process.LogEmitted += OnLog;
            _subscribed = true;
        }

        // Auto-load the product's active deck the first time the page is opened.
        if (Entries.Count == 0 && _selectedDeck == null && _host.Process.ActiveDeck is { } active)
            SelectedDeck = active;
    }

    public void Deactivate()
    {
        if (!_subscribed) return;
        _host.Process.DeckStepEvent -= OnDeckStep;
        _host.Process.DeckAwaitingActivation -= OnAwaiting;
        _host.Process.LogEmitted -= OnLog;
        _subscribed = false;
    }

    // --- deck list / programs ---

    private void RefreshPrograms()
    {
        var names = _host.Process.ListPrograms();
        if (AvailablePrograms.SequenceEqual(names)) return;
        AvailablePrograms.Clear();
        foreach (var n in names) AvailablePrograms.Add(n);
    }

    private void RefreshDecks()
    {
        var names = _host.Process.ListDecks();
        Decks.Clear();
        foreach (var n in names) Decks.Add(n);
        Status = $"{Decks.Count}/{ProcessService.MaxDecks} deck(s).";
    }

    // --- edit ---

    private void NewDeck()
    {
        _suppressDirty = true;
        _deckUid = NewDeckId();
        _selectedDeck = null;
        OnPropertyChanged(nameof(SelectedDeck));
        DeleteCommand.RaiseCanExecuteChanged();
        DeckName = "New Deck";
        Entries.Clear();
        RunCommand.RaiseCanExecuteChanged();
        _suppressDirty = false;
        IsDirty = false;
        RefreshVersionText();
        Status = "New deck.";
    }

    private void AddProgram()
    {
        var program = AvailablePrograms.FirstOrDefault() ?? string.Empty;
        var row = new DeckEntryRowViewModel(this, AvailablePrograms)
        {
            Program = program,
            Enabled = true,
            Auto = false,          // default: operator must activate the next program
            DelaySeconds = 2,
            Condition = string.Empty,
            ShowInRun = true,
        };
        Track(row);
        Entries.Add(row);
        Renumber();
        RunCommand.RaiseCanExecuteChanged();
        MarkDirty();
        Status = "Program added.";
    }

    internal void MoveUp(DeckEntryRowViewModel row)
    {
        var i = Entries.IndexOf(row);
        if (i <= 0) return;
        Entries.Move(i, i - 1);
        Renumber();
        MarkDirty();
    }

    internal void MoveDown(DeckEntryRowViewModel row)
    {
        var i = Entries.IndexOf(row);
        if (i < 0 || i >= Entries.Count - 1) return;
        Entries.Move(i, i + 1);
        Renumber();
        MarkDirty();
    }

    internal void Remove(DeckEntryRowViewModel row)
    {
        Entries.Remove(row);
        Renumber();
        RunCommand.RaiseCanExecuteChanged();
        MarkDirty();
    }

    /// <summary>Refreshes per-row order labels and the first-row flag (the first row has no transition).</summary>
    private void Renumber()
    {
        for (int i = 0; i < Entries.Count; i++)
        {
            Entries[i].Order = i + 1;
            Entries[i].IsFirst = i == 0;
        }
    }

    // --- persistence ---

    private WorkflowDeck BuildDeck() => new()
    {
        Uid = _deckUid,
        Name = DeckName.Trim(),
        Entries = Entries.Select(r => new DeckEntry
        {
            Uid = r.Uid,
            Program = r.Program ?? string.Empty,
            Enabled = r.Enabled,
            Auto = r.Auto,
            DelaySeconds = r.DelaySeconds,
            Condition = r.Condition?.Trim() ?? string.Empty,
            ShowInRun = r.ShowInRun,
        }).ToList(),
    };

    /// <summary>
    /// Loads a deck by name into the editor (always reloads, even if it's already the
    /// selected one — so the Load window can be used to discard edits). Used by the
    /// custom Load picker.
    /// </summary>
    public void LoadDeckByName(string name)
    {
        if (_isRunning) { Status = "Stop the run before loading."; return; }
        if (string.IsNullOrWhiteSpace(name)) return;
        _selectedDeck = name;
        OnPropertyChanged(nameof(SelectedDeck));
        DeleteCommand.RaiseCanExecuteChanged();
        LoadDeck(name);
    }

    /// <summary>Refreshes <see cref="CurrentVersionText"/> from the loaded deck's latest committed revision.</summary>
    private void RefreshVersionText()
    {
        var latest = string.IsNullOrEmpty(_deckUid) ? null : _host.Process.GetDeckHistory(_deckUid)?.Latest;
        CurrentVersionText = latest != null ? $"v{latest.Version}" : "uncommitted";
    }

    private void LoadDeck(string name)
    {
        var result = _host.Process.LoadDeck(name);
        if (!result.IsSuccess) { Status = result.Message; return; }

        var deck = result.Value;
        _suppressDirty = true;
        _deckUid = string.IsNullOrEmpty(deck.Uid) ? NewDeckId() : deck.Uid;
        DeckName = deck.Name;
        Entries.Clear();
        foreach (var e in deck.Entries)
        {
            var row = new DeckEntryRowViewModel(this, AvailablePrograms)
            {
                Program = e.Program,
                Enabled = e.Enabled,
                Auto = e.Auto,
                DelaySeconds = e.DelaySeconds,
                Condition = e.Condition,
                ShowInRun = e.ShowInRun,
            };
            if (!string.IsNullOrEmpty(e.Uid)) row.Uid = e.Uid;
            Track(row);
            Entries.Add(row);
        }
        Renumber();
        RunCommand.RaiseCanExecuteChanged();
        _suppressDirty = false;
        IsDirty = false;
        RefreshVersionText();
        Status = $"Loaded '{name}' — {Entries.Count} program(s).";
    }

    /// <summary>Save writes the working <c>.hwd</c> only — it does not create a version (that's Commit).</summary>
    private void SaveDeck()
    {
        if (!ValidateForSave()) return;

        var result = _host.Process.SaveDeck(BuildDeck());
        if (!result.IsSuccess) { Status = result.Message; return; }

        AfterPersisted();
        Status = $"Saved '{DeckName.Trim()}'.";
    }

    /// <summary>
    /// Commit saves the working deck <i>and</i> records a new numbered version (with the
    /// optional <see cref="CommitNote"/>) in the version repository; the note is cleared after.
    /// </summary>
    private void CommitDeck()
    {
        if (!ValidateForSave()) return;

        var result = _host.Process.CommitDeck(BuildDeck(), CommitNote);
        if (!result.IsSuccess) { Status = result.Message; return; }

        CommitNote = string.Empty;
        AfterPersisted();
        Status = $"Committed '{DeckName.Trim()}'.";
    }

    /// <summary>Shared pre-save validation (name + every row has a program). Sets <see cref="Status"/> on failure.</summary>
    private bool ValidateForSave()
    {
        if (string.IsNullOrWhiteSpace(DeckName)) { Status = "Deck name is required."; return false; }
        if (Entries.Any(r => string.IsNullOrWhiteSpace(r.Program)))
        {
            Status = "Every row needs a program selected.";
            return false;
        }
        return true;
    }

    /// <summary>Refreshes the deck list + selection and clears the dirty flag after a save/commit.</summary>
    private void AfterPersisted()
    {
        RefreshDecks();
        _selectedDeck = DeckName.Trim();
        OnPropertyChanged(nameof(SelectedDeck));
        DeleteCommand.RaiseCanExecuteChanged();
        IsDirty = false;
        RefreshVersionText();   // a commit bumps the version; a plain save leaves it
    }

    /// <summary>
    /// Saves the current deck under a new name as an <b>independent</b> deck — a fresh
    /// deck UID and fresh per-row UIDs, so its history / diff do not fork the source's.
    /// Intended for building a product-variation deck from an existing one; the new
    /// deck becomes the loaded/selected deck. Returns false (with <see cref="Status"/>
    /// set) if the name is empty, unchanged, or already in use.
    /// </summary>
    public bool SaveDeckAs(string newName)
    {
        if (_isRunning) { Status = "Stop the run before saving."; return false; }
        if (Entries.Count == 0) { Status = "Add at least one program first."; return false; }
        if (Entries.Any(r => string.IsNullOrWhiteSpace(r.Program)))
        {
            Status = "Every row needs a program selected.";
            return false;
        }

        newName = (newName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(newName)) { Status = "Enter a new deck name."; return false; }
        if (string.Equals(newName, _selectedDeck, StringComparison.OrdinalIgnoreCase))
        {
            Status = "Pick a name different from the current deck.";
            return false;
        }
        if (_host.Process.ListDecks().Contains(newName, StringComparer.OrdinalIgnoreCase))
        {
            Status = $"Deck '{newName}' already exists — pick a new name.";
            return false;
        }

        // An independent copy: fresh deck + row UIDs so history/diff don't fork the source.
        _suppressDirty = true;
        _deckUid = NewDeckId();
        foreach (var row in Entries) row.Uid = DeckEntryRowViewModel.NewId();
        DeckName = newName;
        _suppressDirty = false;

        var result = _host.Process.SaveDeck(BuildDeck());
        if (!result.IsSuccess) { Status = result.Message; return false; }

        RefreshDecks();
        _selectedDeck = newName;
        OnPropertyChanged(nameof(SelectedDeck));
        DeleteCommand.RaiseCanExecuteChanged();
        IsDirty = false;
        RefreshVersionText();   // a fresh independent deck — uncommitted until first commit
        Status = $"Saved as '{newName}'.";
        return true;
    }

    /// <summary>
    /// Imports a deck from another product into the active product under
    /// <paramref name="newName"/> (copying any programs it references that are missing
    /// here), then loads it into the editor. Returns false (with <see cref="Status"/>
    /// set) on failure.
    /// </summary>
    public bool ImportDeck(string sourceProduct, string deckName, string newName)
    {
        if (_isRunning) { Status = "Stop the run before importing."; return false; }

        var result = _host.Process.ImportDeck(sourceProduct, deckName, newName);
        if (!result.IsSuccess) { Status = result.Message; return false; }

        RefreshPrograms();          // copied programs are now selectable in the rows
        RefreshDecks();
        SelectedDeck = newName.Trim();   // load the imported deck into the editor
        Status = $"Imported '{deckName}' from '{sourceProduct}' as '{newName.Trim()}'.";
        return true;
    }

    private void DeleteDeck()
    {
        var name = _selectedDeck;
        if (string.IsNullOrWhiteSpace(name)) { Status = "Pick a deck to delete."; return; }

        var result = _host.Process.DeleteDeck(name);
        if (!result.IsSuccess) { Status = result.Message; return; }

        NewDeck();
        RefreshDecks();
        Status = $"Deleted '{name}'.";
    }

    // --- run ---

    /// <summary>A row's program may be run on its own when nothing else is running and it has a program.</summary>
    internal bool CanRunRow(DeckEntryRowViewModel row) => !_isRunning && !string.IsNullOrWhiteSpace(row.Program);

    /// <summary>Runs just one row's referenced program (not the whole deck).</summary>
    internal async Task RunProgram(DeckEntryRowViewModel row)
    {
        if (IsRunning) return;
        if (string.IsNullOrWhiteSpace(row.Program)) { Status = "Pick a program for this row first."; return; }

        var loaded = _host.Process.LoadProgram(row.Program);
        if (!loaded.IsSuccess) { Status = loaded.Message; return; }

        foreach (var r in Entries) r.RunState = "";
        Log.Clear();
        SetAwaiting(false);
        row.RunState = "running";

        _runCts = new CancellationTokenSource();
        IsRunning = true;
        Status = $"Running program '{row.Program}'…";
        try
        {
            var result = await _host.Process.RunAsync(loaded.Value, ct: _runCts.Token).ConfigureAwait(true);
            row.RunState = _runCts.IsCancellationRequested ? "" : result.IsSuccess ? "done" : "failed";
            Status = _runCts.IsCancellationRequested
                ? $"Stopped: {row.Program}"
                : result.IsSuccess ? $"Completed: {row.Program}" : $"Failed — {result.Message}";
        }
        catch (Exception ex)
        {
            row.RunState = "failed";
            Status = $"Error: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    private async Task RunAsync()
    {
        if (IsRunning || Entries.Count == 0) return;
        if (Entries.Any(r => string.IsNullOrWhiteSpace(r.Program)))
        {
            Status = "Every row needs a program selected.";
            return;
        }

        foreach (var r in Entries) r.RunState = "";
        Log.Clear();
        SetAwaiting(false);

        var deck = BuildDeck();
        _runCts = new CancellationTokenSource();
        IsRunning = true;
        Status = $"Running '{deck.Name}'…";
        try
        {
            var result = await _host.Process.RunDeck(deck, null, _runCts.Token).ConfigureAwait(true);
            Status = _runCts.IsCancellationRequested
                ? $"Stopped: {deck.Name}"
                : result.IsSuccess ? $"Completed: {deck.Name}" : $"Stopped — {result.Message}";
        }
        catch (Exception ex)
        {
            Status = $"Error: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
            SetAwaiting(false);
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    private void ActivateNext()
    {
        _host.Process.ActivateNextDeckProgram();
        SetAwaiting(false);
    }

    // --- engine events (background threads → marshal to UI) ---

    private void OnDeckStep(DeckProgress p)
    {
        if (_dispatcher.CheckAccess()) Apply(p);
        else _dispatcher.BeginInvoke(() => Apply(p));

        void Apply(DeckProgress e)
        {
            var awaiting = (e.Message ?? string.Empty).Contains("awaiting", StringComparison.OrdinalIgnoreCase);

            if (e.Index >= 0 && e.Index < Entries.Count)
            {
                var row = Entries[e.Index];
                if (!e.Completed)
                {
                    row.RunState = awaiting ? "awaiting" : "running";
                    if (!awaiting) SetAwaiting(false);
                }
                else
                {
                    row.RunState = e.Success ? "done" : "failed";
                }
            }

            if (!e.Completed && !awaiting)
                Status = $"Running {e.Index + 1}/{e.Total}: {e.Program}";
        }
    }

    private void OnAwaiting(DeckProgress p)
    {
        if (_dispatcher.CheckAccess()) Apply(p);
        else _dispatcher.BeginInvoke(() => Apply(p));

        void Apply(DeckProgress e)
        {
            SetAwaiting(true);
            if (e.Index >= 0 && e.Index < Entries.Count)
                Entries[e.Index].RunState = "awaiting";
            Status = $"Awaiting activation: {e.Program} — press Activate Next.";
        }
    }

    private void OnLog(LogEntry entry)
    {
        if (_dispatcher.CheckAccess()) Append(entry);
        else _dispatcher.BeginInvoke(() => Append(entry));

        void Append(LogEntry e)
        {
            Log.Add($"{e.Timestamp:HH:mm:ss}  {e.Message}");
            while (Log.Count > MaxLogLines)
                Log.RemoveAt(0);
        }
    }
}
