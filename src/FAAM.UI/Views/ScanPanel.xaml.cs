using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Horizon.Engine;
using Horizon.Workflow.Scanning;

namespace FAAM.UI.Views;

/// <summary>
/// Run-page Hexapod Scan panel — drives the Layer 2 <see cref="HexapodScanEngine"/>
/// (the coarse-to-fine beam-alignment scan ported from <c>bobcat_engine</c>) from the
/// utility-app springboard. The operator picks a connected hexapod, an engine-facet
/// feedback source (current-meter / SMU / power-supply) and a step-size mode, then
/// Start runs the scan and the engine's <see cref="ScanProgress"/> updates the readouts.
/// A halt (Stop / navigate-away survives) still parks the hexapod at the best position
/// found, matching the legacy <c>HaltScan</c> contract.
/// </summary>
public partial class ScanPanel : UserControl
{
    private EngineHost? _host;
    private HexapodScanEngine? _scanEngine;
    private CancellationTokenSource? _cts;

    private bool _running;
    private bool _error;
    private string _statusDetail = "Idle";
    private string _unit = string.Empty;

    /// <summary>Raised whenever the scan run-state changes — the utility-app launcher badge listens.</summary>
    public event Action? RunStateChanged;

    /// <summary>True while a scan is in progress (drives the launcher's Running badge).</summary>
    public bool IsRunning => _running;

    /// <summary>True when the last scan ended in an error (drives the launcher's Error badge).</summary>
    public bool IsError => _error;

    /// <summary>Current status text, surfaced to the launcher badge / app header.</summary>
    public string RunStatusDetail => _statusDetail;

    /// <summary>Begins a scan (utility-app <c>app.start</c>) — same as pressing Start (uses the panel's current selections).</summary>
    public void RequestStart() => OnStart(this, new RoutedEventArgs());

    /// <summary>Halts an in-flight scan (utility-app <c>app.stop</c>); no-op when idle. The hexapod still returns to the peak.</summary>
    public void RequestStop() => _cts?.Cancel();

    public ScanPanel()
    {
        InitializeComponent();
        KindCombo.SelectedIndex = 0;        // Current meter (closest to bobcat's optical-power feedback)
        ValueTypeCombo.SelectedIndex = 0;   // Current
        ModeCombo.SelectedIndex = 0;        // Coarse
    }

    /// <summary>Supplies the engine host (Layer 1 motion + feedback facets + logging).</summary>
    public void Attach(EngineHost host)
    {
        _host = host;
        _scanEngine = new HexapodScanEngine(host.Engine, Log);

        host.Activity.Changed += OnActivityChanged;
        IsVisibleChanged += (_, _) => { if (IsVisible) RefreshDevices(); };
        RefreshDevices();
        RefreshStartEnabled();
    }

    // --- Device enumeration ---

    private void OnRefreshDevices(object sender, RoutedEventArgs e) => RefreshDevices();

    private void OnKindChanged(object sender, SelectionChangedEventArgs e) => RefreshFeedbackDevices();

    /// <summary>Repopulates the hexapod + feedback device lists from the currently-connected hardware.</summary>
    private void RefreshDevices()
    {
        if (_host == null) return;

        string? hexWas = HexCombo.SelectedItem as string;
        var motion = _host.Engine.Motion;
        var hexapods = motion.DeviceNames.Where(motion.IsConnected).ToList();
        HexCombo.ItemsSource = hexapods;

        // Re-select the previous choice, else prefer the FAAM convention, else the first.
        HexCombo.SelectedItem =
            (hexWas != null && hexapods.Contains(hexWas)) ? hexWas
            : hexapods.FirstOrDefault(d => string.Equals(d, "hex-right", StringComparison.OrdinalIgnoreCase))
              ?? hexapods.FirstOrDefault();

        RefreshFeedbackDevices();
    }

    /// <summary>Repopulates the feedback device list for the selected feedback kind.</summary>
    private void RefreshFeedbackDevices()
    {
        if (_host == null) return;

        string? was = DeviceCombo.SelectedItem as string;
        var devices = ConnectedFeedbackDevices(SelectedKind());
        DeviceCombo.ItemsSource = devices;
        DeviceCombo.SelectedItem = (was != null && devices.Contains(was)) ? was : devices.FirstOrDefault();
    }

    /// <summary>Connected device names for a feedback kind's facet.</summary>
    private List<string> ConnectedFeedbackDevices(FeedbackKind kind) => kind switch
    {
        FeedbackKind.Measurement => _host!.Engine.Measurement.DeviceNames.Where(_host.Engine.Measurement.IsConnected).ToList(),
        FeedbackKind.Smu => _host!.Engine.Smu.DeviceNames.Where(_host.Engine.Smu.IsConnected).ToList(),
        FeedbackKind.PowerSupply => _host!.Engine.PowerSupply.DeviceNames.Where(_host.Engine.PowerSupply.IsConnected).ToList(),
        _ => new List<string>(),
    };

    private FeedbackKind SelectedKind() => KindCombo.SelectedIndex switch
    {
        1 => FeedbackKind.Smu,
        2 => FeedbackKind.PowerSupply,
        _ => FeedbackKind.Measurement,
    };

    private FeedbackValue SelectedValueType()
        => ValueTypeCombo.SelectedIndex == 1 ? FeedbackValue.Voltage : FeedbackValue.Current;

    private ScanMode SelectedMode() => ModeCombo.SelectedIndex switch
    {
        1 => ScanMode.Fine,
        2 => ScanMode.UltraFine,
        _ => ScanMode.Coarse,
    };

    // --- Gating (mirrors HillClimbPanel: a scan drives the hexapod, so it takes the Alignment gate) ---

    private void OnActivityChanged()
    {
        if (Dispatcher.CheckAccess()) RefreshStartEnabled();
        else Dispatcher.BeginInvoke(RefreshStartEnabled);
    }

    /// <summary>Start is available only when nothing else (a process step or another alignment) holds the hexapod.</summary>
    private void RefreshStartEnabled()
    {
        if (_host == null) return;
        bool othersBusy = _host.Activity.ProcessBusy || (_host.Activity.AlignmentBusy && !_running);
        StartButton.IsEnabled = !_running && !othersBusy;
    }

    // --- Run ---

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        if (_host == null || _scanEngine == null || _running) return;

        if (_host.Activity.ProcessBusy || _host.Activity.AlignmentBusy)
        {
            SetState("Busy — wait for the current run", busy: false);
            Log(LogLevel.Warning, "Scan blocked: a process step or alignment is running.");
            return;
        }

        if (HexCombo.SelectedItem is not string hexDevice || string.IsNullOrWhiteSpace(hexDevice))
        {
            SetState("No hexapod selected", busy: false);
            Log(LogLevel.Warning, "Scan blocked: no connected hexapod selected (press ⟳ after connecting).");
            return;
        }
        if (!_host.Engine.Motion.IsConnected(hexDevice))
        {
            SetState("Hexapod not connected", busy: false);
            Log(LogLevel.Warning, $"Scan blocked: hexapod '{hexDevice}' not connected.");
            return;
        }

        if (DeviceCombo.SelectedItem is not string feedbackDevice || string.IsNullOrWhiteSpace(feedbackDevice))
        {
            SetState("No feedback device", busy: false);
            Log(LogLevel.Warning, "Scan blocked: no connected feedback device selected.");
            return;
        }

        if (!int.TryParse(ChannelBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int channel)
            || channel < 1)
        {
            SetState("Bad channel", busy: false);
            return;
        }

        var descriptor = new FeedbackDescriptor
        {
            Kind = SelectedKind(),
            Device = feedbackDevice,
            Channel = channel,
            ValueType = SelectedValueType(),
        };
        var parameters = ScanParameters.ForMode(SelectedMode());
        _unit = descriptor.Kind == FeedbackKind.Measurement || descriptor.ValueType == FeedbackValue.Current ? "A" : "V";

        // Enter the running state and take the alignment gate (blocks process / other alignments).
        _running = true;
        _error = false;
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        ScanProgressBar.Value = 0;
        SnrText.Text = "—";
        SetState("Starting…", busy: true);

        _cts = new CancellationTokenSource();
        var activity = _host.Activity.Begin(ActivityKind.Alignment, "Hexapod scan");
        var progress = new Progress<ScanProgress>(OnProgress);   // marshals to this (UI) thread

        Log(LogLevel.Info,
            $"Scan starting: {hexDevice}, feedback {descriptor.Kind} '{feedbackDevice}' CH{channel} " +
            $"({descriptor.ValueType}), mode {SelectedMode()}.");
        try
        {
            var result = await _scanEngine.RunAsync(hexDevice, descriptor, parameters, progress, _cts.Token);
            ApplyResult(result);
        }
        catch (Exception ex)
        {
            _error = true;
            SetState($"Error: {ex.Message}", busy: false);
            Log(LogLevel.Error, $"Scan failed: {ex.Message}");
        }
        finally
        {
            activity.Dispose();
            _running = false;
            StopButton.IsEnabled = false;
            RefreshStartEnabled();
            _cts?.Dispose();
            _cts = null;
            RunStateChanged?.Invoke();
        }
    }

    private void OnStop(object sender, RoutedEventArgs e) => _cts?.Cancel();

    // --- Progress (fires on the UI thread via Progress<T>) ---

    private void OnProgress(ScanProgress p)
    {
        StateText.Text = p.State.ToString();
        StateText.Foreground = p.State switch
        {
            ScanState.Completed => (Brush)FindResource("Ok"),
            ScanState.Error => Brushes.IndianRed,
            ScanState.Halted => (Brush)FindResource("Subtle"),
            ScanState.Idle => (Brush)FindResource("Subtle"),
            _ => (Brush)FindResource("Accent"),
        };
        PhaseText.Text = p.Message;
        AxisStepText.Text = string.IsNullOrEmpty(p.CurrentAxis)
            ? "—"
            : $"{p.CurrentAxis} / {p.CurrentStepSize * 1000:0.###}µm";
        ValueText.Text = FormatValue(p.CurrentValue);
        PeakText.Text = FormatValue(p.PeakValue);
        ScanProgressBar.Value = Math.Clamp(p.Fraction, 0, 1);

        _statusDetail = p.Message;
        RunStateChanged?.Invoke();
    }

    private void ApplyResult(ScanResult r)
    {
        _error = r.FinalState == ScanState.Error;
        StateText.Text = r.FinalState.ToString();
        PhaseText.Text = r.Message;
        if (r.Peak.Valid) PeakText.Text = FormatValue(r.Peak.Value);
        SnrText.Text = double.IsPositiveInfinity(r.PeakSnr) ? "∞" : r.PeakSnr.ToString("F1");
        ScanProgressBar.Value = 1;

        StateText.Foreground = _error
            ? Brushes.IndianRed
            : r.PeakIsSignificant ? (Brush)FindResource("Ok") : (Brush)FindResource("Subtle");

        _statusDetail = r.FinalState == ScanState.Completed && r.PeakIsSignificant
            ? $"Peak {FormatValue(r.Peak.Value)}"
            : r.Message;

        Log(_error ? LogLevel.Error : r.PeakIsSignificant ? LogLevel.Info : LogLevel.Warning,
            $"Scan {r.FinalState}: {r.Message} ({r.Duration.TotalSeconds:F0} s, {r.Measurements.Count} points).");
    }

    private string FormatValue(double value)
        => string.IsNullOrEmpty(_unit) ? value.ToString("G4", CultureInfo.InvariantCulture)
                                       : $"{value.ToString("G4", CultureInfo.InvariantCulture)} {_unit}";

    /// <summary>Sets the state readout + status detail and refreshes the launcher badge.</summary>
    private void SetState(string text, bool busy)
    {
        StateText.Text = text;
        StateText.Foreground = busy ? (Brush)FindResource("Accent") : (Brush)FindResource("Subtle");
        _statusDetail = text;
        RunStateChanged?.Invoke();
    }

    private void Log(LogLevel level, string message)
        => _host?.Log.Add(new LogEntry(level, DateTimeOffset.Now, "Scan", message));
}
