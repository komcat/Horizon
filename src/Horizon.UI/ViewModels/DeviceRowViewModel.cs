using Horizon.Engine;
using Horizon.UI.Mvvm;

namespace Horizon.UI.ViewModels;

/// <summary>
/// One row of the hardware status dashboard. A long-lived object: the sampler
/// updates <see cref="IsConnected"/> / <see cref="Detail"/> in place each tick
/// rather than rebuilding the collection, so selection and scroll position
/// survive and the grid does not flicker.
/// </summary>
public sealed class DeviceRowViewModel : ObservableObject
{
    private bool _isConnected;
    private string _detail = string.Empty;

    public DeviceRowViewModel(DeviceStatus status)
    {
        Domain = status.Domain;
        Name = status.Name;
        Key = $"{status.Domain}/{status.Name}";
        Update(status);
    }

    /// <summary>Stable identity — domain + device name.</summary>
    public string Key { get; }

    public string Domain { get; }

    public string Name { get; }

    public bool IsConnected
    {
        get => _isConnected;
        private set => SetField(ref _isConnected, value);
    }

    public string Detail
    {
        get => _detail;
        private set => SetField(ref _detail, value);
    }

    /// <summary>Applies a fresh snapshot to this row.</summary>
    public void Update(DeviceStatus status)
    {
        IsConnected = status.IsConnected;
        Detail = status.Detail;
    }
}
