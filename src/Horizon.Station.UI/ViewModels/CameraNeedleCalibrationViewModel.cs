using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Threading;
using Horizon.Workflow.Calibration;

namespace Horizon.Station.UI.ViewModels;

/// <summary>
/// The Calibration › Camera → Needle page's parameters and status (FireDog's Needle page): the
/// protocol's parameters laid under the needle-motion drawing (the drawing labelled with them),
/// the hardware it drives, the "clear first" moves, and what the machine currently believes —
/// the offset, whether a second pass verified it in XY and Z, and the three poses behind it.
/// </summary>
public sealed class CameraNeedleCalibrationViewModel : ObservableObject
{
    private readonly EngineHost _host;
    private readonly Dispatcher _dispatcher;

    private string _gantryDevice = "", _dispenserSlide = "", _shotIoDevice = "", _shotPin = "";
    private int _shotMs, _settleMs, _passes;
    private double _clearMm, _firstClearMm, _verifyGapMm, _liftMm, _tolMm, _zTolMm;
    private double _vTraverse, _vApproach, _vFinal, _vLiftoff;
    private string _clearMovesText = "";
    private bool _isEditable = true;
    private string _saveStatus = "";

    private string _offsetText = "", _stateText = "", _stateBrushKey = "Subtle", _calibratedText = "", _measuredText = "";

    public CameraNeedleCalibrationViewModel(EngineHost host)
    {
        _host = host;
        _dispatcher = Dispatcher.CurrentDispatcher;
        SaveCommand = new RelayCommand(Save, () => IsEditable);
        ReloadCommand = new RelayCommand(Reload, () => IsEditable);
        host.Process.CameraNeedleCalibrationChanged += () => _dispatcher.BeginInvoke(RefreshStatus);
        Reload();
    }

    public RelayCommand SaveCommand { get; }
    public RelayCommand ReloadCommand { get; }

    /// <summary>False while a calibration runs — the parameters are locked.</summary>
    public bool IsEditable
    {
        get => _isEditable;
        set
        {
            if (!SetField(ref _isEditable, value)) return;
            SaveCommand.RaiseCanExecuteChanged();
            ReloadCommand.RaiseCanExecuteChanged();
        }
    }

    public string SaveStatus { get => _saveStatus; private set => SetField(ref _saveStatus, value); }

    // --- hardware ---
    public string GantryDevice { get => _gantryDevice; set => SetField(ref _gantryDevice, value); }
    public string DispenserSlide { get => _dispenserSlide; set => SetField(ref _dispenserSlide, value); }
    public string ShotIoDevice { get => _shotIoDevice; set => SetField(ref _shotIoDevice, value); }
    public string ShotPin { get => _shotPin; set => SetField(ref _shotPin, value); }

    // --- 1 traverse ---
    public double VTraverse { get => _vTraverse; set { if (SetField(ref _vTraverse, value)) { Raise(nameof(TraverseText)); Raise(nameof(BackText)); } } }

    // --- 2-4 down to the surface ---
    public int SettleMs { get => _settleMs; set => SetField(ref _settleMs, value); }
    public double VApproach { get => _vApproach; set { if (SetField(ref _vApproach, value)) Raise(nameof(ApproachText)); } }
    public double ClearMm { get => _clearMm; set { if (SetField(ref _clearMm, value)) Raise(nameof(SafetyText)); } }
    public double VFinal { get => _vFinal; set { if (SetField(ref _vFinal, value)) Raise(nameof(FinalText)); } }
    public double VerifyGapMm { get => _verifyGapMm; set { if (SetField(ref _verifyGapMm, value)) Raise(nameof(Final2Text)); } }
    public double FirstClearMm { get => _firstClearMm; set { if (SetField(ref _firstClearMm, value)) Raise(nameof(FootnoteText)); } }

    // --- 5-7 shot and up ---
    public int ShotMs { get => _shotMs; set { if (SetField(ref _shotMs, value)) Raise(nameof(ShotText)); } }
    public double LiftMm { get => _liftMm; set { if (SetField(ref _liftMm, value)) Raise(nameof(LiftText)); } }
    public double VLiftoff { get => _vLiftoff; set { if (SetField(ref _vLiftoff, value)) Raise(nameof(LiftText)); } }

    // --- 8 inspect and check ---
    public int Passes { get => _passes; set { if (SetField(ref _passes, value)) Raise(nameof(PassesHint)); } }
    public double TolMm { get => _tolMm; set => SetField(ref _tolMm, value); }
    public double ZTolMm { get => _zTolMm; set => SetField(ref _zTolMm, value); }
    public string PassesHint => _passes <= 1 ? "one imprint — nothing checks the answer" : "pass 2+ checks XY and Z";

    /// <summary>"Clear first" moves, one "device = position" per line.</summary>
    public string ClearMovesText { get => _clearMovesText; set => SetField(ref _clearMovesText, value); }

    // --- the drawing's labels (changing a number changes the picture) ---
    public string TraverseText => F("v traverse {0:F1} mm/s", _vTraverse);
    public string ApproachText => F("v approach {0:F1} mm/s", _vApproach);
    public string SafetyText => F("safety {0:F2} mm", _clearMm);
    public string FinalText => F("v final {0:F1} mm/s", _vFinal);
    public string Final2Text => F("verify gap {0:F3} mm", _verifyGapMm);
    public string ShotText => F("shot {0} ms", _shotMs);
    public string LiftText => string.Format(CultureInfo.CurrentCulture, "{0:F2} mm @ {1:F1} mm/s", _liftMm, _vLiftoff);
    public string BackText => F("v traverse {0:F1} mm/s", _vTraverse);
    public string FootnoteText => string.Format(CultureInfo.CurrentCulture,
        "Pass 1 parks {0:F2} mm (first clear) above the previous touch and you jog the rest; pass 2+ runs the drawing " +
        "down to the verify gap and you close it. A dispense routine moves the same way with its own parameters " +
        "(Engineer › Dispensing).", _firstClearMm);

    // --- what the machine currently believes ---
    public string OffsetText { get => _offsetText; private set => SetField(ref _offsetText, value); }
    public string StateText { get => _stateText; private set => SetField(ref _stateText, value); }
    /// <summary>Theme brush key for <see cref="StateText"/> — Ok / Warn / Danger.</summary>
    public string StateBrushKey { get => _stateBrushKey; private set => SetField(ref _stateBrushKey, value); }
    public string CalibratedText { get => _calibratedText; private set => SetField(ref _calibratedText, value); }
    public string MeasuredText { get => _measuredText; private set => SetField(ref _measuredText, value); }

    public void Reload()
    {
        var c = _host.Process.GetCameraNeedleCalibration();
        GantryDevice = c.GantryDevice;
        DispenserSlide = c.DispenserSlide;
        ShotIoDevice = c.ShotIoDevice;
        ShotPin = c.ShotPin;
        ShotMs = c.ShotMs;
        SettleMs = c.SettleMs;
        ClearMm = c.ClearMm;
        FirstClearMm = c.FirstClearMm;
        VerifyGapMm = c.VerifyGapMm;
        LiftMm = c.LiftMm;
        Passes = c.Passes;
        TolMm = c.TolMm;
        ZTolMm = c.ZTolMm;
        VTraverse = c.VTraverse;
        VApproach = c.VApproach;
        VFinal = c.VFinal;
        VLiftoff = c.VLiftoff;
        ClearMovesText = string.Join(Environment.NewLine, c.ClearMoves.Select(m => $"{m.Device} = {m.Position}"));
        SaveStatus = "";
        RefreshStatus();
    }

    private void Save()
    {
        var moves = ParseClearMoves(_clearMovesText, out var bad);
        if (bad != null)
        {
            SaveStatus = $"Clear first: '{bad}' is not 'device = position'.";
            return;
        }
        var c = _host.Process.GetCameraNeedleCalibration() with
        {
            GantryDevice = _gantryDevice,
            DispenserSlide = _dispenserSlide,
            ShotIoDevice = _shotIoDevice,
            ShotPin = _shotPin,
            ShotMs = _shotMs,
            SettleMs = _settleMs,
            ClearMm = _clearMm,
            FirstClearMm = _firstClearMm,
            VerifyGapMm = _verifyGapMm,
            LiftMm = _liftMm,
            Passes = _passes,
            TolMm = _tolMm,
            ZTolMm = _zTolMm,
            VTraverse = _vTraverse,
            VApproach = _vApproach,
            VFinal = _vFinal,
            VLiftoff = _vLiftoff,
            ClearMoves = moves,
        };
        var r = _host.Process.SaveCameraNeedleParameters(c);
        if (!r.IsSuccess)
        {
            SaveStatus = "Save failed: " + r.Message;
            return;
        }
        Reload();   // shows the clamped values actually stored
        SaveStatus = "Saved.";
    }

    private static List<NeedleClearMove> ParseClearMoves(string text, out string? bad)
    {
        bad = null;
        var list = new List<NeedleClearMove>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0 || eq == line.Length - 1) { bad = line; return list; }
            list.Add(new NeedleClearMove(line[..eq].Trim(), line[(eq + 1)..].Trim()));
        }
        return list;
    }

    public void RefreshStatus()
    {
        var c = _host.Process.GetCameraNeedleCalibration();
        OffsetText = string.Format(CultureInfo.CurrentCulture, "dX {0:+0.0000;-0.0000}   dY {1:+0.0000;-0.0000}   dZ {2:+0.0000;-0.0000}  mm",
            c.VcamneedleX, c.VcamneedleY, c.Dz);
        if (!c.IsCalibrated)
        {
            StateText = "NOT CALIBRATED — the offset is a seed: enough to aim the first imprint, not to dispense. Every dispense refuses until it is calibrated.";
            StateBrushKey = "Danger";
        }
        else if (c.Verified)
        {
            StateText = string.Format(CultureInfo.CurrentCulture,
                "VERIFIED — a second pass agreed to within {0:F4} mm XY, {1:+0.0000;-0.0000} mm Z.", c.ResidualMm, c.ZResidualMm);
            StateBrushKey = "Ok";
        }
        else if (c.ZChecked)
        {
            StateText = string.Format(CultureInfo.CurrentCulture,
                "VERIFY FAILED — the last pass was off by {0:F4} mm XY, {1:+0.0000;-0.0000} mm Z.", c.ResidualMm, c.ZResidualMm);
            StateBrushKey = "Danger";
        }
        else
        {
            StateText = "UNVERIFIED — measured once, never re-checked (calibrate with passes ≥ 2).";
            StateBrushKey = "Warn";
        }

        CalibratedText = c.CalibratedAt is { } at
            ? $"calibrated {FormatWhen(at)}{(c.CalibratedBy is { } by ? " by " + by : "")}"
            : "never calibrated by this procedure";

        MeasuredText = c.Camera is { } cam && c.Needle is { } ndl && c.Imprint is { } dot
            ? string.Format(CultureInfo.CurrentCulture,
                "camera  X {0,10:F4}  Y {1,10:F4}  Z {2,10:F4}   focused on the coupon\n" +
                "needle  X {3,10:F4}  Y {4,10:F4}  Z {5,10:F4}   touching, imprinting\n" +
                "imprint X {6,10:F4}  Y {7,10:F4}                where the dot landed\n" +
                "dX/dY = needle − imprint      dZ = needle Z − camera Z",
                cam.X, cam.Y, cam.Z, ndl.X, ndl.Y, ndl.Z, dot.X, dot.Y)
            : "—";
    }

    private static string FormatWhen(string iso)
        => DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)
            : iso;

    private static string F(string fmt, double v) => string.Format(CultureInfo.CurrentCulture, fmt, v);

    private void Raise(string name) => OnPropertyChanged(name);
}
