using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Horizon.App.Controls;
using Horizon.Core;
using Horizon.Process;
using Horizon.Process.Interfaces;
using Horizon.Process.Models;
using Serilog;

namespace Horizon.App;

public partial class ProcessPanel : UserControl
{
    private readonly IAppContext _context;
    private readonly ProcessPluginRegistry _registry;
    private readonly PropertyTable _propertyTable;
    private readonly List<PluginStepNode> _builderSteps = new();

    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _linkedCts;
    private bool _isRunning;
    private string? _currentFilePath;

    private ProcessContext? _runContext;
    private readonly ObservableCollection<ContextWatchItem> _watchItems = new();
    private Dictionary<string, string> _prevWatchValues = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string SharedProcessDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Horizon", "processes");

    private static readonly string LastProgramFile =
        Path.Combine(SharedProcessDir, ".lastprogram");

    private string DefaultProcessDir => _context.IsInitialized
        ? _context.ProductManager.GetProcessDir()
        : SharedProcessDir;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public ProcessPanel(IAppContext context)
    {
        _context = context;
        _registry = context.PluginRegistry;
        _propertyTable = new PropertyTable(context);

        InitializeComponent();

        PropertyHost.Content = _propertyTable;
        _propertyTable.ValuesChanged += OnPropertyValuesChanged;

        WatchGrid.ItemsSource = _watchItems;

        PopulateCatalog();
        RefreshBuilderList();
        TryLoadLastProgram();
    }

    // --- Catalog ---

    private void PopulateCatalog()
    {
        CatalogTree.Items.Clear();
        var categories = _registry.GetByCategory();

        foreach (var (category, plugins) in categories.OrderBy(c => c.Key))
        {
            var categoryItem = new TreeViewItem
            {
                Header = $"{category} ({plugins.Count})",
                IsExpanded = true,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                Foreground = (Brush)FindResource("Theme.ForegroundBrush")
            };

            foreach (var plugin in plugins)
            {
                var pluginItem = new TreeViewItem
                {
                    Header = plugin.DisplayName,
                    Tag = plugin,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11,
                    Foreground = (Brush)FindResource("Theme.ForegroundBrush"),
                    ToolTip = plugin.Description
                };
                categoryItem.Items.Add(pluginItem);
            }

            CatalogTree.Items.Add(categoryItem);
        }
    }

    private void CatalogSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var filter = CatalogSearchBox.Text.Trim();
        if (string.IsNullOrEmpty(filter))
        {
            // Show all — restore full catalog
            PopulateCatalog();
            return;
        }

        CatalogTree.Items.Clear();
        var categories = _registry.GetByCategory();

        foreach (var (category, plugins) in categories.OrderBy(c => c.Key))
        {
            var matched = plugins.Where(p =>
                p.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                p.Category.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                (p.Description ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

            if (matched.Count == 0) continue;

            var categoryItem = new TreeViewItem
            {
                Header = $"{category} ({matched.Count})",
                IsExpanded = true,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                Foreground = (Brush)FindResource("Theme.ForegroundBrush")
            };

            foreach (var plugin in matched)
            {
                categoryItem.Items.Add(new TreeViewItem
                {
                    Header = plugin.DisplayName,
                    Tag = plugin,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11,
                    Foreground = (Brush)FindResource("Theme.ForegroundBrush"),
                    ToolTip = plugin.Description
                });
            }

            CatalogTree.Items.Add(categoryItem);
        }
    }

    private void CatalogTree_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (CatalogTree.SelectedItem is TreeViewItem item && item.Tag is IProcessPlugin plugin)
        {
            var node = new PluginStepNode(plugin);

            // Set defaults
            foreach (var param in plugin.Parameters)
            {
                if (param.Default != null)
                    node.ParamValues[param.Name] = param.Default;
            }

            // Insert after last selected item or at end
            var selected = GetSelectedIndices();
            var insertAfter = selected.Count > 0 ? selected[^1] : -1;
            if (insertAfter >= 0 && insertAfter < _builderSteps.Count)
                _builderSteps.Insert(insertAfter + 1, node);
            else
                _builderSteps.Add(node);

            var newIdx = _builderSteps.IndexOf(node);
            RefreshBuilderList(new[] { newIdx });
        }
    }

    // --- Builder List ---

    private static readonly Brush DisabledBrush = new SolidColorBrush(Color.FromRgb(0x6A, 0x6A, 0x6A));
    private static readonly Brush StructBrush = new SolidColorBrush(Color.FromRgb(0x56, 0x9C, 0xD6)); // blue for flow control
    private static readonly Brush CommentBrush = new SolidColorBrush(Color.FromRgb(0x6A, 0x99, 0x55)); // green for comments

    private const string LoopStartId = "system.loop_start";
    private const string LoopEndId = "system.loop_end";
    private const string CommentId = "system.comment";

    private void RefreshBuilderList(IEnumerable<int>? selectIndices = null)
    {
        // Save selection if not explicitly provided
        var savedIndices = selectIndices?.ToList()
            ?? GetSelectedIndices();

        BuilderList.Items.Clear();

        int depth = 0;
        for (int i = 0; i < _builderSteps.Count; i++)
        {
            var node = _builderSteps[i];
            bool isLoopEnd = node.Plugin.Id == LoopEndId;
            bool isLoopStart = node.Plugin.Id == LoopStartId;

            // Decrease depth before rendering Loop End
            if (isLoopEnd && depth > 0) depth--;

            var indent = depth > 0 ? new string(' ', depth * 3) : "";
            var prefix = node.IsEnabled ? $"[{i}]" : $"[{i}] //";

            bool isComment = node.Plugin.Id == CommentId;

            Brush fg;
            if (!node.IsEnabled)
                fg = DisabledBrush;
            else if (isLoopStart || isLoopEnd)
                fg = StructBrush;
            else if (isComment)
                fg = CommentBrush;
            else
                fg = (Brush)FindResource("Theme.ForegroundBrush");

            var item = new ListBoxItem
            {
                Content = $"{prefix}  {indent}{node.DisplayText}",
                Tag = node,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                Foreground = fg,
                Padding = new Thickness(4, 2, 4, 2)
            };
            BuilderList.Items.Add(item);

            // Increase depth after rendering Loop Start
            if (isLoopStart) depth++;
        }

        var enabled = _builderSteps.Count(n => n.IsEnabled);
        StepCountText.Text = enabled < _builderSteps.Count
            ? $"{enabled}/{_builderSteps.Count} steps"
            : $"{_builderSteps.Count} steps";

        // Restore selection
        foreach (var idx in savedIndices)
        {
            if (idx >= 0 && idx < BuilderList.Items.Count)
                ((ListBoxItem)BuilderList.Items[idx]).IsSelected = true;
        }
    }

    private List<int> GetSelectedIndices()
    {
        var indices = new List<int>();
        for (int i = 0; i < BuilderList.Items.Count; i++)
        {
            if (BuilderList.Items[i] is ListBoxItem li && li.IsSelected)
                indices.Add(i);
        }
        return indices;
    }

    private void BuilderList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BuilderList.SelectedItem is ListBoxItem item && item.Tag is PluginStepNode node)
        {
            _propertyTable.BindPlugin(node.Plugin, node.ParamValues);
            _propertyTable.BindStepHeader(node.Uid, node.CustomName, newName =>
            {
                node.CustomName = newName;
                // Refresh the selected item's display text
                var idx = BuilderList.SelectedIndex;
                if (idx >= 0 && idx < BuilderList.Items.Count &&
                    BuilderList.Items[idx] is ListBoxItem li)
                {
                    int d = 0;
                    for (int j = 0; j < idx; j++)
                    {
                        if (_builderSteps[j].Plugin.Id == LoopStartId) d++;
                        else if (_builderSteps[j].Plugin.Id == LoopEndId && d > 0) d--;
                    }
                    if (node.Plugin.Id == LoopEndId && d > 0) d--;
                    var indent = d > 0 ? new string(' ', d * 3) : "";
                    var prefix = node.IsEnabled ? $"[{idx}]" : $"[{idx}] //";
                    li.Content = $"{prefix}  {indent}{node.DisplayText}";
                }
            });
        }
        else
        {
            _propertyTable.BindPlugin(null, null);
        }
    }

    private void BuilderList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            DeleteSelected();
            e.Handled = true;
        }
        else if (e.Key == Key.Up && Keyboard.Modifiers == ModifierKeys.Control)
        {
            MoveSelectedUp();
            e.Handled = true;
        }
        else if (e.Key == Key.Down && Keyboard.Modifiers == ModifierKeys.Control)
        {
            MoveSelectedDown();
            e.Handled = true;
        }
        else if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ToggleSelectedEnable();
            e.Handled = true;
        }
    }

    private void OnPropertyValuesChanged()
    {
        if (BuilderList.SelectedItem is ListBoxItem item && item.Tag is PluginStepNode node)
        {
            var newValues = _propertyTable.GetValues();
            node.ParamValues.Clear();
            foreach (var (k, v) in newValues)
                node.ParamValues[k] = v;

            // Update display text for selected item — recalc indent
            var idx = BuilderList.SelectedIndex;
            if (idx >= 0 && idx < BuilderList.Items.Count)
            {
                int d = 0;
                for (int j = 0; j < idx; j++)
                {
                    if (_builderSteps[j].Plugin.Id == LoopStartId) d++;
                    else if (_builderSteps[j].Plugin.Id == LoopEndId && d > 0) d--;
                }
                if (node.Plugin.Id == LoopEndId && d > 0) d--;
                var indent = d > 0 ? new string(' ', d * 3) : "";
                var prefix = node.IsEnabled ? $"[{idx}]" : $"[{idx}] //";
                item.Content = $"{prefix}  {indent}{node.DisplayText}";
            }
        }
    }

    // --- File handlers ---

    private void TryLoadLastProgram()
    {
        try
        {
            if (!File.Exists(LastProgramFile)) return;
            var lastPath = File.ReadAllText(LastProgramFile).Trim();
            if (string.IsNullOrEmpty(lastPath) || !File.Exists(lastPath)) return;

            LoadProgram(lastPath);
            StatusText.Text = $"Restored: {Path.GetFileName(lastPath)}";
        }
        catch
        {
            // Silent — don't block startup
        }
    }

    private void SaveLastProgramPath(string? path)
    {
        try
        {
            Directory.CreateDirectory(DefaultProcessDir);
            if (path != null)
                File.WriteAllText(LastProgramFile, path);
            else if (File.Exists(LastProgramFile))
                File.Delete(LastProgramFile);
        }
        catch
        {
            // Non-critical
        }
    }

    private bool LoadProgram(string filePath)
    {
        ProcessProgram? program;
        try
        {
            var json = File.ReadAllText(filePath);
            program = JsonSerializer.Deserialize<ProcessProgram>(json, JsonOpts);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Load error: {ex.Message}";
            return false;
        }

        if (program == null)
        {
            StatusText.Text = "Load error: invalid file";
            return false;
        }

        _builderSteps.Clear();
        int skipped = 0;

        foreach (var step in program.Steps)
        {
            var plugin = _registry.GetById(step.PluginId);
            if (plugin == null)
            {
                skipped++;
                continue;
            }

            var node = new PluginStepNode(plugin)
            {
                IsEnabled = step.Enabled,
                Uid = string.IsNullOrEmpty(step.Uid) ? Guid.NewGuid().ToString("N")[..8] : step.Uid,
                CustomName = step.CustomName ?? ""
            };
            foreach (var (k, v) in step.Parameters)
                node.ParamValues[k] = v;
            _builderSteps.Add(node);
        }

        _currentFilePath = filePath;
        _propertyTable.BindPlugin(null, null);
        RefreshBuilderList();

        if (_builderSteps.Count > 0)
            BuilderList.SelectedIndex = 0;

        if (skipped > 0)
            StatusText.Text = $"Loaded: {Path.GetFileName(filePath)} ({skipped} unknown plugin(s) skipped)";
        else
            StatusText.Text = $"Loaded: {Path.GetFileName(filePath)}";

        return true;
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        _builderSteps.Clear();
        _currentFilePath = null;
        _propertyTable.BindPlugin(null, null);
        RefreshBuilderList();
        SaveLastProgramPath(null);
        StatusText.Text = "New program";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFilePath != null)
        {
            SaveToFile(_currentFilePath);
            return;
        }

        Directory.CreateDirectory(DefaultProcessDir);

        var dlg = new FilePickerDialog(FilePickerMode.Save, DefaultProcessDir, ".hproc", "Horizon Process", "Untitled")
        { Owner = Window.GetWindow(this) };

        if (dlg.ShowDialog() != true || dlg.SelectedPath == null) return;

        _currentFilePath = dlg.SelectedPath;
        SaveToFile(dlg.SelectedPath);
    }

    private void SaveToFile(string filePath)
    {
        var program = new ProcessProgram
        {
            Name = Path.GetFileNameWithoutExtension(filePath),
            Steps = _builderSteps.Select(node => new ProcessProgramStep
            {
                PluginId = node.Plugin.Id,
                Parameters = new Dictionary<string, string>(node.ParamValues),
                Enabled = node.IsEnabled,
                Uid = node.Uid,
                CustomName = node.CustomName
            }).ToList()
        };

        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        var json = JsonSerializer.Serialize(program, JsonOpts);
        File.WriteAllText(filePath, json);
        SaveLastProgramPath(filePath);
        StatusText.Text = $"Saved: {Path.GetFileName(filePath)}";
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(DefaultProcessDir);

        var dlg = new FilePickerDialog(FilePickerMode.Open, DefaultProcessDir, ".hproc", "Horizon Process")
        { Owner = Window.GetWindow(this) };

        if (dlg.ShowDialog() != true || dlg.SelectedPath == null) return;

        if (LoadProgram(dlg.SelectedPath))
            SaveLastProgramPath(dlg.SelectedPath);
    }

    // --- Enable / Disable ---

    private void ToggleEnable_Click(object sender, RoutedEventArgs e)
    {
        ToggleSelectedEnable();
    }

    private void ToggleSelectedEnable()
    {
        var indices = GetSelectedIndices();
        if (indices.Count == 0) return;

        // Determine target state: if any selected item is enabled, disable all; otherwise enable all
        bool anyEnabled = indices.Any(i => _builderSteps[i].IsEnabled);
        foreach (var idx in indices)
            _builderSteps[idx].IsEnabled = !anyEnabled;

        RefreshBuilderList();
    }

    // --- Toolbar handlers ---

    private void DeleteSelected()
    {
        var indices = GetSelectedIndices();
        if (indices.Count == 0) return;

        // Remove from highest index first to preserve lower indices
        for (int i = indices.Count - 1; i >= 0; i--)
            _builderSteps.RemoveAt(indices[i]);

        // Select item at first deleted position (or last item)
        var newIdx = Math.Min(indices[0], _builderSteps.Count - 1);
        RefreshBuilderList(newIdx >= 0 ? new[] { newIdx } : Array.Empty<int>());
    }

    private void Delete_Click(object sender, RoutedEventArgs e) => DeleteSelected();

    private void MoveSelectedUp()
    {
        var indices = GetSelectedIndices();
        if (indices.Count == 0 || indices[0] <= 0) return;

        // Check contiguous block can move up
        foreach (var idx in indices)
        {
            var target = idx - 1;
            if (target < 0 || indices.Contains(target)) continue;
            (_builderSteps[idx], _builderSteps[target]) = (_builderSteps[target], _builderSteps[idx]);
        }

        RefreshBuilderList(indices.Select(i => i - 1));
    }

    private void MoveSelectedDown()
    {
        var indices = GetSelectedIndices();
        if (indices.Count == 0 || indices[^1] >= _builderSteps.Count - 1) return;

        // Move from bottom up to avoid collisions
        for (int i = indices.Count - 1; i >= 0; i--)
        {
            var idx = indices[i];
            var target = idx + 1;
            if (target >= _builderSteps.Count || indices.Contains(target)) continue;
            (_builderSteps[idx], _builderSteps[target]) = (_builderSteps[target], _builderSteps[idx]);
        }

        RefreshBuilderList(indices.Select(i => i + 1));
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveSelectedUp();

    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveSelectedDown();

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning || _builderSteps.Count == 0) return;
        if (!_builderSteps.Any(n => n.IsEnabled))
        {
            StatusText.Text = "No enabled steps to run";
            return;
        }

        var engine = _context.ProcessEngine;
        if (!engine.TryAcquire("ProcessPanel"))
        {
            StatusText.Text = $"Engine busy — {engine.RunningOwner} is running";
            Log.Warning("Process: Cannot run — engine locked by {Owner}", engine.RunningOwner);
            return;
        }

        _isRunning = true;
        _cts = new CancellationTokenSource();
        var sw = _context.SafetyWatchManager;
        _linkedCts = sw != null ? sw.LinkExecutionCts(_cts.Token) : _cts;
        RunBtn.IsEnabled = false;
        StopBtn.IsEnabled = true;
        ContinueBtn.IsEnabled = StepModeCheck.IsChecked == true;
        StatusText.Text = "Running...";

        var enabledCount = _builderSteps.Count(n => n.IsEnabled);
        Log.Information("Process: Running {Count} enabled step(s){File}",
            enabledCount,
            _currentFilePath != null ? $" — {Path.GetFileName(_currentFilePath)}" : "");

        engine.StepByStepMode = StepModeCheck.IsChecked == true;

        try
        {
            // Build steps and track mapping from flat step index to builder index (skip disabled)
            var stepToBuilderMap = new Dictionary<int, int>();
            int flatIdx = 0;

            for (int bi = 0; bi < _builderSteps.Count; bi++)
            {
                if (!_builderSteps[bi].IsEnabled) continue;

                // Structural plugins (loop start/end) don't produce action steps
                var pluginId = _builderSteps[bi].Plugin.Id;
                if (pluginId is LoopStartId or LoopEndId) continue;

                var tempBuilder = new ProcessBuilder();
                _builderSteps[bi].Plugin.BuildSteps(tempBuilder, _builderSteps[bi].ParamValues);
                var tempSteps = tempBuilder.Build();

                for (int s = 0; s < tempSteps.Count; s++)
                    stepToBuilderMap[flatIdx + s] = bi;

                flatIdx += tempSteps.Count;
            }

            // Now build the real step list (skip disabled)
            var builder = new ProcessBuilder();
            foreach (var node in _builderSteps)
            {
                if (node.IsEnabled)
                    node.Plugin.BuildSteps(builder, node.ParamValues);
            }

            List<ProcessStep> processSteps;
            try
            {
                processSteps = builder.Build();
            }
            catch (InvalidOperationException ex)
            {
                StatusText.Text = $"Build error: {ex.Message}";
                return;
            }

            engine.OnStepStarted = (idx, device, method) =>
            {
                var bi = stepToBuilderMap.GetValueOrDefault(idx, -1);
                var name = bi >= 0 ? _builderSteps[bi].CustomName : "";
                var label = string.IsNullOrWhiteSpace(name) ? $"{device}.{method}" : $"{name} ({device}.{method})";

                Log.Information("Process: Step {Index} — {Label}", idx, label);
                Dispatcher.Invoke(() =>
                {
                    StatusText.Text = $"Step {idx}: {label}";
                    if (bi >= 0) HighlightStep(bi, Brushes.Yellow);
                });

                var pos = bi >= 0 ? _builderSteps[bi].ParamValues.GetValueOrDefault("target_position") : null;
                _context.RaiseProcessStepNotify(device, pos, null);
            };

            engine.OnStepCompleted = (idx, success) =>
            {
                var bi = stepToBuilderMap.GetValueOrDefault(idx, -1);
                var name = bi >= 0 ? _builderSteps[bi].CustomName : "";
                var label = string.IsNullOrWhiteSpace(name) ? $"Step {idx}" : $"{name} (step {idx})";

                if (!success)
                    Log.Error("Process: {Label} FAILED", label);
                Dispatcher.Invoke(() =>
                {
                    if (bi >= 0) HighlightStep(bi, success ? Brushes.LimeGreen : Brushes.OrangeRed);
                    RefreshContextWatch();
                });

                var pos = bi >= 0 ? _builderSteps[bi].ParamValues.GetValueOrDefault("target_position") : null;
                var dev = bi >= 0 ? _builderSteps[bi].ParamValues.GetValueOrDefault("device") ?? "" : "";
                _context.RaiseProcessStepNotify(dev, pos, success);
            };

            engine.OnLoopIteration = (stepIdx, current, total) =>
            {
                var iterLabel = total > 0 ? $"Loop {current}/{total}" : $"Loop {current}/∞";
                Log.Information("Process: {IterLabel}", iterLabel);
                Dispatcher.Invoke(() => StatusText.Text = iterLabel);
            };

            var ctx = new ProcessContext { CancellationToken = _linkedCts!.Token };
            _runContext = ctx;
            _prevWatchValues.Clear();
            _watchItems.Clear();
            WatchCountText.Text = "";

            var result = await Task.Run(() =>
            {
                _context.MotionManager.MotionSourceContext = 1; // Process
                return engine.ExecuteAsync(processSteps, ctx, _linkedCts.Token);
            });
            Dispatcher.Invoke(RefreshContextWatch);
            StatusText.Text = result ? "Completed successfully" : "Completed with errors";
            if (result)
                Log.Information("Process: Completed successfully");
            else
                Log.Error("Process: Completed with errors");
        }
        catch (OperationCanceledException)
        {
            RefreshContextWatch();
            StatusText.Text = "Stopped by user";
            Log.Warning("Process: Stopped by user");
        }
        catch (Exception ex)
        {
            RefreshContextWatch();
            StatusText.Text = $"Error: {ex.Message}";
            Log.Error("Process: Error — {Message}", ex.Message);
        }
        finally
        {
            _isRunning = false;
            engine.Release();
            if (_linkedCts != _cts) _linkedCts?.Dispose();
            _linkedCts = null;
            RunBtn.IsEnabled = true;
            StopBtn.IsEnabled = false;
            ContinueBtn.IsEnabled = false;
            engine.OnStepStarted = null;
            engine.OnStepCompleted = null;
            engine.OnLoopIteration = null;
            ClearHighlights();
            _context.RaiseProcessStepNotify("", null, null);
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        _context.ProcessEngine.ContinueStep();
    }

    // --- Context Watch ---

    private void RefreshContextWatch()
    {
        if (_runContext == null) return;

        var snapshot = _runContext.GetSnapshot();
        var newPrev = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        _watchItems.Clear();
        foreach (var (key, value, type) in snapshot)
        {
            var changed = !_prevWatchValues.TryGetValue(key, out var old) || old != value;
            _watchItems.Add(new ContextWatchItem
            {
                Variable = key,
                Value = value,
                Type = type,
                Changed = changed
            });
            newPrev[key] = value;
        }

        _prevWatchValues = newPrev;
        WatchCountText.Text = snapshot.Count > 0 ? $"{snapshot.Count} var(s)" : "";
    }

    // --- Step highlighting ---

    private void HighlightStep(int flatIndex, Brush color)
    {
        if (flatIndex >= 0 && flatIndex < BuilderList.Items.Count)
        {
            if (BuilderList.Items[flatIndex] is ListBoxItem item)
            {
                item.Foreground = color;
                item.BringIntoView();
            }
        }
    }

    private void ClearHighlights()
    {
        var fg = (Brush)FindResource("Theme.ForegroundBrush");
        foreach (ListBoxItem item in BuilderList.Items)
        {
            if (item.Tag is PluginStepNode node)
            {
                if (!node.IsEnabled)
                    item.Foreground = DisabledBrush;
                else if (node.Plugin.Id is LoopStartId or LoopEndId)
                    item.Foreground = StructBrush;
                else if (node.Plugin.Id == CommentId)
                    item.Foreground = CommentBrush;
                else
                    item.Foreground = fg;
            }
        }
    }

    public void StopExecution()
    {
        _cts?.Cancel();
    }

    // --- Inner model ---

    private sealed class ContextWatchItem
    {
        public string Variable { get; set; } = "";
        public string Value { get; set; } = "";
        public string Type { get; set; } = "";
        public bool Changed { get; set; }
    }

    private sealed class PluginStepNode
    {
        public IProcessPlugin Plugin { get; }
        public Dictionary<string, string> ParamValues { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool IsEnabled { get; set; } = true;
        public string Uid { get; set; } = Guid.NewGuid().ToString("N")[..8];
        public string CustomName { get; set; } = "";

        public PluginStepNode(IProcessPlugin plugin)
        {
            Plugin = plugin;
        }

        public string DisplayText
        {
            get
            {
                // Loop Start: show iteration count
                if (Plugin.Id == "system.loop_start")
                {
                    var countStr = ParamValues.GetValueOrDefault("count") ?? "0";
                    var label = int.TryParse(countStr, out var c) && c > 0 ? $"{c}×" : "∞";
                    return $"┌ Loop ({label})";
                }

                if (Plugin.Id == "system.loop_end")
                    return "└ End Loop";

                // Comment: show text with // prefix
                if (Plugin.Id == "system.comment")
                {
                    var text = ParamValues.GetValueOrDefault("text") ?? "";
                    return string.IsNullOrWhiteSpace(text) ? "// ..." : $"// {text}";
                }

                // Custom name overrides default display
                if (!string.IsNullOrWhiteSpace(CustomName))
                    return CustomName;

                var devicePart = ParamValues.GetValueOrDefault("device")
                              ?? ParamValues.GetValueOrDefault("camera_name")
                              ?? "";
                return string.IsNullOrEmpty(devicePart)
                    ? Plugin.DisplayName
                    : $"{Plugin.DisplayName}  [{devicePart}]";
            }
        }
    }
}
