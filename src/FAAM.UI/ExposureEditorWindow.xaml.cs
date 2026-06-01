using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Horizon.Common;

namespace FAAM.UI;

/// <summary>
/// Small modal editor for the per-point camera exposure used by the U-correction and
/// FAU pre-align panels. Shows one µs field per reference point (P1/P2/F1/F2 or
/// Out1/Out2/Fiber1/Fiber2); Apply writes the values into
/// <see cref="AppSettings.PointExposureUs"/> (keyed by the full position name) and
/// persists. A 0 (or blank) entry means "leave the camera exposure untouched before
/// moving to that point".
/// </summary>
public partial class ExposureEditorWindow : Window
{
    private readonly AppSettingsStore _settings;
    private readonly List<(string Key, TextBox Box)> _fields = new();

    /// <param name="title">Header shown at the top of the dialog.</param>
    /// <param name="points">Display label + full position-name key, in display order.</param>
    /// <param name="settings">The store read for current values and written on Apply.</param>
    public ExposureEditorWindow(string title, IReadOnlyList<(string Label, string Key)> points, AppSettingsStore settings)
    {
        InitializeComponent();
        _settings = settings;
        HeaderText.Text = title;

        var current = settings.Current.PointExposureUs;
        foreach (var (label, key) in points)
        {
            double us = current.TryGetValue(key, out var v) ? v : 0;
            var box = AddRow(label, us);
            _fields.Add((key, box));
        }
    }

    /// <summary>Adds a "label : µs textbox" row and returns the textbox.</summary>
    private TextBox AddRow(string label, double value)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });

        var text = new TextBlock
        {
            Text = label,
            Foreground = (System.Windows.Media.Brush)FindResource("Text"),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(text, 0);

        var box = new TextBox
        {
            Height = 28,
            FontSize = 13,
            VerticalContentAlignment = VerticalAlignment.Center,
            Text = value.ToString(CultureInfo.InvariantCulture),
        };
        Grid.SetColumn(box, 1);

        grid.Children.Add(text);
        grid.Children.Add(box);
        Rows.Children.Add(grid);
        return box;
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        // Start from the existing map so other panels' points are preserved.
        var map = new Dictionary<string, double>(_settings.Current.PointExposureUs);
        foreach (var (key, box) in _fields)
        {
            double us = double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0
                ? v : 0;
            if (us > 0) map[key] = us;
            else map.Remove(key);   // 0 / blank clears the per-point setting
        }

        _settings.Update(_settings.Current with { PointExposureUs = map });
        _settings.Save();
        DialogResult = true;
    }
}
