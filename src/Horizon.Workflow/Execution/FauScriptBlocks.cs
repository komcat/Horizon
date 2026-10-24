using System.Globalization;
using Horizon.Common;

namespace Horizon.Workflow;

/// <summary>
/// The FAU alignment blocks (<c>fau.powerClimb</c>, <c>fau.gridScan</c>, <c>fau.wOptimize</c>) are
/// typed front-ends for the product's <c>FAU_PowerClimb</c> / <c>FAU_GridScan</c> /
/// <c>FAU_WOptimize</c> .moon scripts: each block param maps to one script argument, and the
/// composed Lua table is handed to the same script runner as <c>script.run</c>. One algorithm
/// (the hardware-tested script), with its tuning knobs as block fields.
///
/// A blank param leaves the script's own default. Every value is checked here before the script
/// is started; the script re-validates its <c>args</c> as well (unknown key / wrong type / range).
/// <c>moreArgs</c> (optional Lua table text) is appended last, so any other script key can still be
/// set and wins over the fields.
/// </summary>
public static class FauScriptBlocks
{
    public const string PowerClimbScript = "FAU_PowerClimb";
    public const string GridScanScript = "FAU_GridScan";
    public const string WOptimizeScript = "FAU_WOptimize";

    /// <summary>The script to run and the Lua argument table text it receives as <c>args</c>.</summary>
    public sealed record ScriptCall(string Script, string Args);

    /// <summary>
    /// Builds the script call for <paramref name="op"/>. <paramref name="param"/> returns a block
    /// param's (resolved, trimmed) value, or null when it is blank.
    /// </summary>
    public static Result<ScriptCall> Compose(string op, Func<string, string?> param)
    {
        var a = new ArgsBuilder(param);
        string script;
        switch (op.ToLowerInvariant())
        {
            case "fau.powerclimb":
                script = PowerClimbScript;
                PowerClimb(a);
                break;
            case "fau.gridscan":
                script = GridScanScript;
                GridScan(a);
                break;
            case "fau.woptimize":
                script = WOptimizeScript;
                WOptimize(a);
                break;
            default:
                return Result<ScriptCall>.Fail(ErrorCode.InvalidParameter, $"'{op}' is not a FAU script block.");
        }

        return a.Errors.Count > 0
            ? Result<ScriptCall>.Fail(ErrorCode.InvalidParameter, string.Join(" ", a.Errors))
            : Result<ScriptCall>.Ok(new ScriptCall(script, a.Build()));
    }

    // Step sets {zUm, xyUm} per mode — the "FAU climb - coarse / fine / XY only" presets.
    private const string CoarseSets = "{{10,1},{5,1},{2,0.5},{1,0.5},{0.5,0.25}}";
    private const string FineSets = "{{2,0.5},{1,0.5},{0.5,0.25}}";
    private const string PeakXySets = "{{0,0.5},{0,0.25}}";

    private static void PowerClimb(ArgsBuilder a)
    {
        switch ((a.Get("mode") ?? "coarse").ToLowerInvariant().Replace(" ", ""))
        {
            case "coarse": a.Raw("sets", CoarseSets); break;
            case "fine": a.Raw("sets", FineSets); break;
            case "peakxy" or "xy" or "xyonly":
                a.Bool("z", false);
                a.Raw("sets", PeakXySets);
                break;
            default:
                a.Errors.Add($"mode '{a.Get("mode")}' must be coarse, fine or peak XY.");
                break;
        }

        bool exit = a.OptBool("exit") ?? true;
        if (!exit) a.Bool("exit", false);

        // A fixed exit level replaces the photo-current target table: one row any PD reading passes.
        if (a.OptNum("exitV", min: 0, minExclusive: true, max: 10) is { } exitV && exit)
            a.Raw("targets", $"{{{{aboveUa = -1, sagV = {Lua(exitV)}}}}}");
    }

    private static void GridScan(ArgsBuilder a)
    {
        switch ((a.Get("axes") ?? "X,Y").ToUpperInvariant().Replace(",", "").Replace(" ", ""))
        {
            case "XY": break;                          // the script default
            case "YX": a.Str("axes", "YX"); break;
            default: a.Errors.Add($"axes '{a.Get("axes")}' must be X,Y or Y,X (Z never moves in the grid scan)."); break;
        }

        // Line: ± range at 'step' → start at +range, then 2·range/step steps down to −range.
        double? range = a.OptNum("lineRangeUm", min: 0, minExclusive: true, max: 200);
        double? step = a.OptNum("lineStepUm", min: 0, minExclusive: true, max: 200);
        if (range != null || step != null)
        {
            double r = range ?? 50, s = step ?? 10;   // the script's jumpUm / stepUm defaults
            int steps = (int)Math.Floor((2 * r / s) + 1e-9);
            if (steps < 1) a.Errors.Add($"line range ±{Lua(r)} µm is shorter than one {Lua(s)} µm step.");
            a.Num("jumpUm", r);
            a.Num("stepUm", s);
            a.Num("steps", steps);
        }

        if (a.OptBool("sweep") is { } sweep) a.Bool("ySweep", sweep);
        if (a.OptNum("sweepRangeUm", min: 0, minExclusive: true, max: 200) is { } sr) a.Num("yRangeUm", sr);
        if (a.OptNum("sweepStepUm", min: 0, minExclusive: true, max: 200) is { } ss) a.Num("yStepUm", ss);

        // Signal = floor (median of readings) + max(noiseK × T0 std, minDeltaV); climb at the first one.
        if (a.OptBool("climb") is { } climb) a.Bool("climb", climb);
        if (a.OptNum("noiseK", min: 0, max: 100) is { } k) a.Num("noiseK", k);
        if (a.OptNum("minDeltaV", min: 0, minExclusive: true, max: 5) is { } d) a.Num("minDeltaV", d);
    }

    private static void WOptimize(ArgsBuilder a)
    {
        if (a.OptNum("wStep", min: 0, minExclusive: true, max: 2) is { } w) a.Num("wStep", w);
        if (a.OptNum("wLimitDeg", min: 0, minExclusive: true, max: 5) is { } lim) a.Num("wLimitDeg", lim);
        if (a.OptNum("maxWSteps", min: 1, max: 100) is { } n) a.Num("maxWSteps", Math.Floor(n));
        if (a.OptNum("minGainPct", min: 0, max: 100) is { } g) a.Num("minGainPct", g);

        // XY centring steps: "1, 0.5" → {1, 0.5}.
        if (a.Get("xySets") is { } text)
        {
            var steps = new List<string>();
            foreach (var part in text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                    && double.IsFinite(v) && v > 0 && v <= 50)
                    steps.Add(Lua(v));
                else
                    a.Errors.Add($"XY centring step '{part}' must be a number > 0 and ≤ 50 µm.");
            }
            if (steps.Count == 0) a.Errors.Add("XY centring steps are empty.");
            else a.Raw("xySets", "{" + string.Join(", ", steps) + "}");
        }
    }

    private static string Lua(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Collects <c>key = value</c> pairs (and errors) for one Lua argument table.</summary>
    private sealed class ArgsBuilder
    {
        private readonly Func<string, string?> _param;
        private readonly List<string> _pairs = new();

        public ArgsBuilder(Func<string, string?> param) => _param = param;

        public List<string> Errors { get; } = new();

        public string? Get(string key) => _param(key) is { Length: > 0 } v ? v : null;

        public void Raw(string key, string luaExpr) => _pairs.Add($"{key} = {luaExpr}");
        public void Num(string key, double v) => Raw(key, Lua(v));
        public void Bool(string key, bool v) => Raw(key, v ? "true" : "false");
        public void Str(string key, string v) => Raw(key, "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"");

        /// <summary>A number param within [min, max] (min exclusive when asked); null when blank, an error when invalid.</summary>
        public double? OptNum(string key, double min, double max, bool minExclusive = false)
        {
            var text = Get(key);
            if (text == null) return null;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || !double.IsFinite(v))
            {
                Errors.Add($"{key} '{text}' is not a number.");
                return null;
            }
            bool low = minExclusive ? v <= min : v < min;
            if (low || v > max)
            {
                Errors.Add($"{key} = {Lua(v)} must be {(minExclusive ? ">" : "≥")} {Lua(min)} and ≤ {Lua(max)}.");
                return null;
            }
            return v;
        }

        /// <summary>A bool param (true/1/on/yes, false/0/off/no); null when blank, an error when unreadable.</summary>
        public bool? OptBool(string key)
        {
            var text = Get(key)?.ToLowerInvariant();
            switch (text)
            {
                case null: return null;
                case "true" or "1" or "on" or "yes": return true;
                case "false" or "0" or "off" or "no": return false;
                default:
                    Errors.Add($"{key} '{text}' must be true or false.");
                    return null;
            }
        }

        /// <summary>
        /// "{ k = v, … }" plus the block's <c>moreArgs</c> appended last (outer braces optional), so a
        /// key there overrides the field. The newline keeps a trailing '--' comment from swallowing the brace.
        /// </summary>
        public string Build()
        {
            var parts = new List<string>(_pairs);
            var more = (Get("moreArgs") ?? "").Trim();
            if (more.StartsWith('{') && more.EndsWith('}')) more = more[1..^1];
            more = more.Trim().TrimEnd(',', ';').TrimEnd();
            if (more.Length > 0) parts.Add(more + "\n");
            return parts.Count == 0 ? "" : "{ " + string.Join(", ", parts) + " }";
        }
    }
}
