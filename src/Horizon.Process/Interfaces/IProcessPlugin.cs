using Horizon.Process.Models;

namespace Horizon.Process.Interfaces;

public interface IProcessPlugin
{
    string Id { get; }
    string DisplayName { get; }
    string Category { get; }
    string Description { get; }
    IReadOnlyList<PluginParamDef> Parameters { get; }
    void BuildSteps(ProcessBuilder builder, IReadOnlyDictionary<string, string> paramValues);
}
