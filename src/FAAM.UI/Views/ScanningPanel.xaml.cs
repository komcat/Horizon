using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Horizon.Engine;

namespace FAAM.UI.Views;

/// <summary>
/// Run-page Scanning panel (under Raster Climb + Auto W, above the log). Two hex-right
/// position slots — a small Save button (top row) stores the current six-axis pose, a Move
/// button (second row) moves the hexapod there (right-click: rename / clear) — and four
/// script buttons (2×2) that run an assigned script preset (<see cref="ScriptPresetStore"/>:
/// a script + its arguments; orange when the preset has no exit threshold) or a bare
/// <c>.moon</c> script from the active product (right-click picks the preset or script /
/// renames / clears). The customisation lives in <see cref="ScanPanelStore"/>
/// (<c>faam_scan_panel.json</c>) and is also shown and edited on Machine › Settings.
/// </summary>
public partial class ScanningPanel : UserControl
{
    /// <summary>Upper bound on one position move (the hexapod's slowest full traverse is well under this).</summary>
    private static readonly TimeSpan MoveTimeout = TimeSpan.FromSeconds(60);

    private EngineHost? _host;
    private ScanPanelSettings _settings = ScanPanelStore.Load();
    private CancellationTokenSource? _cts;
    private bool _moving;

    private readonly Button[] _saveButtons;
    private readonly Button[] _posButtons;
    private readonly TextBlock[] _posCaptions;
    private readonly TextBlock[] _posDetails;
    private readonly Button[] _scriptButtons;
    private readonly TextBlock[] _scriptCaptions;
    private readonly TextBlock[] _scriptDetails;

    public ScanningPanel()
    {
        InitializeComponent();
        _saveButtons = new[] { Save0Button, Save1Button };
        _posButtons = new[] { Pos0Button, Pos1Button };
        _posCaptions = new[] { Pos0Caption, Pos1Caption };
        _posDetails = new[] { Pos0Detail, Pos1Detail };
        _scriptButtons = new[] { Script0Button, Script1Button, Script2Button, Script3Button };
        _scriptCaptions = new[] { Script0Caption, Script1Caption, Script2Caption, Script3Caption };
        _scriptDetails = new[] { Script0Detail, Script1Detail, Script2Detail, Script3Detail };
        // Presets matter too: a preset's "no exit threshold" sets its button colour.
        Unloaded += (_, _) =>
        {
            ScanPanelStore.Changed -= OnStoreChanged;
            ScriptPresetStore.Changed -= OnStoreChanged;
        };
        Loaded += (_, _) =>
        {
            ScanPanelStore.Changed -= OnStoreChanged;
            ScanPanelStore.Changed += OnStoreChanged;
            ScriptPresetStore.Changed -= OnStoreChanged;
            ScriptPresetStore.Changed += OnStoreChanged;
        };
        Render();
    }

    /// <summary>Supplies the engine host (hexapod motion, the script runner, activity gate and log).</summary>
    public void Attach(EngineHost host)
    {
        _host = host;
        host.Activity.Changed += OnActivityChanged;
        RefreshEnabled();
    }

    // --- rendering ---

    /// <summary>Settings saved elsewhere (Machine › Settings) — reload and redraw.</summary>
    private void OnStoreChanged() => Ui(() =>
    {
        _settings = ScanPanelStore.Load();
        Render();
    });

    private void Render()
    {
        for (var i = 0; i < ScanPanelStore.PositionSlotCount; i++)
        {
            var p = _settings.Positions[i];
            _posCaptions[i].Text = p.Label;
            _posDetails[i].Text = p.Position == null ? "not saved" : $"Z {p.Position.Z:0.###}  W {p.Position.W:0.###}";
            _posButtons[i].ToolTip = p.Position == null
                ? $"No {ScanPanelStore.Device} position saved — press Save{i + 1} first"
                : $"Move {ScanPanelStore.Device} to:\n{p.Position}";
            _saveButtons[i].ToolTip = p.Position == null
                ? $"Save the current {ScanPanelStore.Device} pose for {p.Label}"
                : $"Save the current {ScanPanelStore.Device} pose for {p.Label} (asks before overwriting)\nNow: {p.Position}";
        }

        var presets = ScriptPresetStore.Load();
        for (var i = 0; i < ScanPanelStore.ScriptSlotCount; i++)
        {
            var s = _settings.Scripts[i];
            _scriptCaptions[i].Text = s.Label;
            _scriptDetails[i].Text = !string.IsNullOrEmpty(s.Preset) ? s.Preset
                : string.IsNullOrEmpty(s.Script) ? "no script" : s.Script;

            // Orange = a preset with no exit threshold; default (blue) otherwise.
            var preset = string.IsNullOrEmpty(s.Preset) ? null
                : presets.FirstOrDefault(p => string.Equals(p.Name, s.Preset, StringComparison.OrdinalIgnoreCase));
            if (preset?.NoExitThreshold == true)
            {
                _scriptButtons[i].Background = NoExitBackground;
                _scriptButtons[i].Foreground = NoExitForeground;
            }
            else
            {
                _scriptButtons[i].ClearValue(BackgroundProperty);
                _scriptButtons[i].ClearValue(ForegroundProperty);
            }

            _scriptButtons[i].ToolTip = !string.IsNullOrEmpty(s.Preset)
                ? $"Run preset '{s.Preset}' (Machine › Settings › Script presets)"
                  + (preset?.NoExitThreshold == true ? "\nNo exit threshold — climbs every set" : "")
                : string.IsNullOrEmpty(s.Script)
                    ? "No script assigned — right-click › Select preset / Select script"
                    : $"Run script '{s.Script}' (active product)";
        }
    }

    // Script button colour for a preset with no exit threshold (pika orange, dark text).
    private static readonly Brush NoExitBackground = Frozen(Color.FromRgb(0xF5, 0xA5, 0x24));
    private static readonly Brush NoExitForeground = Frozen(Color.FromRgb(0x1E, 0x1E, 0x1E));

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private void OnActivityChanged()
    {
        if (Dispatcher.CheckAccess()) RefreshEnabled();
        else Dispatcher.BeginInvoke(RefreshEnabled);
    }

    /// <summary>The buttons work only when nothing else (a process step, alignment or this panel) holds the machine.</summary>
    private void RefreshEnabled()
    {
        bool idle = _host != null && _cts == null && !OthersBusy();
        foreach (var b in _posButtons) b.IsEnabled = idle;
        foreach (var b in _saveButtons) b.IsEnabled = idle;   // no saving a pose mid-move
        foreach (var b in _scriptButtons) b.IsEnabled = idle;
        StopButton.IsEnabled = _cts != null;
    }

    private bool OthersBusy()
        => _host != null && (_host.Activity.ProcessBusy || _host.Activity.AlignmentBusy || _host.Process.IsRunning);

    // --- position buttons ---

    private async void OnPositionClick(object sender, RoutedEventArgs e)
    {
        if (_host == null || _cts != null || !TryIndex(sender, ScanPanelStore.PositionSlotCount, out var i)) return;
        var slot = _settings.Positions[i];
        var device = ScanPanelStore.Device;

        if (slot.Position == null)
        {
            SetStatus($"{slot.Label}: no position saved — press Save{i + 1} first.", warn: true);
            return;
        }
        if (!CanStart(device)) return;

        var target = slot.Position.ToPosition();
        _cts = new CancellationTokenSource(MoveTimeout);
        _moving = true;
        RefreshEnabled();
        var activity = _host.Activity.Begin(ActivityKind.Alignment, $"Scan move {slot.Label}");
        SetStatus($"Moving {device} to {slot.Label}…");
        Log(LogLevel.Info, $"{slot.Label}: moving {device} to {slot.Position}.");
        try
        {
            var r = await _host.Engine.Motion.MoveToPositionAsync(device, target, _cts.Token).ConfigureAwait(true);
            if (r.IsSuccess)
            {
                SetStatus($"{device} at {slot.Label}.");
                Log(LogLevel.Info, $"{slot.Label}: {device} arrived.");
            }
            else
            {
                SetStatus($"{slot.Label}: move failed — {r.Message}", warn: true);
                Log(LogLevel.Error, $"{slot.Label}: move failed — {r.Message}");
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus($"{slot.Label}: move cancelled.", warn: true);
            Log(LogLevel.Warning, $"{slot.Label}: move cancelled or timed out.");
        }
        catch (Exception ex)
        {
            SetStatus($"{slot.Label}: move failed — {ex.Message}", warn: true);
            Log(LogLevel.Error, $"{slot.Label}: move failed — {ex.Message}");
        }
        finally
        {
            activity.Dispose();
            _moving = false;
            _cts?.Dispose();
            _cts = null;
            RefreshEnabled();
        }
    }

    private void OnPositionRightClick(object sender, MouseButtonEventArgs e)
    {
        if (!TryIndex(sender, ScanPanelStore.PositionSlotCount, out var i) || sender is not Button b) return;
        e.Handled = true;
        var slot = _settings.Positions[i];

        var menu = new ContextMenu();
        menu.Items.Add(Item("Rename…", () => Rename(slot.Label, "position button", name => slot.Label = name)));
        var clear = Item("Clear saved position", () =>
        {
            slot.Position = null;
            Persist($"{slot.Label}: position cleared.");
        });
        clear.IsEnabled = slot.Position != null;
        menu.Items.Add(clear);
        Open(menu, b);
    }

    /// <summary>Save1 / Save2: stores the current hex-right pose in that slot (asks before overwriting).</summary>
    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_host == null || _cts != null || !TryIndex(sender, ScanPanelStore.PositionSlotCount, out var i)) return;
        _ = SaveCurrentPositionAsync(i);
    }

    private async Task SaveCurrentPositionAsync(int i)
    {
        if (_host == null) return;
        var slot = _settings.Positions[i];
        var device = ScanPanelStore.Device;
        if (!_host.Engine.Motion.IsConnected(device))
        {
            SetStatus($"{device} not connected — cannot save.", warn: true);
            return;
        }

        var r = await _host.Engine.Motion.GetPositionAsync(device).ConfigureAwait(true);
        if (!r.IsSuccess)
        {
            SetStatus($"Read {device} failed — {r.Message}", warn: true);
            return;
        }

        var pose = ScanAxes.From(r.Value);
        if (!pose.IsValid())
        {
            SetStatus($"Read {device} returned an invalid position — not saved.", warn: true);
            return;
        }
        if (slot.Position != null
            && MessageBox.Show(Window.GetWindow(this)!,
                   $"Overwrite {slot.Label}?\n\nOld: {slot.Position}\nNew: {pose}",
                   "Save position", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        var old = slot.Position;
        slot.Position = pose;
        Persist($"{slot.Label} saved: {pose}");
        Log(LogLevel.Info, $"{slot.Label}: saved {device} {(old == null ? "" : $"(was {old}) → ")}{pose}.");
    }

    // --- script buttons ---

    private async void OnScriptClick(object sender, RoutedEventArgs e)
    {
        if (_host == null || _cts != null || !TryIndex(sender, ScanPanelStore.ScriptSlotCount, out var i)) return;
        var slot = _settings.Scripts[i];

        // A preset (script + arguments) wins over a bare script.
        string script = slot.Script, args = "", what = slot.Script;
        if (!string.IsNullOrEmpty(slot.Preset))
        {
            var preset = ScriptPresetStore.Find(slot.Preset);
            if (preset == null || string.IsNullOrEmpty(preset.Script))
            {
                SetStatus($"{slot.Label}: preset '{slot.Preset}' not found (or has no script) — Machine › Settings › Script presets.", warn: true);
                return;
            }
            (script, args, what) = (preset.Script, preset.EffectiveArgs(),
                $"{preset.Name} ({preset.Script}{(preset.NoExitThreshold ? ", no exit threshold" : "")})");
        }
        if (string.IsNullOrEmpty(script))
        {
            SetStatus($"{slot.Label}: no script assigned — right-click › Select preset / Select script.", warn: true);
            return;
        }
        if (!CanStart(device: null)) return;

        var source = _host.Process.LoadScript(script);
        if (source == null)
        {
            SetStatus($"{slot.Label}: script '{script}' not found in the active product.", warn: true);
            Log(LogLevel.Warning, $"{slot.Label}: script '{script}' not found in the active product.");
            return;
        }

        _cts = new CancellationTokenSource();
        RefreshEnabled();
        var activity = _host.Activity.Begin(ActivityKind.Alignment, $"Scan script {slot.Label}");
        SetStatus($"{slot.Label}: running '{what}'…");
        Log(LogLevel.Info, $"{slot.Label}: running '{what}'.");
        try
        {
            var result = await _host.Process.RunScriptAsync(source, onOutput: null, ct: _cts.Token, args: args).ConfigureAwait(true);
            if (result.Success) SetStatus($"{slot.Label}: '{what}' completed.");
            else if (result.Cancelled) SetStatus($"{slot.Label}: '{what}' cancelled.", warn: true);
            else SetStatus($"{slot.Label}: '{what}' error — {result.Error}", warn: true);
        }
        catch (Exception ex)
        {
            SetStatus($"{slot.Label}: '{what}' error — {ex.Message}", warn: true);
            Log(LogLevel.Error, $"{slot.Label}: '{what}' failed — {ex.Message}");
        }
        finally
        {
            activity.Dispose();
            _cts?.Dispose();
            _cts = null;
            RefreshEnabled();
        }
    }

    private void OnScriptRightClick(object sender, MouseButtonEventArgs e)
    {
        if (_host == null || !TryIndex(sender, ScanPanelStore.ScriptSlotCount, out var i) || sender is not Button b) return;
        e.Handled = true;
        var slot = _settings.Scripts[i];

        var menu = new ContextMenu();

        // Presets (script + arguments, Machine › Settings) — picking one replaces a bare script.
        var selectPreset = new MenuItem { Header = "Select preset" };
        var presets = ScriptPresetStore.Load();
        if (presets.Count == 0)
            selectPreset.Items.Add(new MenuItem { Header = "(no presets — add them on Machine › Settings)", IsEnabled = false });
        foreach (var preset in presets)
        {
            var name = preset.Name;
            var pick = Item(name, () =>
            {
                slot.Preset = name;
                slot.Script = "";
                Persist($"{slot.Label} → preset '{name}'.");
            });
            pick.IsChecked = string.Equals(name, slot.Preset, StringComparison.OrdinalIgnoreCase);
            pick.ToolTip = string.IsNullOrEmpty(preset.Args) ? preset.Script : $"{preset.Script}  {preset.Args}";
            selectPreset.Items.Add(pick);
        }
        menu.Items.Add(selectPreset);

        var select = new MenuItem { Header = "Select script (no arguments)" };
        var scripts = _host.Process.ListScripts();
        if (scripts.Count == 0)
            select.Items.Add(new MenuItem { Header = "(no scripts in the active product)", IsEnabled = false });
        foreach (var name in scripts)
        {
            var pick = Item(name, () =>
            {
                slot.Script = name;
                slot.Preset = "";
                Persist($"{slot.Label} → script '{name}'.");
            });
            pick.IsChecked = string.IsNullOrEmpty(slot.Preset)
                             && string.Equals(name, slot.Script, StringComparison.OrdinalIgnoreCase);
            select.Items.Add(pick);
        }
        menu.Items.Add(select);
        menu.Items.Add(Item("Rename…", () => Rename(slot.Label, "script button", name => slot.Label = name)));
        var clear = Item("Clear", () =>
        {
            slot.Script = "";
            slot.Preset = "";
            Persist($"{slot.Label}: cleared.");
        });
        clear.IsEnabled = !string.IsNullOrEmpty(slot.Script) || !string.IsNullOrEmpty(slot.Preset);
        menu.Items.Add(clear);
        Open(menu, b);
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        if (_moving && _host != null)
            _ = _host.Engine.Motion.StopAsync(ScanPanelStore.Device);
        Log(LogLevel.Warning, "Stop pressed.");
    }

    // --- helpers ---

    /// <summary>Refuses (with a status line) while another run holds the machine or the device is offline.</summary>
    private bool CanStart(string? device)
    {
        if (_host == null) return false;
        if (OthersBusy())
        {
            SetStatus("Busy — wait for the current run.", warn: true);
            return false;
        }
        if (device != null && !_host.Engine.Motion.IsConnected(device))
        {
            SetStatus($"{device} not connected.", warn: true);
            return false;
        }
        return true;
    }

    private void Rename(string current, string what, Action<string> apply)
    {
        var dlg = new SerialEntryWindow("Rename", $"Text for this {what}:", current)
        {
            Owner = Window.GetWindow(this),
        };
        if (dlg.ShowDialog() != true) return;
        var name = dlg.Serial;
        if (string.IsNullOrWhiteSpace(name) || name == current) return;
        apply(name);
        Persist($"Renamed '{current}' → '{name}'.");
    }

    /// <summary>Writes the settings (Machine › Settings picks the change up) and redraws.</summary>
    private void Persist(string status)
    {
        try
        {
            ScanPanelStore.Save(_settings);   // raises Changed → OnStoreChanged reloads + renders
            SetStatus(status);
        }
        catch (Exception ex)
        {
            SetStatus($"Save failed: {ex.Message}", warn: true);
            _settings = ScanPanelStore.Load();
            Render();
        }
    }

    private static MenuItem Item(string header, Action onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => onClick();
        return item;
    }

    private static void Open(ContextMenu menu, FrameworkElement target)
    {
        menu.PlacementTarget = target;
        menu.IsOpen = true;
    }

    private static bool TryIndex(object sender, int count, out int index)
    {
        index = -1;
        return sender is FrameworkElement { Tag: string tag } && int.TryParse(tag, out index)
               && index >= 0 && index < count;
    }

    private void SetStatus(string text, bool warn = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = (Brush)FindResource(warn ? "Danger" : "Subtle");
    }

    private void Ui(Action action)
    {
        if (Dispatcher.CheckAccess()) action();
        else Dispatcher.BeginInvoke(action);
    }

    private void Log(LogLevel level, string message)
        => _host?.Log.Add(new LogEntry(level, DateTimeOffset.Now, "Scan", message));
}
