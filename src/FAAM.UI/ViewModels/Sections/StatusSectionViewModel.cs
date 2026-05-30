using System.Collections.ObjectModel;
using Horizon.Engine;

namespace FAAM.UI.ViewModels.Sections;

/// <summary>
/// The Machine page's Status section — the hardware status dashboard. Rows are
/// long-lived and updated in place from each engine snapshot (no per-tick
/// rebuild), so selection and scroll position survive and the grid never
/// flickers.
/// </summary>
public sealed class StatusSectionViewModel
{
    private readonly Dictionary<string, DeviceRowViewModel> _rowIndex = new();

    /// <summary>Label shown in the Machine-page section rail.</summary>
    public string Title => "Status";

    /// <summary>One row per engine device.</summary>
    public ObservableCollection<DeviceRowViewModel> Devices { get; } = new();

    /// <summary>Reconciles the device rows against a fresh engine snapshot.</summary>
    public void Apply(HardwareStatusSnapshot snapshot)
    {
        var seen = new HashSet<string>(snapshot.Devices.Count);

        foreach (var status in snapshot.Devices)
        {
            var key = $"{status.Domain}/{status.Name}";
            seen.Add(key);

            if (_rowIndex.TryGetValue(key, out var row))
            {
                row.Update(status);
            }
            else
            {
                row = new DeviceRowViewModel(status);
                _rowIndex[key] = row;
                Devices.Add(row);
            }
        }

        for (var i = Devices.Count - 1; i >= 0; i--)
        {
            if (seen.Contains(Devices[i].Key))
                continue;
            _rowIndex.Remove(Devices[i].Key);
            Devices.RemoveAt(i);
        }
    }
}
