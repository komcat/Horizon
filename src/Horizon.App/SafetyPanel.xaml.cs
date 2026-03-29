using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Horizon.Core;
using Horizon.Core.Interlock;
using Horizon.IO.Models;
using Horizon.Safety;
using Horizon.Safety.Models;
using Serilog;

namespace Horizon.App;

public partial class SafetyPanel : UserControl
{
    private readonly IAppContext _context;
    private readonly ObservableCollection<WatchedInputRow> _inputRows = new();
    private readonly ObservableCollection<EventLogRow> _eventRows = new();
    private readonly ObservableCollection<InterlockRuleRow> _interlockRows = new();
    private readonly DispatcherTimer _pollTimer;

    public SafetyPanel(IAppContext context)
    {
        InitializeComponent();
        _context = context;

        WatchedInputsGrid.ItemsSource = _inputRows;
        EventLogGrid.ItemsSource = _eventRows;
        InterlockRulesGrid.ItemsSource = _interlockRows;

        // Populate combos
        AddEdgeCombo.ItemsSource = new[] { "Rising", "Falling", "Both" };
        AddEdgeCombo.SelectedIndex = 0;
        AddSeverityCombo.ItemsSource = new[] { "Critical — blocks UI with popup", "Warning — indicator only" };
        AddSeverityCombo.SelectedIndex = 0;

        // Poll live input states + interlock states
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _pollTimer.Tick += (_, _) => { PollInputStates(); RefreshInterlockStatuses(); };

        var sw = _context.SafetyWatchManager;
        if (sw != null)
        {
            sw.SafetyTriggered += OnSafetyTriggered;
            sw.SafetyCleared += OnSafetyCleared;
        }

        Loaded += (_, _) =>
        {
            RefreshAll();
            _pollTimer.Start();
        };
        Unloaded += (_, _) =>
        {
            _pollTimer.Stop();
            if (sw != null)
            {
                sw.SafetyTriggered -= OnSafetyTriggered;
                sw.SafetyCleared -= OnSafetyCleared;
            }
        };
    }

    private void RefreshAll()
    {
        RefreshInputRows();
        RefreshEventLog();
        UpdateBannerState();
        UpdateEnforcementMode();
        RefreshInterlockStatuses();
    }

    // ── Watched Inputs ──────────────────────────────────────────────

    private void RefreshInputRows()
    {
        _inputRows.Clear();
        var sw = _context.SafetyWatchManager;
        if (sw == null) return;

        foreach (var input in sw.WatchedInputs)
        {
            _inputRows.Add(new WatchedInputRow
            {
                DeviceName = input.DeviceName,
                PinName = input.PinName,
                Trigger = input.Trigger.ToString(),
                AlertTitle = input.AlertTitle,
                AlertMessage = input.AlertMessage,
                IsCritical = input.IsCritical,
                CurrentState = "—",
                PinState = false
            });
        }
    }

    private void PollInputStates()
    {
        var ioMgr = _context.IOManager;
        foreach (var row in _inputRows)
        {
            var device = ioMgr.GetDevice(row.DeviceName);
            if (device == null || !device.IsConnected)
            {
                row.CurrentState = "N/C";
                row.PinState = false;
                continue;
            }

            var pin = device.GetPinByName(row.PinName);
            if (pin == null)
            {
                row.CurrentState = "N/A";
                row.PinState = false;
                continue;
            }

            var err = device.ReadInputs(out uint inputs);
            if (err != IOError.Success)
            {
                row.CurrentState = "ERR";
                row.PinState = false;
                continue;
            }

            bool state = (inputs & (1u << pin.Value)) != 0;
            row.CurrentState = state ? "HIGH" : "LOW";
            row.PinState = state;
        }

        WatchedInputsGrid.Items.Refresh();
        UpdateBannerState();
    }

    // ── Add / Remove / Save ─────────────────────────────────────────

    private void AddInput_Click(object sender, RoutedEventArgs e)
    {
        // Populate device combo from IO manager
        var deviceNames = _context.IOManager.GetAllDeviceNames();
        AddDeviceCombo.ItemsSource = deviceNames;
        if (deviceNames.Count > 0) AddDeviceCombo.SelectedIndex = 0;
        AddTitleBox.Text = "Safety Alert";
        AddMessageBox.Text = "";
        AddSeverityCombo.SelectedIndex = 0;
        AddEdgeCombo.SelectedIndex = 0;
        AddInputForm.Visibility = Visibility.Visible;
    }

    private void AddDeviceCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        var deviceName = AddDeviceCombo.SelectedItem as string;
        if (string.IsNullOrEmpty(deviceName))
        {
            AddPinCombo.ItemsSource = null;
            return;
        }

        var device = _context.IOManager.GetDevice(deviceName);
        if (device == null)
        {
            AddPinCombo.ItemsSource = null;
            return;
        }

        // Collect input pin names
        var pinNames = new List<string>();
        for (int i = 0; i < device.InputCount; i++)
        {
            var name = device.GetPinName(true, i);
            pinNames.Add(name ?? $"Input_{i}");
        }

        AddPinCombo.ItemsSource = pinNames;
        if (pinNames.Count > 0) AddPinCombo.SelectedIndex = 0;
    }

    private void ConfirmAddInput_Click(object sender, RoutedEventArgs e)
    {
        var sw = _context.SafetyWatchManager;
        if (sw == null) return;

        var deviceName = AddDeviceCombo.SelectedItem as string;
        var pinName = AddPinCombo.SelectedItem as string;
        if (string.IsNullOrEmpty(deviceName) || string.IsNullOrEmpty(pinName))
        {
            MessageBox.Show("Select a device and pin.", "Missing Input", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var edge = AddEdgeCombo.SelectedItem?.ToString() ?? "Rising";
        var trigger = edge switch
        {
            "Falling" => TriggerEdge.Falling,
            "Both" => TriggerEdge.Both,
            _ => TriggerEdge.Rising
        };

        bool isCritical = AddSeverityCombo.SelectedIndex == 0; // index 0 = Critical
        var title = string.IsNullOrWhiteSpace(AddTitleBox.Text) ? "Safety Alert" : AddTitleBox.Text.Trim();
        var message = string.IsNullOrWhiteSpace(AddMessageBox.Text)
            ? $"{deviceName}:{pinName} triggered ({edge})"
            : AddMessageBox.Text.Trim();

        var input = new WatchedInput
        {
            DeviceName = deviceName,
            PinName = pinName,
            Trigger = trigger,
            AlertTitle = title,
            AlertMessage = message,
            IsCritical = isCritical,
            Enabled = true
        };

        // Check for duplicate
        var exists = sw.WatchedInputs.Any(w =>
            w.DeviceName.Equals(deviceName, StringComparison.OrdinalIgnoreCase) &&
            w.PinName.Equals(pinName, StringComparison.OrdinalIgnoreCase));
        if (exists)
        {
            MessageBox.Show($"Input {deviceName}:{pinName} is already watched.", "Duplicate",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        bool wasWatching = sw.IsRunning;
        if (wasWatching) sw.StopWatching();
        sw.AddWatchedInput(input);
        if (wasWatching) sw.StartWatching();

        AddInputForm.Visibility = Visibility.Collapsed;
        RefreshInputRows();
        Log.Information("[SafetyPanel] Added watched input: {Device}:{Pin}", deviceName, pinName);
    }

    private void CancelAddInput_Click(object sender, RoutedEventArgs e)
    {
        AddInputForm.Visibility = Visibility.Collapsed;
    }

    private void RemoveInput_Click(object sender, RoutedEventArgs e)
    {
        var sw = _context.SafetyWatchManager;
        if (sw == null) return;

        var selected = WatchedInputsGrid.SelectedItem as WatchedInputRow;
        if (selected == null)
        {
            MessageBox.Show("Select a watched input to remove.", "No Selection",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(
            $"Remove watched input {selected.DeviceName}:{selected.PinName}?",
            "Remove", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        bool wasWatching = sw.IsRunning;
        if (wasWatching) sw.StopWatching();
        sw.RemoveWatchedInput(selected.DeviceName, selected.PinName);
        if (wasWatching) sw.StartWatching();

        RefreshInputRows();
        Log.Information("[SafetyPanel] Removed watched input: {Device}:{Pin}", selected.DeviceName, selected.PinName);
    }

    private void SaveConfig_Click(object sender, RoutedEventArgs e)
    {
        var sw = _context.SafetyWatchManager;
        if (sw == null) return;

        try
        {
            sw.SaveConfig();
            MessageBox.Show("Safety config saved.", "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to save: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── Banner / Status ─────────────────────────────────────────────

    private void UpdateBannerState()
    {
        var sw = _context.SafetyWatchManager;
        if (sw == null)
        {
            SetBannerSafe();
            StatusText.Text = "Safety: No watchdog configured";
            return;
        }

        if (sw.IsSafetyFlagRaised)
        {
            bool isCritical = sw.IsCriticalFlagRaised;
            StatusBanner.Background = new SolidColorBrush(isCritical ? Color.FromRgb(0x4A, 0x1A, 0x1A) : Color.FromRgb(0x4A, 0x3A, 0x1A));
            StatusBanner.BorderBrush = new SolidColorBrush(isCritical ? Color.FromRgb(0x8A, 0x2A, 0x2A) : Color.FromRgb(0x8A, 0x6A, 0x2A));
            StatusIndicator.Fill = isCritical ? Brushes.Red : Brushes.Orange;
            BannerTitle.Text = isCritical ? "CRITICAL SAFETY FAULT" : "Safety Warning";
            BannerTitle.Foreground = isCritical ? Brushes.Red : Brushes.Orange;
            BannerMessage.Text = "Acknowledge required to resume operations";
            StatusText.Text = isCritical ? "CRITICAL" : "Warning";
            StatusText.Foreground = isCritical ? Brushes.Red : Brushes.Orange;
            AcknowledgeBtn.IsEnabled = true;
        }
        else
        {
            SetBannerSafe();
        }
    }

    private void SetBannerSafe()
    {
        StatusBanner.Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x3A, 0x1B));
        StatusBanner.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2D, 0x5A, 0x2D));
        StatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0));
        BannerTitle.Text = "System Safe";
        BannerTitle.Foreground = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0));
        BannerMessage.Text = "All safety inputs normal";
        StatusText.Text = "Safety OK";
        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0));
        AcknowledgeBtn.IsEnabled = false;
    }

    private void UpdateEnforcementMode()
    {
        var sw = _context.SafetyWatchManager;
        if (sw?.EmergencyActionsConfig == null)
        {
            EnforcementModeText.Text = "Passive (log only)";
            EnforcementModeText.Foreground = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));
            return;
        }

        var actions = sw.EmergencyActionsConfig;
        var parts = new List<string>();
        if (actions.StopMotion) parts.Add("Motion");
        if (actions.RetractPneumatics) parts.Add("Pneumatic");
        if (actions.CancelExecution) parts.Add("Execution");
        if (actions.KillIO != null) parts.Add(actions.KillIO.KillAll ? "All IO" : $"{actions.KillIO.Pins.Count} IO pin(s)");
        if (actions.BlockNewExecution) parts.Add("Block");

        EnforcementModeText.Text = parts.Count > 0 ? string.Join(" + ", parts) : "None";
        EnforcementModeText.Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xAA));
    }

    // ── Event Log ───────────────────────────────────────────────────

    private void RefreshEventLog()
    {
        _eventRows.Clear();

        var logDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Horizon", "logs");
        var logFile = System.IO.Path.Combine(logDir, "safety_events.jsonl");

        if (!System.IO.File.Exists(logFile))
        {
            EventCountText.Text = "(no events)";
            return;
        }

        try
        {
            var lines = System.IO.File.ReadAllLines(logFile);
            var recentLines = lines.Length > 100 ? lines.Skip(lines.Length - 100) : lines;

            foreach (var line in recentLines.Reverse())
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var evt = System.Text.Json.JsonSerializer.Deserialize<SafetyEvent>(line,
                        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (evt == null) continue;

                    _eventRows.Add(new EventLogRow
                    {
                        TimeText = evt.Timestamp.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"),
                        Severity = evt.Severity,
                        Source = evt.Source,
                        EventType = evt.EventType,
                        Message = evt.Message
                    });
                }
                catch { }
            }

            EventCountText.Text = $"({_eventRows.Count} events)";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[SafetyPanel] Failed to read event log");
        }
    }

    // ── Event handlers ──────────────────────────────────────────────

    private void OnSafetyTriggered(string title, string message, bool isCritical)
    {
        Dispatcher.BeginInvoke(() =>
        {
            UpdateBannerState();
            RefreshEventLog();
        });
    }

    private void OnSafetyCleared()
    {
        Dispatcher.BeginInvoke(() =>
        {
            UpdateBannerState();
            RefreshEventLog();
        });
    }

    private void Acknowledge_Click(object sender, RoutedEventArgs e)
    {
        var sw = _context.SafetyWatchManager;
        if (sw == null) return;

        sw.AcknowledgeSafetyFlag();
        Log.Information("[SafetyPanel] Safety flag acknowledged by operator");
        UpdateBannerState();
        RefreshEventLog();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshAll();
    }
    // ── Interlocks tab ────────────────────────────────────────────

    private void RefreshInterlockStatuses(bool forceRebuild = false)
    {
        var ils = _context.InterlockService;
        if (ils == null)
        {
            if (_interlockRows.Count > 0) _interlockRows.Clear();
            InterlockSummaryText.Text = "No interlock service";
            return;
        }

        try
        {
            var statuses = ils.GetAllRuleStatuses();
            int blocking = 0;

            // Rebuild list only when rule count changed, otherwise update in-place
            if (forceRebuild || _interlockRows.Count != statuses.Count)
            {
                _interlockRows.Clear();
                foreach (var s in statuses)
                {
                    if (s.RuleEnabled && !s.IsSatisfied) blocking++;
                    _interlockRows.Add(new InterlockRuleRow
                    {
                        RuleName = s.RuleName,
                        Target = s.TargetDescription,
                        IsEnabled = s.RuleEnabled,
                        IsSatisfied = s.IsSatisfied,
                        BlockReason = s.BlockReason,
                        StatusText = s.RuleEnabled ? (s.IsSatisfied ? "Clear" : "BLOCKED") : "Disabled"
                    });
                }
            }
            else
            {
                // Update existing rows in-place (preserves selection)
                for (int i = 0; i < statuses.Count; i++)
                {
                    var s = statuses[i];
                    if (s.RuleEnabled && !s.IsSatisfied) blocking++;
                    _interlockRows[i].IsSatisfied = s.IsSatisfied;
                    _interlockRows[i].BlockReason = s.BlockReason;
                    _interlockRows[i].StatusText = s.RuleEnabled ? (s.IsSatisfied ? "Clear" : "BLOCKED") : "Disabled";
                }
                InterlockRulesGrid.Items.Refresh();
            }

            InterlockSummaryText.Text = statuses.Count == 0
                ? "No rules configured"
                : blocking > 0
                    ? $"{blocking} of {statuses.Count} rule(s) blocking"
                    : $"{statuses.Count} rule(s), all clear";

            OverrideStatusText.Text = ils.OverrideMode ? "OVERRIDE ACTIVE" : "";
            OverrideStatusText.Foreground = Brushes.Orange;
            OverrideCheck.IsChecked = ils.OverrideMode;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[SafetyPanel] Failed to refresh interlock statuses");
        }
    }

    private void Override_Click(object sender, RoutedEventArgs e)
    {
        var ils = _context.InterlockService;
        if (ils == null) return;

        bool enable = OverrideCheck.IsChecked == true;
        if (enable)
        {
            var result = MessageBox.Show(
                "Override mode allows motion despite interlocks.\n" +
                "This is for setup/debugging only.\n\n" +
                "Enable override?",
                "Override Interlocks", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
            {
                OverrideCheck.IsChecked = false;
                return;
            }
        }

        ils.OverrideMode = enable;
        Log.Information("[SafetyPanel] Interlock override {State}", enable ? "ENABLED" : "disabled");
        RefreshInterlockStatuses();
    }

    private void AddInterlockRule_Click(object sender, RoutedEventArgs e)
    {
        // Populate motion device combo
        var motionDevices = _context.MotionManager.GetAllDeviceNames();
        RuleDeviceCombo.ItemsSource = motionDevices;
        if (motionDevices.Count > 0) RuleDeviceCombo.SelectedIndex = 0;

        // Step 1: Slides — individual slides or "All pneumatics"
        var slideNames = _context.PneumaticManager.GetAllSlideNames();
        var slideOptions = new List<string> { "All pneumatics" };
        slideOptions.AddRange(slideNames);
        RuleSlidesCombo.ItemsSource = slideOptions;
        RuleSlidesCombo.SelectedIndex = 0;

        // Step 2: Axis block
        RuleAxisBlockCombo.ItemsSource = new[] { "All axes", "XY only", "Z only" };
        RuleAxisBlockCombo.SelectedIndex = 0;

        // Step 3: Enforcement layer
        RuleLayerCombo.ItemsSource = new[] { "Script/Run only", "Jog only", "All (script + jog)" };
        RuleLayerCombo.SelectedIndex = 0;

        RuleNameBox.Text = "";
        AddRuleForm.Visibility = Visibility.Visible;
    }

    private void ConfirmAddRule_Click(object sender, RoutedEventArgs e)
    {
        var ils = _context.InterlockService;
        if (ils == null) return;

        var name = RuleNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show("Enter a rule name.", "Missing Name", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var device = RuleDeviceCombo.SelectedItem as string;
        if (string.IsNullOrEmpty(device))
        {
            MessageBox.Show("Select a motion device.", "Missing Device", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Step 1: Which slides must be retracted
        var slideSelection = RuleSlidesCombo.SelectedItem?.ToString() ?? "All pneumatics";
        var slides = slideSelection == "All pneumatics"
            ? new List<string>()  // empty = all slides
            : new List<string> { slideSelection };

        // Step 2: Which axes to block
        var axisBlock = RuleAxisBlockCombo.SelectedIndex switch
        {
            1 => AxisBlock.XY,
            2 => AxisBlock.Z,
            _ => AxisBlock.All
        };

        // Step 3: Which layers enforce this
        var layer = RuleLayerCombo.SelectedIndex switch
        {
            1 => EnforcementLayer.JogOnly,
            2 => EnforcementLayer.All,
            _ => EnforcementLayer.ScriptOnly
        };

        var rule = new InterlockRule
        {
            Name = name,
            Enabled = true,
            TargetDevices = new List<string> { device },
            AxisBlock = axisBlock,
            Layer = layer,
            Conditions = new List<InterlockCondition>
            {
                new()
                {
                    Type = "allRetracted",
                    Slides = slides
                }
            }
        };

        ils.AddRule(rule);
        AddRuleForm.Visibility = Visibility.Collapsed;
        RefreshInterlockStatuses(forceRebuild: true);
        Log.Information("[SafetyPanel] Added interlock rule: {Name}", name);
    }

    private void CancelAddRule_Click(object sender, RoutedEventArgs e)
    {
        AddRuleForm.Visibility = Visibility.Collapsed;
    }

    private void RemoveInterlockRule_Click(object sender, RoutedEventArgs e)
    {
        var ils = _context.InterlockService;
        if (ils == null) return;

        var selected = InterlockRulesGrid.SelectedItem as InterlockRuleRow;
        if (selected == null)
        {
            MessageBox.Show("Select a rule to remove.", "No Selection", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Capture name before dialog (poll timer may clear selection)
        var ruleName = selected.RuleName;

        var result = MessageBox.Show($"Remove interlock rule \"{ruleName}\"?",
            "Remove Rule", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        ils.RemoveRule(ruleName);
        RefreshInterlockStatuses(forceRebuild: true);
        Log.Information("[SafetyPanel] Removed interlock rule: {Name}", ruleName);
    }

    private void SaveInterlockConfig_Click(object sender, RoutedEventArgs e)
    {
        var ils = _context.InterlockService;
        if (ils == null) return;

        try
        {
            ils.SaveConfig();
            MessageBox.Show("Interlock config saved.", "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to save: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

// ── View Models ─────────────────────────────────────────────────

public class WatchedInputRow
{
    public string DeviceName { get; set; } = "";
    public string PinName { get; set; } = "";
    public string Trigger { get; set; } = "";
    public string AlertTitle { get; set; } = "";
    public string AlertMessage { get; set; } = "";
    public bool IsCritical { get; set; }
    public string CurrentState { get; set; } = "—";
    public bool PinState { get; set; }

    public string SeverityText => IsCritical ? "Critical" : "Warning";

    public SolidColorBrush StateColor => CurrentState switch
    {
        "HIGH" => Brushes.LimeGreen,
        "LOW" => new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60)),
        "N/C" => Brushes.DarkGray,
        "ERR" => Brushes.Red,
        _ => Brushes.DarkGray
    };
}

public class EventLogRow
{
    public string TimeText { get; set; } = "";
    public SafetySeverity Severity { get; set; }
    public string Source { get; set; } = "";
    public string EventType { get; set; } = "";
    public string Message { get; set; } = "";

    public string SeverityText => Severity switch
    {
        SafetySeverity.Emergency => "EMRG",
        SafetySeverity.Critical => "CRIT",
        SafetySeverity.Warning => "WARN",
        SafetySeverity.Info => "INFO",
        _ => "?"
    };

    public SolidColorBrush SeverityColor => Severity switch
    {
        SafetySeverity.Emergency => Brushes.Red,
        SafetySeverity.Critical => Brushes.OrangeRed,
        SafetySeverity.Warning => Brushes.Orange,
        SafetySeverity.Info => new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0)),
        _ => Brushes.Gray
    };
}

public class InterlockRuleRow
{
    public string RuleName { get; set; } = "";
    public string Target { get; set; } = "";
    public bool IsEnabled { get; set; }
    public bool IsSatisfied { get; set; }
    public string BlockReason { get; set; } = "";
    public string StatusText { get; set; } = "";

    public SolidColorBrush StatusColor => !IsEnabled
        ? Brushes.Gray
        : IsSatisfied
            ? new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0))
            : new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0x00)); // Yellow
}
