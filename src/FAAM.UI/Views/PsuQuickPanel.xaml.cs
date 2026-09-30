using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Horizon.Common;
using Horizon.Engine;

namespace FAAM.UI.Views;

/// <summary>
/// Run-page power-supply strip: one row per SHOWN channel of the selected supply with live
/// V / A readouts (green-on-black device display, like the Process timer) and an output
/// ON/OFF toggle. CH1 is always shown; the others are added with "+ CHn" and removed with
/// "×" — the choice is saved per supply in <c>faam_psu_panel.json</c>. A channel whose
/// output is ON can't be removed, so a live output is never hidden. All channels are still
/// polled, so a re-added row is current at once. The button shows the polled output state
/// (green = ON) and a click switches it to the other state. Readings are refreshed off the
/// UI thread every <see cref="RefreshInterval"/> while the panel is visible; setpoints are
/// authored on the Machine page's Power supply section or by the PSU blocks.
/// The toggles are LOCKED (readouts keep updating) while anything is running: a Run-page
/// process step / alignment / scan (<see cref="ActivityGate"/>) or a Layer 2 program, deck
/// or recipe run (<see cref="Horizon.Workflow.IProcessService.IsRunning"/>) — the run owns
/// the supply. Re-checked at click time, so a run that starts between polls still wins.
/// </summary>
public partial class PsuQuickPanel : UserControl
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(500);
    private const string PrefsFile = "faam_psu_panel";   // { "<device>": [1, 2], ... }
    private const int DefaultChannel = 1;

    // Fixed device-readout colours (same as the Process timer) — deliberately not themed.
    private static readonly Brush ReadoutFg = Frozen(Color.FromRgb(0x33, 0xFF, 0x33));
    private static readonly Brush ReadoutBg = Frozen(Colors.Black);

    private readonly DispatcherTimer _timer;
    private readonly List<ChannelRow> _rows = new();
    private readonly SortedSet<int> _shown = new();
    private EngineHost? _host;
    private string? _device;
    private bool _refreshInFlight;
    private bool _commandInFlight;
    private bool _connected;

    public PsuQuickPanel()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = RefreshInterval };
        _timer.Tick += async (_, _) => await RefreshAsync();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && _device != null) { ApplyLock(); _timer.Start(); }
            else _timer.Stop();
        };
    }

    /// <summary>Wire to the engine host (called from the Run view).</summary>
    public void Attach(EngineHost host)
    {
        _host = host;
        // Gate transitions fire on any thread — marshal, and lock/unlock immediately
        // rather than waiting for the next poll.
        host.Activity.Changed += () => Dispatcher.BeginInvoke(ApplyLock);
        var devices = host.Engine.PowerSupply.DeviceNames;
        if (devices.Count == 0)
        {
            DeviceText.Text = "none configured";
            SetStatus("No power supply in power_supply_devices.json.", warn: true);
            return;
        }

        if (devices.Count > 1)
        {
            DeviceCombo.ItemsSource = devices;
            DeviceCombo.Visibility = Visibility.Visible;
        }
        DeviceCombo.SelectedItem = devices[0];   // no-op (combo hidden) for one device
        SelectDevice(devices[0]);
    }

    private void OnDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceCombo.SelectedItem is string d && d != _device) SelectDevice(d);
    }

    private void SelectDevice(string device)
    {
        if (_host == null) return;
        _device = device;
        DeviceText.Text = DeviceCombo.Visibility == Visibility.Visible ? "" : device;

        ChannelRows.Children.Clear();
        _rows.Clear();
        var count = _host.Engine.PowerSupply.GetDevice(device)?.ChannelCount ?? 1;
        for (var ch = 1; ch <= count; ch++)
        {
            var row = new ChannelRow(this, ch);
            _rows.Add(row);
            ChannelRows.Children.Add(row.Root);
        }

        _shown.Clear();
        _shown.Add(DefaultChannel);
        foreach (var ch in LoadShown(device).Where(c => c >= 1 && c <= count))
            _shown.Add(ch);
        UpdateShownChannels();

        if (IsVisible) _timer.Start();
        _ = RefreshAsync();
    }

    // ---- shown channels (add / remove) ----

    private void ShowChannel(int channel)
    {
        if (!_shown.Add(channel)) return;
        UpdateShownChannels();
        SaveShown();
    }

    private void HideChannel(ChannelRow row)
    {
        if (row.Channel == DefaultChannel) return;
        if (row.OutputOn)
        {
            // Never hide a live output.
            SetStatus($"CH{row.Channel} output is ON — switch it OFF before removing it.", warn: true);
            return;
        }
        if (!_shown.Remove(row.Channel)) return;
        UpdateShownChannels();
        SaveShown();
    }

    /// <summary>Row visibility + one "+ CHn" button per hidden channel. No I/O.</summary>
    private void UpdateShownChannels()
    {
        foreach (var row in _rows)
            row.Root.Visibility = _shown.Contains(row.Channel) ? Visibility.Visible : Visibility.Collapsed;

        AddButtons.Children.Clear();
        foreach (var row in _rows.Where(r => !_shown.Contains(r.Channel)))
        {
            var ch = row.Channel;
            var add = new Button
            {
                Content = $"+ CH{ch}", Height = 24, Padding = new Thickness(8, 0, 8, 0),
                Margin = new Thickness(4, 0, 0, 0), ToolTip = $"Show CH{ch}",
            };
            add.Click += (_, _) => ShowChannel(ch);
            AddButtons.Children.Add(add);
        }
    }

    private static IReadOnlyList<int> LoadShown(string device)
    {
        try
        {
            var prefs = ConfigLoader.LoadJson<Dictionary<string, List<int>>>(
                ConfigLoader.GetConfigDirectory(), PrefsFile);
            var match = prefs?.FirstOrDefault(p => string.Equals(p.Key, device, StringComparison.OrdinalIgnoreCase));
            return match?.Value ?? (IReadOnlyList<int>)Array.Empty<int>();
        }
        catch
        {
            return Array.Empty<int>();   // unreadable prefs → default (CH1 only)
        }
    }

    private void SaveShown()
    {
        if (_device == null) return;
        try
        {
            var dir = ConfigLoader.GetConfigDirectory();
            var prefs = ConfigLoader.LoadJson<Dictionary<string, List<int>>>(dir, PrefsFile)
                        ?? new Dictionary<string, List<int>>();
            prefs[_device] = _shown.ToList();
            ConfigLoader.SaveJson(dir, PrefsFile, prefs);
        }
        catch (Exception ex)
        {
            // Cosmetic preference — never fail the panel over it.
            _host?.Log.Add(new LogEntry(LogLevel.Warning, DateTimeOffset.Now, "PSU",
                $"Could not save PSU panel channels: {ex.Message}"));
        }
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private async Task RefreshAsync()
    {
        if (_host == null || _device == null || _refreshInFlight) return;
        _refreshInFlight = true;
        try
        {
            var psu = _host.Engine.PowerSupply;
            var device = _device;
            // A refresh is ~9 SCPI round-trips for the UDP3305S — never on the UI thread.
            await Task.Run(() => psu.RefreshAsync()).ConfigureAwait(true);
            if (device != _device) return;   // switched supply mid-refresh

            _connected = psu.IsConnected(device);
            var state = psu.GetState(device);
            foreach (var row in _rows)
                row.Apply(_connected ? state.Channels.FirstOrDefault(c => c.Channel == row.Channel) : null);
            ApplyLock();

            if (!_connected) SetStatus($"{device} not connected.", warn: true);
            else if (!string.IsNullOrEmpty(state.LastError)) SetStatus(state.LastError, warn: true);
            else if (RunLocked(out var why)) SetStatus($"Locked — {why}.");
            else if (StatusText.Tag is not "cmd") SetStatus("");
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, warn: true);
        }
        finally { _refreshInFlight = false; }
    }

    /// <summary>True (with a reason) while a run owns the supply.</summary>
    private bool RunLocked(out string why)
    {
        why = string.Empty;
        if (_host == null) return false;
        if (_host.Process.IsRunning) why = "a program / deck is running";
        else if (_host.Activity.ProcessBusy) why = "a process step is running";
        else if (_host.Activity.AlignmentBusy) why = "an alignment / scan is running";
        return why.Length > 0;
    }

    /// <summary>Enables the toggles only when connected, idle, and no run owns the supply. No I/O.</summary>
    private void ApplyLock()
    {
        bool enabled = _connected && !_commandInFlight && !RunLocked(out _);
        foreach (var row in _rows) row.SetEnabled(enabled);
    }

    private async Task ToggleAsync(ChannelRow row)
    {
        if (_host == null || _device == null || _commandInFlight) return;
        if (RunLocked(out var why))
        {
            // A run started between polls — refuse and re-lock.
            SetStatus($"Locked — {why}.", warn: true);   // not sticky: cleared once the run ends
            ApplyLock();
            return;
        }
        _commandInFlight = true;
        foreach (var r in _rows) r.Button.IsEnabled = false;

        var turnOn = !row.OutputOn;
        var device = _device;
        try
        {
            var psu = _host.Engine.PowerSupply;
            Result result = turnOn
                ? await psu.OutputOnAsync(device, row.Channel).ConfigureAwait(true)
                : await psu.OutputOffAsync(device, row.Channel).ConfigureAwait(true);

            var what = $"{device} CH{row.Channel} output {(turnOn ? "ON" : "OFF")}";
            SetStatus(result.IsSuccess ? what : $"{what} failed: {result.Message}", warn: !result.IsSuccess, fromCommand: true);
            _host.Log.Add(new LogEntry(result.IsSuccess ? LogLevel.Info : LogLevel.Error,
                DateTimeOffset.Now, "PSU", result.IsSuccess ? what : $"{what} failed: {result.Message}"));
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, warn: true, fromCommand: true);
        }
        finally
        {
            _commandInFlight = false;
        }
        await RefreshAsync();   // show the new state straight away
    }

    private void SetStatus(string text, bool warn = false, bool fromCommand = false)
    {
        StatusText.Text = text;
        StatusText.Tag = fromCommand ? "cmd" : null;   // keep a command result until the next one
        StatusText.Foreground = (Brush)FindResource(warn ? "Danger" : "Subtle");
    }

    /// <summary>One channel line: label · [V] · [A] · ON/OFF · × (remove; not on CH1).</summary>
    private sealed class ChannelRow
    {
        private const double ReadoutFontSize = 28;   // 2× the original 14

        private readonly PsuQuickPanel _owner;
        private readonly TextBlock _volts;
        private readonly TextBlock _amps;
        private readonly Button? _remove;

        public ChannelRow(PsuQuickPanel owner, int channel)
        {
            _owner = owner;
            Channel = channel;

            Root = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });

            var label = new TextBlock
            {
                Text = $"CH{channel}", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)owner.FindResource("Accent"),
            };
            _volts = new TextBlock();
            _amps = new TextBlock();
            var voltsBox = ReadoutBox(_volts);
            var ampsBox = ReadoutBox(_amps);

            Button = new Button { Content = "OFF", Height = 30, IsEnabled = false, ToolTip = "Toggle this channel's output" };
            Button.Click += async (_, _) => await _owner.ToggleAsync(this);

            Grid.SetColumn(voltsBox, 1);
            Grid.SetColumn(ampsBox, 2);
            Grid.SetColumn(Button, 3);
            Root.Children.Add(label);
            Root.Children.Add(voltsBox);
            Root.Children.Add(ampsBox);
            Root.Children.Add(Button);

            // CH1 is the fixed default row — only the extra channels can be removed.
            if (channel != DefaultChannel)
            {
                _remove = new Button
                {
                    Content = "×", Width = 26, Height = 26, Margin = new Thickness(4, 0, 0, 0),
                    ToolTip = $"Remove CH{channel} from this panel (output must be OFF)",
                };
                _remove.Click += (_, _) => _owner.HideChannel(this);
                Grid.SetColumn(_remove, 4);
                Root.Children.Add(_remove);
            }
            Apply(null);
        }

        public int Channel { get; }
        public Grid Root { get; }
        public Button Button { get; }
        public bool OutputOn { get; private set; }
        private bool HasReading { get; set; }

        public void SetEnabled(bool enabled) => Button.IsEnabled = enabled && HasReading;

        public void Apply(PowerSupplyChannelState? s)
        {
            OutputOn = s?.OutputOn ?? false;
            HasReading = s != null;
            _volts.Text = s == null ? "—  V" : $"{s.Voltage:F3} V";
            _amps.Text = s == null ? "—  A" : $"{s.Current:F4} A";
            Button.Content = OutputOn ? "ON" : "OFF";
            if (OutputOn)
            {
                Button.Background = (Brush)_owner.FindResource("Ok");
                Button.FontWeight = FontWeights.Bold;
            }
            else
            {
                Button.ClearValue(Control.BackgroundProperty);
                Button.ClearValue(Control.FontWeightProperty);
            }
            if (_remove != null) _remove.IsEnabled = !OutputOn;   // never hide a live output
        }

        /// <summary>Green-on-black device readout (matches the Process timer).</summary>
        private static Border ReadoutBox(TextBlock text)
        {
            text.FontFamily = new FontFamily("Consolas");
            text.FontSize = ReadoutFontSize;
            text.FontWeight = FontWeights.Bold;
            text.Foreground = ReadoutFg;
            text.HorizontalAlignment = HorizontalAlignment.Right;
            text.VerticalAlignment = VerticalAlignment.Center;
            return new Border
            {
                Background = ReadoutBg, CornerRadius = new CornerRadius(3),
                Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(3, 0, 3, 0),
                Child = text,
            };
        }
    }
}
