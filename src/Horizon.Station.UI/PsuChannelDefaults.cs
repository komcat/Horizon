using System;
using System.Collections.Generic;
using System.Linq;
using Horizon.Common;

namespace Horizon.Station.UI;

/// <summary>One channel's Run-page power-up setpoints.</summary>
public sealed class PsuChannelSetpoint
{
    public double Volts { get; set; } = PsuChannelDefaults.DefaultVolts;
    public double Amps { get; set; } = PsuChannelDefaults.DefaultAmps;
}

/// <summary>
/// The voltage + current-limit the Run-page PSU panel applies when its ON button is
/// pressed (verified power-up, <see cref="Horizon.Workflow.PsuPowerUp"/>). Authored on the
/// Machine page's Settings section; stored per supply, per channel in
/// <c>faam_psu_defaults.json</c>: <c>{ "&lt;device&gt;": { "1": { "volts": 3.3, "amps": 1 }, ... } }</c>.
/// A channel with no entry uses <see cref="DefaultVolts"/> / <see cref="DefaultAmps"/>.
/// Read fresh at every lookup, so a Settings save takes effect on the next ON click.
/// </summary>
public static class PsuChannelDefaults
{
    public const double DefaultVolts = 3.3;
    public const double DefaultAmps = 1.0;
    private const string FileName = "faam_psu_defaults";

    /// <summary>The setpoints for one channel (the 3.3 V / 1.0 A default when none is saved).</summary>
    public static PsuChannelSetpoint Get(string device, int channel) => Lookup(LoadAll(), device, channel);

    /// <summary>The setpoints for one channel within an already-loaded set (default when absent / invalid).</summary>
    public static PsuChannelSetpoint Lookup(
        Dictionary<string, Dictionary<string, PsuChannelSetpoint>> all, string device, int channel)
    {
        var match = all.FirstOrDefault(p => string.Equals(p.Key, device, StringComparison.OrdinalIgnoreCase));
        return match.Value != null && match.Value.TryGetValue(channel.ToString(), out var sp) && IsValid(sp)
            ? sp
            : new PsuChannelSetpoint();
    }

    /// <summary>Every saved entry (empty when the file is missing or unreadable).</summary>
    public static Dictionary<string, Dictionary<string, PsuChannelSetpoint>> LoadAll()
    {
        try
        {
            return ConfigLoader.LoadJson<Dictionary<string, Dictionary<string, PsuChannelSetpoint>>>(
                       ConfigLoader.GetConfigDirectory(), FileName)
                   ?? new Dictionary<string, Dictionary<string, PsuChannelSetpoint>>();
        }
        catch
        {
            return new Dictionary<string, Dictionary<string, PsuChannelSetpoint>>();   // unreadable → defaults
        }
    }

    /// <summary>Overwrites the file with <paramref name="all"/>. Throws on I/O failure.</summary>
    public static void SaveAll(Dictionary<string, Dictionary<string, PsuChannelSetpoint>> all)
        => ConfigLoader.SaveJson(ConfigLoader.GetConfigDirectory(), FileName, all);

    /// <summary>Finite, non-negative voltage and a positive current limit.</summary>
    public static bool IsValid(PsuChannelSetpoint sp)
        => double.IsFinite(sp.Volts) && sp.Volts >= 0 && double.IsFinite(sp.Amps) && sp.Amps > 0;
}
