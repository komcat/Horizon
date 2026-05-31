using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Threading;
using FAAM.UI.Mvvm;
using Horizon.Engine;
using Horizon.Workflow;

namespace FAAM.UI.ViewModels;

/// <summary>
/// The Engineer page — a non-coder visual process-sequence editor. Builds a
/// Layer 2 <see cref="WorkflowProgram"/> from a palette of typed commands, a
/// flow-chart of step blocks, and a per-block parameter panel, then saves
/// (<c>.hwf</c>) and runs it through <see cref="IProcessService"/>. Mirrors the
/// run / save wiring of Horizon.UI's program editor, but with a typed,
/// catalog-driven UI. FAAM is single-product, so there is no product picker — the
/// active FAAM product (already loaded by <see cref="EngineHost"/>) is used.
/// </summary>
public sealed class ProcessEditorViewModel : ObservableObject
{
    private const int MaxLogLines = 500;

    private readonly EngineHost _host;
    private readonly Dispatcher _dispatcher;

    private string _programName = "New Program";
    private string? _selectedProgram;
    private ProcessBlockViewModel? _selectedBlock;
    private bool _isRunning;
    private string _status = "—";
    private bool _subscribed;
    private bool _restoredLastProgram;
    private CancellationTokenSource? _runCts;

    public ProcessEditorViewModel(EngineHost host)
    {
        _host = host;
        _dispatcher = Dispatcher.CurrentDispatcher;

        BuildPalette();

        NewProgramCommand = new RelayCommand(NewProgram);
        SaveProgramCommand = new RelayCommand(SaveProgram);
        RemoveBlockCommand = new RelayCommand(RemoveSelectedBlock, () => _selectedBlock != null);
        MoveBlockUpCommand = new RelayCommand(() => MoveBlock(-1), () => _selectedBlock != null);
        MoveBlockDownCommand = new RelayCommand(() => MoveBlock(+1), () => _selectedBlock != null);
        RunCommand = new AsyncRelayCommand(RunAsync, () => Blocks.Count > 0 && !_isRunning);
        StopCommand = new RelayCommand(() => _runCts?.Cancel(), () => _isRunning);
    }

    // --- bound state ---

    public ObservableCollection<PaletteCategoryViewModel> Palette { get; } = new();
    public ObservableCollection<ProcessBlockViewModel> Blocks { get; } = new();
    public ObservableCollection<string> Programs { get; } = new();
    public ObservableCollection<string> Log { get; } = new();

    public string ProgramName { get => _programName; set => SetField(ref _programName, value); }

    public string? SelectedProgram
    {
        get => _selectedProgram;
        set { if (SetField(ref _selectedProgram, value) && value != null) LoadProgram(value); }
    }

    public ProcessBlockViewModel? SelectedBlock
    {
        get => _selectedBlock;
        set
        {
            if (!SetField(ref _selectedBlock, value)) return;
            OnPropertyChanged(nameof(HasSelectedBlock));
            OnPropertyChanged(nameof(HasNoSelectedBlock));
            RemoveBlockCommand.RaiseCanExecuteChanged();
            MoveBlockUpCommand.RaiseCanExecuteChanged();
            MoveBlockDownCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasSelectedBlock => _selectedBlock != null;
    public bool HasNoSelectedBlock => _selectedBlock == null;

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

    public string Status { get => _status; private set => SetField(ref _status, value); }

    public RelayCommand NewProgramCommand { get; }
    public RelayCommand SaveProgramCommand { get; }
    public RelayCommand RemoveBlockCommand { get; }
    public RelayCommand MoveBlockUpCommand { get; }
    public RelayCommand MoveBlockDownCommand { get; }
    public AsyncRelayCommand RunCommand { get; }
    public RelayCommand StopCommand { get; }

    // --- page lifecycle (called by the shell on show / hide) ---

    public void Activate()
    {
        RefreshPrograms();

        // First time the page is shown, reload the last loaded/saved program.
        if (!_restoredLastProgram)
        {
            _restoredLastProgram = true;
            var last = _host.Settings.Current.LastProgram;
            if (!string.IsNullOrEmpty(last) && Programs.Contains(last))
                SelectedProgram = last;   // triggers LoadProgram
        }

        if (_subscribed) return;
        _host.Process.StepEvent += OnStep;
        _host.Process.LogEmitted += OnLog;
        _subscribed = true;
    }

    public void Deactivate()
    {
        if (!_subscribed) return;
        _host.Process.StepEvent -= OnStep;
        _host.Process.LogEmitted -= OnLog;
        _subscribed = false;
    }

    // --- palette / blocks ---

    private void BuildPalette()
    {
        foreach (var category in OpCatalog.Categories)
        {
            var cat = new PaletteCategoryViewModel(category);
            foreach (var spec in OpCatalog.All.Where(s => s.Category == category))
                cat.Items.Add(new PaletteItemViewModel(spec, AddBlock));
            Palette.Add(cat);
        }
    }

    private void AddBlock(OpSpec spec)
    {
        var at = _selectedBlock != null ? Blocks.IndexOf(_selectedBlock) + 1 : Blocks.Count;
        InsertBlock(spec, at);
    }

    /// <summary>Inserts a new block for <paramref name="spec"/> at <paramref name="index"/> (drag-drop drop point).</summary>
    public void InsertBlock(OpSpec spec, int index)
    {
        var block = new ProcessBlockViewModel(_host, spec, ProcessBlockViewModel.NewId());
        Blocks.Insert(Math.Clamp(index, 0, Blocks.Count), block);
        SelectedBlock = block;
        RunCommand.RaiseCanExecuteChanged();
    }

    private void RemoveSelectedBlock()
    {
        if (_selectedBlock == null) return;
        var at = Blocks.IndexOf(_selectedBlock);
        Blocks.Remove(_selectedBlock);
        SelectedBlock = Blocks.Count == 0 ? null : Blocks[Math.Min(at, Blocks.Count - 1)];
        RunCommand.RaiseCanExecuteChanged();
    }

    private void MoveBlock(int delta)
    {
        if (_selectedBlock == null) return;
        var i = Blocks.IndexOf(_selectedBlock);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= Blocks.Count) return;
        Blocks.Move(i, j);
    }

    // --- programs ---

    private void RefreshPrograms()
    {
        var names = _host.Process.ListPrograms();
        if (Programs.SequenceEqual(names)) return;   // unchanged — keep the current selection
        Programs.Clear();
        foreach (var name in names)
            Programs.Add(name);
    }

    /// <summary>Persists the last loaded/saved program name so it reloads next launch.</summary>
    private void RememberLastProgram(string name)
    {
        if (string.Equals(_host.Settings.Current.LastProgram, name, StringComparison.Ordinal))
            return;
        _host.Settings.Update(_host.Settings.Current with { LastProgram = name });
        _host.Settings.Save();
    }

    private void NewProgram()
    {
        _selectedProgram = null;
        OnPropertyChanged(nameof(SelectedProgram));
        ProgramName = "New Program";
        Blocks.Clear();
        SelectedBlock = null;
        Status = "New program.";
        RunCommand.RaiseCanExecuteChanged();
    }

    private void LoadProgram(string name)
    {
        var result = _host.Process.LoadProgram(name);
        if (!result.IsSuccess) { Status = result.Message; return; }

        ProgramName = result.Value.Name;
        Blocks.Clear();
        foreach (var step in result.Value.Steps)
        {
            var spec = OpCatalog.Find(step.Op) ?? OpCatalog.ForUnknown(step.Op);
            Blocks.Add(ProcessBlockViewModel.FromStep(_host, spec, step));
        }
        SelectedBlock = Blocks.Count > 0 ? Blocks[0] : null;
        Status = $"Loaded '{name}' — {Blocks.Count} step(s).";
        RunCommand.RaiseCanExecuteChanged();
        RememberLastProgram(name);
    }

    private void SaveProgram()
    {
        if (string.IsNullOrWhiteSpace(ProgramName)) { Status = "Program name is required."; return; }

        var program = BuildProgram();
        var result = _host.Process.SaveProgram(program);
        Status = result.IsSuccess ? $"Saved '{program.Name}'." : result.Message;
        if (!result.IsSuccess) return;

        RefreshPrograms();
        _selectedProgram = program.Name;
        OnPropertyChanged(nameof(SelectedProgram));
        RememberLastProgram(program.Name);
    }

    private WorkflowProgram BuildProgram() => new()
    {
        Name = ProgramName.Trim(),
        Steps = Blocks.Select(b => b.ToStep()).ToList(),
    };

    // --- run ---

    private async Task RunAsync()
    {
        if (IsRunning) return;

        foreach (var b in Blocks)
            b.RunStatus = "—";
        Log.Clear();

        var program = BuildProgram();
        _runCts = new CancellationTokenSource();
        IsRunning = true;
        Status = $"Running '{program.Name}'…";
        try
        {
            var result = await _host.Process.RunAsync(program, ct: _runCts.Token).ConfigureAwait(true);
            if (_runCts.IsCancellationRequested)
            {
                foreach (var b in Blocks)
                    if (b.RunStatus == "Running") b.RunStatus = "—";
                Status = $"Stopped: {program.Name}";
            }
            else
            {
                Status = result.IsSuccess
                    ? $"Completed: {program.Name}"
                    : $"Failed — {result.Message}";
            }
        }
        catch (Exception ex)
        {
            Status = $"Error: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    // --- engine events (raised on background threads → marshal to UI) ---

    private void OnStep(WorkflowStepEvent e)
    {
        if (_dispatcher.CheckAccess()) Apply(e);
        else _dispatcher.BeginInvoke(() => Apply(e));

        void Apply(WorkflowStepEvent ev)
        {
            if (ev.Index < 0 || ev.Index >= Blocks.Count) return;
            Blocks[ev.Index].RunStatus = !ev.Completed
                ? "Running"
                : ev.Success ? "Passed" : "Failed";
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
