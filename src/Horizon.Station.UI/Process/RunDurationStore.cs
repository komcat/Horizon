using Horizon.Common;

namespace Horizon.Station.UI.Process;

/// <summary>
/// Remembers how long each top-level block of a program took on earlier runs, so the
/// Run-page progress bar can estimate a block that reports nothing (a long Run Script
/// with no <c>machine.progress</c> calls) and weight the whole bar by time.
///
/// <para>Saved to <c>faam_run_durations.json</c> in the config dir, keyed by
/// <c>product/program</c>. A block is identified by its index and description; if
/// either changes (the program was edited) the old timing for it is dropped, and a
/// change in block count drops the whole program's timings. Each block keeps a
/// smoothed average (new = ½ old + ½ latest) so one odd run doesn't swing it.</para>
/// </summary>
public sealed class RunDurationStore
{
    private const string FileName = "faam_run_durations";
    private const double Smoothing = 0.5;

    public sealed class ProgramTimings
    {
        public int Total { get; set; }

        /// <summary>True once a run reached the end — then blocks with no timing are
        /// instant markers (if/loop/group), safe to weight as 0 ms.</summary>
        public bool Complete { get; set; }

        public Dictionary<int, BlockTiming> Blocks { get; set; } = new();
    }

    public sealed class BlockTiming
    {
        public string Description { get; set; } = string.Empty;
        public double AverageMs { get; set; }
        public int Runs { get; set; }
    }

    private Dictionary<string, ProgramTimings> _data = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    public static string Key(string? product, string program) => $"{product ?? "-"}/{program}";

    /// <summary>
    /// Expected ms per block for a program of <paramref name="total"/> blocks, or null if
    /// nothing usable is stored. Entries whose description no longer matches are excluded
    /// later by <see cref="ExpectedFor"/>.
    /// </summary>
    public ProgramTimings? Get(string key, int total)
    {
        EnsureLoaded();
        return _data.TryGetValue(key, out var t) && t.Total == total ? t : null;
    }

    /// <summary>Expected ms of one block, or null when unknown / the block changed.</summary>
    public static double? ExpectedFor(ProgramTimings? timings, int index, string description)
        => timings != null && timings.Blocks.TryGetValue(index, out var b)
           && string.Equals(b.Description, description, StringComparison.Ordinal) && b.AverageMs > 0
            ? b.AverageMs : null;

    /// <summary>Folds this run's measured block times in and saves. Never throws.</summary>
    public void Record(string key, int total, IReadOnlyDictionary<int, (string Description, double Ms)> measured,
        bool completed, Action<string>? onError = null)
    {
        if (total <= 0 || measured.Count == 0) return;
        try
        {
            EnsureLoaded();
            if (!_data.TryGetValue(key, out var t) || t.Total != total)
                _data[key] = t = new ProgramTimings { Total = total };
            if (completed) t.Complete = true;

            foreach (var (index, (desc, ms)) in measured)
            {
                if (ms <= 0) continue;
                if (t.Blocks.TryGetValue(index, out var b) && b.Description == desc && b.AverageMs > 0)
                {
                    b.AverageMs = b.AverageMs * (1 - Smoothing) + ms * Smoothing;
                    b.Runs++;
                }
                else
                {
                    t.Blocks[index] = new BlockTiming { Description = desc, AverageMs = ms, Runs = 1 };
                }
            }
            ConfigLoader.SaveJson(ConfigLoader.GetConfigDirectory(), FileName, _data);
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex.Message);   // cosmetic — a progress estimate is never worth failing a run over
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            var loaded = ConfigLoader.LoadJson<Dictionary<string, ProgramTimings>>(
                ConfigLoader.GetConfigDirectory(), FileName);
            if (loaded != null)
                _data = new Dictionary<string, ProgramTimings>(loaded, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            // Unreadable file → start fresh (it's rewritten on the next completed run).
        }
    }
}
