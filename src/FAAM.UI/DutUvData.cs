using System.Text.Json;
using Horizon.Workflow;

namespace FAAM.UI;

/// <summary>
/// One full-signal snapshot filed in a DUT run by a <c>dut.readAllSignals</c> step —
/// the entry key (<c>uv1.0</c>, <c>uv1.1</c>, <c>uv2.0</c>, <c>uv2.1_final</c>, …), when it
/// was taken, and the signal key → value map that the step serialised.
/// </summary>
internal sealed record UvSnapshot(string Key, DateTime Timestamp, IReadOnlyDictionary<string, double> Values);

/// <summary>
/// One DUT run that carries at least one UV snapshot — the unit of the DUT Data
/// overview (a reworked serial has several).
/// </summary>
internal sealed record UvRun(
    string Serial,
    DateTime StartedUtc,
    string Source,
    DutVerdict Verdict,
    string Operator,
    IReadOnlyList<UvSnapshot> Snapshots)
{
    /// <summary>The snapshot filed under <paramref name="key"/>, or null when this run has none.</summary>
    public UvSnapshot? Find(string key)
    {
        for (int i = 0; i < Snapshots.Count; i++)
            if (string.Equals(Snapshots[i].Key, key, StringComparison.OrdinalIgnoreCase))
                return Snapshots[i];
        return null;
    }

    /// <summary>The value of <paramref name="signal"/> in the <paramref name="key"/> snapshot, or null.</summary>
    public double? Value(string key, string signal)
    {
        var snap = Find(key);
        if (snap == null) return null;
        return snap.Values.TryGetValue(signal, out var v) ? v : null;
    }
}

/// <summary>
/// Pulls the UV-cure signal snapshots out of a <see cref="DutRecord"/>. Pure and UI-free:
/// the DUT Data page runs it on a background thread over every serial of the product.
/// Only entries that look like a <c>dut.readAllSignals</c> dataset are taken — an
/// <see cref="DutValueType.Object"/> entry whose key starts with <c>uv</c> and whose value
/// parses as a flat <c>string → number</c> map. Everything else in the run (jog mirror
/// entries, log lines, step markers) is ignored.
/// </summary>
internal static class DutUvData
{
    /// <summary>The UV process snapshots, in process order (before/after cure 1, before/after cure 2).</summary>
    public static readonly string[] SnapshotKeys = { "uv1.0", "uv1.1", "uv2.0", "uv2.1_final" };

    /// <summary>The signal shown by default in the overview + chart.</summary>
    public const string DefaultSignal = "siphog.sagPwrV";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>One <see cref="UvRun"/> per run of the record that holds a UV snapshot, in record order.</summary>
    public static IReadOnlyList<UvRun> Extract(DutRecord record)
    {
        var runs = new List<UvRun>();
        foreach (var run in record.Runs)
        {
            var snapshots = ExtractSnapshots(run);
            if (snapshots.Count == 0) continue;

            var op = string.IsNullOrEmpty(run.OperatorName) ? (run.OperatorId ?? "") : run.OperatorName;
            runs.Add(new UvRun(record.Serial, run.StartedUtc, run.Source, run.Verdict, op, snapshots));
        }
        return runs;
    }

    /// <summary>
    /// The UV snapshots of one run: same key filed twice → the latest timestamp wins; ordered by
    /// <see cref="SnapshotKeys"/> first, then any other <c>uv*</c> keys by time.
    /// </summary>
    private static List<UvSnapshot> ExtractSnapshots(DutRun run)
    {
        var byKey = new Dictionary<string, UvSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in run.Entries)
        {
            if (e.Type != DutValueType.Object) continue;
            if (!e.Key.StartsWith("uv", StringComparison.OrdinalIgnoreCase)) continue;

            var values = TryParseValues(e.Value);
            if (values == null) continue;

            if (!byKey.TryGetValue(e.Key, out var existing) || e.Timestamp >= existing.Timestamp)
                byKey[e.Key] = new UvSnapshot(e.Key, e.Timestamp, values);
        }

        var ordered = new List<UvSnapshot>(byKey.Count);
        foreach (var key in SnapshotKeys)
            if (byKey.Remove(key, out var snap)) ordered.Add(snap);

        var extras = new List<UvSnapshot>(byKey.Values);
        extras.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        ordered.AddRange(extras);
        return ordered;
    }

    /// <summary>A flat signal map from a serialised dataset, or null when the text is anything else.</summary>
    private static Dictionary<string, double>? TryParseValues(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, double>>(json, JsonOptions);
            return map == null || map.Count == 0 ? null : map;
        }
        catch (JsonException)
        {
            return null;   // activity-mirror objects, free text — not a signal dataset
        }
    }

    /// <summary>
    /// Every signal key seen across <paramref name="runs"/>, well-known SiPhOG keys first
    /// (in the order <see cref="SiphogSignalOrder"/> lists them), then the rest alphabetically.
    /// </summary>
    public static IReadOnlyList<string> SignalKeys(IEnumerable<UvRun> runs)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var run in runs)
            foreach (var snap in run.Snapshots)
                foreach (var key in snap.Values.Keys)
                    seen.Add(key);

        var ordered = new List<string>();
        foreach (var key in SiphogSignalOrder)
            if (seen.Remove(key)) ordered.Add(key);

        var rest = new List<string>(seen);
        rest.Sort(StringComparer.OrdinalIgnoreCase);
        ordered.AddRange(rest);
        return ordered;
    }

    /// <summary>Display order for the SiPhOG signals — the ones the process cares about first.</summary>
    private static readonly string[] SiphogSignalOrder =
    {
        "siphog.sagPwrV",
        "siphog.targetSagPwrV",
        "siphog.photoCurrentUa",
        "siphog.sledCurrentMa",
        "siphog.sledTempC",
        "siphog.tecCurrentMa",
    };
}
