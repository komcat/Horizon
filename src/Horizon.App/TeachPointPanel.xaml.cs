using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
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

    // Move Map fields
    private readonly MoveMapStore _moveMapStore = new();
    private MoveMapData _moveMapData = new();
    private string? _mmSource;
    private string? _mmTarget;
    private MoveMapEdge? _mmSelectedEdge;
    private bool _mmDragging;
    private string? _mmDragNode;
    private Point _mmDragStartMouse;
    private double _mmDragStartX, _mmDragStartY;

    private const double NodeW = 120;
    private const double NodeH = 36;
    private const double DragThreshold = 3;

    // Zoom state
    private double _mmZoom = 1.0;
    private const double ZoomMin = 0.25;
    private const double ZoomMax = 3.0;
    private const double ZoomStep = 0.15;

    // Pan state
    private bool _mmPanning;
    private Point _mmPanStartMouse;
    private double _mmPanStartOffsetX, _mmPanStartOffsetY;

    private static readonly SolidColorBrush MmNodeBg = new(Color.FromRgb(0x2D, 0x2D, 0x30));
    private static readonly SolidColorBrush MmNodeBorder = new(Color.FromRgb(0x3F, 0x3F, 0x46));
    private static readonly SolidColorBrush MmSourceBrush = new(Color.FromRgb(0x00, 0x78, 0xD4));
    private static readonly SolidColorBrush MmTargetBrush = new(Color.FromRgb(0xFF, 0xB9, 0x00));
    private static readonly SolidColorBrush MmEdgeBrush = new(Color.FromRgb(0x5B, 0x9B, 0xD5));
    private static readonly SolidColorBrush MmEdgeSelBrush = new(Color.FromRgb(0xFF, 0xB9, 0x00));

    /// <summary>Returns the active product name from ProductManager, or empty string.</summary>
    private string ActiveProduct => _context.IsInitialized
        ? _context.ProductManager.ActiveProductName
        : string.Empty;

    public TeachPointPanel(IAppContext context)
    {
        _context = context;
        _teachManager = context.TeachPointManager;

        InitializeComponent();

        BuildDeviceButtons();
        RefreshActiveLabel();
        RefreshPointsGrid();
        StartPolling();

        // Refresh when product changes
        if (_context.IsInitialized)
            _context.ProductManager.ActiveProductChanged += () =>
                Dispatcher.BeginInvoke(() =>
                {
                    RefreshActiveLabel();
                    RefreshPointsGrid();
                    if (MainTabControl.SelectedIndex == 1)
                    {
                        SyncMoveMapDeviceCombo();
                        LoadMoveMap();
                        SyncNodesWithTeachPoints();
                        ClearMoveMapSelection();
                        DrawMoveMapGraph();
                    }
                });
    }

    // ========== Live position polling ==========

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

    // ========== Device button panel ==========

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

    // ========== Active product display ==========

    private void RefreshActiveLabel()
    {
        var active = ActiveProduct;
        if (string.IsNullOrEmpty(active))
        {
            ActiveProductText.Text = "No Product";
            NoProductHint.Text = "(Load a product from Production menu)";
            NoProductHint.Visibility = Visibility.Visible;
        }
        else
        {
            ActiveProductText.Text = active;
            NoProductHint.Visibility = Visibility.Collapsed;
        }
    }

    private void RefreshPointsGrid()
    {
        var productName = ActiveProduct;
        if (string.IsNullOrEmpty(productName) || _selectedDevice == null)
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

    // ========== DataGrid cell edit ==========

    private void PointsDataGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Cancel) return;
        var productName = ActiveProduct;
        if (string.IsNullOrEmpty(productName)) return;
        if (_selectedDevice == null) return;
        if (e.Row.Item is not TeachPointRow row) return;

        // Defer so binding updates first
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _teachManager.UpdatePointCoordinates(productName, _selectedDevice, row.PositionName,
                row.X, row.Y, row.Z, row.U, row.V, row.W);
            _context.ProductManager.SaveActiveProductPositions();
            Log.Information("Teach: Edited {Device}/{Position} coordinates", _selectedDevice, row.PositionName);
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    // ========== Point actions ==========

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
        var productName = ActiveProduct;
        if (string.IsNullOrEmpty(productName)) return;
        if (_selectedDevice == null) return;

        _teachManager.UpdatePoint(productName, _selectedDevice, row.PositionName);
        _context.ProductManager.SaveActiveProductPositions();
        RefreshPointsGrid();
        Log.Information("Teach: Updated {Device}/{Position} from current position", _selectedDevice, row.PositionName);
    }

    private void DeletePoint_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not TeachPointRow row) return;
        var productName = ActiveProduct;
        if (string.IsNullOrEmpty(productName)) return;
        if (_selectedDevice == null) return;

        _teachManager.DeletePoint(productName, _selectedDevice, row.PositionName);
        _context.ProductManager.SaveActiveProductPositions();
        RefreshPointsGrid();
        Log.Information("Teach: Deleted {Device}/{Position}", _selectedDevice, row.PositionName);
    }

    // ========== Teach / Create ==========

    private void TeachCurrent_Click(object sender, RoutedEventArgs e)
    {
        var productName = ActiveProduct;
        if (string.IsNullOrEmpty(productName))
        {
            Log.Warning("Teach: No product loaded");
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
        _context.ProductManager.SaveActiveProductPositions();
        RefreshPointsGrid();
        TeachNameBox.Clear();
        Log.Information("Teach: Taught {Device}/{Position} in product '{Product}'",
            _selectedDevice, positionName, productName);
    }

    private void CreatePoint_Click(object sender, RoutedEventArgs e)
    {
        var productName = ActiveProduct;
        if (string.IsNullOrEmpty(productName))
        {
            Log.Warning("Teach: No product loaded");
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
        _context.ProductManager.SaveActiveProductPositions();
        RefreshPointsGrid();
        TeachNameBox.Clear();
        Log.Information("Teach: Created {Device}/{Position} at ({X:F3}, {Y:F3}, {Z:F3})",
            _selectedDevice, positionName, x, y, z);
    }

    // ================================================================
    //  Move Map — graph editor for motion routing
    // ================================================================

    private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != MainTabControl) return;
        if (MainTabControl.SelectedIndex == 1)
        {
            SyncMoveMapDeviceCombo();
            LoadMoveMap();
            SyncNodesWithTeachPoints();
            UpdateMoveMapButtons();
            DrawMoveMapGraph();
        }
    }

    private void SyncMoveMapDeviceCombo()
    {
        MoveMapDeviceCombo.SelectionChanged -= MoveMapDeviceCombo_SelectionChanged;
        MoveMapDeviceCombo.Items.Clear();

        if (_context.IsInitialized)
        {
            foreach (var name in _context.MotionManager.GetAllDeviceNames())
                MoveMapDeviceCombo.Items.Add(name);
        }

        if (_selectedDevice != null)
            MoveMapDeviceCombo.SelectedItem = _selectedDevice;

        MoveMapDeviceCombo.SelectionChanged += MoveMapDeviceCombo_SelectionChanged;
    }

    private void MoveMapDeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MoveMapDeviceCombo.SelectedItem is string device)
        {
            SelectDevice(device);
            SyncNodesWithTeachPoints();
            ClearMoveMapSelection();
            DrawMoveMapGraph();
        }
    }

    // --- Move Map persistence ---

    private void LoadMoveMap()
    {
        _moveMapData = _moveMapStore.Load(ActiveProduct);
    }

    private void SaveMoveMap()
    {
        _moveMapStore.Save(ActiveProduct, _moveMapData);
    }

    private MoveMapDeviceGraph? GetCurrentDeviceGraph()
    {
        if (_selectedDevice == null) return null;
        return _moveMapData.Devices
            .FirstOrDefault(d => d.DeviceName.Equals(_selectedDevice, StringComparison.OrdinalIgnoreCase));
    }

    private MoveMapDeviceGraph GetOrCreateDeviceGraph(string deviceName)
    {
        var graph = _moveMapData.Devices
            .FirstOrDefault(d => d.DeviceName.Equals(deviceName, StringComparison.OrdinalIgnoreCase));
        if (graph != null) return graph;

        graph = new MoveMapDeviceGraph { DeviceName = deviceName };
        _moveMapData.Devices.Add(graph);
        return graph;
    }

    // --- Node-TeachPoint sync ---

    private void SyncNodesWithTeachPoints()
    {
        if (_selectedDevice == null) return;
        var productName = ActiveProduct;
        if (string.IsNullOrEmpty(productName)) return;

        var teachProduct = _teachManager.GetProduct(productName);
        if (teachProduct == null) return;

        var graph = GetOrCreateDeviceGraph(_selectedDevice);

        var teachNames = teachProduct.Points
            .Where(p => p.DeviceName.Equals(_selectedDevice, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.PositionName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Remove layouts for deleted teach points
        var removedSet = graph.NodeLayouts
            .Where(n => !teachNames.Contains(n.PositionName))
            .Select(n => n.PositionName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        bool changed = false;
        if (removedSet.Count > 0)
        {
            graph.NodeLayouts.RemoveAll(n => removedSet.Contains(n.PositionName));
            graph.Edges.RemoveAll(e => removedSet.Contains(e.From) || removedSet.Contains(e.To));
            changed = true;
        }

        // Add layouts for new teach points
        var existingNames = graph.NodeLayouts
            .Select(n => n.PositionName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var newNames = teachNames.Where(n => !existingNames.Contains(n)).ToList();
        if (newNames.Count > 0)
        {
            const double gap = 4;
            int existingCount = graph.NodeLayouts.Count;
            int cols = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(existingCount + newNames.Count)));

            for (int i = 0; i < newNames.Count; i++)
            {
                int idx = existingCount + i;
                int r = idx / cols;
                int c = idx % cols;
                graph.NodeLayouts.Add(new MoveMapNodeLayout
                {
                    PositionName = newNames[i],
                    CanvasX = 10 + c * (NodeW + gap),
                    CanvasY = 10 + r * (NodeH + gap)
                });
            }
            changed = true;
        }

        if (changed)
            SaveMoveMap();
    }

    // --- Canvas rendering ---

    private void DrawMoveMapGraph()
    {
        MoveMapCanvas.Children.Clear();

        if (_selectedDevice == null)
        {
            MoveMapCanvas.Width = 400;
            MoveMapCanvas.Height = 400;
            return;
        }

        var graph = GetCurrentDeviceGraph();
        if (graph == null || graph.NodeLayouts.Count == 0)
        {
            MoveMapCanvas.Width = 400;
            MoveMapCanvas.Height = 400;
            return;
        }

        // Build node center lookup
        var nodeCenters = new Dictionary<string, Point>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in graph.NodeLayouts)
            nodeCenters[node.PositionName] = new Point(node.CanvasX + NodeW / 2, node.CanvasY + NodeH / 2);

        // Draw edges first (under nodes)
        foreach (var edge in graph.Edges)
        {
            if (!nodeCenters.TryGetValue(edge.From, out var fromCenter)) continue;
            if (!nodeCenters.TryGetValue(edge.To, out var toCenter)) continue;
            DrawEdge(edge, fromCenter, toCenter);
        }

        // Draw nodes on top
        foreach (var node in graph.NodeLayouts)
            DrawNode(node);

        // Resize canvas
        double maxX = graph.NodeLayouts.Max(n => n.CanvasX + NodeW + 30);
        double maxY = graph.NodeLayouts.Max(n => n.CanvasY + NodeH + 30);
        MoveMapCanvas.Width = Math.Max(400, maxX);
        MoveMapCanvas.Height = Math.Max(400, maxY);
    }

    private void DrawNode(MoveMapNodeLayout node)
    {
        bool isSource = node.PositionName.Equals(_mmSource, StringComparison.OrdinalIgnoreCase);
        bool isTarget = node.PositionName.Equals(_mmTarget, StringComparison.OrdinalIgnoreCase);

        var borderBrush = isSource ? MmSourceBrush : isTarget ? MmTargetBrush : MmNodeBorder;
        double thickness = (isSource || isTarget) ? 2.0 : 1.0;

        var text = new TextBlock
        {
            Text = node.PositionName,
            Foreground = Brushes.White,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var border = new Border
        {
            Width = NodeW,
            Height = NodeH,
            Background = MmNodeBg,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(thickness),
            CornerRadius = new CornerRadius(4),
            Child = text,
            Tag = node.PositionName,
            Cursor = Cursors.Hand,
            ToolTip = node.PositionName
        };

        border.MouseDown += Node_MouseDown;

        Canvas.SetLeft(border, node.CanvasX);
        Canvas.SetTop(border, node.CanvasY);
        MoveMapCanvas.Children.Add(border);
    }

    private void DrawEdge(MoveMapEdge edge, Point fromCenter, Point toCenter)
    {
        bool isSelected = _mmSelectedEdge != null &&
                          _mmSelectedEdge.From.Equals(edge.From, StringComparison.OrdinalIgnoreCase) &&
                          _mmSelectedEdge.To.Equals(edge.To, StringComparison.OrdinalIgnoreCase);

        var brush = isSelected ? MmEdgeSelBrush : MmEdgeBrush;
        double thickness = isSelected ? 2.5 : 1.5;

        var line = new Line
        {
            X1 = fromCenter.X, Y1 = fromCenter.Y,
            X2 = toCenter.X, Y2 = toCenter.Y,
            Stroke = brush,
            StrokeThickness = thickness,
            Tag = edge,
            Cursor = Cursors.Hand
        };
        line.MouseDown += Edge_MouseDown;
        MoveMapCanvas.Children.Add(line);

        // Arrowhead at "to" end
        DrawArrowhead(fromCenter, toCenter, brush, edge);
        if (edge.Bidirectional)
            DrawArrowhead(toCenter, fromCenter, brush, edge);
    }

    private void DrawArrowhead(Point from, Point to, Brush brush, MoveMapEdge edge)
    {
        double dx = to.X - from.X;
        double dy = to.Y - from.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1) return;

        double ux = dx / len;
        double uy = dy / len;

        // Intersect with target node rectangle
        double halfW = NodeW / 2;
        double halfH = NodeH / 2;
        double t;
        if (Math.Abs(ux) < 0.001)
            t = halfH / Math.Abs(uy);
        else if (Math.Abs(uy) < 0.001)
            t = halfW / Math.Abs(ux);
        else
            t = Math.Min(halfW / Math.Abs(ux), halfH / Math.Abs(uy));

        double tipX = to.X - ux * t;
        double tipY = to.Y - uy * t;

        const double arrowLen = 10;
        const double arrowW = 5;

        double px = -uy;
        double py = ux;

        var poly = new Polygon
        {
            Points = new PointCollection
            {
                new Point(tipX, tipY),
                new Point(tipX - ux * arrowLen + px * arrowW, tipY - uy * arrowLen + py * arrowW),
                new Point(tipX - ux * arrowLen - px * arrowW, tipY - uy * arrowLen - py * arrowW)
            },
            Fill = brush,
            Tag = edge,
            Cursor = Cursors.Hand
        };
        poly.MouseDown += Edge_MouseDown;
        MoveMapCanvas.Children.Add(poly);
    }

    // --- Node interaction ---

    private void Node_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (sender is not Border border || border.Tag is not string name) return;

        _mmDragNode = name;
        _mmDragStartMouse = e.GetPosition(MoveMapCanvas);
        _mmDragging = false;

        var graph = GetCurrentDeviceGraph();
        var layout = graph?.NodeLayouts.FirstOrDefault(n =>
            n.PositionName.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (layout != null)
        {
            _mmDragStartX = layout.CanvasX;
            _mmDragStartY = layout.CanvasY;
        }

        MoveMapCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void MoveMapCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            if (_mmDragNode != null) { _mmDragNode = null; _mmDragging = false; }
            if (_mmPanning) _mmPanning = false;
            MoveMapCanvas.ReleaseMouseCapture();
            return;
        }

        // Node drag
        if (_mmDragNode != null)
        {
            var pos = e.GetPosition(MoveMapCanvas);
            double dx = pos.X - _mmDragStartMouse.X;
            double dy = pos.Y - _mmDragStartMouse.Y;

            if (!_mmDragging && Math.Sqrt(dx * dx + dy * dy) < DragThreshold)
                return;
            _mmDragging = true;

            var graph = GetCurrentDeviceGraph();
            if (graph == null) return;

            int idx = graph.NodeLayouts.FindIndex(n =>
                n.PositionName.Equals(_mmDragNode, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) return;

            double newX = Math.Max(0, _mmDragStartX + dx);
            double newY = Math.Max(0, _mmDragStartY + dy);

            graph.NodeLayouts[idx] = graph.NodeLayouts[idx] with { CanvasX = newX, CanvasY = newY };
            DrawMoveMapGraph();
            return;
        }

        // Canvas pan
        var panPos = e.GetPosition(MoveMapScrollViewer);
        double panDx = panPos.X - _mmPanStartMouse.X;
        double panDy = panPos.Y - _mmPanStartMouse.Y;

        if (!_mmPanning && Math.Sqrt(panDx * panDx + panDy * panDy) < DragThreshold)
            return;
        _mmPanning = true;
        MoveMapCanvas.Cursor = Cursors.ScrollAll;

        MoveMapScrollViewer.ScrollToHorizontalOffset(_mmPanStartOffsetX - panDx);
        MoveMapScrollViewer.ScrollToVerticalOffset(_mmPanStartOffsetY - panDy);
    }

    private void MoveMapCanvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        // Node drag/click release
        if (_mmDragNode != null)
        {
            string name = _mmDragNode;
            bool wasDragging = _mmDragging;
            _mmDragNode = null;
            _mmDragging = false;
            MoveMapCanvas.ReleaseMouseCapture();

            if (wasDragging)
            {
                SaveMoveMap();
                DrawMoveMapGraph();
            }
            else
            {
                HandleNodeClick(name);
            }
            return;
        }

        // Pan release
        bool wasPanning = _mmPanning;
        _mmPanning = false;
        MoveMapCanvas.Cursor = Cursors.Arrow;
        MoveMapCanvas.ReleaseMouseCapture();

        if (!wasPanning)
        {
            // Was a simple background click — clear selection
            ClearMoveMapSelection();
            DrawMoveMapGraph();
        }
    }

    private void HandleNodeClick(string name)
    {
        _mmSelectedEdge = null;

        if (_mmSource == null)
        {
            _mmSource = name;
            _mmTarget = null;
        }
        else if (_mmSource.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            // Click same node — deselect
            _mmSource = null;
            _mmTarget = null;
        }
        else
        {
            _mmTarget = name;
        }

        UpdateMoveMapButtons();
        DrawMoveMapGraph();
    }

    // --- Edge interaction ---

    private void Edge_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (sender is not FrameworkElement fe || fe.Tag is not MoveMapEdge edge) return;

        _mmSource = null;
        _mmTarget = null;
        _mmSelectedEdge = edge;

        UpdateMoveMapButtons();
        DrawMoveMapGraph();
        e.Handled = true;
    }

    // --- Canvas background click / pan ---

    private void MoveMapCanvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (_mmDragNode != null) return;

        // Start potential pan
        _mmPanning = false;
        _mmPanStartMouse = e.GetPosition(MoveMapScrollViewer);
        _mmPanStartOffsetX = MoveMapScrollViewer.HorizontalOffset;
        _mmPanStartOffsetY = MoveMapScrollViewer.VerticalOffset;
        MoveMapCanvas.CaptureMouse();
    }

    // --- Zoom & pan ---

    private void ApplyZoom(double newZoom)
    {
        _mmZoom = Math.Clamp(newZoom, ZoomMin, ZoomMax);
        MoveMapScaleTransform.ScaleX = _mmZoom;
        MoveMapScaleTransform.ScaleY = _mmZoom;
        ZoomLevelText.Text = $"{(int)(_mmZoom * 100)}%";
    }

    private void MoveMapScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;

        // Zoom centered on mouse position
        var mousePos = e.GetPosition(MoveMapScrollViewer);
        double viewportRelX = (MoveMapScrollViewer.HorizontalOffset + mousePos.X) / (_mmZoom * MoveMapCanvas.Width);
        double viewportRelY = (MoveMapScrollViewer.VerticalOffset + mousePos.Y) / (_mmZoom * MoveMapCanvas.Height);

        double oldZoom = _mmZoom;
        double newZoom = e.Delta > 0 ? _mmZoom + ZoomStep : _mmZoom - ZoomStep;
        ApplyZoom(newZoom);

        // Adjust scroll to keep mouse position stable
        double newOffsetX = viewportRelX * _mmZoom * MoveMapCanvas.Width - mousePos.X;
        double newOffsetY = viewportRelY * _mmZoom * MoveMapCanvas.Height - mousePos.Y;
        MoveMapScrollViewer.ScrollToHorizontalOffset(Math.Max(0, newOffsetX));
        MoveMapScrollViewer.ScrollToVerticalOffset(Math.Max(0, newOffsetY));

        e.Handled = true;
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ApplyZoom(_mmZoom + ZoomStep);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ApplyZoom(_mmZoom - ZoomStep);
    private void ZoomReset_Click(object sender, RoutedEventArgs e) => ApplyZoom(1.0);

    // --- Toolbar buttons ---

    private void AddEdge_Click(object sender, RoutedEventArgs e)
    {
        if (_mmSource == null || _mmTarget == null || _selectedDevice == null) return;

        var graph = GetOrCreateDeviceGraph(_selectedDevice);

        // Reject duplicates
        bool exists = graph.Edges.Any(ed =>
            (ed.From.Equals(_mmSource, StringComparison.OrdinalIgnoreCase) &&
             ed.To.Equals(_mmTarget, StringComparison.OrdinalIgnoreCase)) ||
            (ed.From.Equals(_mmTarget, StringComparison.OrdinalIgnoreCase) &&
             ed.To.Equals(_mmSource, StringComparison.OrdinalIgnoreCase)));

        if (exists)
        {
            Log.Warning("MoveMap: Edge already exists between {From} and {To}", _mmSource, _mmTarget);
            return;
        }

        var from = _mmSource;
        var to = _mmTarget;
        graph.Edges.Add(new MoveMapEdge { From = from, To = to, Bidirectional = true });
        SaveMoveMap();
        ClearMoveMapSelection();
        DrawMoveMapGraph();
        Log.Information("MoveMap: Added edge {From} <-> {To}", from, to);
    }

    private void RemoveEdge_Click(object sender, RoutedEventArgs e)
    {
        if (_mmSelectedEdge == null || _selectedDevice == null) return;

        var graph = GetCurrentDeviceGraph();
        if (graph == null) return;

        var removed = _mmSelectedEdge;
        graph.Edges.Remove(_mmSelectedEdge);
        SaveMoveMap();
        ClearMoveMapSelection();
        DrawMoveMapGraph();
        Log.Information("MoveMap: Removed edge {From} -> {To}", removed.From, removed.To);
    }

    private void AutoLayout_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDevice == null) return;

        var graph = GetCurrentDeviceGraph();
        if (graph == null || graph.NodeLayouts.Count == 0) return;

        // Separate connected vs unconnected nodes
        var connectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var edge in graph.Edges)
        {
            connectedNames.Add(edge.From);
            connectedNames.Add(edge.To);
        }

        var connected = new List<int>();
        var unconnected = new List<int>();
        for (int i = 0; i < graph.NodeLayouts.Count; i++)
        {
            if (connectedNames.Contains(graph.NodeLayouts[i].PositionName))
                connected.Add(i);
            else
                unconnected.Add(i);
        }

        const double gap = 4;
        double startY = 10;

        // Connected nodes: circular layout
        if (connected.Count > 0)
        {
            int n = connected.Count;
            double radius = Math.Max(80, n * 30);
            double cx = radius + NodeW / 2 + 10;
            double cy = startY + radius + NodeH / 2;

            for (int i = 0; i < n; i++)
            {
                double angle = 2 * Math.PI * i / n - Math.PI / 2;
                graph.NodeLayouts[connected[i]] = graph.NodeLayouts[connected[i]] with
                {
                    CanvasX = cx + radius * Math.Cos(angle) - NodeW / 2,
                    CanvasY = cy + radius * Math.Sin(angle) - NodeH / 2
                };
            }

            startY = cy + radius + NodeH / 2 + 20;
        }

        // Unconnected nodes: tight grid, 4px gap
        if (unconnected.Count > 0)
        {
            int cols = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(unconnected.Count)));
            for (int i = 0; i < unconnected.Count; i++)
            {
                int r = i / cols;
                int c = i % cols;
                graph.NodeLayouts[unconnected[i]] = graph.NodeLayouts[unconnected[i]] with
                {
                    CanvasX = 10 + c * (NodeW + gap),
                    CanvasY = startY + r * (NodeH + gap)
                };
            }
        }

        SaveMoveMap();
        DrawMoveMapGraph();
    }

    // --- Selection state ---

    private void ClearMoveMapSelection()
    {
        _mmSource = null;
        _mmTarget = null;
        _mmSelectedEdge = null;
        UpdateMoveMapButtons();
    }

    private void UpdateMoveMapButtons()
    {
        AddEdgeBtn.IsEnabled = _mmSource != null && _mmTarget != null;
        RemoveEdgeBtn.IsEnabled = _mmSelectedEdge != null;

        if (_mmSource != null && _mmTarget != null)
            MoveMapHintText.Text = $"Source: {_mmSource}  \u2192  Target: {_mmTarget}   (click Add Edge)";
        else if (_mmSource != null)
            MoveMapHintText.Text = $"Source: {_mmSource}   (click another node to set target)";
        else if (_mmSelectedEdge != null)
            MoveMapHintText.Text = $"Edge: {_mmSelectedEdge.From} \u2192 {_mmSelectedEdge.To}   (click Remove Edge to delete)";
        else
            MoveMapHintText.Text = "Click a node to select source, then click another to select target. Drag nodes to reposition.";
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
