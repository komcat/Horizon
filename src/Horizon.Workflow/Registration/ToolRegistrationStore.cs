using Horizon.Common;

namespace Horizon.Workflow.Registration;

/// <summary>
/// Loads / saves the machine-global device registrations
/// (<c>%APPDATA%\Horizon\config\tool_registrations.json</c> — a list keyed by tool
/// device), mirroring <c>CameraNeedleCalibrationStore</c>. A registration describes the
/// physical mount, not a product, so this is not a per-product store.
/// </summary>
public sealed class ToolRegistrationStore
{
    private const string FileName = "tool_registrations";

    private readonly string _dir;
    private readonly object _gate = new();
    private List<ToolRegistration> _all;

    public ToolRegistrationStore(string? configDir = null)
    {
        _dir = configDir ?? ConfigLoader.GetConfigDirectory();
        _all = ConfigLoader.LoadJson<List<ToolRegistration>>(_dir, FileName) ?? new List<ToolRegistration>();
    }

    /// <summary>Every saved registration (snapshot copy).</summary>
    public IReadOnlyList<ToolRegistration> All
    {
        get { lock (_gate) return _all.ToArray(); }
    }

    /// <summary>The registration for a tool device, or null if none has been saved.</summary>
    public ToolRegistration? Get(string toolDevice)
    {
        lock (_gate)
            return _all.FirstOrDefault(r => string.Equals(r.ToolDevice, toolDevice, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Adds or replaces the registration for <paramref name="reg"/>'s tool device and persists.</summary>
    public void Update(ToolRegistration reg)
    {
        lock (_gate)
        {
            _all.RemoveAll(r => string.Equals(r.ToolDevice, reg.ToolDevice, StringComparison.OrdinalIgnoreCase));
            _all.Add(reg);
            ConfigLoader.SaveJson(_dir, FileName, _all);
        }
    }

    /// <summary>Removes a tool device's registration; returns false if there was none.</summary>
    public bool Remove(string toolDevice)
    {
        lock (_gate)
        {
            var n = _all.RemoveAll(r => string.Equals(r.ToolDevice, toolDevice, StringComparison.OrdinalIgnoreCase));
            if (n == 0) return false;
            ConfigLoader.SaveJson(_dir, FileName, _all);
            return true;
        }
    }
}
