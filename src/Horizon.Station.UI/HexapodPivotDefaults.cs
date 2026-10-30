using System;
using System.Collections.Generic;
using System.Linq;
using Horizon.Common;
using Horizon.Engine;

namespace Horizon.Station.UI;

/// <summary>One hexapod's configured pivot point (R, S, T), as stored in JSON.</summary>
public sealed class HexapodPivotSetting
{
    public double R { get; set; } = HexapodPivotDefaults.DefaultR;
    public double S { get; set; } = HexapodPivotDefaults.DefaultS;
    public double T { get; set; } = HexapodPivotDefaults.DefaultT;

    public PivotPoint ToPivot() => new(R, S, T);
}

/// <summary>
/// The pivot point (R, S, T) FAAM keeps on each PI hexapod. The C-887 resets its pivot
/// to 0,0,0 at power-down, so <see cref="EngineHost"/> reads it after connect and writes
/// this value back when the controller's differs. Authored on the Machine page's Settings
/// section; stored per device in <c>faam_hexapod_pivot.json</c>:
/// <c>{ "hex-right": { "r": -14.52, "s": 0, "t": 111 } }</c>.
/// A device with no entry uses R=-14.52, S=0, T=111.
/// </summary>
public static class HexapodPivotDefaults
{
    public const double DefaultR = -14.52;
    public const double DefaultS = 0.0;
    public const double DefaultT = 111.0;
    private const string FileName = "faam_hexapod_pivot";

    /// <summary>The configured pivot for one device (the built-in default when none is saved).</summary>
    public static PivotPoint Get(string device) => Lookup(LoadAll(), device).ToPivot();

    /// <summary>The setting for one device within an already-loaded set (default when absent / invalid).</summary>
    public static HexapodPivotSetting Lookup(Dictionary<string, HexapodPivotSetting> all, string device)
    {
        var match = all.FirstOrDefault(p => string.Equals(p.Key, device, StringComparison.OrdinalIgnoreCase));
        return match.Value != null && IsValid(match.Value) ? match.Value : new HexapodPivotSetting();
    }

    /// <summary>Every saved entry (empty when the file is missing or unreadable).</summary>
    public static Dictionary<string, HexapodPivotSetting> LoadAll()
    {
        try
        {
            return ConfigLoader.LoadJson<Dictionary<string, HexapodPivotSetting>>(
                       ConfigLoader.GetConfigDirectory(), FileName)
                   ?? new Dictionary<string, HexapodPivotSetting>();
        }
        catch
        {
            return new Dictionary<string, HexapodPivotSetting>();   // unreadable → defaults
        }
    }

    /// <summary>Overwrites the file with <paramref name="all"/>. Throws on I/O failure.</summary>
    public static void SaveAll(Dictionary<string, HexapodPivotSetting> all)
        => ConfigLoader.SaveJson(ConfigLoader.GetConfigDirectory(), FileName, all);

    /// <summary>All three coordinates finite.</summary>
    public static bool IsValid(HexapodPivotSetting p)
        => double.IsFinite(p.R) && double.IsFinite(p.S) && double.IsFinite(p.T);
}
