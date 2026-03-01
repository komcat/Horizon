using Horizon.Process.Interfaces;
using Horizon.Process.Models;

namespace Horizon.Process.Plugins;

public sealed class LoopStartPlugin : IProcessPlugin
{
    public string Id => "system.loop_start";
    public string DisplayName => "Loop Start";
    public string Category => "Flow Control";
    public string Description => "Begin a loop block. Set count to 0 for infinite (runs until stopped).";

    public IReadOnlyList<PluginParamDef> Parameters { get; } = new[]
    {
        new PluginParamDef("count", "Iterations", PluginParamType.Number,
            Category: "Settings", Default: "0",
            Min: 0, Max: 999999, Step: 1, DecimalPlaces: 0,
            UnitSuffix: "× (0=∞)")
    };

    public void BuildSteps(ProcessBuilder builder, IReadOnlyDictionary<string, string> paramValues)
    {
        var countStr = paramValues.GetValueOrDefault("count") ?? "0";
        int count = int.TryParse(countStr, out var c) ? c : 0;
        builder.BeginLoop(count);
    }
}
