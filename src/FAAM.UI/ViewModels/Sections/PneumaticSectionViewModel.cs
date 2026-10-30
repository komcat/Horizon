using System.Collections.ObjectModel;

namespace FAAM.UI.ViewModels.Sections;

/// <summary>
/// The Machine page's Pneumatic section — a flat list of slides, each with live
/// position / sensor state and Extend / Retract controls. Slides are top-level
/// (no device picker). Refreshed from the engine's cached state by the shared
/// sampler each tick.
/// </summary>
public sealed class PneumaticSectionViewModel : ObservableObject
{
    private readonly EngineHost _host;

    public PneumaticSectionViewModel(EngineHost host)
    {
        _host = host;
        foreach (var name in host.Engine.Pneumatic.SlideNames)
            Slides.Add(new PneumaticSlideViewModel(host, name));
    }

    /// <summary>Label shown in the Machine-page section rail.</summary>
    public string Title => "Pneumatic";

    /// <summary>One entry per configured slide.</summary>
    public ObservableCollection<PneumaticSlideViewModel> Slides { get; } = new();

    /// <summary>Refreshes every slide from the engine's cached state — called each sampler tick.</summary>
    public void Refresh()
    {
        foreach (var slide in Slides)
            slide.Apply(_host.Engine.Pneumatic.GetState(slide.Name));
    }
}
