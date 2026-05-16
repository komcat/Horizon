namespace Horizon.Core.Interlock;

// ── Enums ───────────────────────────────────────────────────────

/// <summary>Which axes to block when condition is not met.</summary>
public enum AxisBlock
{
    All,
    XY,
    Z
}

/// <summary>Which execution layers the rule applies to.</summary>
public enum EnforcementLayer
{
    /// <summary>Block in all contexts (jog, process, recipe).</summary>
    All,
    /// <summary>Block only in process programs and recipe scripts, not jog.</summary>
    ScriptOnly,
    /// <summary>Block only in jog/manual motion.</summary>
    JogOnly
}

/// <summary>Motion source context passed to interlock checks.</summary>
public enum MotionSource
{
    Jog,
    Process,
    Recipe,
    Manual
}

// ── Config models ───────────────────────────────────────────────

public record InterlockConfig
{
    public bool Enabled { get; init; } = true;
    public List<InterlockRule> Rules { get; init; } = new();
}

public record InterlockRule
{
    public string Name { get; init; } = string.Empty;
    public bool Enabled { get; init; } = true;
    /// <summary>Motion device names this rule applies to (case-insensitive).</summary>
    public List<string> TargetDevices { get; init; } = new();
    /// <summary>Which axes to block. Default: All.</summary>
    public AxisBlock AxisBlock { get; init; } = AxisBlock.All;
    /// <summary>Which execution layers enforce this rule. Default: ScriptOnly.</summary>
    public EnforcementLayer Layer { get; init; } = EnforcementLayer.ScriptOnly;
    public List<InterlockCondition> Conditions { get; init; } = new();

    // Legacy support: if TargetAxes was used in old configs
    public List<string> TargetAxes { get; init; } = new();
}

public record InterlockCondition
{
    /// <summary>"allRetracted", "allExtended", or "ioPin"</summary>
    public string Type { get; init; } = string.Empty;
    /// <summary>For slide conditions: slide names. Empty = all configured slides.</summary>
    public List<string> Slides { get; init; } = new();
    /// <summary>For ioPin condition: IO device name.</summary>
    public string DeviceName { get; init; } = string.Empty;
    /// <summary>For ioPin condition: input pin name.</summary>
    public string PinName { get; init; } = string.Empty;
    /// <summary>For ioPin condition: required pin state (true=HIGH, false=LOW).</summary>
    public bool RequiredState { get; init; }
}

// ── Status models (for UI) ──────────────────────────────────────

public record InterlockRuleStatus
{
    public string RuleName { get; init; } = string.Empty;
    public bool RuleEnabled { get; init; }
    public bool IsSatisfied { get; init; }
    public string BlockReason { get; init; } = string.Empty;
    public string TargetDescription { get; init; } = string.Empty;
}
