using System.Threading.Channels;
using Horizon.Common;
using Horizon.Engine;
using Microsoft.Data.Sqlite;

namespace Horizon.Workflow.Oee;

/// <summary>
/// Live station-utilisation (OEE) tracking — the Layer 2 half. Records into
/// <see cref="OeeStore"/> (<c>%APPDATA%\Horizon\oee.db</c>):
/// <list type="bullet">
/// <item><b>Online</b> — a Layer 3 head calls <see cref="StartSession"/> once it is up and
/// <see cref="EndSessionAsync"/> on exit; a 60 s heartbeat bounds a crash.</item>
/// <item><b>Running</b> — every execution is bracketed with <see cref="BeginProduction"/>
/// (re-entrant: a deck running programs running scripts is one run). ProcessService
/// brackets programs / decks / recipes / scripts; a head adds its own (FAAM's Run-page steps).</item>
/// <item><b>Down</b> — <see cref="GoDown"/> / <see cref="GoUp"/>, each signed by the engineer /
/// technician who declared it. Down persists across restarts until someone brings it up.</item>
/// <item><b>Units</b> — <see cref="RecordUnit"/> per finished DUT, for the quality ratio.</item>
/// </list>
/// Idle is not recorded — it is derived (<see cref="OeeCalculator"/>): online, not running,
/// for at least <see cref="IdleThreshold"/>. Thread-safe; database writes go through one
/// background writer so callers (the UI thread included) never block on disk.
/// </summary>
public sealed class OeeTracker
{
    public static readonly TimeSpan DefaultIdleThreshold = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan HeartbeatPeriod = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(3);

    private readonly OeeStore _store;
    private readonly Action<LogLevel, string>? _log;
    private readonly object _lock = new();
    private readonly Channel<Action<OeeStore>> _writes =
        Channel.CreateUnbounded<Action<OeeStore>>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _writer;
    private Timer? _heartbeat;

    private string? _sessionId;     // non-null while this head is online
    private string? _station;
    private string? _runId;         // the open run row (only written while online)
    private int _runDepth;          // re-entrant BeginProduction count
    private DateTime _runStart;
    private DateTime _gapStart;     // start of the current not-running stretch (standby / idle)
    private OeeDowntime? _down;     // open downtime, or null when up
    private TimeSpan _idleThreshold = DefaultIdleThreshold;

    public OeeTracker(OeeStore? store = null, Action<LogLevel, string>? log = null)
    {
        _store = store ?? new OeeStore();
        _log = log;
        _gapStart = DateTime.UtcNow;
        try { _down = _store.GetOpenDowntime(); }   // a station left down stays down
        catch (SqliteException) { _down = null; }
        _writer = Task.Run(WriteLoopAsync);
    }

    /// <summary>Raised (on the calling thread) when online / running / down changes. Idle is time-derived — poll <see cref="Status"/>.</summary>
    public event Action? Changed;

    /// <summary>Where the data lives (shown on the OEE page).</summary>
    public string DatabasePath => _store.DbPath;

    /// <summary>A not-running stretch at least this long is Idle (whole stretch); shorter is Standby. Default 30 min.</summary>
    public TimeSpan IdleThreshold
    {
        get { lock (_lock) return _idleThreshold; }
        set { lock (_lock) _idleThreshold = value > TimeSpan.Zero ? value : DefaultIdleThreshold; }
    }

    /// <summary>The live state — Down / Offline / Running / Standby / Idle, and since when (UTC).</summary>
    public OeeLiveStatus Status
    {
        get
        {
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                var online = _sessionId != null;
                if (_down != null) return new(OeeState.Down, _down.Start, online, _down, _idleThreshold);
                if (!online) return new(OeeState.Offline, _gapStart, false, null, _idleThreshold);
                if (_runDepth > 0) return new(OeeState.Running, _runStart, true, null, _idleThreshold);
                var state = now - _gapStart >= _idleThreshold ? OeeState.Idle : OeeState.Standby;
                return new(state, _gapStart, true, null, _idleThreshold);
            }
        }
    }

    // --- online session ---

    /// <summary>Marks the station online (this head is up). Closes anything a crashed run left open first. Idempotent.</summary>
    public void StartSession(string app, string? station)
    {
        string id;
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            if (_sessionId != null) return;
            id = _sessionId = NewId();
            _station = station;
            _gapStart = now;
        }
        Enqueue(s => s.RecoverDangling());
        Enqueue(s => s.OpenSession(id, now, app, station));
        _heartbeat = new Timer(_ => Enqueue(s => s.Heartbeat(id, DateTime.UtcNow)), null, HeartbeatPeriod, HeartbeatPeriod);
        _log?.Invoke(LogLevel.Info, $"OEE: station online ({app}).");
        Changed?.Invoke();
    }

    /// <summary>
    /// Marks the station offline (head exiting): closes the open run and session, then
    /// flushes pending writes (bounded by a short timeout). Final — the tracker stops writing.
    /// </summary>
    public async Task EndSessionAsync()
    {
        var now = DateTime.UtcNow;
        string? sessionId, runId;
        lock (_lock)
        {
            sessionId = _sessionId;
            runId = _runId;
            _sessionId = null;
            _runId = null;
            _runDepth = 0;
        }
        _heartbeat?.Dispose();
        _heartbeat = null;
        if (runId != null) Enqueue(s => s.CloseRun(runId, now));
        if (sessionId != null) Enqueue(s => s.CloseSession(sessionId, now));
        _writes.Writer.TryComplete();
        await Task.WhenAny(_writer, Task.Delay(DrainTimeout)).ConfigureAwait(false);
    }

    // --- production (running) ---

    /// <summary>
    /// Marks an execution in flight; dispose the token when it ends. Re-entrant — only the
    /// outermost bracket opens / closes the recorded run.
    /// </summary>
    public IDisposable BeginProduction(string kind, string? name)
    {
        bool changed = false;
        lock (_lock)
        {
            if (++_runDepth == 1)
            {
                changed = true;
                _runStart = DateTime.UtcNow;
                if (_sessionId is { } sid)
                {
                    var id = _runId = NewId();
                    var at = _runStart;
                    Enqueue(s => s.OpenRun(id, sid, at, kind, name));
                }
            }
        }
        if (changed) Changed?.Invoke();
        return new ProductionToken(this);
    }

    private void EndProduction()
    {
        bool changed = false;
        lock (_lock)
        {
            if (_runDepth == 0) return;
            if (--_runDepth == 0)
            {
                changed = true;
                var now = _gapStart = DateTime.UtcNow;
                if (_runId is { } id) Enqueue(s => s.CloseRun(id, now));
                _runId = null;
            }
        }
        if (changed) Changed?.Invoke();
    }

    // --- down / up ---

    /// <summary>
    /// Declares the station down — opens a maintenance-log entry signed by <paramref name="by"/>
    /// (engineer / technician name) with a <paramref name="category"/> and the
    /// <paramref name="reason"/> as seen now. Name and reason are required.
    /// </summary>
    public Result GoDown(string by, string? category, string reason)
    {
        if (string.IsNullOrWhiteSpace(by))
            return Result.Fail(ErrorCode.InvalidParameter, "A name is required to put the station down.");
        if (string.IsNullOrWhiteSpace(reason))
            return Result.Fail(ErrorCode.InvalidParameter, "A reason is required to put the station down.");
        OeeDowntime d;
        lock (_lock)
        {
            if (_down != null)
                return Result.Fail(ErrorCode.OperationFailed, $"Already down since {_down.Start.ToLocalTime():g} (by {_down.DownBy}).");
            d = _down = new OeeDowntime(NewId(), DateTime.UtcNow, null, by.Trim(), null,
                string.IsNullOrWhiteSpace(category) ? null : category.Trim(), reason.Trim(),
                null, null, null, _station);
        }
        Enqueue(s => s.OpenDowntime(d));
        _log?.Invoke(LogLevel.Warning,
            $"OEE: station DOWN — by {d.DownBy}" + (d.Category != null ? $" [{d.Category}]" : "") + $": {d.Reason}");
        Changed?.Invoke();
        return Result.Ok();
    }

    /// <summary>
    /// Brings the station back up — closes the open maintenance-log entry, signed by
    /// <paramref name="by"/>, with the real <paramref name="rootCause"/> and the
    /// troubleshooting <paramref name="actionTaken"/>. All three are required.
    /// </summary>
    public Result GoUp(string by, string rootCause, string actionTaken)
    {
        if (string.IsNullOrWhiteSpace(by))
            return Result.Fail(ErrorCode.InvalidParameter, "A name is required to bring the station up.");
        if (string.IsNullOrWhiteSpace(rootCause) || string.IsNullOrWhiteSpace(actionTaken))
            return Result.Fail(ErrorCode.InvalidParameter, "Root cause and action taken are required to bring the station up.");
        OeeDowntime d;
        var now = DateTime.UtcNow;
        var upBy = by.Trim();
        var cause = rootCause.Trim();
        var action = actionTaken.Trim();
        lock (_lock)
        {
            if (_down == null) return Result.Fail(ErrorCode.OperationFailed, "The station is not down.");
            d = _down;
            _down = null;
            _gapStart = now;
        }
        Enqueue(s => s.CloseDowntime(d.Id, now, upBy, cause, action));
        _log?.Invoke(LogLevel.Info,
            $"OEE: station UP — by {upBy} (down {Format(now - d.Start)}, by {d.DownBy}). Root cause: {cause}. Action: {action}.");
        Changed?.Invoke();
        return Result.Ok();
    }

    /// <summary>
    /// The maintenance history — downtimes overlapping [<paramref name="fromUtc"/>,
    /// <paramref name="toUtc"/>), newest first, filtered by free <paramref name="search"/> text
    /// over category / reason / root cause / action / names. Synchronous SQLite — call off the UI thread.
    /// </summary>
    public IReadOnlyList<OeeDowntime> GetMaintenanceHistory(DateTime fromUtc, DateTime toUtc, string? search)
        => _store.SearchDowntimes(fromUtc, toUtc, search);

    // --- units (quality) ---

    /// <summary>Records a finished DUT with its verdict. In-progress verdicts are ignored.</summary>
    public void RecordUnit(string serial, string? product, DutVerdict verdict)
    {
        if (verdict == DutVerdict.InProgress || string.IsNullOrWhiteSpace(serial)) return;
        lock (_lock) { if (_sessionId == null) return; }
        var unit = new OeeUnit(DateTime.UtcNow, serial, product, verdict);
        Enqueue(s => s.AddUnit(unit));
    }

    // --- reports ---

    /// <summary>The report for one local calendar day (24 h total available time; 23 / 25 h across a DST change).</summary>
    public OeeReport GetDayReport(DateOnly day)
        => GetReport(
            day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local).ToUniversalTime(),
            day.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Local).ToUniversalTime());

    /// <summary>Classifies [<paramref name="fromUtc"/>, <paramref name="toUtc"/>). Synchronous SQLite reads — call off the UI thread.</summary>
    public OeeReport GetReport(DateTime fromUtc, DateTime toUtc)
    {
        string? live;
        TimeSpan threshold;
        lock (_lock) { live = _sessionId; threshold = _idleThreshold; }
        var now = DateTime.UtcNow;
        var extFrom = fromUtc - threshold;
        var extTo = toUtc + threshold;

        var segments = OeeCalculator.Classify(fromUtc, toUtc, now,
            _store.Sessions(extFrom, extTo, live),
            _store.Runs(extFrom, extTo),
            _store.DownIntervals(extFrom, extTo),
            threshold);

        return new OeeReport
        {
            From = fromUtc,
            To = toUtc,
            ElapsedEnd = toUtc < now ? toUtc : now,
            IdleThreshold = threshold,
            Segments = segments,
            Downtimes = _store.Downtimes(fromUtc, toUtc),
            Units = _store.Units(fromUtc, toUtc),
        };
    }

    // --- internals ---

    private void Enqueue(Action<OeeStore> write) => _writes.Writer.TryWrite(write);

    private async Task WriteLoopAsync()
    {
        await foreach (var write in _writes.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try { write(_store); }
            catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException)
            {
                _log?.Invoke(LogLevel.Warning, $"OEE: store write failed — {ex.Message}");
            }
        }
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static string Format(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" : $"{t.Minutes}m {t.Seconds:00}s";

    private sealed class ProductionToken : IDisposable
    {
        private OeeTracker? _owner;
        public ProductionToken(OeeTracker owner) => _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.EndProduction();
    }
}
