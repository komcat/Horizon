using Horizon.Process.Interfaces;
using Horizon.Process.Models;

namespace Horizon.Process.Plugins;

public sealed class CalculateExpressionPlugin : IProcessPlugin
{
    public string Id => "context.calculate_expression";
    public string DisplayName => "Calculate Expression";
    public string Category => "Variables";
    public string Description =>
        "Evaluate an arithmetic expression and store the result in a context variable. " +
        "Use variable names directly (e.g., posA - posB * 2). " +
        "Supports: + - * / ( )";

    public IReadOnlyList<PluginParamDef> Parameters { get; } = new[]
    {
        new PluginParamDef("expression", "Expression", PluginParamType.Text,
            Category: "Formula", Required: true),
        new PluginParamDef("result_key", "Store In", PluginParamType.Text,
            Category: "Formula", Required: true, Default: "result")
    };

    public void BuildSteps(ProcessBuilder builder, IReadOnlyDictionary<string, string> paramValues)
    {
        var expression = paramValues.GetValueOrDefault("expression") ?? "";
        var resultKey = paramValues.GetValueOrDefault("result_key") ?? "result";

        builder.AddAction("context", "evaluate", new Dictionary<string, string>
        {
            ["expression"] = expression,
            ["result"] = resultKey
        });
    }
}
