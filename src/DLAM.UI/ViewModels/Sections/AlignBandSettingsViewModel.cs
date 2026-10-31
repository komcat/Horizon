using System.Globalization;

namespace DLAM.UI.ViewModels.Sections;

/// <summary>
/// Machine › Settings, DLAM panel: the Run page's alignment band (<see cref="AlignBandSettings"/>,
/// <c>dlam_align_band.json</c>). Numbers are edited as text so a half-typed value is reported by
/// <see cref="Validate"/> instead of being silently rejected by the binding.
/// </summary>
public sealed class AlignBandSettingsViewModel : ObservableObject, IHeadSettingsPanel
{
    private string _leftDevice = "";
    private string _rightDevice = "";
    private string _smu = "";
    private int _feedbackChannel = 1;
    private string _axes = "";
    private string _coarseStepsUm = "";
    private string _fineStepsUm = "";
    private string _exitTargetUa = "";
    private string _minSignalUa = "";
    private string _twoLensTargetUa = "";
    private string _zoneLowUa = "";
    private string _targetAUa = "";
    private string _targetAZoneLowUa = "";
    private string _targetBUa = "";
    private string _targetBZoneLowUa = "";
    private string _climbTargetUa = "";
    private string _climbZStepUm = "";
    private string _climbMaxZUm = "";

    public string LeftDevice { get => _leftDevice; set => SetField(ref _leftDevice, value); }
    public string RightDevice { get => _rightDevice; set => SetField(ref _rightDevice, value); }
    public string Smu { get => _smu; set => SetField(ref _smu, value); }

    /// <summary>Feedback channel as the combo's index (0 = ch1, 1 = ch2).</summary>
    public int FeedbackChannelIndex { get => _feedbackChannel - 1; set => SetField(ref _feedbackChannel, value + 1); }

    public string Axes { get => _axes; set => SetField(ref _axes, value); }
    public string CoarseStepsUm { get => _coarseStepsUm; set => SetField(ref _coarseStepsUm, value); }
    public string FineStepsUm { get => _fineStepsUm; set => SetField(ref _fineStepsUm, value); }
    public string ExitTargetUa { get => _exitTargetUa; set => SetField(ref _exitTargetUa, value); }
    public string MinSignalUa { get => _minSignalUa; set => SetField(ref _minSignalUa, value); }
    public string TwoLensTargetUa { get => _twoLensTargetUa; set => SetField(ref _twoLensTargetUa, value); }
    public string ZoneLowUa { get => _zoneLowUa; set => SetField(ref _zoneLowUa, value); }
    public string TargetAUa { get => _targetAUa; set => SetField(ref _targetAUa, value); }
    public string TargetAZoneLowUa { get => _targetAZoneLowUa; set => SetField(ref _targetAZoneLowUa, value); }
    public string TargetBUa { get => _targetBUa; set => SetField(ref _targetBUa, value); }
    public string TargetBZoneLowUa { get => _targetBZoneLowUa; set => SetField(ref _targetBZoneLowUa, value); }
    public string ClimbTargetUa { get => _climbTargetUa; set => SetField(ref _climbTargetUa, value); }
    public string ClimbZStepUm { get => _climbZStepUm; set => SetField(ref _climbZStepUm, value); }
    public string ClimbMaxZUm { get => _climbMaxZUm; set => SetField(ref _climbMaxZUm, value); }

    public string SavedFiles => AlignBandSettings.FileName + ".json";

    /// <summary>The settings as loaded — fields this panel doesn't show (the XCAN target ladders,
    /// edited on the Run page) are carried through a save unchanged.</summary>
    private AlignBandSettings _loaded = new();

    public void Load()
    {
        var s = AlignBandSettings.Load();
        _loaded = s;
        LeftDevice = s.LeftDevice;
        RightDevice = s.RightDevice;
        Smu = s.Smu;
        FeedbackChannelIndex = s.FeedbackChannel - 1;
        Axes = s.Axes;
        CoarseStepsUm = s.CoarseStepsUm;
        FineStepsUm = s.FineStepsUm;
        ExitTargetUa = Text(s.ExitTargetUa);
        MinSignalUa = Text(s.MinSignalUa);
        TwoLensTargetUa = Text(s.TwoLensTargetUa);
        ZoneLowUa = Text(s.ZoneLowUa);
        TargetAUa = Text(s.TargetAUa);
        TargetAZoneLowUa = Text(s.TargetAZoneLowUa);
        TargetBUa = Text(s.TargetBUa);
        TargetBZoneLowUa = Text(s.TargetBZoneLowUa);
        ClimbTargetUa = Text(s.ClimbTargetUa);
        ClimbZStepUm = Text(s.ClimbZStepUm);
        ClimbMaxZUm = Text(s.ClimbMaxZUm);
    }

    public string? Validate()
    {
        var (settings, error) = Build();
        return error ?? (settings!.Validate() is { } bad ? $"Alignment band: {bad}" : null);
    }

    public void Save()
    {
        var (settings, error) = Build();
        if (settings == null)
            throw new InvalidOperationException(error);
        settings.Save();
    }

    /// <summary>The edits as settings, or the first field that is not a number.</summary>
    private (AlignBandSettings? Settings, string? Error) Build()
    {
        var fields = new (string Label, string Text)[]
        {
            ("XCAN exit target", ExitTargetUa), ("Min signal", MinSignalUa), ("Two-lens target", TwoLensTargetUa),
            ("Zone low", ZoneLowUa), ("Climb target", ClimbTargetUa), ("Climb Z step", ClimbZStepUm),
            ("Climb Z budget", ClimbMaxZUm), ("Target A", TargetAUa), ("Target A zone low", TargetAZoneLowUa),
            ("Target B", TargetBUa), ("Target B zone low", TargetBZoneLowUa),
        };
        var values = new double[fields.Length];
        for (int i = 0; i < fields.Length; i++)
        {
            if (!double.TryParse(fields[i].Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                return (null, $"Alignment band: {fields[i].Label} must be a number");
        }

        return (_loaded with
        {
            LeftDevice = LeftDevice.Trim(),
            RightDevice = RightDevice.Trim(),
            Smu = Smu.Trim(),
            FeedbackChannel = _feedbackChannel,
            Axes = Axes.Trim().ToUpperInvariant(),
            CoarseStepsUm = CoarseStepsUm.Trim(),
            FineStepsUm = FineStepsUm.Trim(),
            ExitTargetUa = values[0],
            MinSignalUa = values[1],
            TwoLensTargetUa = values[2],
            ZoneLowUa = values[3],
            ClimbTargetUa = values[4],
            ClimbZStepUm = values[5],
            ClimbMaxZUm = values[6],
            TargetAUa = values[7],
            TargetAZoneLowUa = values[8],
            TargetBUa = values[9],
            TargetBZoneLowUa = values[10],
        }, null);
    }

    private static string Text(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
