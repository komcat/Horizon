namespace Horizon.Workflow.Oee;

/// <summary>
/// What the station was doing at a moment in time. Every instant of a calendar day is
/// exactly one of these (priority: Down &gt; Offline &gt; Running &gt; Standby / Idle).
/// </summary>
public enum OeeState
{
    /// <summary>No Horizon head (FAAM.UI) was running and the station was not marked down.</summary>
    Offline,
    /// <summary>A program / deck / recipe / script / Run-page step was executing.</summary>
    Running,
    /// <summary>Online, nothing executing, but for less than the idle threshold — load/unload, handling. Counts as production.</summary>
    Standby,
    /// <summary>Online, nothing executing for at least the idle threshold (default 30 min) — the whole gap is idle.</summary>
    Idle,
    /// <summary>An engineer / technician declared the station down (repair, maintenance). Wins over every other state, online or not.</summary>
    Down,
}

/// <summary>A closed-or-open time interval in UTC; <see cref="End"/> null = still open.</summary>
public readonly record struct OeeInterval(DateTime Start, DateTime? End);

/// <summary>A stretch of one state on the timeline (UTC, end exclusive).</summary>
public sealed record OeeSegment(DateTime Start, DateTime End, OeeState State)
{
    public TimeSpan Duration => End - Start;
}

/// <summary>
/// One down → up bracket — the maintenance log entry. Down records who declared it, a
/// category and the reason as seen at the time; Up records who cleared it, the real root
/// cause and the troubleshooting action taken. <see cref="UpNote"/> is the free note written
/// by builds before root cause / action existed (read-only, legacy rows).
/// </summary>
public sealed record OeeDowntime(
    string Id, DateTime Start, DateTime? End, string DownBy, string? UpBy,
    string? Category, string? Reason, string? RootCause, string? ActionTaken,
    string? UpNote, string? Station)
{
    public bool IsOpen => End == null;

    /// <summary>Down duration so far (open) or in total (closed).</summary>
    public TimeSpan Duration(DateTime nowUtc) => (End ?? nowUtc) - Start;
}

/// <summary>A finished DUT (unit) — the quality side of OEE.</summary>
public sealed record OeeUnit(DateTime At, string Serial, string? Product, DutVerdict Verdict);

/// <summary>The station's live state, cheap to read from any thread.</summary>
public sealed record OeeLiveStatus(
    OeeState State, DateTime SinceUtc, bool Online, OeeDowntime? OpenDowntime, TimeSpan IdleThreshold);

/// <summary>
/// The OEE picture for one window (normally one local calendar day). Durations are
/// classified up to <see cref="ElapsedEnd"/> (now, for today); time after it has not
/// happened yet. Ratios are over the elapsed time so "today so far" reads sensibly; for a
/// past day elapsed = the full 24 h total available time.
/// </summary>
public sealed class OeeReport
{
    public required DateTime From { get; init; }
    public required DateTime To { get; init; }
    public required DateTime ElapsedEnd { get; init; }
    public required TimeSpan IdleThreshold { get; init; }
    public required IReadOnlyList<OeeSegment> Segments { get; init; }
    public required IReadOnlyList<OeeDowntime> Downtimes { get; init; }
    public required IReadOnlyList<OeeUnit> Units { get; init; }

    /// <summary>Total available time — the whole window (24 h for a normal day).</summary>
    public TimeSpan Total => To - From;

    /// <summary>The part of the window that has already happened.</summary>
    public TimeSpan Elapsed => ElapsedEnd > From ? ElapsedEnd - From : TimeSpan.Zero;

    public TimeSpan TimeIn(OeeState state)
        => TimeSpan.FromTicks(Segments.Where(s => s.State == state).Sum(s => s.Duration.Ticks));

    public TimeSpan Running => TimeIn(OeeState.Running);
    public TimeSpan Standby => TimeIn(OeeState.Standby);
    public TimeSpan Idle => TimeIn(OeeState.Idle);
    public TimeSpan Down => TimeIn(OeeState.Down);
    public TimeSpan Offline => TimeIn(OeeState.Offline);

    /// <summary>Production time = executing + the short (&lt; threshold) gaps between executions.</summary>
    public TimeSpan Production => Running + Standby;

    /// <summary>Up time = FAAM online and not down (production + idle).</summary>
    public TimeSpan Up => Production + Idle;

    public int Passed => Units.Count(u => u.Verdict == DutVerdict.Pass);
    public int Failed => Units.Count(u => u.Verdict == DutVerdict.Fail);
    public int Aborted => Units.Count(u => u.Verdict == DutVerdict.Aborted);

    /// <summary>Up / elapsed.</summary>
    public double UptimeRatio => Ratio(Up);

    /// <summary>(elapsed − down) / elapsed — how much of the time the station was not down.</summary>
    public double Availability => Elapsed > TimeSpan.Zero ? 1.0 - Ratio(Down) : 0;

    /// <summary>Production / elapsed — the share of time spent making units.</summary>
    public double Utilization => Ratio(Production);

    /// <summary>Pass / (pass + fail); null with no finished units. Aborted units are not judged.</summary>
    public double? Quality => Passed + Failed == 0 ? null : (double)Passed / (Passed + Failed);

    /// <summary>
    /// Utilization × Quality — the OEE figure available without an ideal cycle time
    /// (Performance is not tracked, i.e. taken as 100 %). Null with no judged units.
    /// </summary>
    public double? Oee => Quality is { } q ? Utilization * q : null;

    private double Ratio(TimeSpan t) => Elapsed > TimeSpan.Zero ? t.TotalSeconds / Elapsed.TotalSeconds : 0;
}
