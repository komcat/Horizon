using Horizon.Common;

namespace Horizon.Engine;

/// <summary>One device's saved adaptive-profile ratios (the file's per-device entry).</summary>
internal sealed class SavedProfileRatios
{
    public double AccPerVelocity { get; set; }
    public double JerkPerAcc { get; set; }
    public double KdecPerAcc { get; set; }
}

/// <summary>The whole <c>acs_profile_ratios.json</c> file: device name → ratios.</summary>
internal sealed class ProfileRatiosFile
{
    public Dictionary<string, SavedProfileRatios> Devices { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Machine-global ACS adaptive-profile ratios in <c>%APPDATA%\Horizon\config\acs_profile_ratios.json</c>,
/// keyed by motion device. A device with no (or an invalid) entry uses <see cref="MotionProfileRatios.Default"/>.
/// </summary>
internal sealed class MotionProfileRatioStore
{
    private const string FileName = "acs_profile_ratios";

    private readonly string _configDirectory;
    private readonly object _gate = new();
    private readonly Dictionary<string, MotionProfileRatios> _ratios = new(StringComparer.OrdinalIgnoreCase);

    public MotionProfileRatioStore(string configDirectory, Action<LogLevel, string> log)
    {
        _configDirectory = configDirectory;
        if (string.IsNullOrEmpty(configDirectory)) return;
        try
        {
            var file = ConfigLoader.LoadJson<ProfileRatiosFile>(configDirectory, FileName);
            foreach (var (device, saved) in file?.Devices ?? new Dictionary<string, SavedProfileRatios>())
            {
                if (saved == null) continue;
                var r = new MotionProfileRatios(saved.AccPerVelocity, saved.JerkPerAcc, saved.KdecPerAcc);
                var why = r.Validate();
                if (why == null) _ratios[device] = r;
                else log(LogLevel.Warning, $"{FileName}.json: '{device}' ignored — {why} Using defaults.");
            }
        }
        catch (Exception ex)
        {
            log(LogLevel.Warning, $"{FileName}.json unreadable ({ex.Message}) — using default ratios.");
        }
    }

    /// <summary>The saved ratios for <paramref name="device"/>, or the defaults.</summary>
    public MotionProfileRatios Get(string device)
    {
        lock (_gate)
            return _ratios.TryGetValue(device, out var r) ? r : MotionProfileRatios.Default;
    }

    /// <summary>Stores and writes the ratios for <paramref name="device"/>. Throws on I/O failure (nothing changes then).</summary>
    public void Save(string device, MotionProfileRatios ratios)
    {
        lock (_gate)
        {
            var file = new ProfileRatiosFile();
            foreach (var (name, r) in _ratios)
                file.Devices[name] = ToSaved(r);
            file.Devices[device] = ToSaved(ratios);
            ConfigLoader.SaveJson(_configDirectory, FileName, file);
            _ratios[device] = ratios;
        }
    }

    private static SavedProfileRatios ToSaved(MotionProfileRatios r)
        => new() { AccPerVelocity = r.AccPerVelocity, JerkPerAcc = r.JerkPerAcc, KdecPerAcc = r.KdecPerAcc };
}
