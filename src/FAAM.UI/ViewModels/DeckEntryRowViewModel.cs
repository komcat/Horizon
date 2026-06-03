using System;
using System.Collections.ObjectModel;
using FAAM.UI.Mvvm;

namespace FAAM.UI.ViewModels;

/// <summary>
/// One row of the Process Deck editor — a program reference plus the transition
/// into it. The first row (<see cref="IsFirst"/>) has no transition (it runs
/// immediately); every later row shows Auto / Delay / Condition. Move / Remove
/// delegate back to the owning <see cref="DeckEditorViewModel"/>.
/// </summary>
public sealed class DeckEntryRowViewModel : ObservableObject
{
    private readonly DeckEditorViewModel _owner;

    private int _order;
    private string? _program;
    private bool _enabled = true;
    private bool _auto;
    private double _delaySeconds = 2;
    private string _condition = string.Empty;
    private bool _showInRun = true;
    private bool _isFirst;
    private string _runState = "";

    public DeckEntryRowViewModel(DeckEditorViewModel owner, ObservableCollection<string> availablePrograms)
    {
        _owner = owner;
        AvailablePrograms = availablePrograms;

        MoveUpCommand = new RelayCommand(() => _owner.MoveUp(this));
        MoveDownCommand = new RelayCommand(() => _owner.MoveDown(this));
        RemoveCommand = new RelayCommand(() => _owner.Remove(this));
        RunProgramCommand = new AsyncRelayCommand(() => _owner.RunProgram(this), () => _owner.CanRunRow(this));
    }

    /// <summary>A fresh, globally-unique entry id, the basis for deck history / diff.</summary>
    public static string NewId() => "dge-" + Guid.NewGuid().ToString("N");

    /// <summary>Stable entry UID — assigned at creation, preserved across save / load / reorder.</summary>
    public string Uid { get; set; } = NewId();

    /// <summary>Shared program-name list backing the per-row picker.</summary>
    public ObservableCollection<string> AvailablePrograms { get; }

    /// <summary>1-based position shown on the row.</summary>
    public int Order { get => _order; set => SetField(ref _order, value); }

    public string? Program
    {
        get => _program;
        set { if (SetField(ref _program, value)) RunProgramCommand.RaiseCanExecuteChanged(); }
    }

    public bool Enabled { get => _enabled; set => SetField(ref _enabled, value); }

    /// <summary>True = auto-advance (delay + optional condition); false = wait for operator.</summary>
    public bool Auto { get => _auto; set => SetField(ref _auto, value); }

    public double DelaySeconds { get => _delaySeconds; set => SetField(ref _delaySeconds, value); }

    /// <summary>Optional boolean expression gating an auto transition (e.g. <c>sag_v &gt; 0.5</c>).</summary>
    public string Condition { get => _condition; set => SetField(ref _condition, value ?? string.Empty); }

    /// <summary>When true, this program gets a button on the Run page.</summary>
    public bool ShowInRun { get => _showInRun; set => SetField(ref _showInRun, value); }

    /// <summary>True for the first row — its transition panel is hidden (it runs immediately).</summary>
    public bool IsFirst
    {
        get => _isFirst;
        set { if (SetField(ref _isFirst, value)) OnPropertyChanged(nameof(HasTransition)); }
    }

    /// <summary>The transition editor shows for every row except the first.</summary>
    public bool HasTransition => !_isFirst;

    /// <summary>Live run marker: "" / "running" / "awaiting" / "done" / "failed".</summary>
    public string RunState { get => _runState; set => SetField(ref _runState, value); }

    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }
    public RelayCommand RemoveCommand { get; }

    /// <summary>Runs just this row's program (not the whole deck).</summary>
    public AsyncRelayCommand RunProgramCommand { get; }
}
