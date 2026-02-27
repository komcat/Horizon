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

    public static int LoadSlides(PneumaticManager manager, EziIOManager ioManager, string? configDir = null)
    {
        configDir ??= ConfigLoader.GetConfigDirectory();
        var config = ConfigLoader.LoadJson<PneumaticSlidesFile>(configDir, "pneumatic_slides");
        if (config?.PneumaticSlides == null) return 0;

        int count = 0;
        foreach (var slideConfig in config.PneumaticSlides)
        {
            manager.AddSlide(slideConfig, ioManager);
            count++;
        }
        return count;
    }
}
