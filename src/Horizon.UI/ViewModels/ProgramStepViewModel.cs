using System.Collections.ObjectModel;
using Horizon.UI.Mvvm;

namespace Horizon.UI.ViewModels;

/// <summary>
/// One editable step of a process program — operation, device, flags, and a
/// generic name/value parameter grid. Reorder / remove are page-level commands
/// acting on the selected step (see <see cref="ProgramEditorPageViewModel"/>).
/// </summary>
public sealed class ProgramStepViewModel : ObservableObject
{
    private string _name;
    private string _op;
    private string _device;
    private bool _enabled;
    private bool _breakpoint;
    private string _runStatus = "—";

    public ProgramStepViewModel(
        string id, string name, string op, string device, bool enabled, bool breakpoint,
        IEnumerable<KeyValuePair<string, string>> parameters)
    {
        Id = id;
        _name = name;
        _op = op;
        _device = device;
        _enabled = enabled;
        _breakpoint = breakpoint;
        foreach (var kv in parameters)
            Parameters.Add(new StepParamViewModel(kv.Key, kv.Value, RemoveParam));
        AddParamCommand = new RelayCommand(() => Parameters.Add(new StepParamViewModel("", "", RemoveParam)));
    }

    /// <summary>Stable step id, preserved across edits.</summary>
    public string Id { get; }

    public string Name
    {
        get => _name;
        set { if (SetField(ref _name, value)) OnPropertyChanged(nameof(Summary)); }
    }

    /// <summary>Operation key, e.g. <c>motion.moveToPosition</c>.</summary>
    public string Op
    {
        get => _op;
        set { if (SetField(ref _op, value)) OnPropertyChanged(nameof(Summary)); }
    }

    public string Device { get => _device; set => SetField(ref _device, value); }
    public bool Enabled { get => _enabled; set => SetField(ref _enabled, value); }
    public bool Breakpoint { get => _breakpoint; set => SetField(ref _breakpoint, value); }

    /// <summary>Live status during a run: — / Running / Passed / Failed.</summary>
    public string RunStatus { get => _runStatus; set => SetField(ref _runStatus, value); }

    /// <summary>Generic parameter rows.</summary>
    public ObservableCollection<StepParamViewModel> Parameters { get; } = new();

    /// <summary>Label shown in the step list.</summary>
    public string Summary => string.IsNullOrWhiteSpace(_name) ? _op : $"{_name}  ·  {_op}";

    public RelayCommand AddParamCommand { get; }

    private void RemoveParam(StepParamViewModel param) => Parameters.Remove(param);
}
