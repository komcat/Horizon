using Horizon.Station.UI.Process;

namespace Horizon.Station.UI.Views;

/// <summary>
/// A page the station shell hosts: <see cref="Activate"/> when it is shown (start its camera
/// pipeline, samplers, timers), <see cref="Deactivate"/> when the shell leaves it or shuts down
/// (idempotent — the shell may call it on a page that is not showing). Implemented by a
/// <c>UserControl</c>; the shell puts the instance itself in its page host.
/// </summary>
public interface IStationPage
{
    void Activate();

    void Deactivate();
}

/// <summary>
/// The head's production page (FAAM: FAU attach; DLAM: double-lens alignment). The shell drives
/// the serial-bracketed DUT run in its header and shows the page's step progress there.
/// </summary>
public interface IRunPage : IStationPage
{
    /// <summary>Step progress while a Run-page step executes (the header's progress bar).</summary>
    event Action<RunProgressSnapshot>? ProgressChanged;

    /// <summary>The operator asked to open a program in the Engineer editor (program name).</summary>
    event Action<string>? EditProgramRequested;

    /// <summary>A DUT run started (header Start DUT) — start the page's process timer.</summary>
    void StartProcessTimer();

    /// <summary>The DUT run ended (Pass / Fail) — stop the process timer.</summary>
    void StopProcessTimer();
}
