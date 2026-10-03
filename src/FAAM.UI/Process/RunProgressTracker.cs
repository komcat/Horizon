using Horizon.Workflow;
using Horizon.Workflow.Scripting;

namespace FAAM.UI.Process;

/// <summary>
/// Turns a running program's step events (+ any <c>machine.progress</c> reports from a
/// Run Script block, + earlier-run timings) into one 0–100 % for the Run-page progress bars.
///
/// <para><b>Weighting.</b> When an earlier complete run of this program is on record
/// (<see cref="RunDurationStore"/>), each top-level block is weighted by its usual
/// duration, so a 5-minute script counts for more than a 1-second move, and a
/// time-left estimate is available. Otherwise every top-level block counts the same:
/// % = (blocks finished + current block's share) ÷ total blocks.</para>
///
/// <para><b>Current block's share</b>, first that applies:</para>
/// <list type="bullet">
///   <item>a <c>flow.parallel</c> stage — blocks finished across all its branches ÷ blocks in all branches;</item>
///   <item>a <c>flow.subProgram</c> — the child program's finished ÷ total blocks;</item>
///   <item>a Run Script block — the script's own <c>machine.progress</c> percent;</item>
///   <item>otherwise — elapsed ÷ its usual duration from earlier runs, capped at 95 %
///         (the block isn't done until it says so).</item>
/// </list>
/// <para>The shown value never goes backwards within a program (loops re-run earlier
/// indices). Not thread-safe — feed it on the UI thread.</para>
/// </summary>
public sealed class RunProgressTracker
{
    private const double EstimateCap = 0.95;

    private RunDurationStore? _store;
    private string _key = string.Empty;
    private RunDurationStore.ProgramTimings? _timings;

    private int _total;                     // top-level block count (0 = no program events yet)
    private int _current;                   // index of the executing top-level block
    private bool _inBlock;                  // a top-level block has started and not completed
    private string _block = string.Empty;   // its description
    private DateTime _blockStartUtc;
    private double _reported = -1;          // 0..1 share reported by branches/sub-program/script (-1 = none)
    private readonly Dictionary<string, (int Done, int Total)> _lanes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, (string Description, double Ms)> _measured = new();

    /// <summary>True once the program has reported a step — until then the bar is indeterminate.</summary>
    public bool HasProgress => _total > 0;

    /// <summary>Display % (0–100), monotonic within one program.</summary>
    public double Percent { get; private set; }

    /// <summary>True when the % leans on earlier-run timings (shown as "≈").</summary>
    public bool IsEstimate { get; private set; }

    /// <summary>Estimated time left (time-weighted mode only), else null.</summary>
    public TimeSpan? Remaining { get; private set; }

    /// <summary>Current block name, with the script's message when it gave one.</summary>
    public string Detail { get; private set; } = string.Empty;

    /// <summary>Starts a fresh program (a step click or the next Auto-chain entry).</summary>
    /// <param name="store">Earlier-run timings; null disables estimates (built-in steps).</param>
    /// <param name="key">The program's <see cref="RunDurationStore.Key"/>.</param>
    public void Reset(RunDurationStore? store = null, string key = "")
    {
        _store = store;
        _key = key;
        _timings = null;
        _total = 0;
        _current = 0;
        _inBlock = false;
        _block = string.Empty;
        _reported = -1;
        _lanes.Clear();
        _measured.Clear();
        Percent = 0;
        IsEstimate = false;
        Remaining = null;
        Detail = string.Empty;
    }

    public void OnStep(WorkflowStepEvent e)
    {
        if (e.Total <= 0) return;
        var now = DateTime.UtcNow;

        if (e.Depth == 0 && e.Lane == null)
        {
            if (_total != e.Total)
            {
                _total = e.Total;
                _timings = _store?.Get(_key, _total);
            }
            if (!e.Completed)
            {
                _current = e.Index;
                _inBlock = true;
                _block = e.Description;
                _blockStartUtc = now;
                _reported = -1;
                _lanes.Clear();
                Detail = _block;
            }
            else
            {
                if (_inBlock && e.Index == _current)
                {
                    // Sum per index so a block re-run by a loop weighs its full time.
                    var ms = (now - _blockStartUtc).TotalMilliseconds;
                    _measured[e.Index] = _measured.TryGetValue(e.Index, out var m)
                        ? (e.Description, m.Ms + ms) : (e.Description, ms);
                }
                _current = e.Index + 1;     // this block is done
                _inBlock = false;
                _reported = -1;
            }
        }
        else if (e.Depth == 1 && e.Completed)
        {
            if (e.Lane != null)
            {
                // A branch of the current top-level parallel stage.
                _lanes[e.Lane] = (e.Index + 1, e.Total);
                int done = 0, total = 0;
                foreach (var (d, t) in _lanes.Values) { done += d; total += t; }
                if (total > 0) _reported = Math.Max(_reported, (double)done / total);
            }
            else
            {
                // A step of the current block's sub-program.
                _reported = Math.Max(_reported, (double)(e.Index + 1) / e.Total);
            }
        }
        Recompute(now);
    }

    public void OnScript(ScriptProgress p)
    {
        if (_lanes.Count == 0)                       // inside a parallel stage the branches drive it
            _reported = Math.Max(_reported, p.Percent / 100.0);
        Detail = string.IsNullOrWhiteSpace(p.Message) ? _block : $"{_block} — {p.Message}";
        Recompute(DateTime.UtcNow);
    }

    /// <summary>Advances the time-based estimate; call a few times a second while running.</summary>
    public void Tick() => Recompute(DateTime.UtcNow);

    /// <summary>Saves this run's block timings for next time. <paramref name="completed"/> = ran to the end.</summary>
    public void Commit(bool completed, Action<string>? onError = null)
    {
        if (_store != null && _total > 0)
            _store.Record(_key, _total, _measured, completed, onError);
    }

    private void Recompute(DateTime now)
    {
        if (_total <= 0) return;

        // Share of the executing block: what it reports, else elapsed ÷ its usual time.
        double share = 0;
        bool estimated = false;
        double? expectedCur = _inBlock
            ? RunDurationStore.ExpectedFor(_timings, _current, _block) : null;
        double elapsedMs = _inBlock ? (now - _blockStartUtc).TotalMilliseconds : 0;
        if (_inBlock)
        {
            if (_reported >= 0) share = _reported;
            else if (expectedCur is { } exp)
            {
                share = Math.Min(elapsedMs / exp, EstimateCap);
                estimated = true;
            }
        }
        share = Math.Clamp(share, 0, 1);

        double pct;
        Remaining = null;
        if (_timings is { Complete: true } && (!_inBlock || expectedCur != null))
        {
            // Time-weighted: blocks with no record are instant markers (0 ms).
            double totalMs = 0, doneMs = 0;
            for (int i = 0; i < _total; i++)
            {
                double ms = _timings.Blocks.TryGetValue(i, out var b) ? b.AverageMs : 0;
                totalMs += ms;
                if (i < _current) doneMs += ms;
            }
            if (totalMs <= 0) { pct = CountBased(share); }
            else
            {
                double curMs = _inBlock ? (expectedCur ?? 0) * share : 0;
                pct = (doneMs + curMs) / totalMs * 100.0;
                double leftMs = totalMs - doneMs - (_inBlock ? Math.Min(elapsedMs, expectedCur ?? 0) : 0);
                Remaining = TimeSpan.FromMilliseconds(Math.Max(0, leftMs));
                estimated = true;
            }
        }
        else
        {
            pct = CountBased(share);
        }

        IsEstimate = estimated;
        Percent = Math.Max(Percent, Math.Clamp(pct, 0, 100));
    }

    private double CountBased(double share)
        => (Math.Min(_current, _total) + (_inBlock ? share : 0)) / _total * 100.0;
}

/// <summary>One immutable reading of <see cref="RunProgressTracker"/> for the progress bars.</summary>
public sealed record RunProgressSnapshot(
    bool Running, bool HasProgress, double Percent, bool IsEstimate, TimeSpan? Remaining, string Detail)
{
    public static readonly RunProgressSnapshot Idle = new(false, false, 0, false, null, string.Empty);

    public static RunProgressSnapshot From(RunProgressTracker t, bool running)
        => new(running, t.HasProgress, t.Percent, t.IsEstimate, t.Remaining, t.Detail);

    /// <summary>"≈42% · 2:10 left" (no block name) — for the big header.</summary>
    public string Short
    {
        get
        {
            if (!HasProgress) return "Running…";
            var pct = $"{(IsEstimate ? "≈" : "")}{Percent:0}%";
            return Remaining is { } r ? $"{pct} · {FormatLeft(r)} left" : pct;
        }
    }

    /// <summary><see cref="Short"/> plus the current block — for the steps-panel bar.</summary>
    public string Text => !HasProgress || string.IsNullOrEmpty(Detail) ? Short : $"{Short} — {Detail}";

    private static string FormatLeft(TimeSpan r)
        => r.TotalHours >= 1 ? r.ToString(@"h\:mm\:ss") : r.ToString(@"m\:ss");
}
