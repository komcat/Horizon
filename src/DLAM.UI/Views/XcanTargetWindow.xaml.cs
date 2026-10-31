using System.Globalization;
using System.Windows;

namespace DLAM.UI.Views;

/// <summary>
/// Edits one XCAN-to-target group of the Run page's alignment band (right-click on the group):
/// its target threshold, adaptive zone low and coarse / fine ladders. Save validates the whole
/// <see cref="AlignBandSettings"/> and writes <c>dlam_align_band.json</c>; the result is
/// <see cref="Saved"/>.
/// </summary>
public partial class XcanTargetWindow : Window
{
    private readonly AlignBandSettings _settings;
    private readonly bool _targetB;

    public XcanTargetWindow(AlignBandSettings settings, bool targetB)
    {
        InitializeComponent();
        _settings = settings;
        _targetB = targetB;
        Title = $"XCAN target {(targetB ? "B" : "A")}";
        Heading.Text = $"XCAN TO TARGET — group {(targetB ? "B" : "A")}";
        Fill(settings);
        Loaded += (_, _) => { TargetBox.Focus(); TargetBox.SelectAll(); };
    }

    /// <summary>The saved settings (valid only when DialogResult is true).</summary>
    public AlignBandSettings? Saved { get; private set; }

    private void Fill(AlignBandSettings s)
    {
        TargetBox.Text = Text(_targetB ? s.TargetBUa : s.TargetAUa);
        ZoneLowBox.Text = Text(_targetB ? s.TargetBZoneLowUa : s.TargetAZoneLowUa);
        CoarseBox.Text = _targetB ? s.TargetBCoarseStepsUm : s.TargetACoarseStepsUm;
        FineBox.Text = _targetB ? s.TargetBFineStepsUm : s.TargetAFineStepsUm;
    }

    private void OnDefaults(object sender, RoutedEventArgs e) => Fill(new AlignBandSettings());

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!TryNumber(TargetBox.Text, out var target))
        {
            ShowError("The target threshold must be a number (µA).");
            return;
        }
        if (!TryNumber(ZoneLowBox.Text, out var zoneLow))
        {
            ShowError("The zone low must be a number (µA).");
            return;
        }

        var coarse = CoarseBox.Text.Trim();
        var fine = FineBox.Text.Trim();
        var candidate = _targetB
            ? _settings with { TargetBUa = target, TargetBZoneLowUa = zoneLow, TargetBCoarseStepsUm = coarse, TargetBFineStepsUm = fine }
            : _settings with { TargetAUa = target, TargetAZoneLowUa = zoneLow, TargetACoarseStepsUm = coarse, TargetAFineStepsUm = fine };
        if (candidate.Validate() is { } bad)
        {
            ShowError($"Not saved — {bad}.");
            return;
        }

        try
        {
            candidate.Save();
        }
        catch (Exception ex)
        {
            ShowError($"Save failed: {ex.Message}");
            return;
        }
        Saved = candidate;
        DialogResult = true;
    }

    private void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorText.Visibility = Visibility.Visible;
    }

    private static bool TryNumber(string text, out double value)
        => double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static string Text(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
