using Horizon.Process.Interfaces;
using Horizon.Process.Models;

namespace Horizon.Process.Plugins;

public sealed class DispensePlugin : IProcessPlugin
{
    public string Id => "system.dispense";
    public string DisplayName => "Dispense";
    public string Category => "System";
    public string Description => "Execute a dispensing sequence: navigate camera, translate to needle, approach at safety speed, dispense, lift off, and verify.";

    public IReadOnlyList<PluginParamDef> Parameters { get; } = new[]
    {
        // Target
        new PluginParamDef("name", "Profile Name", PluginParamType.Text,
            Category: "Target", Required: true),
        new PluginParamDef("motionDevice", "Motion Device", PluginParamType.MotionDevice,
            Category: "Target", Required: false),
        new PluginParamDef("pneumatic", "Pneumatic Slide", PluginParamType.PneumaticDevice,
            Category: "Target", Required: false),

        // Motion
        new PluginParamDef("velocity", "Velocity", PluginParamType.Number,
            Category: "Motion", Default: "20", UnitSuffix: "mm/s",
            Min: 0.1, Max: 100, Step: 1, DecimalPlaces: 1),
        new PluginParamDef("safetyHeight", "Safety Height", PluginParamType.Number,
            Category: "Motion", Default: "5", UnitSuffix: "mm",
            Min: 0.1, Max: 50, Step: 0.5, DecimalPlaces: 1),
        new PluginParamDef("safetySpeed", "Safety Speed", PluginParamType.Number,
            Category: "Motion", Default: "0.5", UnitSuffix: "mm/s",
            Min: 0.01, Max: 10, Step: 0.1, DecimalPlaces: 2),
        new PluginParamDef("liftOffHeight", "Lift Off Height", PluginParamType.Number,
            Category: "Motion", Default: "3", UnitSuffix: "mm",
            Min: 0.1, Max: 20, Step: 0.5, DecimalPlaces: 1),
        new PluginParamDef("liftOffSpeed", "Lift Off Speed", PluginParamType.Number,
            Category: "Motion", Default: "1", UnitSuffix: "mm/s",
            Min: 0.01, Max: 10, Step: 0.1, DecimalPlaces: 2),

        // Timing
        new PluginParamDef("delayBefore", "Delay Before", PluginParamType.Number,
            Category: "Timing", Default: "300", UnitSuffix: "ms",
            Min: 0, Max: 10000, Step: 50, DecimalPlaces: 0),
        new PluginParamDef("dispenseDuration", "Dispense Duration", PluginParamType.Number,
            Category: "Timing", Default: "300", UnitSuffix: "ms",
            Min: 0, Max: 30000, Step: 50, DecimalPlaces: 0),
        new PluginParamDef("delayAfter", "Delay After", PluginParamType.Number,
            Category: "Timing", Default: "300", UnitSuffix: "ms",
            Min: 0, Max: 10000, Step: 50, DecimalPlaces: 0),

        // Options
        new PluginParamDef("skipCamera", "Skip Camera Verification", PluginParamType.Boolean,
            Category: "Options", Default: "false"),
        new PluginParamDef("automatic", "Automatic Mode", PluginParamType.Boolean,
            Category: "Options", Default: "false"),
        new PluginParamDef("reteachZ", "Reteach Z Height", PluginParamType.Boolean,
            Category: "Options", Default: "false"),
    };

    public void BuildSteps(ProcessBuilder builder, IReadOnlyDictionary<string, string> paramValues)
    {
        var parms = new Dictionary<string, string>();

        foreach (var param in Parameters)
        {
            var value = paramValues.GetValueOrDefault(param.Name) ?? param.Default ?? "";
            if (!string.IsNullOrWhiteSpace(value))
                parms[param.Name] = value;
        }

        builder.AddAction("system", "dispense", parms);
    }
}
