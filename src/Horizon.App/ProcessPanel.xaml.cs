using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Horizon.App.Controls;
using Horizon.Core;
using Horizon.Process;
using Horizon.Process.Interfaces;
using Horizon.Process.Models;
using Microsoft.Win32;

namespace Horizon.App;

public partial class ProcessPanel : UserControl
{
    private readonly IAppContext _context;
    private readonly ProcessPluginRegistry _registry;
    private readonly PropertyTable _propertyTable;
    private readonly List<PluginStepNode> _builderSteps = new();

    private CancellationTokenSource? _cts;
    private bool _isRunning;
    private string? _currentFilePath;

    private static readonly string DefaultProcessDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Horizon", "processes");

    private static readonly string LastProgramFile =
        Path.Combine(DefaultProcessDir, ".lastprogram");

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

            // Insert after selection or at end
            var selectedIndex = BuilderList.SelectedIndex;
            if (selectedIndex >= 0 && selectedIndex < _builderSteps.Count)
                _builderSteps.Insert(selectedIndex + 1, node);
            else
                _builderSteps.Add(node);

            RefreshBuilderList();
            BuilderList.SelectedIndex = _builderSteps.IndexOf(node);
        }
    }

    // --- Builder List ---

    private static readonly Brush DisabledBrush = new SolidColorBrush(Color.FromRgb(0x6A, 0x6A, 0x6A));
    private static readonly Brush StructBrush = new SolidColorBrush(Color.FromRgb(0x56, 0x9C, 0xD6)); // blue for flow control

    private const string LoopStartId = "system.loop_start";
    private const string LoopEndId = "system.loop_end";

    private void RefreshBuilderList()
    {
        var selectedIdx = BuilderList.SelectedIndex;
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

            Brush fg;
            if (!node.IsEnabled)
                fg = DisabledBrush;
            else if (isLoopStart || isLoopEnd)
                fg = StructBrush;
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
        if (selectedIdx >= 0 && selectedIdx < BuilderList.Items.Count)
            BuilderList.SelectedIndex = selectedIdx;
    }

    private void BuilderList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BuilderList.SelectedItem is ListBoxItem item && item.Tag is PluginStepNode node)
        {
            _propertyTable.BindPlugin(node.Plugin, node.ParamValues);
        }
        else
        {
            _propertyTable.BindPlugin(null, null);
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

            var node = new PluginStepNode(plugin) { IsEnabled = step.Enabled };
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

        var dlg = new SaveFileDialog
        {
            Title = "Save Process Program",
            Filter = "Horizon Process (*.hproc)|*.hproc",
            DefaultExt = ".hproc",
            InitialDirectory = DefaultProcessDir,
            FileName = "Untitled"
        };

        if (dlg.ShowDialog() != true) return;

        _currentFilePath = dlg.FileName;
        SaveToFile(dlg.FileName);
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
                Enabled = node.IsEnabled
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

        var dlg = new OpenFileDialog
        {
            Title = "Open Process Program",
            Filter = "Horizon Process (*.hproc)|*.hproc",
            DefaultExt = ".hproc",
            InitialDirectory = DefaultProcessDir
        };

        if (dlg.ShowDialog() != true) return;

        if (LoadProgram(dlg.FileName))
            SaveLastProgramPath(dlg.FileName);
    }

    // --- Enable / Disable ---

    private void ToggleEnable_Click(object sender, RoutedEventArgs e)
    {
        var idx = BuilderList.SelectedIndex;
        if (idx < 0 || idx >= _builderSteps.Count) return;

        _builderSteps[idx].IsEnabled = !_builderSteps[idx].IsEnabled;
        RefreshBuilderList();
    }

    // --- Toolbar handlers ---

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var idx = BuilderList.SelectedIndex;
        if (idx < 0 || idx >= _builderSteps.Count) return;

        _builderSteps.RemoveAt(idx);
        RefreshBuilderList();

        if (_builderSteps.Count > 0)
            BuilderList.SelectedIndex = Math.Min(idx, _builderSteps.Count - 1);
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        var idx = BuilderList.SelectedIndex;
        if (idx <= 0) return;

        (_builderSteps[idx], _builderSteps[idx - 1]) = (_builderSteps[idx - 1], _builderSteps[idx]);
        RefreshBuilderList();
        BuilderList.SelectedIndex = idx - 1;
    }

    private void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        var idx = BuilderList.SelectedIndex;
        if (idx < 0 || idx >= _builderSteps.Count - 1) return;

        (_builderSteps[idx], _builderSteps[idx + 1]) = (_builderSteps[idx + 1], _builderSteps[idx]);
        RefreshBuilderList();
        BuilderList.SelectedIndex = idx + 1;
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning || _builderSteps.Count == 0) return;
        if (!_builderSteps.Any(n => n.IsEnabled))
        {
            StatusText.Text = "No enabled steps to run";
            return;
        }

        _isRunning = true;
        _cts = new CancellationTokenSource();
        RunBtn.IsEnabled = false;
        StopBtn.IsEnabled = true;
        ContinueBtn.IsEnabled = StepModeCheck.IsChecked == true;
        StatusText.Text = "Running...";

        var engine = _context.ProcessEngine;
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
                Dispatcher.Invoke(() =>
                {
                    StatusText.Text = $"Step {idx}: {device}.{method}";
                    var bi = stepToBuilderMap.GetValueOrDefault(idx, -1);
                    if (bi >= 0) HighlightStep(bi, Brushes.Yellow);
                });
            };

            engine.OnStepCompleted = (idx, success) =>
            {
                Dispatcher.Invoke(() =>
                {
                    var bi = stepToBuilderMap.GetValueOrDefault(idx, -1);
                    if (bi >= 0) HighlightStep(bi, success ? Brushes.LimeGreen : Brushes.OrangeRed);
                });
            };

            var ctx = new ProcessContext { CancellationToken = _cts.Token };

            var result = await Task.Run(() => engine.ExecuteAsync(processSteps, ctx, _cts.Token));
            StatusText.Text = result ? "Completed successfully" : "Completed with errors";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Stopped by user";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
        }
        finally
        {
            _isRunning = false;
            RunBtn.IsEnabled = true;
            StopBtn.IsEnabled = false;
            ContinueBtn.IsEnabled = false;
            engine.OnStepStarted = null;
            engine.OnStepCompleted = null;
            ClearHighlights();
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

    private sealed class PluginStepNode
    {
        public IProcessPlugin Plugin { get; }
        public Dictionary<string, string> ParamValues { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool IsEnabled { get; set; } = true;

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
