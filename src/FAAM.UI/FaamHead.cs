using FAAM.UI.ViewModels;
using FAAM.UI.ViewModels.Sections;
using Horizon.Station.UI.ViewModels.Sections;
using FAAM.UI.Views;
using Horizon.Station.UI.Views;

namespace FAAM.UI;

/// <summary>The FAU-attach head: its Run page (FAU alignment, SiPhOG), its DUT Data page (UV + SiPhOG snapshots) and its FAU / SiPhOG Engineer blocks.</summary>
public sealed class FaamHead : IStationHead
{
    public string DutProcessName => "FAU attach";

    public IReadOnlyList<OpSpec> Ops => FaamOps.All;

    public IReadOnlyDictionary<string, OpHelp> OpHelp => FaamOpHelp.ByKey;

    public IReadOnlyList<IHeadSettingsPanel> CreateSettingsPanels(EngineHost host)
        => new IHeadSettingsPanel[] { new StartCorrectionSettingsViewModel(host), new ScanPanelSettingsViewModel(host) };

    public IRunPage CreateRunPage(EngineHost host) => new RunView(host);

    public IStationPage CreateDutDataPage(EngineHost host)
    {
        var page = new DutDataView();
        page.Attach(host);
        return page;
    }
}
