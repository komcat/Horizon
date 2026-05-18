using System.Threading;
using Horizon.Pneumatic.Models;

namespace Horizon.Pneumatic.Interfaces;

public interface IPneumaticSlide
{
    PneumaticError Extend();
    PneumaticError Retract();
    PneumaticError ExtendAndWait(int pollIntervalMs = 50, CancellationToken ct = default);
    PneumaticError RetractAndWait(int pollIntervalMs = 50, CancellationToken ct = default);
    PneumaticStatus GetStatus();
    PneumaticPosition GetPosition();
    string SlideName { get; }
}
