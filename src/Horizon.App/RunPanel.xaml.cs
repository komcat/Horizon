using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Horizon.Core;
using Horizon.Process;
using Horizon.Process.Models;
using Horizon.Process.Recipe;
using Serilog;

namespace Horizon.App;

// ── Deck model ──────────────────────────────────────────────────────

public class DeckSlot
{
    public string Name { get; set; } = "";
    public List<string> Programs { get; set; } = new();
}

public class ChainLink
{
    public int FromIndex { get; set; }
    public int ToIndex { get; set; }
    public string Condition { get; set; } = "";
    public int DelaySec { get; set; } = 3;
}

public class Deck
{
    public string Name { get; set; } = "";
    public List<DeckSlot> Slots { get; set; } = new();
    public List<ChainLink> ChainLinks { get; set; } = new();
}

// ── RunPanel ────────────────────────────────────────────────────────

public partial class RunPanel : UserControl
{
    private readonly IAppContext _context;
    private Deck? _deck;
    private string? _currentDeckPath;

    // Execution state
    private CancellationTokenSource? _cts;
    private bool _isRunning;
    private bool _isPaused;
    private bool _isRunningRecipe;
    private ProcessContext? _lastContext;
    private RecipeContext? _activeRecipeContext;

    // Execution monitor
    private readonly ObservableCollection<RunWatchItem> _monitorWatchItems = new();

    // Chain editor state
    private int _chainSourceIdx = -1;
    private int _chainTargetIdx = -1;
    private ChainLink? _selectedChainLink;
    private readonly List<Border> _chainBlocks = new();

    // Layout constants
    private const double BlockWidth = 280;
    private const double BlockHeight = 40;
    private const double BlockLeft = 60;
    private const double BlockStartY = 20;
    private const double BlockSpacingY = 90;

    private static readonly Brush YellowBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00));
    private static readonly Brush GreenBrush = new SolidColorBrush(Color.FromRgb(0x00, 0xCC, 0x00));
    private static readonly Brush RedBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x44, 0x44));
    private static readonly Brush BlueBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4));
    private static readonly Brush OrangeBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xB9, 0x00));
    private static readonly Brush LinkBrush = new SolidColorBrush(Color.FromRgb(0x5B, 0x9B, 0xD5));
    private static readonly Brush BlockBorderNormal = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46));
    private static readonly Brush BlockBg = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30));
    private static readonly Brush DimGray = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));
    private static readonly Brush CanvasLabelBg = new SolidColorBrush(Color.FromArgb(0xDD, 0x1E, 0x1E, 0x1E));

    private static readonly string DefaultDeckDir =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Horizon", "decks");

    private static readonly string DefaultProcessDir =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Horizon", "processes");

    private static readonly string DefaultRecipeDir =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Horizon", "recipes");

    private static readonly string LastDeckFile =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Horizon", "decks", ".lastdeck");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public RunPanel(IAppContext context)
    {
        _context = context;
        InitializeComponent();
        MonitorWatchGrid.ItemsSource = _monitorWatchItems;
        Directory.CreateDirectory(DefaultDeckDir);
        RestoreLastDeck();
    }

    private void RestoreLastDeck()
    {
        try
        {
            if (!File.Exists(LastDeckFile)) return;
            var lastPath = File.ReadAllText(LastDeckFile).Trim();
            if (string.IsNullOrEmpty(lastPath) || !File.Exists(lastPath)) return;

            var json = File.ReadAllText(lastPath);
            _deck = JsonSerializer.Deserialize<Deck>(json, JsonOpts);
            _currentDeckPath = lastPath;
            RefreshAll();
            Log.Information("[RunPanel] Restored last deck: {Path}", lastPath);
        }
        catch { }
    }

    private void SaveLastDeckPath(string path)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LastDeckFile)!);
            File.WriteAllText(LastDeckFile, path);
        }
        catch { }
    }

    // ── Execute: Run (selected program only) / Pause / Stop ─────────

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        Log.Information("[RunPanel] Run clicked");
        if (_isRunning) return;

        // Delegate to chain execution if enabled
        if (EnableChainCheck.IsChecked == true)
        {
            // Find the selected program's index in the flattened list
            var (selPath, selSi, selPi) = GetSelectedProgram();
            int startFrom = -1;
            if (selPath != null)
            {
                var flat = FlattenDeckPrograms();
                for (int i = 0; i < flat.Count; i++)
                {
                    if (flat[i].SlotIdx == selSi && flat[i].ProgIdx == selPi)
                    { startFrom = i; break; }
                }
            }
            RunChainAsync(startFrom);
            return;
        }

        var (programPath, slotIndex, progIndex) = GetSelectedProgram();
        if (programPath == null)
        {
            Log.Warning("[RunPanel] No program selected — select a program in the tree");
            return;
        }

        var isRecipe = System.IO.Path.GetExtension(programPath)
            .Equals(".hrcp", StringComparison.OrdinalIgnoreCase);

        if (isRecipe)
        {
            var engine = _context.RecipeEngine;
            if (!engine.TryAcquire("RunPanel"))
            {
                MessageBox.Show($"Recipe engine busy — {engine.RunningOwner} is running.",
                    "Busy", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _isRunning = true;
            _isRunningRecipe = true;
            _isPaused = false;
            _cts = new CancellationTokenSource();
            SetButtonStates(running: true);
            StartMonitor(System.IO.Path.GetFileName(programPath));

            var slotNode = (TreeViewItem)ExecuteTree.Items[slotIndex];
            var progNode = (TreeViewItem)slotNode.Items[progIndex];

            try
            {
                progNode.Foreground = YellowBrush;
                Log.Information("[RunPanel] Running recipe: {Path}", System.IO.Path.GetFileName(programPath));

                var (success, rctx) = await RunRecipeAsync(engine, programPath, _cts.Token);
                _lastContext = RecipeToProcessContext(rctx);
                progNode.Foreground = success ? GreenBrush : RedBrush;
                MonitorStatusText.Text = success ? "Completed" : "Failed";
            }
            catch (OperationCanceledException)
            {
                progNode.Foreground = RedBrush;
                MonitorStatusText.Text = "Stopped";
                Log.Warning("[RunPanel] Recipe cancelled by user");
            }
            catch (Exception ex)
            {
                progNode.Foreground = RedBrush;
                MonitorStatusText.Text = "Error";
                Log.Error(ex, "[RunPanel] Recipe execution error");
            }
            finally
            {
                _isRunning = false;
                _isRunningRecipe = false;
                _isPaused = false;
                _activeRecipeContext = null;
                engine.StepByStepMode = false;
                engine.OnStepStarted = null;
                engine.OnStepCompleted = null;
                engine.OnLoopIteration = null;
                engine.OnLog = null;
                engine.OnContextChanged = null;
                engine.Release();
                SetButtonStates(running: false);
                RefreshMonitorWatch();
            }
        }
        else
        {
            var engine = _context.ProcessEngine;
            if (!engine.TryAcquire("RunPanel"))
            {
                Log.Warning("[RunPanel] Cannot run — engine locked by {Owner}", engine.RunningOwner);
                MessageBox.Show($"Engine is busy — {engine.RunningOwner} is running.",
                    "Busy", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _isRunning = true;
            _isRunningRecipe = false;
            _isPaused = false;
            _cts = new CancellationTokenSource();
            SetButtonStates(running: true);
            StartMonitor(System.IO.Path.GetFileName(programPath));

            var slotNode = (TreeViewItem)ExecuteTree.Items[slotIndex];
            var progNode = (TreeViewItem)slotNode.Items[progIndex];

            try
            {
                progNode.Foreground = YellowBrush;
                Log.Information("[RunPanel] Running: {Path}", System.IO.Path.GetFileName(programPath));

                var (success, ctx) = await RunProgramAsync(engine, _context.PluginRegistry, programPath, _cts.Token);
                _lastContext = ctx;

                progNode.Foreground = success ? GreenBrush : RedBrush;
                MonitorStatusText.Text = success ? "Completed" : "Failed";
                RefreshMonitorWatchFromProcess(ctx);
                Log.Information("[RunPanel] Program {Name} — {Result}",
                    System.IO.Path.GetFileName(programPath), success ? "OK" : "FAIL");
            }
            catch (OperationCanceledException)
            {
                progNode.Foreground = RedBrush;
                MonitorStatusText.Text = "Stopped";
                Log.Warning("[RunPanel] Cancelled by user");
            }
            catch (Exception ex)
            {
                progNode.Foreground = RedBrush;
                MonitorStatusText.Text = "Error";
                Log.Error(ex, "[RunPanel] Execution error");
            }
            finally
            {
                _isRunning = false;
                _isPaused = false;
                engine.StepByStepMode = false;
                engine.OnStepStarted = null;
                engine.OnStepCompleted = null;
                engine.OnLoopIteration = null;
                engine.Release();
                SetButtonStates(running: false);
            }
        }
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (!_isRunning) return;

        if (!_isPaused)
        {
            Log.Information("[RunPanel] Pause — will pause after current step");
            if (_isRunningRecipe)
                _context.RecipeEngine.StepByStepMode = true;
            else
                _context.ProcessEngine.StepByStepMode = true;
            _isPaused = true;
            PauseIconImg.Source = new BitmapImage(new Uri("pack://application:,,,/Assets/StepMode.png"));
            PauseLabel.Text = " Continue";
            PauseLabel.Foreground = GreenBrush;
        }
        else
        {
            Log.Information("[RunPanel] Resume");
            if (_isRunningRecipe)
            {
                _context.RecipeEngine.StepByStepMode = false;
                _context.RecipeEngine.ContinueStep();
            }
            else
            {
                _context.ProcessEngine.StepByStepMode = false;
                _context.ProcessEngine.ContinueStep();
            }
            _isPaused = false;
            PauseIconImg.Source = new BitmapImage(new Uri("pack://application:,,,/Assets/Pause.png"));
            PauseLabel.Text = " Pause";
            PauseLabel.Foreground = YellowBrush;
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (!_isRunning) return;
        Log.Information("[RunPanel] Stop — cancelling and stopping hardware");
        _cts?.Cancel();
        if (_isRunningRecipe)
        {
            _context.RecipeEngine.StepByStepMode = false;
            _context.RecipeEngine.ContinueStep();
        }
        else
        {
            _context.ProcessEngine.StepByStepMode = false;
            _context.ProcessEngine.ContinueStep();
        }
        StopAllHardware();
    }

    public void StopExecution()
    {
        _cts?.Cancel();
        _context.ProcessEngine.StepByStepMode = false;
        _context.ProcessEngine.ContinueStep();
        _context.RecipeEngine.StepByStepMode = false;
        _context.RecipeEngine.ContinueStep();
        StopAllHardware();
    }

    // ── Chain Editor ─────────────────────────────────────────────────

    private void ChainEdit_Click(object sender, RoutedEventArgs e)
    {
        Log.Information("[RunPanel] Chain Exec clicked");
        if (_deck == null)
        {
            MessageBox.Show("Create or load a deck first.", "Info",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ShowRightPanel(RightPanel.Chain);
        ClearChainSelection();
        RefreshChainCanvas();
    }

    private void ChainEditorClose_Click(object sender, RoutedEventArgs e)
    {
        Log.Information("[RunPanel] Chain Editor closed");
        ClearChainSelection();
        ShowRightPanel(RightPanel.Empty);
    }

    private List<(string SlotName, string Path, int SlotIdx, int ProgIdx)> FlattenDeckPrograms()
    {
        var result = new List<(string, string, int, int)>();
        if (_deck == null) return result;
        for (int si = 0; si < _deck.Slots.Count; si++)
        {
            var slot = _deck.Slots[si];
            for (int pi = 0; pi < slot.Programs.Count; pi++)
                result.Add((slot.Name, slot.Programs[pi], si, pi));
        }
        return result;
    }

    // ── Canvas rendering ─────────────────────────────────────────────

    private void RefreshChainCanvas()
    {
        ChainCanvas.Children.Clear();
        _chainBlocks.Clear();
        if (_deck == null) return;

        var programs = FlattenDeckPrograms();
        if (programs.Count == 0) return;

        // Prune invalid links
        _deck.ChainLinks.RemoveAll(l =>
            l.FromIndex < 0 || l.FromIndex >= programs.Count ||
            l.ToIndex < 0 || l.ToIndex >= programs.Count);

        // Draw blocks
        for (int i = 0; i < programs.Count; i++)
        {
            var (slotName, path, _, _) = programs[i];
            var isRecipe = path.EndsWith(".hrcp", StringComparison.OrdinalIgnoreCase);
            var typeTag = isRecipe ? " [R]" : "";
            var displayName = $"[{slotName}] {System.IO.Path.GetFileNameWithoutExtension(path)}{typeTag}";

            var label = new TextBlock
            {
                Text = displayName,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                Foreground = Brushes.White,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 8, 0)
            };

            var block = new Border
            {
                Width = BlockWidth,
                Height = BlockHeight,
                Background = BlockBg,
                BorderBrush = BlockBorderNormal,
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(4),
                Child = label,
                Tag = i,
                Cursor = Cursors.Hand,
                ToolTip = path
            };
            block.MouseLeftButtonDown += ChainBlock_Click;

            Canvas.SetLeft(block, BlockLeft);
            Canvas.SetTop(block, BlockStartY + i * BlockSpacingY);

            // Index number
            var idxLabel = new TextBlock
            {
                Text = $"{i + 1}",
                FontFamily = new FontFamily("Consolas"),
                FontSize = 10,
                Foreground = DimGray,
                TextAlignment = TextAlignment.Right,
                Width = 20
            };
            Canvas.SetLeft(idxLabel, BlockLeft - 28);
            Canvas.SetTop(idxLabel, BlockStartY + i * BlockSpacingY + 12);

            ChainCanvas.Children.Add(idxLabel);
            ChainCanvas.Children.Add(block);
            _chainBlocks.Add(block);
        }

        // Draw links
        foreach (var link in _deck.ChainLinks)
            DrawLink(link, programs.Count);

        // Size canvas
        var totalHeight = BlockStartY + programs.Count * BlockSpacingY + 40;
        ChainCanvas.Width = Math.Max(400, BlockLeft + BlockWidth + 160);
        ChainCanvas.Height = Math.Max(400, totalHeight);
    }

    private void DrawLink(ChainLink link, int programCount)
    {
        if (link.FromIndex < 0 || link.FromIndex >= programCount ||
            link.ToIndex < 0 || link.ToIndex >= programCount)
            return;

        bool isSelected = link == _selectedChainLink;
        var brush = isSelected ? OrangeBrush : LinkBrush;
        var thickness = isSelected ? 2.5 : 1.5;

        double fromCenterX = BlockLeft + BlockWidth / 2;
        double fromBottomY = BlockStartY + link.FromIndex * BlockSpacingY + BlockHeight;
        double toCenterX = BlockLeft + BlockWidth / 2;
        double toTopY = BlockStartY + link.ToIndex * BlockSpacingY;

        bool adjacent = Math.Abs(link.ToIndex - link.FromIndex) == 1 && link.ToIndex > link.FromIndex;

        if (adjacent)
        {
            // Straight vertical line
            var line = new Line
            {
                X1 = fromCenterX, Y1 = fromBottomY,
                X2 = toCenterX, Y2 = toTopY,
                Stroke = brush, StrokeThickness = thickness,
                Tag = link, Cursor = Cursors.Hand
            };
            line.MouseLeftButtonDown += ChainLinkLine_Click;
            ChainCanvas.Children.Add(line);

            // Arrow at target
            DrawArrow(toCenterX, toTopY, link.ToIndex > link.FromIndex, brush, link);

            // Condition label at midpoint
            var midY = (fromBottomY + toTopY) / 2;
            AddConditionLabel(link, fromCenterX + 12, midY - 8, brush);
        }
        else
        {
            // Route around the right side of blocks
            double xOffset = BlockWidth / 2 + 25;
            var pathFig = new PathFigure { StartPoint = new Point(fromCenterX, fromBottomY) };
            pathFig.Segments.Add(new LineSegment(new Point(fromCenterX + xOffset, fromBottomY), true));
            pathFig.Segments.Add(new LineSegment(new Point(toCenterX + xOffset, toTopY), true));
            pathFig.Segments.Add(new LineSegment(new Point(toCenterX, toTopY), true));

            var pathGeom = new PathGeometry();
            pathGeom.Figures.Add(pathFig);

            var pathShape = new System.Windows.Shapes.Path
            {
                Data = pathGeom,
                Stroke = brush, StrokeThickness = thickness,
                Tag = link, Cursor = Cursors.Hand
            };
            pathShape.MouseLeftButtonDown += ChainLinkLine_Click;
            ChainCanvas.Children.Add(pathShape);

            // Arrow at target
            DrawArrow(toCenterX, toTopY, link.ToIndex > link.FromIndex, brush, link);

            // Condition label on the right side
            var midY = (fromBottomY + toTopY) / 2;
            AddConditionLabel(link, fromCenterX + xOffset + 6, midY - 8, brush);
        }
    }

    private void DrawArrow(double x, double y, bool pointDown, Brush brush, ChainLink link)
    {
        double dir = pointDown ? 1 : -1;
        var arrow = new Polygon
        {
            Points = new PointCollection
            {
                new Point(x, y),
                new Point(x - 5, y - dir * 8),
                new Point(x + 5, y - dir * 8)
            },
            Fill = brush,
            Tag = link, Cursor = Cursors.Hand
        };
        arrow.MouseLeftButtonDown += ChainLinkLine_Click;
        ChainCanvas.Children.Add(arrow);
    }

    private void AddConditionLabel(ChainLink link, double x, double y, Brush brush)
    {
        var condText = string.IsNullOrWhiteSpace(link.Condition) ? "(no condition)" : link.Condition;
        if (link.DelaySec > 0)
            condText += $"  [{link.DelaySec}s]";

        var label = new TextBlock
        {
            Text = condText,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 10,
            Foreground = brush,
            FontStyle = string.IsNullOrWhiteSpace(link.Condition) ? FontStyles.Italic : FontStyles.Normal,
            Background = CanvasLabelBg,
            Padding = new Thickness(3, 1, 3, 1),
            Cursor = Cursors.Hand,
            Tag = link
        };
        label.MouseLeftButtonDown += ChainLinkLine_Click;
        Canvas.SetLeft(label, x);
        Canvas.SetTop(label, y);
        ChainCanvas.Children.Add(label);
    }

    // ── Block / Link selection ───────────────────────────────────────

    private void ChainBlock_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border block || block.Tag is not int idx) return;
        e.Handled = true;

        if (_chainSourceIdx < 0)
        {
            // First click: select source
            ClearChainSelection();
            _chainSourceIdx = idx;
            block.BorderBrush = BlueBrush;
            AddLinkBtn.IsEnabled = false;
        }
        else if (_chainSourceIdx == idx)
        {
            // Click same block: deselect
            ClearChainSelection();
        }
        else
        {
            // Second click: select target
            if (_chainTargetIdx >= 0 && _chainTargetIdx < _chainBlocks.Count)
                _chainBlocks[_chainTargetIdx].BorderBrush = BlockBorderNormal;

            _chainTargetIdx = idx;
            block.BorderBrush = OrangeBrush;
            AddLinkBtn.IsEnabled = true;
        }
    }

    private void ClearChainSelection()
    {
        _chainSourceIdx = -1;
        _chainTargetIdx = -1;
        _selectedChainLink = null;
        AddLinkBtn.IsEnabled = false;
        RemoveLinkBtn.IsEnabled = false;
        LinkEditorBar.Visibility = Visibility.Collapsed;

        foreach (var block in _chainBlocks)
            block.BorderBrush = BlockBorderNormal;
    }

    private void ChainLinkLine_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not ChainLink link) return;
        e.Handled = true;

        // Save link reference before redraw
        var fromIdx = link.FromIndex;
        var toIdx = link.ToIndex;

        _selectedChainLink = link;
        _chainSourceIdx = -1;
        _chainTargetIdx = -1;
        AddLinkBtn.IsEnabled = false;
        RemoveLinkBtn.IsEnabled = true;

        // Redraw with selection highlight
        RefreshChainCanvas();

        // Re-find the link after redraw (RefreshChainCanvas doesn't clear _selectedChainLink)
        _selectedChainLink = _deck?.ChainLinks.FirstOrDefault(l =>
            l.FromIndex == fromIdx && l.ToIndex == toIdx);

        if (_selectedChainLink != null)
        {
            RemoveLinkBtn.IsEnabled = true;

            // Highlight connected blocks
            if (_selectedChainLink.FromIndex < _chainBlocks.Count)
                _chainBlocks[_selectedChainLink.FromIndex].BorderBrush = BlueBrush;
            if (_selectedChainLink.ToIndex < _chainBlocks.Count)
                _chainBlocks[_selectedChainLink.ToIndex].BorderBrush = OrangeBrush;

            ShowLinkEditor(_selectedChainLink);
        }
    }

    private void ShowLinkEditor(ChainLink link)
    {
        var programs = FlattenDeckPrograms();
        var fromName = link.FromIndex < programs.Count
            ? System.IO.Path.GetFileNameWithoutExtension(programs[link.FromIndex].Path) : "?";
        var toName = link.ToIndex < programs.Count
            ? System.IO.Path.GetFileNameWithoutExtension(programs[link.ToIndex].Path) : "?";

        LinkEditorTitle.Text = $"Link: {fromName}  \u2192  {toName}";
        LinkConditionBox.Text = link.Condition;
        LinkDelayBox.Text = link.DelaySec.ToString();
        LinkEditorBar.Visibility = Visibility.Visible;
    }

    // ── Link CRUD ────────────────────────────────────────────────────

    private void AddLink_Click(object sender, RoutedEventArgs e)
    {
        if (_deck == null || _chainSourceIdx < 0 || _chainTargetIdx < 0) return;
        if (_chainSourceIdx == _chainTargetIdx) return;

        if (_deck.ChainLinks.Any(l => l.FromIndex == _chainSourceIdx && l.ToIndex == _chainTargetIdx))
        {
            MessageBox.Show("This link already exists.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var link = new ChainLink
        {
            FromIndex = _chainSourceIdx,
            ToIndex = _chainTargetIdx,
            Condition = "",
            DelaySec = 3
        };
        _deck.ChainLinks.Add(link);
        Log.Information("[RunPanel] Added chain link: {From} \u2192 {To}", _chainSourceIdx, _chainTargetIdx);

        ClearChainSelection();
        RefreshChainCanvas();

        // Auto-select the new link for editing
        _selectedChainLink = link;
        RemoveLinkBtn.IsEnabled = true;
        if (link.FromIndex < _chainBlocks.Count)
            _chainBlocks[link.FromIndex].BorderBrush = BlueBrush;
        if (link.ToIndex < _chainBlocks.Count)
            _chainBlocks[link.ToIndex].BorderBrush = OrangeBrush;
        ShowLinkEditor(link);
    }

    private void RemoveLink_Click(object sender, RoutedEventArgs e)
    {
        if (_deck == null || _selectedChainLink == null) return;

        Log.Information("[RunPanel] Removed chain link: {From} \u2192 {To}",
            _selectedChainLink.FromIndex, _selectedChainLink.ToIndex);
        _deck.ChainLinks.Remove(_selectedChainLink);
        ClearChainSelection();
        RefreshChainCanvas();
    }

    private void ApplyLinkEdit_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedChainLink == null) return;

        _selectedChainLink.Condition = LinkConditionBox.Text.Trim();
        if (int.TryParse(LinkDelayBox.Text.Trim(), out var delay) && delay >= 0)
            _selectedChainLink.DelaySec = delay;

        Log.Information("[RunPanel] Updated link {From}\u2192{To}: condition=\"{Cond}\", delay={Delay}s",
            _selectedChainLink.FromIndex, _selectedChainLink.ToIndex,
            _selectedChainLink.Condition, _selectedChainLink.DelaySec);

        // Redraw and re-select
        var savedLink = _selectedChainLink;
        RefreshChainCanvas();
        _selectedChainLink = savedLink;
        RemoveLinkBtn.IsEnabled = true;
        if (savedLink.FromIndex < _chainBlocks.Count)
            _chainBlocks[savedLink.FromIndex].BorderBrush = BlueBrush;
        if (savedLink.ToIndex < _chainBlocks.Count)
            _chainBlocks[savedLink.ToIndex].BorderBrush = OrangeBrush;
        ShowLinkEditor(savedLink);
    }

    // ── Chain Run (graph traversal) ──────────────────────────────────

    private void ChainRun_Click(object sender, RoutedEventArgs e)
    {
        RunChainAsync(startFromIndex: -1);
    }

    /// <summary>
    /// Run the chain graph. If startFromIndex >= 0, start from that specific program.
    /// Otherwise, find the root node (first with no incoming links).
    /// </summary>
    private async void RunChainAsync(int startFromIndex = -1)
    {
        Log.Information("[RunPanel] Chain Run — startFrom={StartIdx}", startFromIndex);
        if (_isRunning) return;
        if (_deck == null || _deck.Slots.Count == 0) return;

        var programs = FlattenDeckPrograms();
        if (programs.Count == 0) return;

        if (_deck.ChainLinks.Count == 0)
        {
            MessageBox.Show("No chain links defined. Add links between program blocks first.",
                "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        int startIdx;
        if (startFromIndex >= 0 && startFromIndex < programs.Count)
        {
            startIdx = startFromIndex;
        }
        else
        {
            // Find root node: first node with no incoming links
            var incomingSet = new HashSet<int>(_deck.ChainLinks.Select(l => l.ToIndex));
            startIdx = 0;
            for (int i = 0; i < programs.Count; i++)
            {
                if (!incomingSet.Contains(i)) { startIdx = i; break; }
            }
        }

        var engine = _context.ProcessEngine;
        if (!engine.TryAcquire("RunPanel-Chain"))
        {
            Log.Warning("[RunPanel] Cannot run chain — engine locked by {Owner}", engine.RunningOwner);
            MessageBox.Show($"Engine is busy — {engine.RunningOwner} is running.",
                "Busy", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var recipeEngine = _context.RecipeEngine;
        if (!recipeEngine.TryAcquire("RunPanel-Chain"))
        {
            engine.Release();
            MessageBox.Show($"Recipe engine busy — {recipeEngine.RunningOwner} is running.",
                "Busy", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _isRunning = true;
        _isPaused = false;
        _cts = new CancellationTokenSource();
        SetButtonStates(running: true);
        ChainRunBtn.IsEnabled = false;
        StartMonitor("Chain Execution");
        RefreshExecuteTree();
        RefreshChainCanvas();

        var registry = _context.PluginRegistry;
        _lastContext = null;
        bool chainSuccess = true;
        int currentIdx = startIdx;
        var visited = new HashSet<int>();

        try
        {
            while (currentIdx >= 0 && currentIdx < programs.Count && !visited.Contains(currentIdx))
            {
                if (_cts.Token.IsCancellationRequested) { chainSuccess = false; break; }

                visited.Add(currentIdx);
                var (slotName, programPath, si, pi) = programs[currentIdx];

                // Highlight current block on canvas
                if (currentIdx < _chainBlocks.Count)
                    _chainBlocks[currentIdx].BorderBrush = YellowBrush;

                // Highlight in execute tree
                if (si < ExecuteTree.Items.Count)
                {
                    var slotNode = (TreeViewItem)ExecuteTree.Items[si];
                    if (pi < slotNode.Items.Count)
                    {
                        slotNode.Foreground = YellowBrush;
                        ((TreeViewItem)slotNode.Items[pi]).Foreground = YellowBrush;
                    }
                }

                Log.Information("[RunPanel] Chain executing: [{SlotName}] {Program}",
                    slotName, System.IO.Path.GetFileName(programPath));

                var ext = System.IO.Path.GetExtension(programPath);
                var isRecipe = ext.Equals(".hrcp", StringComparison.OrdinalIgnoreCase);
                _isRunningRecipe = isRecipe;

                bool success;
                if (isRecipe)
                {
                    var (s, rctx) = await RunRecipeAsync(recipeEngine, programPath, _cts.Token, _lastContext);
                    _lastContext = RecipeToProcessContext(rctx);
                    success = s;
                }
                else
                {
                    var (s, ctx) = await RunProgramAsync(engine, registry, programPath, _cts.Token);
                    _lastContext = ctx;
                    success = s;
                }

                // Update block color
                if (currentIdx < _chainBlocks.Count)
                    _chainBlocks[currentIdx].BorderBrush = success ? GreenBrush : RedBrush;

                // Update execute tree
                if (si < ExecuteTree.Items.Count)
                {
                    var slotNode = (TreeViewItem)ExecuteTree.Items[si];
                    if (pi < slotNode.Items.Count)
                        ((TreeViewItem)slotNode.Items[pi]).Foreground = success ? GreenBrush : RedBrush;

                    // Mark slot green if all its programs are done
                    bool slotDone = true;
                    for (int j = 0; j < slotNode.Items.Count; j++)
                    {
                        var n = (TreeViewItem)slotNode.Items[j];
                        if (n.Foreground != GreenBrush) { slotDone = false; break; }
                    }
                    if (slotDone) slotNode.Foreground = GreenBrush;
                }

                if (!success) { chainSuccess = false; break; }

                // Find next node via outgoing links
                int nextIdx = -1;
                var outgoing = _deck.ChainLinks.Where(l => l.FromIndex == currentIdx).ToList();

                foreach (var link in outgoing)
                {
                    // Evaluate condition
                    if (!string.IsNullOrWhiteSpace(link.Condition) && _lastContext != null)
                    {
                        if (!EvaluateCondition(link.Condition, _lastContext))
                        {
                            Log.Information("[RunPanel] Condition failed: {From}\u2192{To} [{Cond}]",
                                link.FromIndex, link.ToIndex, link.Condition);
                            continue;
                        }
                    }

                    // Countdown delay
                    if (link.DelaySec > 0)
                    {
                        var nextName = System.IO.Path.GetFileNameWithoutExtension(programs[link.ToIndex].Path);
                        var countdown = new CountdownDialog(nextName, link.DelaySec)
                        { Owner = Window.GetWindow(this) };
                        countdown.ShowDialog();

                        if (countdown.Result == CountdownResult.Stop)
                        {
                            Log.Information("[RunPanel] Chain stopped by user at countdown");
                            chainSuccess = false;
                            nextIdx = -2;
                            break;
                        }
                    }

                    nextIdx = link.ToIndex;
                    break; // take first passing link
                }

                if (nextIdx == -2) break; // user stopped at countdown
                currentIdx = nextIdx;
            }

            MonitorStatusText.Text = chainSuccess ? "Chain Completed" : "Chain Stopped/Failed";
            Log.Information("[RunPanel] Chain execution {Result}", chainSuccess ? "completed" : "stopped/failed");
        }
        catch (OperationCanceledException)
        {
            MonitorStatusText.Text = "Chain Stopped";
            Log.Warning("[RunPanel] Chain cancelled by user");
        }
        catch (Exception ex)
        {
            MonitorStatusText.Text = "Chain Error";
            Log.Error(ex, "[RunPanel] Chain execution error");
        }
        finally
        {
            _isRunning = false;
            _isRunningRecipe = false;
            _isPaused = false;
            _activeRecipeContext = null;
            engine.StepByStepMode = false;
            engine.OnStepStarted = null;
            engine.OnStepCompleted = null;
            engine.OnLoopIteration = null;
            engine.Release();
            recipeEngine.StepByStepMode = false;
            recipeEngine.OnStepStarted = null;
            recipeEngine.OnStepCompleted = null;
            recipeEngine.OnLoopIteration = null;
            recipeEngine.OnLog = null;
            recipeEngine.OnContextChanged = null;
            recipeEngine.Release();
            SetButtonStates(running: false);
            ChainRunBtn.IsEnabled = true;
        }
    }

    // ── Condition evaluator ──────────────────────────────────────────

    private static bool EvaluateCondition(string condition, ProcessContext ctx)
    {
        if (string.IsNullOrWhiteSpace(condition)) return true;

        // Supported operators (check longer ones first)
        var ops = new[] { ">=", "<=", "!=", "==", ">", "<" };
        foreach (var op in ops)
        {
            var idx = condition.IndexOf(op, StringComparison.Ordinal);
            if (idx < 0) continue;

            var varName = condition[..idx].Trim();
            var valueStr = condition[(idx + op.Length)..].Trim();
            var actual = ctx.GetString(varName);

            // Try numeric comparison
            if (double.TryParse(actual, NumberStyles.Float, CultureInfo.InvariantCulture, out var actualNum) &&
                double.TryParse(valueStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var expectedNum))
            {
                return op switch
                {
                    ">" => actualNum > expectedNum,
                    "<" => actualNum < expectedNum,
                    ">=" => actualNum >= expectedNum,
                    "<=" => actualNum <= expectedNum,
                    "==" => Math.Abs(actualNum - expectedNum) < 1e-12,
                    "!=" => Math.Abs(actualNum - expectedNum) >= 1e-12,
                    _ => false
                };
            }

            // String comparison (== and != only)
            return op switch
            {
                "==" => string.Equals(actual, valueStr, StringComparison.OrdinalIgnoreCase),
                "!=" => !string.Equals(actual, valueStr, StringComparison.OrdinalIgnoreCase),
                _ => false
            };
        }

        // No operator found — treat as boolean variable check
        var boolVal = ctx.GetString(condition.Trim());
        return string.Equals(boolVal, "true", StringComparison.OrdinalIgnoreCase);
    }

    // ── Run single program helper ───────────────────────────────────

    private async Task<(bool Success, ProcessContext Context)> RunProgramAsync(
        ProcessEngine engine, ProcessPluginRegistry registry,
        string filePath, CancellationToken ct)
    {
        var ctx = new ProcessContext { CancellationToken = ct };

        if (!File.Exists(filePath))
        {
            Log.Error("[RunPanel] Program file not found: {Path}", filePath);
            return (false, ctx);
        }

        ProcessProgram? program;
        try
        {
            var json = File.ReadAllText(filePath);
            program = JsonSerializer.Deserialize<ProcessProgram>(json, JsonOpts);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[RunPanel] Failed to load program: {Path}", filePath);
            return (false, ctx);
        }

        if (program == null) return (false, ctx);

        var builder = new ProcessBuilder();
        foreach (var step in program.Steps)
        {
            if (!step.Enabled) continue;
            var plugin = registry.GetById(step.PluginId);
            if (plugin == null)
            {
                Log.Warning("[RunPanel] Unknown plugin {PluginId} in {File}, skipping", step.PluginId, System.IO.Path.GetFileName(filePath));
                continue;
            }
            plugin.BuildSteps(builder, step.Parameters);
        }

        List<ProcessStep> processSteps;
        try
        {
            processSteps = builder.Build();
        }
        catch (InvalidOperationException ex)
        {
            Log.Error("[RunPanel] Build error in {File}: {Message}", System.IO.Path.GetFileName(filePath), ex.Message);
            return (false, ctx);
        }

        if (processSteps.Count == 0) return (true, ctx);

        engine.OnStepStarted = (idx, device, method) =>
        {
            Log.Information("[RunPanel]   Step {Index}: {Device}.{Method}", idx, device, method);
            Dispatcher.BeginInvoke(() => MonitorStatusText.Text = $"Step {idx}: {device}.{method}");
        };
        engine.OnStepCompleted = (idx, success) =>
        {
            if (!success) Log.Warning("[RunPanel]   Step {Index} failed", idx);
            Dispatcher.BeginInvoke(() => RefreshMonitorWatchFromProcess(ctx));
        };
        engine.OnLoopIteration = (idx, current, total) =>
        {
            Log.Information("[RunPanel]   Loop step {Index}: iteration {Current}/{Total}", idx, current, total == 0 ? "inf" : total);
            Dispatcher.BeginInvoke(() => MonitorStatusText.Text = $"Loop step {idx}: {current}/{(total == 0 ? "inf" : total)}");
        };

        var result = await Task.Run(() => engine.ExecuteAsync(processSteps, ctx, ct));
        return (result, ctx);
    }

    private async Task<(bool Success, RecipeContext Context)> RunRecipeAsync(
        RecipeEngine engine, string filePath, CancellationToken ct, ProcessContext? seedFrom = null)
    {
        var ctx = new RecipeContext { CancellationToken = ct };

        if (seedFrom != null)
        {
            foreach (var (key, value, _) in seedFrom.GetSnapshot())
            {
                if (double.TryParse(value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var num))
                    ctx.Set(key, num);
                else
                    ctx.Set(key, value);
            }
        }

        if (!File.Exists(filePath))
        {
            Log.Error("[RunPanel] Recipe file not found: {Path}", filePath);
            return (false, ctx);
        }

        string source;
        try
        {
            source = File.ReadAllText(filePath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[RunPanel] Failed to read recipe: {Path}", filePath);
            return (false, ctx);
        }

        var (program, errors) = RecipeEngine.Parse(source);
        if (program == null)
        {
            foreach (var err in errors)
                Log.Error("[RunPanel] Parse error in {File} line {Line}: {Message}",
                    System.IO.Path.GetFileName(filePath), err.Line, err.Message);
            return (false, ctx);
        }

        _activeRecipeContext = ctx;

        engine.OnStepStarted = (line, device, method) =>
        {
            Log.Information("[RunPanel]   Recipe step {Line}: {Device}.{Method}", line, device, method);
            Dispatcher.BeginInvoke(() => MonitorStatusText.Text = $"Line {line}: {device}.{method}");
        };
        engine.OnStepCompleted = (line, success, desc) =>
        {
            if (!success) Log.Warning("[RunPanel]   Recipe step {Line} failed: {Desc}", line, desc);
            Dispatcher.BeginInvoke(() => RefreshMonitorWatch());
        };
        engine.OnLoopIteration = (line, current, total) =>
        {
            Log.Information("[RunPanel]   Recipe loop {Line}: iteration {Current}/{Total}", line, current, total == 0 ? "inf" : total);
            Dispatcher.BeginInvoke(() => MonitorStatusText.Text = $"Loop L{line}: {current}/{(total > 0 ? total : "inf")}");
        };
        engine.OnLog = msg =>
        {
            Log.Information("[RunPanel] [recipe] {Message}", msg);
            Dispatcher.BeginInvoke(() => AddMonitorOutput(msg));
        };

        var result = await engine.ExecuteAsync(program, ctx, ct);
        return (result, ctx);
    }

    private static ProcessContext RecipeToProcessContext(RecipeContext recipe)
    {
        var pc = new ProcessContext();
        foreach (var (key, value, _) in recipe.GetSnapshot())
            pc.SetValue(key, value);
        return pc;
    }

    // ── Execution Monitor ────────────────────────────────────────────

    private void StartMonitor(string fileName)
    {
        _monitorWatchItems.Clear();
        MonitorOutputList.Items.Clear();
        MonitorStatusText.Text = $"Running {fileName}...";
        ShowRightPanel(RightPanel.ExecutionMonitor);
    }

    private void RefreshMonitorWatch()
    {
        if (_activeRecipeContext == null) return;
        var snapshot = _activeRecipeContext.GetSnapshot();
        _monitorWatchItems.Clear();
        foreach (var (key, value, _) in snapshot)
        {
            if (key.EndsWith(".unit")) continue;
            _monitorWatchItems.Add(new RunWatchItem { Variable = key, Value = value });
        }
    }

    private void RefreshMonitorWatchFromProcess(ProcessContext ctx)
    {
        var snapshot = ctx.GetSnapshot();
        _monitorWatchItems.Clear();
        foreach (var (key, value, _) in snapshot)
            _monitorWatchItems.Add(new RunWatchItem { Variable = key, Value = value });
    }

    private void AddMonitorOutput(string message)
    {
        MonitorOutputList.Items.Add(message);
        if (MonitorOutputList.Items.Count > 500)
            MonitorOutputList.Items.RemoveAt(0);
        MonitorOutputList.ScrollIntoView(MonitorOutputList.Items[^1]);
    }

    private void MonitorClear_Click(object sender, RoutedEventArgs e)
    {
        MonitorOutputList.Items.Clear();
        _monitorWatchItems.Clear();
    }

    // ── Tree selection helper ───────────────────────────────────────

    private (string? Path, int SlotIndex, int ProgIndex) GetSelectedProgram()
    {
        if (_deck == null || ExecuteTree.SelectedItem is not TreeViewItem selected)
            return (null, -1, -1);

        for (int si = 0; si < ExecuteTree.Items.Count; si++)
        {
            var slotNode = (TreeViewItem)ExecuteTree.Items[si];
            for (int pi = 0; pi < slotNode.Items.Count; pi++)
            {
                if (slotNode.Items[pi] == selected)
                    return (_deck.Slots[si].Programs[pi], si, pi);
            }
        }

        return (null, -1, -1);
    }

    // ── Hardware stop ───────────────────────────────────────────────

    private void StopAllHardware()
    {
        try
        {
            _context.MotionManager.StopAllDevices();
            Log.Information("[RunPanel] All motion devices stopped");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[RunPanel] Error stopping motion devices");
        }
    }

    private void SetButtonStates(bool running)
    {
        RunBtn.IsEnabled = !running;
        PauseBtn.IsEnabled = running;
        StopBtn.IsEnabled = running;

        if (!running)
        {
            // Reset pause button to default state
            PauseIconImg.Source = new BitmapImage(new Uri("pack://application:,,,/Assets/Pause.png"));
            PauseLabel.Text = " Pause";
            PauseLabel.Foreground = YellowBrush;
        }
    }

    // ── Right panel switching ───────────────────────────────────────

    private enum RightPanel { Empty, DeckEditor, Chain, ExecutionMonitor }

    private void ShowRightPanel(RightPanel panel)
    {
        EmptyRightText.Visibility = panel == RightPanel.Empty ? Visibility.Visible : Visibility.Collapsed;
        DeckEditorPanel.Visibility = panel == RightPanel.DeckEditor ? Visibility.Visible : Visibility.Collapsed;
        ChainEditorPanel.Visibility = panel == RightPanel.Chain ? Visibility.Visible : Visibility.Collapsed;
        ExecutionMonitorPanel.Visibility = panel == RightPanel.ExecutionMonitor ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Deck tab buttons ────────────────────────────────────────────

    private void DeckNew_Click(object sender, RoutedEventArgs e)
    {
        Log.Information("[RunPanel] New Deck clicked");
        var dlg = new InputDialog("New Deck", "Enter deck name:") { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.ResultText))
            return;

        _deck = new Deck { Name = dlg.ResultText.Trim() };
        _currentDeckPath = null;
        Log.Information("[RunPanel] Created new deck: {DeckName}", _deck.Name);
        RefreshAll();
    }

    private void DeckLoad_Click(object sender, RoutedEventArgs e)
    {
        Log.Information("[RunPanel] Load Deck clicked");
        var dlg = new FilePickerDialog(FilePickerMode.Open, DefaultDeckDir, ".hdeck", "Horizon Deck")
        { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true || dlg.SelectedPath == null) return;

        try
        {
            var json = File.ReadAllText(dlg.SelectedPath);
            _deck = JsonSerializer.Deserialize<Deck>(json, JsonOpts);
            _currentDeckPath = dlg.SelectedPath;
            SaveLastDeckPath(dlg.SelectedPath);
            Log.Information("[RunPanel] Loaded deck: {Path}", dlg.SelectedPath);
            RefreshAll();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[RunPanel] Failed to load deck: {Path}", dlg.SelectedPath);
            MessageBox.Show($"Failed to load deck:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DeckSave_Click(object sender, RoutedEventArgs e)
    {
        Log.Information("[RunPanel] Save Deck clicked");
        if (_deck == null)
        {
            MessageBox.Show("No deck to save.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_currentDeckPath == null)
        {
            var dlg = new FilePickerDialog(FilePickerMode.Save, DefaultDeckDir, ".hdeck", "Horizon Deck", _deck.Name)
            { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true || dlg.SelectedPath == null) return;
            _currentDeckPath = dlg.SelectedPath;
        }

        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_currentDeckPath)!);
            var json = JsonSerializer.Serialize(_deck, JsonOpts);
            File.WriteAllText(_currentDeckPath, json);
            SaveLastDeckPath(_currentDeckPath);
            Log.Information("[RunPanel] Saved deck: {Path}", _currentDeckPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[RunPanel] Failed to save deck: {Path}", _currentDeckPath);
            MessageBox.Show($"Failed to save deck:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DeckEdit_Click(object sender, RoutedEventArgs e)
    {
        Log.Information("[RunPanel] Edit Deck clicked");
        if (_deck == null)
        {
            MessageBox.Show("Create or load a deck first.", "Info",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ShowRightPanel(RightPanel.DeckEditor);
        RefreshSlotList();
    }

    // ── Deck editor ─────────────────────────────────────────────────

    private void DeckEditorClose_Click(object sender, RoutedEventArgs e)
    {
        Log.Information("[RunPanel] Deck Editor closed");
        ShowRightPanel(RightPanel.Empty);
        RefreshAll();
    }

    private void SlotAdd_Click(object sender, RoutedEventArgs e)
    {
        Log.Information("[RunPanel] Add Slot clicked");
        if (_deck == null) return;

        var dlg = new InputDialog("New Slot", "Enter slot name:") { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.ResultText))
            return;

        _deck.Slots.Add(new DeckSlot { Name = dlg.ResultText.Trim() });
        Log.Information("[RunPanel] Added slot: {SlotName}", dlg.ResultText.Trim());
        RefreshSlotList();
    }

    private void SlotRemove_Click(object sender, RoutedEventArgs e)
    {
        if (_deck == null || SlotListBox.SelectedIndex < 0) return;

        var idx = SlotListBox.SelectedIndex;
        var slot = _deck.Slots[idx];
        var result = MessageBox.Show($"Remove slot \"{slot.Name}\"?", "Confirm",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        Log.Information("[RunPanel] Removed slot: {SlotName}", slot.Name);
        _deck.Slots.RemoveAt(idx);
        RefreshSlotList();
        ProgramListBox.ItemsSource = null;
        SlotProgramsHeader.Text = "Programs";
    }

    private void SlotList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshProgramList();
    }

    private void ProgramAdd_Click(object sender, RoutedEventArgs e)
    {
        Log.Information("[RunPanel] Add Program clicked");
        var slot = GetSelectedSlot();
        if (slot == null)
        {
            MessageBox.Show("Select a slot first.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new FilePickerDialog(FilePickerMode.Open, DefaultProcessDir, ".hproc", "Horizon Process")
        { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;

        foreach (var file in dlg.SelectedPaths)
        {
            slot.Programs.Add(file);
            Log.Information("[RunPanel] Added program to slot {SlotName}: {Path}", slot.Name, file);
        }

        RefreshProgramList();
    }

    private void RecipeAdd_Click(object sender, RoutedEventArgs e)
    {
        Log.Information("[RunPanel] Add Recipe clicked");
        var slot = GetSelectedSlot();
        if (slot == null)
        {
            MessageBox.Show("Select a slot first.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new FilePickerDialog(FilePickerMode.Open, DefaultRecipeDir, ".hrcp", "Horizon Recipe")
        { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;

        foreach (var file in dlg.SelectedPaths)
        {
            slot.Programs.Add(file);
            Log.Information("[RunPanel] Added recipe to slot {SlotName}: {Path}", slot.Name, file);
        }

        RefreshProgramList();
    }

    private void ProgramRemove_Click(object sender, RoutedEventArgs e)
    {
        var slot = GetSelectedSlot();
        if (slot == null || ProgramListBox.SelectedIndex < 0) return;

        var removed = slot.Programs[ProgramListBox.SelectedIndex];
        Log.Information("[RunPanel] Removed program from slot {SlotName}: {Path}", slot.Name, removed);
        slot.Programs.RemoveAt(ProgramListBox.SelectedIndex);
        RefreshProgramList();
    }

    private void ProgramMoveUp_Click(object sender, RoutedEventArgs e)
    {
        var slot = GetSelectedSlot();
        if (slot == null) return;
        var idx = ProgramListBox.SelectedIndex;
        if (idx <= 0) return;

        Log.Information("[RunPanel] Moved program up in slot {SlotName}: index {Index}", slot.Name, idx);
        (slot.Programs[idx - 1], slot.Programs[idx]) = (slot.Programs[idx], slot.Programs[idx - 1]);
        RefreshProgramList();
        ProgramListBox.SelectedIndex = idx - 1;
    }

    private void ProgramMoveDown_Click(object sender, RoutedEventArgs e)
    {
        var slot = GetSelectedSlot();
        if (slot == null) return;
        var idx = ProgramListBox.SelectedIndex;
        if (idx < 0 || idx >= slot.Programs.Count - 1) return;

        Log.Information("[RunPanel] Moved program down in slot {SlotName}: index {Index}", slot.Name, idx);
        (slot.Programs[idx], slot.Programs[idx + 1]) = (slot.Programs[idx + 1], slot.Programs[idx]);
        RefreshProgramList();
        ProgramListBox.SelectedIndex = idx + 1;
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private DeckSlot? GetSelectedSlot()
    {
        if (_deck == null || SlotListBox.SelectedIndex < 0) return null;
        return _deck.Slots[SlotListBox.SelectedIndex];
    }

    private void RefreshAll()
    {
        RefreshDeckName();
        RefreshExecuteTree();
        RefreshDeckTree();
    }

    private void RefreshDeckName()
    {
        if (_deck != null)
        {
            DeckNameText.Text = _deck.Name;
            DeckNameText.Foreground = (Brush)FindResource("Theme.ForegroundBrush");
        }
        else
        {
            DeckNameText.Text = "(No Deck Loaded)";
            DeckNameText.Foreground = (Brush)FindResource("Theme.ForegroundDisabledBrush");
        }
    }

    private void RefreshExecuteTree()
    {
        ExecuteTree.Items.Clear();
        if (_deck == null) return;

        foreach (var slot in _deck.Slots)
        {
            var slotNode = new TreeViewItem
            {
                Header = slot.Name,
                IsExpanded = true,
                FontWeight = FontWeights.SemiBold
            };

            foreach (var prog in slot.Programs)
            {
                var isRecipe = System.IO.Path.GetExtension(prog)
                    .Equals(".hrcp", StringComparison.OrdinalIgnoreCase);
                var header = new StackPanel { Orientation = Orientation.Horizontal };
                header.Children.Add(new TextBlock
                {
                    Text = isRecipe ? "[R] " : "[P] ",
                    Foreground = isRecipe ? new SolidColorBrush(Color.FromRgb(0x00, 0xBF, 0xFF))
                                          : new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)),
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11
                });
                header.Children.Add(new TextBlock
                {
                    Text = System.IO.Path.GetFileNameWithoutExtension(prog),
                    Foreground = (Brush)FindResource("Theme.ForegroundBrush"),
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11
                });

                slotNode.Items.Add(new TreeViewItem
                {
                    Header = header,
                    ToolTip = prog,
                    FontWeight = FontWeights.Normal
                });
            }

            ExecuteTree.Items.Add(slotNode);
        }
    }

    private void RefreshDeckTree()
    {
        DeckTree.Items.Clear();
        if (_deck == null) return;

        foreach (var slot in _deck.Slots)
        {
            var slotNode = new TreeViewItem
            {
                Header = $"{slot.Name}  ({slot.Programs.Count} programs)",
                IsExpanded = true,
                FontWeight = FontWeights.SemiBold
            };

            foreach (var prog in slot.Programs)
            {
                var isRecipe = System.IO.Path.GetExtension(prog)
                    .Equals(".hrcp", StringComparison.OrdinalIgnoreCase);
                var header = new StackPanel { Orientation = Orientation.Horizontal };
                header.Children.Add(new TextBlock
                {
                    Text = isRecipe ? "[R] " : "[P] ",
                    Foreground = isRecipe ? new SolidColorBrush(Color.FromRgb(0x00, 0xBF, 0xFF))
                                          : new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)),
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11
                });
                header.Children.Add(new TextBlock
                {
                    Text = System.IO.Path.GetFileNameWithoutExtension(prog),
                    Foreground = (Brush)FindResource("Theme.ForegroundBrush"),
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11
                });

                slotNode.Items.Add(new TreeViewItem
                {
                    Header = header,
                    ToolTip = prog,
                    FontWeight = FontWeights.Normal
                });
            }

            DeckTree.Items.Add(slotNode);
        }
    }

    private void RefreshSlotList()
    {
        if (_deck == null) return;
        var prevIdx = SlotListBox.SelectedIndex;
        SlotListBox.ItemsSource = null;
        SlotListBox.ItemsSource = _deck.Slots.Select(s => s.Name).ToList();
        if (prevIdx >= 0 && prevIdx < _deck.Slots.Count)
            SlotListBox.SelectedIndex = prevIdx;
    }

    private void RefreshProgramList()
    {
        var slot = GetSelectedSlot();
        if (slot == null)
        {
            ProgramListBox.ItemsSource = null;
            SlotProgramsHeader.Text = "Programs";
            return;
        }
        SlotProgramsHeader.Text = $"Programs - {slot.Name}";
        ProgramListBox.ItemsSource = null;
        ProgramListBox.ItemsSource = slot.Programs
            .Select(p =>
            {
                var isRecipe = System.IO.Path.GetExtension(p)
                    .Equals(".hrcp", StringComparison.OrdinalIgnoreCase);
                return $"{(isRecipe ? "[R] " : "[P] ")}{System.IO.Path.GetFileNameWithoutExtension(p)}";
            })
            .ToList();
    }
}

public class RunWatchItem
{
    public string Variable { get; set; } = "";
    public string Value { get; set; } = "";
}
