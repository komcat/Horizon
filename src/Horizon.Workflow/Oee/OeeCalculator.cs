namespace Horizon.Workflow.Oee;

/// <summary>
/// Pure classification of a time window into <see cref="OeeState"/> segments from the
/// raw intervals the tracker records (online sessions, executions, downtimes). No I/O,
/// no clock — the caller passes <c>now</c>, so it is deterministic and testable.
///
/// <para><b>Rules.</b> Down wins over everything (a station under repair is down whether
/// or not FAAM is open). Otherwise, outside every online session = Offline; inside an
/// execution = Running. The remaining online time forms <i>gaps</i>; a gap shorter than
/// the idle threshold is Standby (handling between units, counts as production), a gap of
/// the threshold or longer is Idle <i>in full</i>. A gap is measured across the window
/// edges (the window is widened by the threshold on both sides before classifying), so a
/// gap straddling midnight is judged by its real length.</para>
/// </summary>
public static class OeeCalculator
{
    /// <summary>Upper bound on boundary points per window — a guard against a corrupt store, not a real limit.</summary>
    private const int MaxPoints = 200_000;

    public static IReadOnlyList<OeeSegment> Classify(
        DateTime from, DateTime to, DateTime now,
        IEnumerable<OeeInterval> sessions,
        IEnumerable<OeeInterval> runs,
        IEnumerable<OeeInterval> downs,
        TimeSpan idleThreshold)
    {
        var end = to < now ? to : now;
        if (end <= from) return Array.Empty<OeeSegment>();

        var extFrom = from - idleThreshold;
        var extTo = Min(to + idleThreshold, now);

        var s = Clip(sessions, extFrom, extTo, now);
        var r = Clip(runs, extFrom, extTo, now);
        var d = Clip(downs, extFrom, extTo, now);

        var points = new SortedSet<DateTime> { extFrom, extTo };
        foreach (var (a, b) in s.Concat(r).Concat(d))
        {
            points.Add(a);
            points.Add(b);
            if (points.Count > MaxPoints) break;
        }

        // Elementary pieces, then merge equal neighbours. "Gap" is a provisional state.
        var raw = new List<(DateTime A, DateTime B, Piece P)>();
        DateTime? prev = null;
        foreach (var p in points)
        {
            if (prev is { } a && p > a)
            {
                var mid = a + TimeSpan.FromTicks((p - a).Ticks / 2);
                var piece = Inside(d, mid) ? Piece.Down
                          : Inside(r, mid) ? Piece.Running
                          : Inside(s, mid) ? Piece.Gap
                          : Piece.Offline;
                if (raw.Count > 0 && raw[^1].P == piece && raw[^1].B == a)
                    raw[^1] = (raw[^1].A, p, piece);
                else
                    raw.Add((a, p, piece));
            }
            prev = p;
        }

        var result = new List<OeeSegment>(raw.Count);
        foreach (var (a, b, piece) in raw)
        {
            var state = piece switch
            {
                Piece.Down => OeeState.Down,
                Piece.Running => OeeState.Running,
                Piece.Offline => OeeState.Offline,
                _ => b - a >= idleThreshold ? OeeState.Idle : OeeState.Standby,
            };
            var ca = a < from ? from : a;
            var cb = b > end ? end : b;
            if (cb > ca) result.Add(new OeeSegment(ca, cb, state));
        }
        return result;
    }

    private enum Piece { Offline, Running, Gap, Down }

    private static List<(DateTime A, DateTime B)> Clip(
        IEnumerable<OeeInterval> intervals, DateTime from, DateTime to, DateTime now)
    {
        var list = new List<(DateTime, DateTime)>();
        foreach (var i in intervals)
        {
            var a = i.Start < from ? from : i.Start;
            var b = Min(i.End ?? now, to);
            if (b > a) list.Add((a, b));
        }
        return list;
    }

    private static bool Inside(List<(DateTime A, DateTime B)> intervals, DateTime t)
    {
        foreach (var (a, b) in intervals)
            if (t >= a && t < b) return true;
        return false;
    }

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
}
