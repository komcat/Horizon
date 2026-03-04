using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Horizon.Core;
using Horizon.Core.Scanning;
using Horizon.CurrentMeasurement;
using Serilog;

namespace Horizon.App;

public partial class HexScanPanel : UserControl
{
    private readonly IAppContext _context;
    private readonly DispatcherTimer _positionTimer;
    private readonly ObservableCollection<ScanDataRow> _dataRows = new();

    private string? _selectedDevice;
    private string? _selectedChannel;
    private ScanParameters _currentParameters;
    private ScanEngine? _scanEngine;
    private CancellationTokenSource? _cts;
    private bool _isScanning;

    public HexScanPanel(IAppContext context)
    {
        _context = context;
        _currentParameters = ScanParameters.Coarse();
        InitializeComponent();

        ResultsGrid.ItemsSource = _dataRows;

        BuildDeviceButtons();
        RefreshChannelList();

        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _positionTimer.Tick += (_, _) => UpdatePositionDisplay();

        Loaded += (_, _) => _positionTimer.Start();
        Unloaded += (_, _) => _positionTimer.Stop();
    }

    // ── Device selector ──────────────────────────────────────────────

    private void BuildDeviceButtons()
    {
        DevicePanel.Children.Clear();
        if (!_context.IsInitialized) return;

        var names = _context.MotionManager.GetAllDeviceNames()
            .Where(n => n.Contains("hex", StringComparison.OrdinalIgnoreCase));
        bool first = true;

        foreach (var name in names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            var ctrl = _context.MotionManager.GetController(name);
            if (ctrl == null || !ctrl.IsConnected) continue;

            var btn = new ToggleButton
            {
                Content = name,
                Tag = name,
                Style = (Style)FindResource("ToggleBtn"),
                IsChecked = first
            };
            btn.Checked += DeviceButton_Checked;
            DevicePanel.Children.Add(btn);

            if (first)
            {
                _selectedDevice = name;
                first = false;
            }
        }
    }

    private void DeviceButton_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clicked) return;

        foreach (var child in DevicePanel.Children.OfType<ToggleButton>())
        {
            if (child != clicked)
                child.IsChecked = false;
        }

        _selectedDevice = clicked.Tag as string;
        UpdatePositionDisplay();
    }

    // ── Channel selector ─────────────────────────────────────────────

    private void RefreshChannelList()
    {
        var prev = _selectedChannel;

        // Build channel keys from CurrentMeasurementManager device configs
        var keys = new List<string>();
        foreach (var devName in _context.CurrentMeasurementManager.GetAllDeviceNames())
        {
            var device = _context.CurrentMeasurementManager.GetDevice(devName);
            if (device == null) continue;
            foreach (var ch in device.EnabledChannels)
                keys.Add($"{devName}.CH{ch}");
        }

        // Also merge any keys already in the hub (e.g. laser controller channels)
        foreach (var hubKey in _context.MeasurementHub.GetAllChannelKeys())
        {
            if (!keys.Contains(hubKey, StringComparer.OrdinalIgnoreCase))
                keys.Add(hubKey);
        }

        ChannelCombo.ItemsSource = keys;

        // Restore previous selection if still available
        var idx = prev != null ? keys.FindIndex(k => string.Equals(k, prev, StringComparison.OrdinalIgnoreCase)) : -1;
        if (idx >= 0)
        {
            ChannelCombo.SelectedIndex = idx;
        }
        else if (keys.Count > 0)
        {
            ChannelCombo.SelectedIndex = 0;
            _selectedChannel = keys[0];
        }
    }

    private void ChannelCombo_DropDownOpened(object? sender, EventArgs e)
    {
        RefreshChannelList();
    }

    private void ChannelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedChannel = ChannelCombo.SelectedItem as string;
    }

    // ── Preset toggle ────────────────────────────────────────────────

    private void CoarseToggle_Checked(object sender, RoutedEventArgs e)
    {
        if (FineToggle is not null) FineToggle.IsChecked = false;
        _currentParameters = ScanParameters.Coarse();
    }

    private void FineToggle_Checked(object sender, RoutedEventArgs e)
    {
        if (CoarseToggle is not null) CoarseToggle.IsChecked = false;
        _currentParameters = ScanParameters.Fine();
    }

    // ── Position display ─────────────────────────────────────────────

    private void UpdatePositionDisplay()
    {
        if (_selectedDevice == null)
        {
            PosX.Text = "X=---"; PosY.Text = "Y=---"; PosZ.Text = "Z=---";
            return;
        }

        var pos = _context.MotionManager.GetCurrentPosition(_selectedDevice);
        if (pos == null)
        {
            PosX.Text = "X=---"; PosY.Text = "Y=---"; PosZ.Text = "Z=---";
            return;
        }

        PosX.Text = $"X={pos.X:F3}";
        PosY.Text = $"Y={pos.Y:F3}";
        PosZ.Text = $"Z={pos.Z:F3}";
    }

    // ── Scan execution ───────────────────────────────────────────────

    private void StartScan_Click(object sender, RoutedEventArgs e)
    {
        if (_isScanning) return;

        if (_selectedDevice == null)
        {
            Log.Warning("HexScan: No device selected");
            StatusText.Text = "No device selected";
            return;
        }

        if (string.IsNullOrEmpty(_selectedChannel))
        {
            // Try refreshing channel list
            RefreshChannelList();
            if (string.IsNullOrEmpty(_selectedChannel))
            {
                Log.Warning("HexScan: No measurement channel available");
                StatusText.Text = "No channel selected";
                return;
            }
        }

        var ctrl = _context.MotionManager.GetController(_selectedDevice);
        if (ctrl == null || !ctrl.IsConnected)
        {
            Log.Warning("HexScan: Device '{Device}' not connected", _selectedDevice);
            StatusText.Text = "Device not connected";
            return;
        }

        _isScanning = true;
        SetControlsEnabled(false);
        _dataRows.Clear();

        var device = _selectedDevice;
        var channel = _selectedChannel;
        var parameters = _currentParameters;

        _scanEngine = new ScanEngine(_context.MotionManager, _context.MeasurementHub);

        // Wire events
        _scanEngine.StatusChanged += status =>
            Dispatcher.InvokeAsync(() => StatusText.Text = status);

        _scanEngine.ProgressChanged += (progress, message) =>
            Dispatcher.InvokeAsync(() =>
            {
                var pct = (int)(progress * 100);
                ScanProgress.Value = pct;
                ProgressText.Text = $"{pct}%";
            });

        _scanEngine.DataPointRecorded += dp =>
            Dispatcher.InvokeAsync(() =>
            {
                var row = new ScanDataRow(dp);
                _dataRows.Add(row);

                // Auto-scroll to latest
                if (_dataRows.Count > 0)
                    ResultsGrid.ScrollIntoView(_dataRows[^1]);
            });

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        Log.Information("HexScan: Starting scan on {Device}, channel {Channel}, preset {Preset}",
            device, channel, CoarseToggle.IsChecked == true ? "Coarse" : "Fine");

        Task.Run(() =>
        {
            try
            {
                var results = _scanEngine.Execute(device, channel, parameters, ct);

                Dispatcher.InvokeAsync(() =>
                {
                    UpdateSummary(results);
                    Log.Information("HexScan: Scan {Status} — Peak={Peak:E4}, Elapsed={Elapsed}",
                        results.Cancelled ? "cancelled" : "completed",
                        results.PeakValue, results.Elapsed);
                });
            }
            catch (Exception ex)
            {
                Dispatcher.InvokeAsync(() =>
                {
                    StatusText.Text = $"Error: {ex.Message}";
                    Log.Error("HexScan: Scan error — {Error}", ex.Message);
                });
            }
            finally
            {
                Dispatcher.InvokeAsync(() =>
                {
                    _isScanning = false;
                    SetControlsEnabled(true);
                });
            }
        }, ct);
    }

    private void StopScan_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        Log.Information("HexScan: Stop requested");
    }

    private void SetControlsEnabled(bool enabled)
    {
        StartButton.IsEnabled = enabled;
        StopButton.IsEnabled = !enabled;
        CoarseToggle.IsEnabled = enabled;
        FineToggle.IsEnabled = enabled;
        ChannelCombo.IsEnabled = enabled;

        foreach (var btn in DevicePanel.Children.OfType<ToggleButton>())
            btn.IsEnabled = enabled;
    }

    private void UpdateSummary(ScanResults results)
    {
        BaselineText.Text = FormatValue(results.BaselineValue);
        PeakText.Text = FormatValue(results.PeakValue);

        if (results.BaselineValue > 0)
        {
            double improvement = (results.PeakValue - results.BaselineValue) / results.BaselineValue * 100;
            ImprovementText.Text = $"({improvement:+0.0;-0.0}%)";
        }
        else
        {
            ImprovementText.Text = "";
        }
    }

    private static string FormatValue(double value)
    {
        double abs = Math.Abs(value);
        if (abs < 1e-9) return $"{value * 1e12:F2} pA";
        if (abs < 1e-6) return $"{value * 1e9:F4} nA";
        if (abs < 1e-3) return $"{value * 1e6:F4} uA";
        return $"{value:E4}";
    }

    // ── Cleanup ──────────────────────────────────────────────────────

    public void StopPolling()
    {
        _positionTimer.Stop();
        _cts?.Cancel();
    }

    // ── DataGrid row view model ──────────────────────────────────────

    public sealed class ScanDataRow : INotifyPropertyChanged
    {
        public int StepIndex { get; }
        public string Axis { get; }
        public string DirectionLabel { get; }
        public string StepSizeLabel { get; }
        public string PositionLabel { get; }
        public string ValueLabel { get; }
        public string PeakLabel { get; }

        public ScanDataRow(ScanDataPoint dp)
        {
            StepIndex = dp.StepIndex;
            Axis = dp.Axis;
            DirectionLabel = dp.Direction > 0 ? "+" : "-";
            StepSizeLabel = $"{dp.StepSize:F3}";
            PositionLabel = $"{dp.Position:F4}";
            ValueLabel = FormatValue(dp.Value);
            PeakLabel = dp.IsPeak ? "*" : "";
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
