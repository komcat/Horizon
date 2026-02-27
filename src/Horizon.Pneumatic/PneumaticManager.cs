using Horizon.IO;
using Horizon.Pneumatic.Interfaces;
using Horizon.Pneumatic.Models;

namespace Horizon.Pneumatic;

public sealed class PneumaticManager
{
    private readonly Dictionary<string, IPneumaticSlide> _slides = new(StringComparer.OrdinalIgnoreCase);

    public void AddSlide(PneumaticConfig config, EziIOManager ioManager)
    {
        _slides[config.Name] = new PneumaticSlide(config, ioManager);
    }

    public PneumaticError Extend(string slideName)
    {
        if (!_slides.TryGetValue(slideName, out var slide))
            return PneumaticError.SlideNotFound;
        return slide.Extend();
    }

    public PneumaticError Retract(string slideName)
    {
        if (!_slides.TryGetValue(slideName, out var slide))
            return PneumaticError.SlideNotFound;
        return slide.Retract();
    }

    public PneumaticError ExtendAndWait(string slideName, int pollIntervalMs = 50)
    {
        if (!_slides.TryGetValue(slideName, out var slide))
            return PneumaticError.SlideNotFound;
        return slide.ExtendAndWait(pollIntervalMs);
    }

    public PneumaticError RetractAndWait(string slideName, int pollIntervalMs = 50)
    {
        if (!_slides.TryGetValue(slideName, out var slide))
            return PneumaticError.SlideNotFound;
        return slide.RetractAndWait(pollIntervalMs);
    }

    public IPneumaticSlide? GetSlide(string name)
    {
        _slides.TryGetValue(name, out var slide);
        return slide;
    }

    public List<string> GetAllSlideNames() => _slides.Keys.ToList();

    public void StopAll()
    {
        foreach (var slide in _slides.Values)
            slide.Retract();
    }
}
