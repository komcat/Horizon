using Horizon.Station.UI.ViewModels;
using Horizon.Station.UI.ViewModels.Sections;
using Horizon.Station.UI.Views;

namespace Horizon.Station.UI;

/// <summary>
/// What a head supplies to the shared station shell: its own pages and process identity.
/// Everything else (Home, Machine, Engineer, Repository, OEE, …) is the shared station UI.
/// A head passes its implementation to <c>new EngineHost(head)</c> (docs/station-ui-extraction.md).
/// </summary>
public interface IStationHead
{
    /// <summary>The process name stamped on an interactive DUT run (Run-page Start DUT).</summary>
    string DutProcessName { get; }

    /// <summary>Builds the head's Run page — created once, when the shell is.</summary>
    IRunPage CreateRunPage(EngineHost host);

    /// <summary>Builds the head's DUT Data page — created once, when the shell is.</summary>
    IStationPage CreateDutDataPage(EngineHost host);

    /// <summary>The head's own Engineer blocks, added after the generic ones (<see cref="OpCatalog.Register"/>).</summary>
    IReadOnlyList<OpSpec> Ops { get; }

    /// <summary>Help for <see cref="Ops"/>, keyed by op (<see cref="OpCatalogHelp.Register"/>).</summary>
    IReadOnlyDictionary<string, OpHelp> OpHelp { get; }

    /// <summary>The head's settings groups for Machine › Settings (each type needs a DataTemplate in the head's App.xaml).</summary>
    IReadOnlyList<IHeadSettingsPanel> CreateSettingsPanels(EngineHost host);

    /// <summary>The head's own Engineer-page tabs, shown after the shared ones.</summary>
    IReadOnlyList<EngineerTab> CreateEngineerTabs(EngineHost host);

    /// <summary>The product the station creates (if missing) and makes active at startup.</summary>
    string DefaultProductName { get; }

    /// <summary>
    /// Runs once at startup after the default product is loaded — e.g. a one-time import of the
    /// head's legacy data into the product (FAAM: faam_teach_points.json). Must be idempotent.
    /// </summary>
    void OnProductReady(EngineHost host);
}
