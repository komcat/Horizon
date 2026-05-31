using System.Windows.Controls;
using FAAM.UI.Siphog;

namespace FAAM.UI.Views;

/// <summary>
/// Run-page SiPhOG client panel — connect/disconnect to the SiPhOG TCP server, a
/// six-field value table, a throttled data-rate readout, and SET_CURRENT presets.
/// Self-contained: owns its <see cref="SiphogPanelViewModel"/> (and TCP client).
/// </summary>
public partial class SiphogPanel : UserControl
{
    private readonly SiphogPanelViewModel _vm = new();

    public SiphogPanel()
    {
        InitializeComponent();
        DataContext = _vm;
    }

    /// <summary>The shared client — used by the SiPhOG chart so both run off one connection.</summary>
    public SiphogClient Client => _vm.Client;

    /// <summary>Disconnects and tears down the client — call on application shutdown.</summary>
    public void Shutdown() => _vm.Dispose();
}
