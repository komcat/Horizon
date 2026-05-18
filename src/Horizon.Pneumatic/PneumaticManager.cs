using Horizon.IO;
using Horizon.Pneumatic.Interfaces;
using Horizon.Pneumatic.Models;
using Serilog;

namespace Horizon.Pneumatic;

public sealed class PneumaticManager
{
    private readonly Dictionary<string, IPneumaticSlide> _slides = new(StringComparer.OrdinalIgnoreCase);

    public void AddSlide(PneumaticConfig config, EziIOManager ioManager)
    {
        _slides[config.Name] = new PneumaticSlide(config, ioManager);
        Log.Information("[Pneumatic] Slide added: '{SlideName}'", config.Name);
    }

    public PneumaticError Extend(string slideName)
    {
        Log.Information("[Pneumatic] Extend requested: '{SlideName}'", slideName);
        if (!_slides.TryGetValue(slideName, out var slide))
        {
            Log.Warning("[Pneumatic] Extend failed — slide not found: '{SlideName}'", slideName);
            return PneumaticError.SlideNotFound;
        }
        var result = slide.Extend();
        if (result != PneumaticError.Success)
            Log.Error("[Pneumatic] Extend error: '{SlideName}' -> {Result}", slideName, result);
        else
            Log.Information("[Pneumatic] Extend succeeded: '{SlideName}'", slideName);
        return result;
    }

    public PneumaticError Retract(string slideName)
    {
        Log.Information("[Pneumatic] Retract requested: '{SlideName}'", slideName);
        if (!_slides.TryGetValue(slideName, out var slide))
        {
            Log.Warning("[Pneumatic] Retract failed — slide not found: '{SlideName}'", slideName);
            return PneumaticError.SlideNotFound;
        }
        var result = slide.Retract();
        if (result != PneumaticError.Success)
            Log.Error("[Pneumatic] Retract error: '{SlideName}' -> {Result}", slideName, result);
        else
            Log.Information("[Pneumatic] Retract succeeded: '{SlideName}'", slideName);
        return result;
    }

    public PneumaticError ExtendAndWait(string slideName, int pollIntervalMs = 50, CancellationToken ct = default)
    {
        Log.Information("[Pneumatic] ExtendAndWait requested: '{SlideName}' (pollInterval={PollIntervalMs}ms)", slideName, pollIntervalMs);
        if (!_slides.TryGetValue(slideName, out var slide))
        {
            Log.Warning("[Pneumatic] ExtendAndWait failed — slide not found: '{SlideName}'", slideName);
            return PneumaticError.SlideNotFound;
        }
        var result = slide.ExtendAndWait(pollIntervalMs, ct);
        if (result != PneumaticError.Success)
            Log.Error("[Pneumatic] ExtendAndWait error: '{SlideName}' -> {Result}", slideName, result);
        else
            Log.Information("[Pneumatic] ExtendAndWait succeeded: '{SlideName}'", slideName);
        return result;
    }

    public PneumaticError RetractAndWait(string slideName, int pollIntervalMs = 50, CancellationToken ct = default)
    {
        Log.Information("[Pneumatic] RetractAndWait requested: '{SlideName}' (pollInterval={PollIntervalMs}ms)", slideName, pollIntervalMs);
        if (!_slides.TryGetValue(slideName, out var slide))
        {
            Log.Warning("[Pneumatic] RetractAndWait failed — slide not found: '{SlideName}'", slideName);
            return PneumaticError.SlideNotFound;
        }
        var result = slide.RetractAndWait(pollIntervalMs, ct);
        if (result != PneumaticError.Success)
            Log.Error("[Pneumatic] RetractAndWait error: '{SlideName}' -> {Result}", slideName, result);
        else
            Log.Information("[Pneumatic] RetractAndWait succeeded: '{SlideName}'", slideName);
        return result;
    }

    public IPneumaticSlide? GetSlide(string name)
    {
        _slides.TryGetValue(name, out var slide);
        return slide;
    }

    public List<string> GetAllSlideNames() => _slides.Keys.ToList();

    public void StopAll()
    {
        Log.Information("[Pneumatic] StopAll — retracting {Count} slide(s)", _slides.Count);
        foreach (var slide in _slides.Values)
            slide.Retract();
    }
}
