using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using FAAM.UI.Process;
using Horizon.Engine;

namespace FAAM.UI.Views;

/// <summary>
/// Non-modal editor for one process position — change the device velocity, the
/// taught axis values, and the camera + exposure applied during a recall move.
/// Axis fields are driven by the device's installed axes (an ACS 3-axis gantry
/// shows X/Y/Z; a PI hexapod adds U/V/W). Applying does NOT command motion: it
/// hands the new settings back via the supplied callback (which persists them via
/// Layer 2). A blank exposure means "leave the camera untouched".
/// </summary>
public partial class PositionEditorWindow : Window
{
    private readonly PositionEditContext _ctx;
    private readonly Func<PositionEditResult, Task> _onApply;
    private readonly TextBox _velocityBox;
    private readonly Dictionary<string, TextBox> _axisBoxes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ComboBox _cameraBox;
    private readonly TextBox _exposureBox;

    public PositionEditorWindow(PositionEditContext ctx, Func<PositionEditResult, Task> onApply)
    {
        _ctx = ctx;
        _onApply = onApply;
        InitializeComponent();

        Title = $"Edit Position — {ctx.Position}";
        HeaderText.Text = ctx.Position;
        DeviceText.Text = $"{ctx.Device}  ({ctx.ControllerType})";

        _velocityBox = AddNumberField("Velocity", ctx.Velocity);
        foreach (var axis in ctx.Axes)
            _axisBoxes[axis] = AddNumberField(axis, AxisValue(ctx.Pose, axis));

        _cameraBox = AddComboField("Camera", ctx.Cameras, ctx.Camera);
        _exposureBox = AddTextField("Exposure µs",
            ctx.ExposureUs?.ToString("0.###", CultureInfo.InvariantCulture) ?? "");
    }

    private TextBox AddNumberField(string label, double value)
        => AddTextField(label, value.ToString("0.###", CultureInfo.InvariantCulture));

    private TextBox AddTextField(string label, string value)
    {
        var box = new TextBox { Text = value };
        AddRow(label, box);
        return box;
    }

    private ComboBox AddComboField(string label, IReadOnlyList<string> items, string? selected)
    {
        var combo = new ComboBox { IsEditable = true };
        foreach (var it in items)
            combo.Items.Add(it);
        combo.Text = selected ?? string.Empty;
        AddRow(label, combo);
        return combo;
    }

    /// <summary>Appends a labelled field (90px label + 160px control) to the field stack.</summary>
    private void AddRow(string label, FrameworkElement field)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });

        var lbl = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        lbl.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        Grid.SetColumn(lbl, 0);
        Grid.SetColumn(field, 1);

        row.Children.Add(lbl);
        row.Children.Add(field);
        Fields.Children.Add(row);
    }

    private async void OnApply(object sender, RoutedEventArgs e)
    {
        if (!TryParse(_velocityBox.Text, out var vel))
        {
            StatusText.Text = "Invalid velocity.";
            return;
        }

        foreach (var kv in _axisBoxes)
            if (!TryParse(kv.Value.Text, out _))
            {
                StatusText.Text = $"Invalid {kv.Key} value.";
                return;
            }

        // Exposure is optional — blank = "leave the camera untouched".
        double? exposureUs = null;
        var expText = _exposureBox.Text?.Trim();
        if (!string.IsNullOrEmpty(expText))
        {
            if (!TryParse(expText, out var ex))
            {
                StatusText.Text = "Invalid exposure.";
                return;
            }
            exposureUs = ex;
        }

        // Unshown axes keep their existing pose value (e.g. U/V/W on a 3-axis device).
        double Val(string a, double fallback)
            => _axisBoxes.TryGetValue(a, out var tb) && TryParse(tb.Text, out var v) ? v : fallback;

        var pose = new MotionPosition(
            Val("X", _ctx.Pose.X), Val("Y", _ctx.Pose.Y), Val("Z", _ctx.Pose.Z),
            Val("U", _ctx.Pose.U), Val("V", _ctx.Pose.V), Val("W", _ctx.Pose.W));

        var camera = string.IsNullOrWhiteSpace(_cameraBox.Text) ? null : _cameraBox.Text.Trim();

        try
        {
            await _onApply(new PositionEditResult(
                _ctx.Position, _ctx.Device, vel, pose, camera, exposureUs));
            StatusText.Text = "Saved.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Save failed: {ex.Message}";
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private static double AxisValue(MotionPosition p, string axis) => axis.Trim().ToUpperInvariant() switch
    {
        "X" => p.X, "Y" => p.Y, "Z" => p.Z, "U" => p.U, "V" => p.V, "W" => p.W, _ => 0,
    };

    private static bool TryParse(string? s, out double value)
        => double.TryParse(s?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
