using System.IO;
using Horizon.Common;
using Horizon.Engine;
using Horizon.Workflow.Oee;

namespace Horizon.Station.UI;

/// <summary>Which IO device + output pins drive the tower light (<c>faam_tower_light.json</c>).</summary>
public sealed class TowerLightConfig
{
    public bool Enabled { get; set; } = true;
    public string Device { get; set; } = "IOTower";
    public string Green { get; set; } = "Tower_Green";
    public string Yellow { get; set; } = "Tower_Yellow";
    public string Red { get; set; } = "Tower_Red";
    /// <summary>Buzzer pin; empty = no buzzer.</summary>
    public string Buzzer { get; set; } = "Tower_Buzzer";
    /// <summary>Beeps sounded when a safety stop trips (0 = silent).</summary>
    public int SafetyBeeps { get; set; } = 3;
    /// <summary>Seconds the buzzer sounds (steady) when the machine is declared Down (0 = silent).</summary>
    public double DownBuzzerSeconds { get; set; } = 3;
}

/// <summary>
/// Drives the station tower light from the OEE state, so the floor can read the machine
/// from across the room:
/// <list type="table">
/// <item><term>Running</term><description>green steady</description></item>
/// <item><term>Standby</term><description>green blinking — ready, waiting for the next unit</description></item>
/// <item><term>Idle (≥ 30 min)</term><description>yellow steady</description></item>
/// <item><term>Down (declared)</term><description>red steady; the buzzer sounds for <see cref="TowerLightConfig.DownBuzzerSeconds"/> (3 s) on the declaration, then stops</description></item>
/// <item><term>Waiting on the operator</term><description>yellow blinking — a prompt / serial entry / alert / jog popup is up</description></item>
/// <item><term>Safety stop</term><description>red blinking + a few beeps, until Acknowledge</description></item>
/// <item><term>FAAM closed</term><description>all off</description></item>
/// </list>
/// A 250 ms pump on a background task computes the wanted pattern and writes only the pins
/// that changed (each write time-bounded). A manual toggle on the Machine IO page holds until
/// the next state change. Pins / device come from <c>faam_tower_light.json</c> (written with
/// defaults on first run); a missing device or failing write is logged and retried every 5 s.
/// </summary>
public sealed class TowerLight
{
    private const string FileName = "faam_tower_light";
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RetryAfterFault = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

    private readonly IHorizonEngine _engine;
    private readonly OeeTracker _oee;
    private readonly LogHub _log;
    private readonly Func<bool> _operatorWaiting;
    private TowerLightConfig _cfg = new();
    private CancellationTokenSource? _cts;
    private Task? _pump;

    private Lamps? _written;          // last pattern on the hardware; null = unknown → rewrite all
    private DateTime _retryAt;        // after a failed write, wait before trying again
    private int _tick;
    private bool _wasTripped;
    private int _beepTicks;
    private bool? _wasDown;           // null until the first tick — a Down carried over from a restart doesn't buzz
    private int _downBuzzTicks;

    /// <param name="operatorWaiting">True while a blocking operator prompt is up (polled every tick, any thread).</param>
    public TowerLight(IHorizonEngine engine, OeeTracker oee, LogHub log, Func<bool> operatorWaiting)
    {
        _engine = engine;
        _oee = oee;
        _log = log;
        _operatorWaiting = operatorWaiting;
    }

    private readonly record struct Lamps(bool Green, bool Yellow, bool Red, bool Buzzer);

    /// <summary>Loads the config and starts the pump. No-op when disabled or the device is not configured.</summary>
    public void Start()
    {
        if (_pump != null) return;
        _cfg = LoadConfig();
        if (!_cfg.Enabled)
        {
            Log(LogLevel.Info, "Tower light disabled (faam_tower_light.json).");
            return;
        }
        if (!_engine.IO.DeviceNames.Contains(_cfg.Device, StringComparer.OrdinalIgnoreCase))
        {
            Log(LogLevel.Warning, $"Tower light: IO device '{_cfg.Device}' is not configured — tower light off.");
            return;
        }
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _pump = Task.Run(() => RunAsync(ct), ct);
    }

    /// <summary>Stops the pump and switches every lamp + the buzzer off (FAAM closing). Bounded.</summary>
    public async Task StopAsync()
    {
        if (_pump == null || _cts == null) return;
        await _cts.CancelAsync().ConfigureAwait(false);
        await Task.WhenAny(_pump, Task.Delay(StopTimeout)).ConfigureAwait(false);
        _cts.Dispose();
        _cts = null;
        _pump = null;
        _written = null;
        _retryAt = default;
        await WriteAsync(new Lamps(false, false, false, false), CancellationToken.None).ConfigureAwait(false);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Tick);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                await WriteAsync(NextPattern(), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected on stop
        }
    }

    /// <summary>The pattern for this tick (blink = 500 ms on / 500 ms off).</summary>
    private Lamps NextPattern()
    {
        _tick = (_tick + 1) % 4000;
        bool blinkOn = (_tick / 2) % 2 == 0;

        bool tripped = _engine.Safety.GetState().FlagRaised;
        if (tripped && !_wasTripped) _beepTicks = Math.Max(0, _cfg.SafetyBeeps) * 4;
        _wasTripped = tripped;
        if (_beepTicks > 0) _beepTicks--;

        var state = _oee.Status.State;
        bool down = state == OeeState.Down;
        if (down && _wasDown == false)
            _downBuzzTicks = (int)Math.Ceiling(Math.Clamp(_cfg.DownBuzzerSeconds, 0, 30) * 1000 / Tick.TotalMilliseconds);
        else if (!down) _downBuzzTicks = 0;           // declared UP again — stop at once
        _wasDown = down;
        bool downBuzz = _downBuzzTicks > 0;
        if (downBuzz) _downBuzzTicks--;

        if (tripped) return new Lamps(false, false, blinkOn, (_beepTicks > 0 && blinkOn) || downBuzz);

        if (down) return new Lamps(false, false, true, downBuzz);
        if (_operatorWaiting()) return new Lamps(false, blinkOn, false, false);   // someone needs to come over

        return state switch
        {
            OeeState.Running => new Lamps(true, false, false, false),
            OeeState.Standby => new Lamps(blinkOn, false, false, false),
            OeeState.Idle => new Lamps(false, true, false, false),
            _ => new Lamps(false, false, false, false),
        };
    }

    /// <summary>Writes the pins that differ from what is on the hardware.</summary>
    private async Task WriteAsync(Lamps want, CancellationToken ct)
    {
        if (!_engine.IO.IsConnected(_cfg.Device)) { _written = null; return; }
        if (_written == want || DateTime.UtcNow < _retryAt) return;

        var have = _written;
        var ok = await SetAsync(_cfg.Green, want.Green, have?.Green, ct).ConfigureAwait(false)
               & await SetAsync(_cfg.Yellow, want.Yellow, have?.Yellow, ct).ConfigureAwait(false)
               & await SetAsync(_cfg.Red, want.Red, have?.Red, ct).ConfigureAwait(false)
               & await SetAsync(_cfg.Buzzer, want.Buzzer, have?.Buzzer, ct).ConfigureAwait(false);

        if (ok)
        {
            _written = want;
        }
        else
        {
            _written = null;                                  // unknown — rewrite everything next time
            _retryAt = DateTime.UtcNow + RetryAfterFault;
        }
    }

    private async Task<bool> SetAsync(string pin, bool on, bool? current, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(pin) || current == on) return true;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(WriteTimeout);
        try
        {
            var r = await _engine.IO.SetOutputByNameAsync(_cfg.Device, pin, on, timeout.Token).ConfigureAwait(false);
            if (r.IsSuccess) return true;
            Log(LogLevel.Warning, $"Tower light: {_cfg.Device}.{pin} → {(on ? "ON" : "OFF")} failed — {r.Message}");
            return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log(LogLevel.Warning, $"Tower light: {_cfg.Device}.{pin} write timed out.");
            return false;
        }
    }

    private TowerLightConfig LoadConfig()
    {
        try
        {
            var dir = ConfigLoader.GetConfigDirectory();
            var cfg = ConfigLoader.LoadJson<TowerLightConfig>(dir, FileName);
            if (cfg != null) return cfg;
            cfg = new TowerLightConfig();
            ConfigLoader.SaveJson(dir, FileName, cfg);   // first run: leave an editable file behind
            return cfg;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Log(LogLevel.Warning, $"Tower light: cannot read {FileName}.json ({ex.Message}) — using defaults.");
            return new TowerLightConfig();
        }
    }

    private void Log(LogLevel level, string message)
        => _log.Add(new LogEntry(level, DateTimeOffset.Now, "Tower", message));
}
