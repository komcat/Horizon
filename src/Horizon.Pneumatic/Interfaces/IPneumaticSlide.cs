using Horizon.Pneumatic.Models;

namespace Horizon.Pneumatic.Interfaces;

public interface IPneumaticSlide
{
    PneumaticError Extend();
    PneumaticError Retract();
    PneumaticError ExtendAndWait(int pollIntervalMs = 50);
    PneumaticError RetractAndWait(int pollIntervalMs = 50);
    PneumaticStatus GetStatus();
    PneumaticPosition GetPosition();
    string SlideName { get; }
}
