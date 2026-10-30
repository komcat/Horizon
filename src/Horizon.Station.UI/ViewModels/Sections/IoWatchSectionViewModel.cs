using System.Collections.ObjectModel;
using System.Windows.Threading;
using Horizon.Engine;

namespace Horizon.Station.UI.ViewModels.Sections;

/// <summary>
/// The Machine page's IO Watch section — a read-only view of the engine's two IO
/// watchers: the <c>io_watch.json</c> input→output bindings (<see cref="IIoWatchFacet"/>)
/// and the safety-watched inputs (<see cref="ISafetyFacet"/>). Each row shows the live
/// state of its pins, taken from the IO facet's cached state each sampler tick, and a
/// short event log records binding fires and safety trips/clears.
/// </summary>
public sealed class IoWatchSectionViewModel : ObservableObject, IDisposable
{
    private const int MaxEvents = 200;

    private readonly EngineHost _host;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, IoWatchBindingViewModel> _bindingsByName =
        new(StringComparer.OrdinalIgnoreCase);

    private string _watchStatus = "—";
    private string _safetyStatus = "—";
    private bool _safetyFlagRaised;
    private bool _disposed;

    public IoWatchSectionViewModel(EngineHost host)
    {
        _host = host;
        _dispatcher = Dispatcher.CurrentDispatcher;

        foreach (var b in host.Engine.IoWatch.Bindings)
        {
            var row = new IoWatchBindingViewModel(b,
                ResolveEndpoint(b.Input, output: false),
                ResolveEndpoint(b.Output, output: true),
                b.Indicator == null ? null : ResolveEndpoint(b.Indicator, output: true));
            Bindings.Add(row);
            _bindingsByName.TryAdd(b.Name, row);
        }

        foreach (var w in host.Engine.Safety.WatchedInputs)
            WatchedInputs.Add(new SafetyWatchedInputViewModel(w, Resolve(w.Device, w.Pin, output: false)));

        host.Engine.IoWatch.Fired += OnFired;
        host.Engine.Safety.SafetyTripped += OnSafetyTripped;
        host.Engine.Safety.SafetyCleared += OnSafetyCleared;
    }

    /// <summary>Label shown in the Machine-page section rail.</summary>
    public string Title => "IO Watch";

    /// <summary>The configured input→output bindings from <c>io_watch.json</c>.</summary>
    public ObservableCollection<IoWatchBindingViewModel> Bindings { get; } = new();

    /// <summary>The safety watchdog's watched inputs.</summary>
    public ObservableCollection<SafetyWatchedInputViewModel> WatchedInputs { get; } = new();

    /// <summary>Recent binding fires and safety trips, newest first.</summary>
    public ObservableCollection<string> Events { get; } = new();

    /// <summary>One-line io-watch state: running / enabled / poll interval / binding count.</summary>
    public string WatchStatus
    {
        get => _watchStatus;
        private set => SetField(ref _watchStatus, value);
    }

    /// <summary>One-line safety-watch state: watching / flag / interlocks.</summary>
    public string SafetyStatus
    {
        get => _safetyStatus;
        private set => SetField(ref _safetyStatus, value);
    }

    /// <summary>True while a safety flag is raised and not yet acknowledged.</summary>
    public bool SafetyFlagRaised
    {
        get => _safetyFlagRaised;
        private set => SetField(ref _safetyFlagRaised, value);
    }

    /// <summary>Refreshes the headers and every pin dot — called each sampler tick.</summary>
    public void Refresh()
    {
        var ws = _host.Engine.IoWatch.GetState();
        WatchStatus = $"{(ws.IsRunning ? "Running" : "Stopped")} · {(ws.Enabled ? "enabled" : "disabled")}"
                      + $" · poll {ws.PollIntervalMs} ms · {ws.BindingCount} binding(s)";

        var ss = _host.Engine.Safety.GetState();
        SafetyFlagRaised = ss.FlagRaised;
        SafetyStatus = $"{(ss.IsWatching ? "Watching" : "Not watching")}"
                       + $" · flag {(ss.CriticalRaised ? "CRITICAL" : ss.FlagRaised ? "RAISED" : "clear")}"
                       + $" · interlocks {(ss.InterlocksEnabled ? "on" : "off")}"
                       + (ss.OverrideMode ? " · OVERRIDE" : "");

        // One cached-state read per device per tick, shared by every row.
        var states = new Dictionary<string, IoState?>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Bindings)
            row.Apply(Read(row.InputPin, states), Read(row.OutputPin, states), Read(row.IndicatorPin, states));
        foreach (var row in WatchedInputs)
            row.Apply(Read(row.InputPin, states));
    }

    private bool? Read(PinRef? pin, Dictionary<string, IoState?> states)
    {
        if (pin == null) return null;
        if (!states.TryGetValue(pin.Device, out var state))
        {
            state = _host.Engine.IO.IsConnected(pin.Device) ? _host.Engine.IO.GetState(pin.Device) : null;
            states[pin.Device] = state;
        }
        if (state is not { Valid: true }) return null;
        var bits = pin.IsOutput ? state.Outputs : state.Inputs;
        return (bits & (1u << pin.Pin)) != 0;
    }

    /// <summary>Resolves a facet endpoint string of the form <c>Device.PinName</c>.</summary>
    private PinRef? ResolveEndpoint(string endpoint, bool output)
    {
        // Device names may themselves contain dots — match the longest known device prefix.
        var device = _host.Engine.IO.DeviceNames
            .Where(d => endpoint.Length > d.Length + 1
                        && endpoint.StartsWith(d + ".", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => d.Length)
            .FirstOrDefault();
        return device == null ? null : Resolve(device, endpoint[(device.Length + 1)..], output);
    }

    /// <summary>Maps a device + pin name (or numeric index) to a bit in the device's cached state.</summary>
    private PinRef? Resolve(string device, string pinName, bool output)
    {
        var info = _host.Engine.IO.GetDevice(device);
        if (info == null) return null;
        var pins = output ? info.OutputPins : info.InputPins;
        var hit = pins.FirstOrDefault(p => string.Equals(p.Name, pinName, StringComparison.OrdinalIgnoreCase));
        var index = hit?.Pin ?? (int.TryParse(pinName, out var n) ? n : -1);
        return index is >= 0 and < 32 ? new PinRef(device, index, output) : null;
    }

    private void OnFired(IoBindingFired fired) =>
        Post(() =>
        {
            if (_bindingsByName.TryGetValue(fired.Name, out var row))
                row.MarkFired(fired.State);
            AddEvent($"{fired.Name} → {fired.Output} = {(fired.State ? "ON" : "OFF")}");
        });

    private void OnSafetyTripped(SafetyTrip trip) =>
        Post(() => AddEvent($"SAFETY{(trip.IsCritical ? " (critical)" : "")}: {trip.Title} — {trip.Message}"));

    private void OnSafetyCleared() => Post(() => AddEvent("Safety flag cleared"));

    private void Post(Action action)
    {
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(action);
    }

    private void AddEvent(string text)
    {
        if (_disposed) return;
        Events.Insert(0, $"{DateTime.Now:HH:mm:ss.fff}  {text}");
        while (Events.Count > MaxEvents)
            Events.RemoveAt(Events.Count - 1);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _host.Engine.IoWatch.Fired -= OnFired;
        _host.Engine.Safety.SafetyTripped -= OnSafetyTripped;
        _host.Engine.Safety.SafetyCleared -= OnSafetyCleared;
    }
}

/// <summary>A resolved IO pin: device, bit index, and which bank (input/output).</summary>
public sealed record PinRef(string Device, int Pin, bool IsOutput);

/// <summary>One io-watch binding row. Pin states are null when the pin can't be read.</summary>
public sealed class IoWatchBindingViewModel : ObservableObject
{
    private bool? _inputOn;
    private bool? _outputOn;
    private bool? _indicatorOn;
    private string _lastFired = "—";

    public IoWatchBindingViewModel(IoBindingInfo info, PinRef? inputPin, PinRef? outputPin, PinRef? indicatorPin)
    {
        Name = info.Name;
        Input = info.Input;
        Output = info.Output;
        Indicator = info.Indicator ?? "—";
        HasIndicator = info.Indicator != null;
        Trigger = info.Trigger;
        DebounceMs = info.DebounceMs;
        Enabled = info.Enabled;
        InputPin = inputPin;
        OutputPin = outputPin;
        IndicatorPin = indicatorPin;
    }

    public string Name { get; }
    public string Input { get; }
    public string Output { get; }

    /// <summary>The output mirroring <see cref="Output"/> (e.g. the switch LED), or "—".</summary>
    public string Indicator { get; }
    public bool HasIndicator { get; }
    public string Trigger { get; }
    public int DebounceMs { get; }
    public bool Enabled { get; }
    public PinRef? InputPin { get; }
    public PinRef? OutputPin { get; }
    public PinRef? IndicatorPin { get; }

    public bool? IndicatorOn
    {
        get => _indicatorOn;
        private set => SetField(ref _indicatorOn, value);
    }

    public bool? InputOn
    {
        get => _inputOn;
        private set => SetField(ref _inputOn, value);
    }

    public bool? OutputOn
    {
        get => _outputOn;
        private set => SetField(ref _outputOn, value);
    }

    /// <summary>Time + driven state of the most recent fire this session.</summary>
    public string LastFired
    {
        get => _lastFired;
        private set => SetField(ref _lastFired, value);
    }

    public void Apply(bool? input, bool? output, bool? indicator)
    {
        InputOn = input;
        OutputOn = output;
        IndicatorOn = indicator;
    }

    public void MarkFired(bool state) => LastFired = $"{DateTime.Now:HH:mm:ss} {(state ? "ON" : "OFF")}";
}

/// <summary>One safety-watched input row.</summary>
public sealed class SafetyWatchedInputViewModel : ObservableObject
{
    private bool? _isOn;

    public SafetyWatchedInputViewModel(WatchedInputInfo info, PinRef? inputPin)
    {
        Device = info.Device;
        Pin = info.Pin;
        TriggerEdge = info.TriggerEdge;
        Critical = info.IsCritical ? "Yes" : "No";
        InputPin = inputPin;
    }

    public string Device { get; }
    public string Pin { get; }
    public string TriggerEdge { get; }
    public string Critical { get; }
    public PinRef? InputPin { get; }

    public bool? IsOn
    {
        get => _isOn;
        private set => SetField(ref _isOn, value);
    }

    public void Apply(bool? state) => IsOn = state;
}
