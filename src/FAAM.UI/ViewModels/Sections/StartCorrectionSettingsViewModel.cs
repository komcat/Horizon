using Horizon.Common;
using Horizon.Station.UI.ViewModels.Sections;

namespace FAAM.UI.ViewModels.Sections;

/// <summary>
/// Machine › Settings, FAAM panel: the Run-page Start correction — move gantry-main to the vision
/// facets (visionFacet / visionFAUFacet) and measure U by vision first. Off skips straight to the
/// manual UCorr_P1/P2/F1/F2 capture. Same flag as the Find First Light tab's "Auto-run vision
/// U-correction" (app_settings.json).
/// </summary>
public sealed class StartCorrectionSettingsViewModel : ObservableObject, IHeadSettingsPanel
{
    private readonly AppSettingsStore _store;
    private bool _autoVisionUCorrection;

    public StartCorrectionSettingsViewModel(EngineHost host)
    {
        _store = host.Settings;
        // The Engineer › Find First Light tab also edits this flag — pick up its saves so our Save doesn't revert it.
        _store.Changed += s => AutoVisionUCorrection = s.FindFirstLight.AutoVisionUCorrection;
    }

    public bool AutoVisionUCorrection
    {
        get => _autoVisionUCorrection;
        set => SetField(ref _autoVisionUCorrection, value);
    }

    public string SavedFiles => "app_settings.json";

    public void Load() => AutoVisionUCorrection = _store.Current.FindFirstLight.AutoVisionUCorrection;

    public string? Validate() => null;

    public void Save()
    {
        var current = _store.Current;
        _store.Update(current with { FindFirstLight = current.FindFirstLight with { AutoVisionUCorrection = _autoVisionUCorrection } });
        _store.Save();
    }
}
