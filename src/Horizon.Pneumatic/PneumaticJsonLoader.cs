using Horizon.Common;
using Horizon.IO;
using Horizon.Pneumatic.Models;

namespace Horizon.Pneumatic;

public static class PneumaticJsonLoader
{
    private record PneumaticSlidesFile
    {
        public List<PneumaticConfig> PneumaticSlides { get; init; } = new();
    }

    /// <summary>Reads the pneumatic-slide config, or an empty list if the file is absent.</summary>
    public static IReadOnlyList<PneumaticConfig> Load(string? configDir = null)
    {
        configDir ??= ConfigLoader.GetConfigDirectory();
        return ConfigLoader.LoadJson<PneumaticSlidesFile>(configDir, "pneumatic_slides")?.PneumaticSlides
               ?? new List<PneumaticConfig>();
    }

    /// <summary>Writes the pneumatic-slide config to <c>pneumatic_slides.json</c>.</summary>
    public static void Save(IReadOnlyList<PneumaticConfig> slides, string? configDir = null)
    {
        configDir ??= ConfigLoader.GetConfigDirectory();
        ConfigLoader.SaveJson(configDir, "pneumatic_slides",
            new PneumaticSlidesFile { PneumaticSlides = slides.ToList() });
    }

    public static int LoadSlides(PneumaticManager manager, EziIOManager ioManager, string? configDir = null)
    {
        var slides = Load(configDir);
        foreach (var slideConfig in slides)
            manager.AddSlide(slideConfig, ioManager);
        return slides.Count;
    }
}
