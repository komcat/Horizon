using Horizon.Process.Interfaces;
using Horizon.Process.Models;

namespace Horizon.Process.Plugins;

public sealed class CommentPlugin : IProcessPlugin
{
    public string Id => "system.comment";
    public string DisplayName => "Comment";
    public string Category => "System";
    public string Description => "Insert a comment line into the process. Does not execute anything.";

    public IReadOnlyList<PluginParamDef> Parameters { get; } = new[]
    {
        new PluginParamDef("text", "Text", PluginParamType.Text,
            Category: "Comment", Required: false, Default: "")
    };

    public void BuildSteps(ProcessBuilder builder, IReadOnlyDictionary<string, string> paramValues)
    {
        // No-op — comments are purely visual annotations in the build list
    }
}
