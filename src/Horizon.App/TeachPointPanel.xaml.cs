using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Horizon.Core;
using Serilog;

namespace Horizon.App;

public partial class TeachPointPanel : UserControl
{
    private readonly IAppContext _context;
    private readonly TeachPointManager _teachManager;

    private string? _selectedDevice;
    private readonly Dictionary<string, Button> _deviceButtons = new(StringComparer.OrdinalIgnoreCase);
    private DispatcherTimer? _pollTimer;

    private static readonly SolidColorBrush ActiveDeviceBrush = new(Color.FromRgb(0x4E, 0xC9, 0xB0)); // cyan
    private static readonly SolidColorBrush InactiveDeviceFg = new(Color.FromRgb(0xF1, 0xF1, 0xF1));

    public TeachPointPanel(IAppContext context)
    {
        _context = context;
        _teachManager = context.TeachPointManager;

        InitializeComponent();

        BuildDeviceButtons();
        RefreshProductList();
        RefreshActiveLabel();
        StartPolling();
    }

    // --- Live position polling ---

    private void StartPolling()
    {
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _pollTimer.Tick += PollTimer_Tick;
        _pollTimer.Start();
    }

    public void StopPolling()
    {
        if (_pollTimer != null)
        {
            _pollTimer.Stop();
            _pollTimer.Tick -= PollTimer_Tick;
            _pollTimer = null;
        }
    }

    private void PollTimer_Tick(object? sender, EventArgs e)
    {
        if (!_context.IsInitialized || _selectedDevice == null)
        {
            LivePositionText.Text = "-- not connected --";
            return;
        }

        var pos = _context.MotionManager.GetCurrentPosition(_selectedDevice);
        if (pos == null)
        {
            LivePositionText.Text = "-- not connected --";
            return;
        }

        var ctrl = _context.MotionManager.GetController(_selectedDevice);
        var axes = ctrl?.AxisNames;
        if (axes == null || axes.Count == 0)
        {
            LivePositionText.Text = $"X={pos.X:F4}  Y={pos.Y:F4}  Z={pos.Z:F4}";
            return;
        }

        var parts = new List<string>();
        foreach (var axis in axes)
        {
            var val = axis switch
            {
                "X" => pos.X, "Y" => pos.Y, "Z" => pos.Z,
                "U" => pos.U, "V" => pos.V, "W" => pos.W,
                _ => 0
            };
            parts.Add($"{axis}={val:F4}");
        }
        LivePositionText.Text = string.Join("  ", parts);
    }

    // --- Device button panel ---

    private void BuildDeviceButtons()
    {
        DeviceButtonStack.Children.Clear();
        _deviceButtons.Clear();

        if (!_context.IsInitialized) return;

        var devices = _context.MotionManager.GetAllDeviceNames();
        foreach (var deviceName in devices)
        {
            var btn = new Button
            {
                Content = deviceName,
                Tag = deviceName,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 2),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Foreground = InactiveDeviceFg,
                Background = (Brush)FindResource("Theme.BackgroundAltBrush"),
                BorderBrush = (Brush)FindResource("Theme.BorderBrush"),
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            btn.Click += DeviceButton_Click;
            DeviceButtonStack.Children.Add(btn);
            _deviceButtons[deviceName] = btn;
        }

        // Auto-select first device
        if (devices.Count > 0)
            SelectDevice(devices[0]);
    }

    private void DeviceButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string deviceName) return;
        SelectDevice(deviceName);
    }

    private void SelectDevice(string deviceName)
    {
        _selectedDevice = deviceName;

        // Update button visuals
        foreach (var kvp in _deviceButtons)
        {
            if (kvp.Key.Equals(deviceName, StringComparison.OrdinalIgnoreCase))
            {
                kvp.Value.Background = ActiveDeviceBrush;
                kvp.Value.Foreground = new SolidColorBrush(Colors.Black);
                kvp.Value.FontWeight = FontWeights.Bold;
            }
            else
            {
                kvp.Value.Background = (Brush)FindResource("Theme.BackgroundAltBrush");
                kvp.Value.Foreground = InactiveDeviceFg;
                kvp.Value.FontWeight = FontWeights.Normal;
            }
        }

        RefreshPointsGrid();
    }

    // --- Product management ---

    private void RefreshProductList()
    {
        var names = _teachManager.GetProductNames();
        var selected = ProductCombo.SelectedItem as string;
        ProductCombo.ItemsSource = names;

        if (selected != null && names.Contains(selected))
            ProductCombo.SelectedItem = selected;
        else if (names.Count > 0)
            ProductCombo.SelectedIndex = 0;
    }

    private void RefreshActiveLabel()
    {
        var active = _teachManager.ActiveProductName;
        ActiveProductText.Text = string.IsNullOrEmpty(active)
            ? "No active product"
            : $"Active: {active}";
    }

    private void RefreshPointsGrid()
    {
        if (ProductCombo.SelectedItem is not string productName || _selectedDevice == null)
        {
            PointsDataGrid.ItemsSource = null;
            return;
        }

        var product = _teachManager.GetProduct(productName);
        if (product == null)
        {
            PointsDataGrid.ItemsSource = null;
            return;
        }

        // Filter by selected device
        var filtered = product.Points
            .Where(p => p.DeviceName.Equals(_selectedDevice, StringComparison.OrdinalIgnoreCase))
            .Select(p => new TeachPointRow(p))
            .ToList();

        PointsDataGrid.ItemsSource = filtered;
    }

    private void ProductCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshPointsGrid();
    }

    private void NewProduct_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new InputDialog("New Product", "Enter product name:")
        {
            Owner = Window.GetWindow(this)
        };
        if (dlg.ShowDialog() != true) return;

        var name = dlg.ResultText;
        if (string.IsNullOrWhiteSpace(name)) return;

        _teachManager.CreateProduct(name);
        RefreshProductList();
        ProductCombo.SelectedItem = name;
        Log.Information("Teach: Created product '{ProductName}'", name);
    }

    private void RenameProduct_Click(object sender, RoutedEventArgs e)
    {
        if (ProductCombo.SelectedItem is not string oldName) return;

        var dlg = new InputDialog("Rename Product", "Enter new name:", oldName)
        {
            Owner = Window.GetWindow(this)
        };
        if (dlg.ShowDialog() != true) return;

        var newName = dlg.ResultText;
        if (string.IsNullOrWhiteSpace(newName) || newName == oldName) return;

        _teachManager.RenameProduct(oldName, newName);
        RefreshProductList();
        ProductCombo.SelectedItem = newName;
        RefreshActiveLabel();
        Log.Information("Teach: Renamed product '{OldName}' -> '{NewName}'", oldName, newName);
    }

    private void DeleteProduct_Click(object sender, RoutedEventArgs e)
    {
        if (ProductCombo.SelectedItem is not string productName) return;

        var result = MessageBox.Show(
            $"Delete product '{productName}' and all its teach points?",
            "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        _teachManager.DeleteProduct(productName);
        RefreshProductList();
        RefreshActiveLabel();
        RefreshPointsGrid();
        Log.Information("Teach: Deleted product '{ProductName}'", productName);
    }

    private void SetActive_Click(object sender, RoutedEventArgs e)
    {
        if (ProductCombo.SelectedItem is not string productName) return;

        _teachManager.SetActiveProduct(productName);
        RefreshActiveLabel();
        Log.Information("Teach: Set active product to '{ProductName}'", productName);
    }

    // --- DataGrid cell edit ---

    private void PointsDataGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Cancel) return;
        if (ProductCombo.SelectedItem is not string productName) return;
        if (_selectedDevice == null) return;
        if (e.Row.Item is not TeachPointRow row) return;

        // Defer so binding updates first
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _teachManager.UpdatePointCoordinates(productName, _selectedDevice, row.PositionName,
                row.X, row.Y, row.Z, row.U, row.V, row.W);
            Log.Information("Teach: Edited {Device}/{Position} coordinates", _selectedDevice, row.PositionName);
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    // --- Point actions ---

    private void GoPoint_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not TeachPointRow row) return;
        if (_selectedDevice == null) return;

        var ok = _teachManager.MoveToPosition(_selectedDevice, row.X, row.Y, row.Z, row.U, row.V, row.W);
        Log.Information("Teach: Move {Device} to '{Position}' {Result}",
            _selectedDevice, row.PositionName, ok ? "OK" : "FAILED");
    }

    private void UpdatePoint_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not TeachPointRow row) return;
        if (ProductCombo.SelectedItem is not string productName) return;
        if (_selectedDevice == null) return;

        _teachManager.UpdatePoint(productName, _selectedDevice, row.PositionName);
        RefreshPointsGrid();
        Log.Information("Teach: Updated {Device}/{Position} from current position", _selectedDevice, row.PositionName);
    }

    private void DeletePoint_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not TeachPointRow row) return;
        if (ProductCombo.SelectedItem is not string productName) return;
        if (_selectedDevice == null) return;

        _teachManager.DeletePoint(productName, _selectedDevice, row.PositionName);
        RefreshPointsGrid();
        Log.Information("Teach: Deleted {Device}/{Position}", _selectedDevice, row.PositionName);
    }

    // --- Teach / Create ---

    private void TeachCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (ProductCombo.SelectedItem is not string productName)
        {
            Log.Warning("Teach: No product selected");
            return;
        }
        if (_selectedDevice == null)
        {
            Log.Warning("Teach: No device selected");
            return;
        }

        var positionName = TeachNameBox.Text.Trim();
        if (string.IsNullOrEmpty(positionName))
        {
            Log.Warning("Teach: Position name is required");
            return;
        }

        _teachManager.TeachCurrentPosition(productName, _selectedDevice, positionName);
        RefreshPointsGrid();
        TeachNameBox.Clear();
        Log.Information("Teach: Taught {Device}/{Position} in product '{Product}'",
            _selectedDevice, positionName, productName);
    }

    private void CreatePoint_Click(object sender, RoutedEventArgs e)
    {
        if (ProductCombo.SelectedItem is not string productName)
        {
            Log.Warning("Teach: No product selected");
            return;
        }
        if (_selectedDevice == null)
        {
            Log.Warning("Teach: No device selected");
            return;
        }

        var positionName = TeachNameBox.Text.Trim();
        if (string.IsNullOrEmpty(positionName))
        {
            Log.Warning("Teach: Position name is required");
            return;
        }

        double.TryParse(CreateXBox.Text, out var x);
        double.TryParse(CreateYBox.Text, out var y);
        double.TryParse(CreateZBox.Text, out var z);
        double.TryParse(CreateUBox.Text, out var u);
        double.TryParse(CreateVBox.Text, out var v);
        double.TryParse(CreateWBox.Text, out var w);

        _teachManager.CreatePointManual(productName, _selectedDevice, positionName, x, y, z, u, v, w);
        RefreshPointsGrid();
        TeachNameBox.Clear();
        Log.Information("Teach: Created {Device}/{Position} at ({X:F3}, {Y:F3}, {Z:F3})",
            _selectedDevice, positionName, x, y, z);
    }
}

/// <summary>
/// Mutable view model for DataGrid two-way binding (TeachPoint is an immutable record).
/// </summary>
public sealed class TeachPointRow : INotifyPropertyChanged
{
    public string DeviceName { get; }
    public string PositionName { get; }

    private double _x, _y, _z, _u, _v, _w;

    public double X { get => _x; set { _x = value; OnPropertyChanged(nameof(X)); } }
    public double Y { get => _y; set { _y = value; OnPropertyChanged(nameof(Y)); } }
    public double Z { get => _z; set { _z = value; OnPropertyChanged(nameof(Z)); } }
    public double U { get => _u; set { _u = value; OnPropertyChanged(nameof(U)); } }
    public double V { get => _v; set { _v = value; OnPropertyChanged(nameof(V)); } }
    public double W { get => _w; set { _w = value; OnPropertyChanged(nameof(W)); } }

    public TeachPointRow(TeachPoint point)
    {
        DeviceName = point.DeviceName;
        PositionName = point.PositionName;
        _x = point.X; _y = point.Y; _z = point.Z;
        _u = point.U; _v = point.V; _w = point.W;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
