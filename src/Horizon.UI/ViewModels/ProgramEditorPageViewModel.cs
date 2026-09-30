using System.Collections.ObjectModel;
using System.Windows.Threading;
using Horizon.Engine;
using Horizon.UI.Mvvm;
using Horizon.Workflow;

namespace Horizon.UI.ViewModels;

/// <summary>
/// The Programming page — a process-program step editor. Edits the <c>.hwf</c>
/// programs in the active product: step list with a generic operation /
/// device / parameter editor, plus Run against the engine.
/// </summary>
public sealed class ProgramEditorPageViewModel : PageViewModel
{
    private readonly EngineHost _host;
    private readonly Dispatcher _dispatcher;

    private string? _selectedProduct;
    private string? _selectedProgram;
    private string _programName = "New Program";
    private ProgramStepViewModel? _selectedStep;
    private bool _isRunning;
    private string _status = "—";
    private CancellationTokenSource? _runCts;

    public ProgramEditorPageViewModel(EngineHost host)
    {
        _host = host;
        _dispatcher = Dispatcher.CurrentDispatcher;
        NewProgramCommand = new RelayCommand(NewProgram);
        SaveProgramCommand = new RelayCommand(SaveProgram);
        AddStepCommand = new RelayCommand(AddStep);
        RemoveStepCommand = new RelayCommand(RemoveSelectedStep, () => _selectedStep != null);
        MoveStepUpCommand = new RelayCommand(() => MoveStep(-1), () => _selectedStep != null);
        MoveStepDownCommand = new RelayCommand(() => MoveStep(+1), () => _selectedStep != null);
        RunCommand = new AsyncRelayCommand(RunAsync, () => Steps.Count > 0);
        StopCommand = new RelayCommand(() => _runCts?.Cancel(), () => _isRunning);
    }

    public override string Title => "Programming";

    public IReadOnlyList<string> Products => _host.Process.Products;
    public ObservableCollection<string> Programs { get; } = new();
    public ObservableCollection<ProgramStepViewModel> Steps { get; } = new();
    public ObservableCollection<string> Log { get; } = new();

    /// <summary>Known step operations for the Op dropdown — editable, so others can be typed.</summary>
    public IReadOnlyList<string> Ops { get; } = new[]
    {
        "motion.setVelocity", "motion.moveToPosition", "motion.moveRelative", "motion.parallelMove", "motion.stop",
        "pneumatic.extend", "pneumatic.retract", "pneumatic.retractAll",
        "io.setOutput", "io.setOutputByName",
        "camera.setExposure",
        "measurement.connect", "measurement.setVoltage", "measurement.outputOn", "measurement.outputOff",
        "measurement.startPolling", "measurement.stopPolling", "measurement.readCurrent", "measurement.read",
        "laser.connect", "laser.setTemperature", "laser.setCurrent",
        "tec.connect", "tec.setTemperature", "tec.tecOn", "tec.tecOff", "tec.readTemperature", "tec.waitForTemperature",
        "smu.connect", "smu.setSourceVoltage", "smu.setSourceCurrent", "smu.setCompliance",
        "smu.outputOn", "smu.outputOff", "smu.readCurrent", "smu.readVoltage",
        "psu.connect", "psu.setVoltage", "psu.setCurrentLimit", "psu.outputOn", "psu.outputOff",
        "psu.readVoltage", "psu.readCurrent",
        "lighting.connect", "lighting.disconnect", "lighting.setIntensity",
        "lighting.setLightPercent", "lighting.setLightColor",
        "flow.wait", "flow.log", "flow.comment", "flow.loopStart", "flow.loopEnd", "flow.askUser",
        "var.set", "var.calculate",
    };

    public string? SelectedProduct
    {
        get => _selectedProduct;
        set
        {
            if (!SetField(ref _selectedProduct, value) || value == null) return;
            _host.Process.LoadProduct(value);
            Steps.Clear();
            SelectedStep = null;
            RefreshPrograms();
        }
    }

    public string? SelectedProgram
    {
        get => _selectedProgram;
        set { if (SetField(ref _selectedProgram, value) && value != null) LoadProgram(value); }
    }

    public string ProgramName { get => _programName; set => SetField(ref _programName, value); }

    public ProgramStepViewModel? SelectedStep
    {
        get => _selectedStep;
        set
        {
            if (!SetField(ref _selectedStep, value)) return;
            OnPropertyChanged(nameof(HasSelectedStep));
            OnPropertyChanged(nameof(HasNoSelectedStep));
            RemoveStepCommand.RaiseCanExecuteChanged();
            MoveStepUpCommand.RaiseCanExecuteChanged();
            MoveStepDownCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasSelectedStep => _selectedStep != null;
    public bool HasNoSelectedStep => _selectedStep == null;

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
    public RelayCommand AddStepCommand { get; }
    public RelayCommand RemoveStepCommand { get; }
    public RelayCommand MoveStepUpCommand { get; }
    public RelayCommand MoveStepDownCommand { get; }
    public AsyncRelayCommand RunCommand { get; }
    public RelayCommand StopCommand { get; }

    public override void OnActivated()
    {
        OnPropertyChanged(nameof(Products));
        _selectedProduct = _host.Process.ActiveProduct;
        OnPropertyChanged(nameof(SelectedProduct));
        RefreshPrograms();
        _host.Process.StepEvent += OnStep;
        _host.Process.LogEmitted += OnLog;
    }

    public override void OnDeactivated()
    {
        _host.Process.StepEvent -= OnStep;
        _host.Process.LogEmitted -= OnLog;
    }

    private void RefreshPrograms()
    {
        Programs.Clear();
        foreach (var name in _host.Process.ListPrograms())
            Programs.Add(name);
    }

    private void LoadProgram(string name)
    {
        var result = _host.Process.LoadProgram(name);
        if (!result.IsSuccess) { Status = result.Message; return; }

        ProgramName = result.Value.Name;
        Steps.Clear();
        foreach (var step in result.Value.Steps)
            Steps.Add(new ProgramStepViewModel(
                step.Id, step.Name, step.Op, step.Device, step.Enabled, step.Breakpoint, step.Parameters));
        SelectedStep = Steps.Count > 0 ? Steps[0] : null;
        Status = $"Loaded program '{name}' — {Steps.Count} step(s).";
        RunCommand.RaiseCanExecuteChanged();
    }

    private void NewProgram()
    {
        _selectedProgram = null;
        OnPropertyChanged(nameof(SelectedProgram));
        ProgramName = "New Program";
        Steps.Clear();
        SelectedStep = null;
        Status = "New program.";
        RunCommand.RaiseCanExecuteChanged();
    }

    private void AddStep()
    {
        var step = new ProgramStepViewModel(
            "s" + Guid.NewGuid().ToString("N")[..8],
            name: "", op: "flow.comment", device: "", enabled: true, breakpoint: false,
            Array.Empty<KeyValuePair<string, string>>());
        var at = _selectedStep != null ? Steps.IndexOf(_selectedStep) + 1 : Steps.Count;
        Steps.Insert(at, step);
        SelectedStep = step;
        RunCommand.RaiseCanExecuteChanged();
    }

    private void RemoveSelectedStep()
    {
        if (_selectedStep == null) return;
        var at = Steps.IndexOf(_selectedStep);
        Steps.Remove(_selectedStep);
        SelectedStep = Steps.Count == 0 ? null : Steps[Math.Min(at, Steps.Count - 1)];
        RunCommand.RaiseCanExecuteChanged();
    }

    private void MoveStep(int delta)
    {
        if (_selectedStep == null) return;
        var i = Steps.IndexOf(_selectedStep);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= Steps.Count) return;
        Steps.Move(i, j);
    }

    private void SaveProgram()
    {
        if (string.IsNullOrWhiteSpace(ProgramName)) { Status = "Program name is required."; return; }

        var program = BuildProgram();
        // Horizon.UI has no separate Commit UI yet, so Save records a version too
        // (commit with no note) — preserves this head's prior auto-version behaviour.
        var result = _host.Process.CommitProgram(program, null);
        Status = result.IsSuccess ? $"Saved program '{program.Name}'." : result.Message;
        if (!result.IsSuccess) return;

        RefreshPrograms();
        _selectedProgram = program.Name;
        OnPropertyChanged(nameof(SelectedProgram));
    }

    private WorkflowProgram BuildProgram()
        => new()
        {
            Name = ProgramName.Trim(),
            Steps = Steps.Select(s => new WorkflowStep
            {
                Id = s.Id,
                Name = s.Name,
                Op = s.Op,
                Device = s.Device,
                Enabled = s.Enabled,
                Breakpoint = s.Breakpoint,
                Parameters = BuildParameters(s),
            }).ToList(),
        };

    private static Dictionary<string, string> BuildParameters(ProgramStepViewModel step)
    {
        var dict = new Dictionary<string, string>();
        foreach (var p in step.Parameters)
        {
            var key = p.Key?.Trim() ?? string.Empty;
            if (key.Length > 0)
                dict[key] = p.Value ?? string.Empty;   // last wins on a duplicate key
        }
        return dict;
    }

    private async Task RunAsync()
    {
        if (IsRunning) return;

        foreach (var step in Steps)
            step.RunStatus = "—";
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
                foreach (var step in Steps)
                    if (step.RunStatus == "Running") step.RunStatus = "—";
                Status = $"Program stopped: {program.Name}";
            }
            else
            {
                Status = result.IsSuccess
                    ? $"Program completed: {program.Name}"
                    : $"Program failed — {result.Message}";
            }
        }
        catch (Exception ex)
        {
            Status = $"Program error: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    private void OnStep(WorkflowStepEvent e)
    {
        if (_dispatcher.CheckAccess())
            Apply(e);
        else
            _dispatcher.BeginInvoke(() => Apply(e));

        void Apply(WorkflowStepEvent ev)
        {
            if (ev.Index < 0 || ev.Index >= Steps.Count) return;
            Steps[ev.Index].RunStatus = !ev.Completed
                ? "Running"
                : ev.Success ? "Passed" : "Failed";
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
