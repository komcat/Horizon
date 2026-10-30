using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Horizon.Workflow.Dispensing;

namespace Horizon.Station.UI.ViewModels;

/// <summary>
/// View-model for the Engineer "Dispensing" tab: browse / create / edit / delete the
/// active product's dispense routines. Holds a flat set of mutable fields mapped to /
/// from the immutable <see cref="DispenseRoutine"/> record. The pattern fields drive a
/// live preview (see <see cref="PreviewPoints"/>) the view redraws on any change.
/// </summary>
public sealed class DispenseEditorViewModel : ObservableObject
{
    private readonly EngineHost _host;

    private string? _selectedRoutine;
    private string _status = "—";

    // editable fields
    private string _name = "";
    private string _gantryDevice = "gantry-main";
    private string _dispenserSlide = "Dispenser_Head";
    private DispensePatternKind _patternKind = DispensePatternKind.Dot;
    private int _columns = 2;
    private int _rows = 2;
    private double _pitchXmm = 1.0;
    private double _pitchYmm = 1.0;
    private int _pointCount = 3;
    private double _lengthMm = 5.0;
    private double _angleDeg;
    private int _dwellMs = 500;
    private int _outputDebounceMs = 300;
    private double _liftoffMm = 5.0;
    private double _approachMm = 10.0;
    private double _slowZVelocity = 1.0;
    private double _touchZAdjustMm;
    private string _shotIoDevice = "IOBottom";
    private string _shotPin = "Dispenser_Shot";
    private bool _returnToView;
    private string _notes = "";

    public DispenseEditorViewModel(EngineHost host)
    {
        _host = host;
        NewCommand = new RelayCommand(NewRoutine);
        SaveCommand = new RelayCommand(Save);
        DeleteCommand = new RelayCommand(Delete, () => SelectedRoutine != null);
        ReloadCommand = new RelayCommand(Reload);
        Reload();
        LoadInto(host.Process.NewDispenseRoutine(""));
    }

    public string Title => "Dispensing";

    /// <summary>Saved routine names in the active product.</summary>
    public ObservableCollection<string> Routines { get; } = new();

    /// <summary>Pattern kinds for the combo.</summary>
    public IReadOnlyList<DispensePatternKind> PatternKinds { get; } =
        new[] { DispensePatternKind.Dot, DispensePatternKind.Grid, DispensePatternKind.Line };

    public string? SelectedRoutine
    {
        get => _selectedRoutine;
        set
        {
            if (!SetField(ref _selectedRoutine, value)) return;
            DeleteCommand.RaiseCanExecuteChanged();
            if (value != null)
            {
                var r = _host.Process.LoadDispenseRoutine(value);
                if (r.IsSuccess) LoadInto(r.Value);
            }
        }
    }

    public string Status { get => _status; private set => SetField(ref _status, value); }

    public string Name { get => _name; set => SetField(ref _name, value); }
    public string GantryDevice { get => _gantryDevice; set => SetField(ref _gantryDevice, value); }
    public string DispenserSlide { get => _dispenserSlide; set => SetField(ref _dispenserSlide, value); }

    public DispensePatternKind PatternKind
    {
        get => _patternKind;
        set { if (SetField(ref _patternKind, value)) RaisePreview(); }
    }

    public int Columns { get => _columns; set { if (SetField(ref _columns, value)) RaisePreview(); } }
    public int Rows { get => _rows; set { if (SetField(ref _rows, value)) RaisePreview(); } }
    public double PitchXmm { get => _pitchXmm; set { if (SetField(ref _pitchXmm, value)) RaisePreview(); } }
    public double PitchYmm { get => _pitchYmm; set { if (SetField(ref _pitchYmm, value)) RaisePreview(); } }
    public int PointCount { get => _pointCount; set { if (SetField(ref _pointCount, value)) RaisePreview(); } }
    public double LengthMm { get => _lengthMm; set { if (SetField(ref _lengthMm, value)) RaisePreview(); } }
    public double AngleDeg { get => _angleDeg; set { if (SetField(ref _angleDeg, value)) RaisePreview(); } }

    public int DwellMs { get => _dwellMs; set => SetField(ref _dwellMs, value); }
    public int OutputDebounceMs { get => _outputDebounceMs; set => SetField(ref _outputDebounceMs, value); }
    public double LiftoffMm { get => _liftoffMm; set => SetField(ref _liftoffMm, value); }
    public double ApproachMm { get => _approachMm; set => SetField(ref _approachMm, value); }
    public double SlowZVelocity { get => _slowZVelocity; set => SetField(ref _slowZVelocity, value); }
    public double TouchZAdjustMm { get => _touchZAdjustMm; set => SetField(ref _touchZAdjustMm, value); }
    public string ShotIoDevice { get => _shotIoDevice; set => SetField(ref _shotIoDevice, value); }
    public string ShotPin { get => _shotPin; set => SetField(ref _shotPin, value); }

    /// <summary>Return the camera over the dispensed location after the last dot (for inspection).</summary>
    public bool ReturnToView { get => _returnToView; set => SetField(ref _returnToView, value); }

    public string Notes { get => _notes; set => SetField(ref _notes, value); }

    public RelayCommand NewCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand ReloadCommand { get; }

    /// <summary>Raised when a pattern field changes, so the view can redraw the preview.</summary>
    public event System.Action? PreviewChanged;

    /// <summary>The current pattern expanded into dot offsets (mm) — drives the preview.</summary>
    public IReadOnlyList<DispenseOffset> PreviewPoints() => BuildPattern().Expand();

    private void RaisePreview() => PreviewChanged?.Invoke();

    private DispensePattern BuildPattern() => new()
    {
        Kind = _patternKind,
        Columns = _columns,
        Rows = _rows,
        PitchXmm = _pitchXmm,
        PitchYmm = _pitchYmm,
        PointCount = _pointCount,
        LengthMm = _lengthMm,
        AngleDeg = _angleDeg,
    };

    private DispenseRoutine ToRoutine() => new()
    {
        Name = _name.Trim(),
        GantryDevice = _gantryDevice,
        DispenserSlide = _dispenserSlide,
        Pattern = BuildPattern(),
        Shot = new DispenseShot
        {
            DwellMs = _dwellMs,
            OutputDebounceMs = _outputDebounceMs,
            LiftoffMm = _liftoffMm,
            ApproachMm = _approachMm,
            SlowZVelocity = _slowZVelocity,
            TouchZAdjustMm = _touchZAdjustMm,
            ShotIoDevice = _shotIoDevice,
            ShotPin = _shotPin,
        },
        ReturnToView = _returnToView,
        Notes = string.IsNullOrWhiteSpace(_notes) ? null : _notes,
    };

    private void LoadInto(DispenseRoutine r)
    {
        Name = r.Name;
        GantryDevice = r.GantryDevice;
        DispenserSlide = r.DispenserSlide;
        var p = r.Pattern;
        PatternKind = p.Kind;
        Columns = p.Columns;
        Rows = p.Rows;
        PitchXmm = p.PitchXmm;
        PitchYmm = p.PitchYmm;
        PointCount = p.PointCount;
        LengthMm = p.LengthMm;
        AngleDeg = p.AngleDeg;
        var s = r.Shot;
        DwellMs = s.DwellMs;
        OutputDebounceMs = s.OutputDebounceMs;
        LiftoffMm = s.LiftoffMm;
        ApproachMm = s.ApproachMm;
        SlowZVelocity = s.SlowZVelocity;
        TouchZAdjustMm = s.TouchZAdjustMm;
        ShotIoDevice = s.ShotIoDevice;
        ShotPin = s.ShotPin;
        ReturnToView = r.ReturnToView;
        Notes = r.Notes ?? "";
        RaisePreview();
    }

    private void NewRoutine()
    {
        SelectedRoutine = null;
        LoadInto(_host.Process.NewDispenseRoutine(UniqueName("dispense")));
        Status = "New routine — edit and Save.";
    }

    private void Save()
    {
        if (string.IsNullOrWhiteSpace(_name))
        {
            Status = "Enter a routine name first.";
            return;
        }
        var r = _host.Process.SaveDispenseRoutine(ToRoutine());
        Status = r.IsSuccess ? $"Saved '{_name.Trim()}'." : $"Save failed: {r.Message}";
        if (r.IsSuccess)
        {
            Reload();
            SelectedRoutine = _name.Trim();
        }
    }

    private void Delete()
    {
        if (_selectedRoutine == null) return;
        var name = _selectedRoutine;
        var r = _host.Process.DeleteDispenseRoutine(name);
        Status = r.IsSuccess ? $"Deleted '{name}'." : $"Delete failed: {r.Message}";
        if (r.IsSuccess)
        {
            Reload();
            SelectedRoutine = null;
        }
    }

    private void Reload()
    {
        var names = _host.Process.ListDispenseRoutines();
        Routines.Clear();
        foreach (var n in names) Routines.Add(n);
    }

    private string UniqueName(string baseName)
    {
        var existing = new HashSet<string>(Routines, System.StringComparer.OrdinalIgnoreCase);
        if (!existing.Contains(baseName)) return baseName;
        for (int i = 2; i < 1000; i++)
        {
            var candidate = $"{baseName}{i}";
            if (!existing.Contains(candidate)) return candidate;
        }
        return baseName;
    }
}
