using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Horizon.Core;
using Horizon.Process.Interfaces;
using Horizon.Process.Models;
using Microsoft.Win32;

namespace Horizon.App.Controls;

public partial class PropertyTable : UserControl
{
    private readonly IAppContext _context;
    private IProcessPlugin? _plugin;
    private Dictionary<string, string> _values = new();
    private readonly Dictionary<string, FrameworkElement> _controlMap = new(StringComparer.OrdinalIgnoreCase);

    public event Action? ValuesChanged;

    public PropertyTable(IAppContext context)
    {
        _context = context;
        InitializeComponent();
    }

    public void BindPlugin(IProcessPlugin? plugin, Dictionary<string, string>? values)
    {
        _plugin = plugin;
        _values = values ?? new();
        _controlMap.Clear();
        PropertiesPanel.Children.Clear();

        if (plugin == null) return;

        // Group parameters by category
        string? lastCategory = null;
        foreach (var param in plugin.Parameters)
        {
            if (param.Category != null && param.Category != lastCategory)
            {
                lastCategory = param.Category;
                AddCategoryHeader(param.Category);
            }

            AddPropertyRow(param);
        }
    }

    public Dictionary<string, string> GetValues()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, control) in _controlMap)
        {
            var val = ExtractValue(control);
            if (!string.IsNullOrEmpty(val))
                result[name] = val;
        }
        return result;
    }

    private void AddCategoryHeader(string category)
    {
        var header = new TextBlock
        {
            Text = category,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, PropertiesPanel.Children.Count > 0 ? 10 : 0, 0, 2)
        };
        header.SetResourceReference(ForegroundProperty, "Theme.ForegroundBrush");
        PropertiesPanel.Children.Add(header);

        var sep = new Separator
        {
            Margin = new Thickness(0, 0, 0, 4)
        };
        sep.SetResourceReference(ForegroundProperty, "Theme.BorderBrush");
        PropertiesPanel.Children.Add(sep);
    }

    private void AddPropertyRow(PluginParamDef param)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var reqMarker = param.Required ? " *" : "";
        var label = new TextBlock
        {
            Text = $"{param.DisplayName}{reqMarker}",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = param.Name
        };
        label.SetResourceReference(ForegroundProperty, "Theme.ForegroundBrush");
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        var control = CreateControl(param);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);

        _controlMap[param.Name] = control;
        PropertiesPanel.Children.Add(grid);
    }

    private FrameworkElement CreateControl(PluginParamDef param)
    {
        var currentValue = _values.GetValueOrDefault(param.Name) ?? param.Default ?? "";

        switch (param.Type)
        {
            case PluginParamType.MotionDevice:
                return CreateDeviceCombo(GetMotionDeviceNames(), currentValue, param);

            case PluginParamType.CameraDevice:
                return CreateDeviceCombo(GetCameraDeviceNames(), currentValue, param);

            case PluginParamType.NamedPosition:
                return CreateDependentCombo(param, currentValue, GetNamedPositions);

            case PluginParamType.Axis:
                return CreateDependentCombo(param, currentValue, GetAxisNames);

            case PluginParamType.Number:
                return CreateNumberControl(param, currentValue);

            case PluginParamType.Boolean:
                return CreateBoolCombo(currentValue);

            case PluginParamType.Enum:
                return CreateEnumCombo(param, currentValue);

            case PluginParamType.FilePath:
                return CreateFilePathControl(currentValue);

            case PluginParamType.Text:
            default:
                return CreateTextBox(currentValue);
        }
    }

    private ComboBox CreateDeviceCombo(List<string> names, string currentValue, PluginParamDef param)
    {
        var combo = new ComboBox
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            ItemsSource = names
        };

        var idx = names.IndexOf(currentValue);
        if (idx >= 0) combo.SelectedIndex = idx;
        else if (names.Count > 0) combo.SelectedIndex = 0;

        combo.SelectionChanged += (_, _) =>
        {
            OnValueChanged();
            RefreshDependentControls(param.Name);
        };

        return combo;
    }

    private ComboBox CreateDependentCombo(PluginParamDef param, string currentValue, Func<string, List<string>> getOptions)
    {
        var combo = new ComboBox
        {
            IsEditable = true,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11
        };

        // Populate from the device param it depends on
        var deviceParamName = param.DependsOnDevice;
        if (!string.IsNullOrEmpty(deviceParamName))
        {
            var deviceName = GetCurrentDeviceValue(deviceParamName);
            if (!string.IsNullOrEmpty(deviceName))
            {
                var options = getOptions(deviceName);
                combo.ItemsSource = options;
                var idx = options.IndexOf(currentValue);
                if (idx >= 0) combo.SelectedIndex = idx;
                else if (options.Count > 0) combo.SelectedIndex = 0;
            }
        }

        combo.SelectionChanged += (_, _) => OnValueChanged();

        return combo;
    }

    private FrameworkElement CreateNumberControl(PluginParamDef param, string currentValue)
    {
        if (param.UnitSuffix == null)
            return CreateTextBox(currentValue);

        var innerGrid = new Grid();
        innerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        innerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var tb = CreateTextBox(currentValue);
        Grid.SetColumn(tb, 0);

        var suffix = new TextBlock
        {
            Text = param.UnitSuffix,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0)
        };
        suffix.SetResourceReference(ForegroundProperty, "Theme.ForegroundDisabledBrush");
        Grid.SetColumn(suffix, 1);

        innerGrid.Children.Add(tb);
        innerGrid.Children.Add(suffix);
        return innerGrid;
    }

    private ComboBox CreateBoolCombo(string currentValue)
    {
        var combo = new ComboBox
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            ItemsSource = new[] { "True", "False" }
        };
        combo.SelectedItem = string.Equals(currentValue, "True", StringComparison.OrdinalIgnoreCase) ? "True" : "False";
        combo.SelectionChanged += (_, _) => OnValueChanged();
        return combo;
    }

    private ComboBox CreateEnumCombo(PluginParamDef param, string currentValue)
    {
        var combo = new ComboBox
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            ItemsSource = param.Options ?? Array.Empty<string>()
        };

        if (param.Options != null)
        {
            var idx = Array.IndexOf(param.Options, currentValue);
            if (idx >= 0) combo.SelectedIndex = idx;
            else if (param.Options.Length > 0) combo.SelectedIndex = 0;
        }

        combo.SelectionChanged += (_, _) => OnValueChanged();
        return combo;
    }

    private FrameworkElement CreateFilePathControl(string currentValue)
    {
        var innerGrid = new Grid();
        innerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        innerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var tb = CreateTextBox(currentValue);
        Grid.SetColumn(tb, 0);

        var browseBtn = new Button
        {
            Content = "...",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(4, 0, 0, 0)
        };
        browseBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog { Title = "Select Path" };
            if (dlg.ShowDialog() == true)
            {
                tb.Text = dlg.FolderName;
                OnValueChanged();
            }
        };
        Grid.SetColumn(browseBtn, 1);

        innerGrid.Children.Add(tb);
        innerGrid.Children.Add(browseBtn);
        return innerGrid;
    }

    private TextBox CreateTextBox(string currentValue)
    {
        var tb = new TextBox
        {
            Text = currentValue,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            Padding = new Thickness(4, 2, 4, 2)
        };
        tb.TextChanged += (_, _) => OnValueChanged();
        return tb;
    }

    private void OnValueChanged()
    {
        _values = GetValues();
        ValuesChanged?.Invoke();
    }

    private void RefreshDependentControls(string changedParamName)
    {
        if (_plugin == null) return;

        foreach (var param in _plugin.Parameters)
        {
            if (!string.Equals(param.DependsOnDevice, changedParamName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!_controlMap.TryGetValue(param.Name, out var control))
                continue;

            var combo = control as ComboBox;
            if (combo == null) continue;

            var deviceName = GetCurrentDeviceValue(changedParamName);
            if (string.IsNullOrEmpty(deviceName))
            {
                combo.ItemsSource = null;
                continue;
            }

            List<string> options = param.Type switch
            {
                PluginParamType.NamedPosition => GetNamedPositions(deviceName),
                PluginParamType.Axis => GetAxisNames(deviceName),
                _ => new()
            };

            combo.ItemsSource = options;
            if (options.Count > 0) combo.SelectedIndex = 0;
        }
    }

    private string GetCurrentDeviceValue(string paramName)
    {
        if (!_controlMap.TryGetValue(paramName, out var control)) return "";
        return ExtractValue(control);
    }

    private static string ExtractValue(FrameworkElement control)
    {
        if (control is TextBox tb) return tb.Text;
        if (control is ComboBox cb) return cb.SelectedItem as string ?? cb.Text ?? "";
        if (control is Grid g)
        {
            var innerTb = g.Children.OfType<TextBox>().FirstOrDefault();
            if (innerTb != null) return innerTb.Text;
        }
        return "";
    }

    // --- Device data access ---

    private List<string> GetMotionDeviceNames()
    {
        if (!_context.IsInitialized) return new();
        try { return _context.MotionManager.GetAllDeviceNames(); }
        catch { return new(); }
    }

    private List<string> GetCameraDeviceNames()
    {
        if (!_context.IsInitialized) return new();
        try { return _context.CameraManager.GetCameraIds(); }
        catch { return new(); }
    }

    private List<string> GetNamedPositions(string deviceName)
    {
        if (!_context.IsInitialized) return new();
        try { return _context.MotionManager.GetNamedPositionNames(deviceName); }
        catch { return new(); }
    }

    private List<string> GetAxisNames(string deviceName)
    {
        if (!_context.IsInitialized) return new();
        try
        {
            var controller = _context.MotionManager.GetController(deviceName);
            if (controller != null)
            {
                var prop = controller.GetType().GetProperty("AxisNames");
                if (prop?.GetValue(controller) is List<string> names)
                    return names;
            }
        }
        catch { }
        return new List<string> { "X", "Y", "Z", "U", "V", "W" };
    }
}
