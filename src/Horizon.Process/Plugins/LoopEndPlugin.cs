using Horizon.Process.Interfaces;
using Horizon.Process.Models;

namespace Horizon.Process.Plugins;

public sealed class LoopEndPlugin : IProcessPlugin
{
    public string Id => "system.loop_end";
    public string DisplayName => "Loop End";
    public string Category => "Flow Control";
    public string Description => "End a loop block. Must match a Loop Start.";

    public IReadOnlyList<PluginParamDef> Parameters { get; } = Array.Empty<PluginParamDef>();

    public void BuildSteps(ProcessBuilder builder, IReadOnlyDictionary<string, string> paramValues)
    {
        builder.EndLoop();
    }
}
