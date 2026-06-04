using System.Windows;
using System.Windows.Controls;
using Horizon.Engine;

namespace FAAM.UI.Views;

/// <summary>
/// One stage in the vision pre-process chain — a filter-kind picker plus the
/// parameter widgets for that kind (threshold Min/Max sliders, gaussian mask,
/// Canny low/high + alpha). Exposes its <see cref="Value"/> as a
/// <see cref="VisionFilter"/> and raises <see cref="Changed"/> on any edit.
/// </summary>
public partial class FilterRow : UserControl
{
    private bool _suppress;

    /// <summary>Raised whenever the kind or any parameter changes.</summary>
    public event Action? Changed;

    public FilterRow()
    {
        InitializeComponent();
        _suppress = true;
        KindCombo.SelectedIndex = 0;
        _suppress = false;
        UpdatePanels();
    }

    /// <summary>Short stage tag shown at the left (e.g. "1").</summary>
    public string Stage
    {
        get => StageLabel.Text;
        set => StageLabel.Text = value;
    }

    /// <summary>The filter this row currently describes.</summary>
    public VisionFilter Value
    {
        get
        {
            var kind = KindCombo.SelectedIndex switch
            {
                1 => VisionFilterKind.BinaryThreshold,
                2 => VisionFilterKind.Gaussian,
                3 => VisionFilterKind.CannyEdge,
                _ => VisionFilterKind.None,
            };
            int mask = GaussMaskCombo.SelectedIndex == 1 ? 5 : 3;
            return new VisionFilter(kind, (int)MinSlider.Value, (int)MaxSlider.Value, mask, AlphaSlider.Value);
        }
        set
        {
            _suppress = true;
            KindCombo.SelectedIndex = value.Kind switch
            {
                VisionFilterKind.BinaryThreshold => 1,
                VisionFilterKind.Gaussian => 2,
                VisionFilterKind.CannyEdge => 3,
                _ => 0,
            };
            MinSlider.Value = value.ThresholdMin;
            MaxSlider.Value = value.ThresholdMax;
            GaussMaskCombo.SelectedIndex = value.GaussMask >= 5 ? 1 : 0;
            AlphaSlider.Value = value.Alpha > 0 ? value.Alpha : 1.0;
            _suppress = false;
            UpdatePanels();
        }
    }

    private void OnKindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThresholdPanel == null) return;   // fires during InitializeComponent
        UpdatePanels();
        if (!_suppress) Changed?.Invoke();
    }

    private void OnComboChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppress) Changed?.Invoke();
    }

    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_suppress) Changed?.Invoke();
    }

    private void UpdatePanels()
    {
        bool threshold = KindCombo.SelectedIndex is 1 or 3;   // binary or canny
        bool canny = KindCombo.SelectedIndex == 3;
        ThresholdPanel.Visibility = threshold ? Visibility.Visible : Visibility.Collapsed;
        GaussPanel.Visibility = KindCombo.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        AlphaPanel.Visibility = canny ? Visibility.Visible : Visibility.Collapsed;
        // Relabel the threshold sliders for Canny (hysteresis low/high).
        MinLabel.Text = canny ? "Low" : "Min";
        MaxLabel.Text = canny ? "High" : "Max";
    }
}
