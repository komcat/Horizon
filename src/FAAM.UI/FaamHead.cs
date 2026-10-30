using FAAM.UI.Process;
using FAAM.UI.ViewModels;
using FAAM.UI.ViewModels.Sections;
using FAAM.UI.Views;
using Horizon.Engine;
using Horizon.Station.UI.ViewModels.Sections;
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

    public IReadOnlyList<EngineerTab> CreateEngineerTabs(EngineHost host) => new[]
    {
        new EngineerTab("Find First Light", new FindFirstLightSettingsView(host)),
        new EngineerTab("Testing Ground", new TestingGroundView(host)),   // scratch area: the spiral search
    };

    public string DefaultProductName => "FAAM";

    /// <summary>Legacy teach points (gantry-main) the Run page used before products existed.</summary>
    private const string TeachDevice = "gantry-main";

    /// <summary>
    /// One-time, idempotent import of legacy <c>faam_teach_points.json</c> points that are not
    /// already present in the product — an existing point is never clobbered.
    /// </summary>
    public void OnProductReady(EngineHost host)
    {
        var imported = 0;
        foreach (var (name, pose) in new TeachPointStore().All)
        {
            if (host.Process.GetPosition(TeachDevice, name) != null)
                continue;   // already migrated / edited — never clobber
            host.Process.SetPosition(TeachDevice, name, pose, velocity: 0);
            imported++;
        }
        if (imported > 0)
            host.Log.Add(new LogEntry(LogLevel.Info, DateTimeOffset.Now, "FAAM",
                $"Migrated {imported} legacy teach point(s) into product '{host.Process.ActiveProduct}'."));
    }

    public IRunPage CreateRunPage(EngineHost host) => new RunView(host);

    public IStationPage CreateDutDataPage(EngineHost host)
    {
        var page = new DutDataView();
        page.Attach(host);
        return page;
    }
}
