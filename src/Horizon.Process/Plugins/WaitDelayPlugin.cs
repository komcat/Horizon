using Horizon.Process.Interfaces;
using Horizon.Process.Models;

namespace Horizon.Process.Plugins;

public sealed class WaitDelayPlugin : IProcessPlugin
{
    public string Id => "system.wait_delay";
    public string DisplayName => "Wait Delay";
    public string Category => "System";
    public string Description => "Pause execution for a specified duration.";

    public IReadOnlyList<PluginParamDef> Parameters { get; } = new[]
    {
        new PluginParamDef("delay_ms", "Delay", PluginParamType.Number,
            Category: "Settings", Default: "1000", UnitSuffix: "ms",
            Min: 0, Max: 60000, Step: 100, DecimalPlaces: 0)
    };

    public void BuildSteps(ProcessBuilder builder, IReadOnlyDictionary<string, string> paramValues)
    {
        var ms = paramValues.GetValueOrDefault("delay_ms") ?? "1000";

        builder.AddAction("system", "wait", new Dictionary<string, string>
        {
            ["ms"] = ms
        });
    }
}
