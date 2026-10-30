using DLAM.UI.ViewModels;
using DLAM.UI.ViewModels.Sections;
using DLAM.UI.Views;

namespace DLAM.UI;

/// <summary>
/// The double-lens active alignment head: its Run page (deck steps + the two-lens alignment band
/// + Keithley readouts), the generic DUT Data page, the FireDog-port Engineer blocks (XCAN, climb,
/// lift-off, map path, waits) and the alignment band's settings.
/// </summary>
public sealed class DlamHead : IStationHead
{
    public string DutProcessName => "Double-lens alignment";

    public IReadOnlyList<OpSpec> Ops => DlamOps.All;

    public IReadOnlyDictionary<string, OpHelp> OpHelp => DlamOpHelp.ByKey;

    public IReadOnlyList<IHeadSettingsPanel> CreateSettingsPanels(EngineHost host)
        => new IHeadSettingsPanel[] { new AlignBandSettingsViewModel() };

    public IReadOnlyList<EngineerTab> CreateEngineerTabs(EngineHost host) => Array.Empty<EngineerTab>();

    public string DefaultProductName => "DLAM";

    /// <summary>Nothing to migrate: DLAM products are converted from FireDog separately (docs/dlam-ui.md §3).</summary>
    public void OnProductReady(EngineHost host)
    {
        // No legacy DLAM data inside Horizon.
    }

    public IRunPage CreateRunPage(EngineHost host) => new DlamRunView(host);

    public IStationPage CreateDutDataPage(EngineHost host) => new DutRecordsView(host);
}
