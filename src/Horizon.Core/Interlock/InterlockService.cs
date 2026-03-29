using System.Text.Json;
using System.Text.Json.Serialization;
using Horizon.Common;
using Horizon.IO;
using Horizon.IO.Models;
using Horizon.Pneumatic;
using Horizon.Pneumatic.Models;
using Serilog;

namespace Horizon.Core.Interlock;

public sealed class InterlockService
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly PneumaticManager _pneumaticManager;
    private readonly EziIOManager _ioManager;
    private InterlockConfig _config = new();
    private volatile bool _overrideMode;

    public event Action? InterlockStateChanged;
    public event Action<string, string, string>? InterlockBlocked; // device, axis, reason

    public bool Enabled => _config.Enabled;
    public IReadOnlyList<InterlockRule> Rules => _config.Rules;

    public bool OverrideMode
    {
        get => _overrideMode;
        set
        {
            if (_overrideMode == value) return;
            _overrideMode = value;
            if (value)
                Log.Warning("[Interlock] Override mode ENABLED — motion interlocks will be bypassed");
            else
                Log.Information("[Interlock] Override mode disabled");
            InterlockStateChanged?.Invoke();
        }
    }

    public InterlockService(PneumaticManager pneumaticManager, EziIOManager ioManager)
    {
        _pneumaticManager = pneumaticManager;
        _ioManager = ioManager;
    }

    public int LoadConfig(string? configDir = null)
    {
        configDir ??= ConfigLoader.GetConfigDirectory();
        var config = ConfigLoader.LoadJson<InterlockConfig>(configDir, "motion_interlocks");
        if (config == null) return 0;
        _config = config;
        return _config.Rules.Count;
    }

    public void Configure(InterlockConfig config)
    {
        _config = config;
    }

    public void SaveConfig(string? configDir = null)
    {
        configDir ??= ConfigLoader.GetConfigDirectory();
        Directory.CreateDirectory(configDir);
        var path = Path.Combine(configDir, "motion_interlocks.json");
        File.WriteAllText(path, JsonSerializer.Serialize(_config, WriteOptions));
        Log.Information("[Interlock] Config saved to {Path}", path);
    }

    /// <summary>
    /// Core check method. Returns (true, null) if allowed, or (false, reason) if blocked.
    /// </summary>
    public (bool Allowed, string? Reason) CheckMotionAllowed(string device, string axis, MotionSource source = MotionSource.Manual)
    {
        if (!_config.Enabled) return (true, null);

        foreach (var rule in _config.Rules)
        {
            if (!rule.Enabled) continue;
            if (!MatchesLayer(rule, source)) continue;
            if (!MatchesTarget(rule, device, axis)) continue;

            var (satisfied, reason) = EvaluateConditions(rule);
            if (!satisfied)
            {
                if (_overrideMode)
                {
                    Log.Warning("[Interlock] OVERRIDE — {Rule} would block {Device}.{Axis}: {Reason}",
                        rule.Name, device, axis, reason);
                    continue;
                }

                Log.Warning("[Interlock] BLOCKED — {Device}.{Axis} by '{Rule}': {Reason}",
                    device, axis, rule.Name, reason);
                InterlockBlocked?.Invoke(device, axis, $"[{rule.Name}] {reason}");
                return (false, $"[{rule.Name}] {reason}");
            }
        }

        return (true, null);
    }

    /// <summary>
    /// Gets the current status of all rules for UI display.
    /// </summary>
    public List<InterlockRuleStatus> GetAllRuleStatuses()
    {
        var results = new List<InterlockRuleStatus>();
        foreach (var rule in _config.Rules)
        {
            var target = string.Join(", ", rule.TargetDevices);
            target += rule.AxisBlock switch
            {
                AxisBlock.XY => " [X,Y]",
                AxisBlock.Z => " [Z]",
                _ => " [all]"
            };
            target += rule.Layer switch
            {
                EnforcementLayer.ScriptOnly => " (script)",
                EnforcementLayer.JogOnly => " (jog)",
                _ => " (all)"
            };

            if (!rule.Enabled)
            {
                results.Add(new InterlockRuleStatus
                {
                    RuleName = rule.Name,
                    RuleEnabled = false,
                    IsSatisfied = true,
                    TargetDescription = target,
                    BlockReason = "(disabled)"
                });
                continue;
            }

            var (satisfied, reason) = EvaluateConditions(rule);
            results.Add(new InterlockRuleStatus
            {
                RuleName = rule.Name,
                RuleEnabled = true,
                IsSatisfied = satisfied,
                TargetDescription = target,
                BlockReason = satisfied ? "" : reason
            });
        }
        return results;
    }

    public void AddRule(InterlockRule rule)
    {
        _config = _config with { Rules = _config.Rules.Append(rule).ToList() };
        InterlockStateChanged?.Invoke();
    }

    public bool RemoveRule(string name)
    {
        var newRules = _config.Rules.Where(r =>
            !r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (newRules.Count == _config.Rules.Count) return false;
        _config = _config with { Rules = newRules };
        InterlockStateChanged?.Invoke();
        return true;
    }

    // ── Private helpers ─────────────────────────────────────────

    private static bool MatchesLayer(InterlockRule rule, MotionSource source)
    {
        return rule.Layer switch
        {
            EnforcementLayer.All => true,
            EnforcementLayer.ScriptOnly => source is MotionSource.Process or MotionSource.Recipe,
            EnforcementLayer.JogOnly => source is MotionSource.Jog,
            _ => true
        };
    }

    private static bool MatchesTarget(InterlockRule rule, string device, string axis)
    {
        // Check device match
        bool deviceMatch = rule.TargetDevices.Any(d =>
            d.Equals(device, StringComparison.OrdinalIgnoreCase));
        if (!deviceMatch) return false;

        // Check axis block type
        return rule.AxisBlock switch
        {
            AxisBlock.XY => axis.Equals("X", StringComparison.OrdinalIgnoreCase) ||
                            axis.Equals("Y", StringComparison.OrdinalIgnoreCase),
            AxisBlock.Z => axis.Equals("Z", StringComparison.OrdinalIgnoreCase),
            _ => true // AxisBlock.All
        };
    }

    private (bool Satisfied, string Reason) EvaluateConditions(InterlockRule rule)
    {
        foreach (var cond in rule.Conditions)
        {
            var type = cond.Type.ToLowerInvariant();
            bool met;
            string failReason;

            switch (type)
            {
                case "allretracted":
                    met = CheckSlideCondition(cond, PneumaticPosition.Retracted, out failReason);
                    break;
                case "allextended":
                    met = CheckSlideCondition(cond, PneumaticPosition.Extended, out failReason);
                    break;
                case "iopin":
                    met = CheckIOPinCondition(cond, out failReason);
                    break;
                default:
                    failReason = $"Unknown condition type: {cond.Type}";
                    met = false;
                    break;
            }

            if (!met)
                return (false, failReason);
        }
        return (true, "");
    }

    private bool CheckSlideCondition(InterlockCondition cond, PneumaticPosition required, out string failReason)
    {
        // Empty slides list = check ALL configured slides
        var slideNames = cond.Slides.Count > 0 ? cond.Slides : _pneumaticManager.GetAllSlideNames();

        foreach (var slideName in slideNames)
        {
            var slide = _pneumaticManager.GetSlide(slideName);
            if (slide == null)
            {
                failReason = $"Slide '{slideName}' not found";
                return false;
            }

            var pos = slide.GetPosition();
            if (pos != required)
            {
                failReason = $"Slide '{slideName}' is {pos}, requires {required}";
                return false;
            }
        }
        failReason = "";
        return true;
    }

    private bool CheckIOPinCondition(InterlockCondition cond, out string failReason)
    {
        var device = _ioManager.GetDevice(cond.DeviceName);
        if (device == null || !device.IsConnected)
        {
            failReason = $"IO device '{cond.DeviceName}' not connected";
            return false;
        }

        var pin = device.GetPinByName(cond.PinName);
        if (pin == null)
        {
            failReason = $"Pin '{cond.PinName}' not found on '{cond.DeviceName}'";
            return false;
        }

        var err = device.ReadInputs(out uint inputs);
        if (err != IOError.Success)
        {
            failReason = $"Failed to read '{cond.DeviceName}': {err}";
            return false;
        }

        bool state = (inputs & (1u << pin.Value)) != 0;
        if (state != cond.RequiredState)
        {
            var expected = cond.RequiredState ? "HIGH" : "LOW";
            var actual = state ? "HIGH" : "LOW";
            failReason = $"Pin '{cond.DeviceName}:{cond.PinName}' is {actual}, requires {expected}";
            return false;
        }

        failReason = "";
        return true;
    }
}
