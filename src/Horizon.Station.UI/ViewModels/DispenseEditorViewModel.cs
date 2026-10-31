using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Horizon.Workflow.Dispensing;

namespace Horizon.Station.UI.ViewModels;

/// <summary>
/// View-model for the Engineer "Dispensing" tab: browse / create / edit / delete the
/// active product's dispense routines. Holds a flat set of mutable fields mapped to /
/// from the immutable <see cref="DispenseRoutine"/> record. The pattern fields drive a
/// live preview (see <see cref="PreviewPoints"/>) the view redraws on any change; the
/// motion / shot fields (FireDog's dispense_run parameters) label the needle-motion
/// drawing they sit under. Hardware and the touch height come from the machine
/// camera→needle calibration, shown read-only (<see cref="CalibrationText"/>).
/// </summary>
public sealed class DispenseEditorViewModel : ObservableObject
{
    private readonly EngineHost _host;

    private string? _selectedRoutine;
    private string _status = "—";

    // editable fields
    private string _name = "";
    private DispensePatternKind _patternKind = DispensePatternKind.Dot;
    private int _columns = 2;
    private int _rows = 2;
    private double _pitchXmm = 1.0;
    private double _pitchYmm = 1.0;
    private int _pointCount = 3;
    private double _lengthMm = 5.0;
    private double _angleDeg;
    private bool _adjust, _confirm, _verifyBack;
    private double _vTravel, _vApproach, _safetyMm, _vFinal, _gapMm, _zTrimMm, _liftMm, _vLiftoff;
    private int _beforeMs, _shotMs, _afterMs, _verifyMs;
    private string _notes = "";

    private string _calibrationText = "";
    private string _calibrationBrushKey = "Subtle";

    public DispenseEditorViewModel(EngineHost host)
    {
        _host = host;
        NewCommand = new RelayCommand(NewRoutine);
        SaveCommand = new RelayCommand(Save);
        DeleteCommand = new RelayCommand(Delete, () => SelectedRoutine != null);
        ReloadCommand = new RelayCommand(Reload);
        Reload();
        LoadInto(host.Process.NewDispenseRoutine(""));
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        host.Process.CameraNeedleCalibrationChanged += () => dispatcher.BeginInvoke(RefreshCalibration);
        RefreshCalibration();
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

    // --- 1: camera over the point, needle over it ---

    public bool Adjust { get => _adjust; set => SetField(ref _adjust, value); }

    public double VTravel
    {
        get => _vTravel;
        set
        {
            if (!SetField(ref _vTravel, value)) return;
            Raise(nameof(TraverseText));
            Raise(nameof(BackText));
            Raise(nameof(VTravelHint));
        }
    }

    public string VTravelHint => _vTravel <= 0 ? "0 = keep the device's speed" : "";

    // --- 2-4: down to the part ---

    public bool Confirm { get => _confirm; set { if (SetField(ref _confirm, value)) Raise(nameof(VerifyMsEnabled)); } }
    public double VApproach { get => _vApproach; set { if (SetField(ref _vApproach, value)) Raise(nameof(ApproachText)); } }
    public double SafetyMm { get => _safetyMm; set { if (SetField(ref _safetyMm, value)) Raise(nameof(SafetyText)); } }
    public double VFinal { get => _vFinal; set { if (SetField(ref _vFinal, value)) Raise(nameof(FinalText)); } }
    public double GapMm { get => _gapMm; set { if (SetField(ref _gapMm, value)) Raise(nameof(Final2Text)); } }

    public double ZTrimMm
    {
        get => _zTrimMm;
        set { if (SetField(ref _zTrimMm, value)) { Raise(nameof(TrimHint)); Raise(nameof(TrimWarn)); } }
    }

    /// <summary>A non-zero trim: a calibration that probably wants redoing.</summary>
    public bool TrimWarn => _zTrimMm != 0;

    public string TrimHint => _zTrimMm != 0
        ? "trim: corrects the calibrated touch height. Normally 0 with a verified calibration — re-calibrate rather than trim every routine."
        : "trim: corrects the calibrated touch height (+ = closer). Leave 0 unless this part needs it.";

    // --- 5-7: shot and up ---

    public int BeforeMs { get => _beforeMs; set { if (SetField(ref _beforeMs, value)) Raise(nameof(ShotText)); } }
    public int ShotMs { get => _shotMs; set { if (SetField(ref _shotMs, value)) Raise(nameof(ShotText)); } }
    public int AfterMs { get => _afterMs; set { if (SetField(ref _afterMs, value)) Raise(nameof(ShotText)); } }
    public double LiftMm { get => _liftMm; set { if (SetField(ref _liftMm, value)) Raise(nameof(LiftText)); } }
    public double VLiftoff { get => _vLiftoff; set { if (SetField(ref _vLiftoff, value)) Raise(nameof(LiftText)); } }

    // --- 8: back and inspect ---

    public bool VerifyBack
    {
        get => _verifyBack;
        set { if (SetField(ref _verifyBack, value)) { Raise(nameof(BackText)); Raise(nameof(VerifyMsEnabled)); } }
    }

    public int VerifyMs { get => _verifyMs; set => SetField(ref _verifyMs, value); }

    /// <summary>The auto-OK time is only read with confirm off — a confirm-on run waits for the answer.</summary>
    public bool VerifyMsEnabled => _verifyBack && !_confirm;

    // --- the drawing's labels (changing a number changes the picture) ---

    public string TraverseText => _vTravel > 0 ? F("v travel {0:F1} mm/s", _vTravel) : "v travel: keep current";
    public string ApproachText => F("v approach {0:F1} mm/s", _vApproach);
    public string SafetyText => F("safety {0:F2} mm", _safetyMm);
    public string FinalText => F("v final {0:F1} mm/s", _vFinal);
    public string Final2Text => F("stops {0:F3} above touch", _gapMm);
    public string ShotText => string.Format(CultureInfo.CurrentCulture, "{0} + shot {1} + {2} ms", _beforeMs, _shotMs, _afterMs);
    public string LiftText => _liftMm > 0
        ? string.Format(CultureInfo.CurrentCulture, "{0:F2} mm @ {1:F1} mm/s", _liftMm, _vLiftoff)
        : "0 mm: slide first";
    public string BackText => _verifyBack ? TraverseText : "verify off: stays over";

    /// <summary>The calibration this routine moves by — the touch height is not a number in the routine.</summary>
    public string CalibrationText { get => _calibrationText; private set => SetField(ref _calibrationText, value); }

    /// <summary>Theme brush key for <see cref="CalibrationText"/> — Ok / Warn / Danger.</summary>
    public string CalibrationBrushKey { get => _calibrationBrushKey; private set => SetField(ref _calibrationBrushKey, value); }

    public string Notes { get => _notes; set => SetField(ref _notes, value); }

    public RelayCommand NewCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand ReloadCommand { get; }

    /// <summary>Raised when a pattern field changes, so the view can redraw the preview.</summary>
    public event System.Action? PreviewChanged;

    /// <summary>The current pattern expanded into dot offsets (mm) — drives the preview.</summary>
    public IReadOnlyList<DispenseOffset> PreviewPoints() => BuildPattern().Expand();

    /// <summary>Re-reads the machine calibration the routine moves by.</summary>
    public void RefreshCalibration()
    {
        var c = _host.Process.GetCameraNeedleCalibration();
        string hw = $"{c.GantryDevice} · slide {c.DispenserSlide} · valve {c.ShotIoDevice}/{c.ShotPin}";
        if (!c.IsCalibrated)
        {
            CalibrationText = $"NEEDLE NOT CALIBRATED — every dispense refuses to run (Home › Calibration › Camera → Needle).   {hw}";
            CalibrationBrushKey = "Danger";
            return;
        }
        CalibrationText = string.Format(CultureInfo.CurrentCulture,
            "touch height = focused camera Z {0:+0.000;-0.000} mm (needle dZ){1}   ·   offset ({2:F3}, {3:F3}) mm   ·   {4}",
            c.Dz, c.Verified ? "" : " — calibration UNVERIFIED", c.VcamneedleX, c.VcamneedleY, hw);
        CalibrationBrushKey = c.Verified ? "Ok" : "Warn";
    }

    private void RaisePreview() => PreviewChanged?.Invoke();

    private void Raise(string name) => OnPropertyChanged(name);

    private static string F(string fmt, double v) => string.Format(CultureInfo.CurrentCulture, fmt, v);

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
        Pattern = BuildPattern(),
        Shot = new DispenseShot
        {
            Adjust = _adjust,
            VTravel = _vTravel,
            Confirm = _confirm,
            VApproach = _vApproach,
            SafetyMm = _safetyMm,
            VFinal = _vFinal,
            GapMm = _gapMm,
            ZTrimMm = _zTrimMm,
            BeforeMs = _beforeMs,
            ShotMs = _shotMs,
            AfterMs = _afterMs,
            LiftMm = _liftMm,
            VLiftoff = _vLiftoff,
            VerifyBack = _verifyBack,
            VerifyMs = _verifyMs,
        },
        Notes = string.IsNullOrWhiteSpace(_notes) ? null : _notes,
    };

    private void LoadInto(DispenseRoutine r)
    {
        Name = r.Name;
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
        Adjust = s.Adjust;
        VTravel = s.VTravel;
        Confirm = s.Confirm;
        VApproach = s.VApproach;
        SafetyMm = s.SafetyMm;
        VFinal = s.VFinal;
        GapMm = s.GapMm;
        ZTrimMm = s.ZTrimMm;
        BeforeMs = s.BeforeMs;
        ShotMs = s.ShotMs;
        AfterMs = s.AfterMs;
        LiftMm = s.LiftMm;
        VLiftoff = s.VLiftoff;
        VerifyBack = s.VerifyBack;
        VerifyMs = s.VerifyMs;
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
        RefreshCalibration();
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
