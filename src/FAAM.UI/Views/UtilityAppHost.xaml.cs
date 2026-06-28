using System.Windows.Controls;
using FAAM.UI.Apps;
using FAAM.UI.Siphog;
using Horizon.Common;
using Horizon.Workflow;

namespace FAAM.UI.Views;

/// <summary>
/// The Run-page alignment utilities — U correction, FAU pre-align, Hill Climber and the
/// SiPhOG connection panel — laid out directly and always ready to use (U correction +
/// FAU on the top row, Hill Climber + SiPhOG below). The old "phone" springboard (tiles +
/// Back navigation) was dropped per operator feedback, and FAAM does not use the Hexapod
/// Scan app.
///
/// Still implements <see cref="IUtilityAppHost"/> (Phase 2) so <c>app.start</c> /
/// <c>app.stop</c> workflow steps can drive a panel by id. <c>app.open</c> / <c>app.home</c>
/// are no-ops now that every panel is permanently visible.
/// </summary>
public partial class UtilityAppHost : UserControl, IUtilityAppHost
{
    private readonly List<IUtilityApp> _apps = new();
    private SiphogPanel? _siphogPanel;
    private AlignmentPanel? _align;

    public UtilityAppHost()
    {
        InitializeComponent();
    }

    /// <summary>The shared SiPhOG client — used by the Run-page chart + the Layer 2 signal/feedback seams.</summary>
    public SiphogClient SiphogClient =>
        _siphogPanel?.Client ?? throw new InvalidOperationException("Attach(host) must be called first.");

    /// <summary>
    /// Routes the alignment panel's per-point Capture / Apply prompts onto a camera overlay
    /// (the Run-page main feed), so they mirror the panel's inline buttons. Call after
    /// <see cref="Attach"/>.
    /// </summary>
    public void SetCapturePromptSurface(ICapturePromptSurface surface) => _align?.SetCapturePromptSurface(surface);

    /// <summary>
    /// Builds and attaches the four panels into the fixed grid. The SiPhOG panel owns the
    /// shared client (exposed via <see cref="SiphogClient"/> for the chart) and feeds the
    /// Hill Climber. SiPhOG is left disconnected on load — the operator connects it from
    /// its panel, or a Phase 2 command block does.
    /// </summary>
    public void Attach(EngineHost host)
    {
        // U correction + FAU pre-align merged into one panel that chains the two routines.
        var align = new AlignmentPanel();
        align.Attach(host);
        _align = align;

        _siphogPanel = new SiphogPanel();          // self-attached (owns its client)

        var hill = new HillClimbPanel();
        hill.Attach(host, _siphogPanel.Client);

        var autoW = new AutoWPanel();
        autoW.Attach(host, _siphogPanel.Client);

        // After the climber parks at peak it auto-runs the Auto W maximizer once per lifetime
        // (re-armed on the climber's Set Start / Clear Distance).
        hill.SetAutoWPanel(autoW);

        // After FAU pre-align, the alignment panel runs the XY grid signal search and, on a hit,
        // hands off to the hill climber (which then chains Auto-W). Completes the corrections chain.
        align.EnableGridSearch(_siphogPanel.Client, hill);

        AlignHost.Content = align;
        SiphogHost.Content = _siphogPanel;
        HillHost.Content = hill;
        AutoWHost.Content = autoW;

        // Thin adapters keep the id → panel + Start/Stop mapping for the app.* command seam.
        // The combined panel backs the chained "align" id plus the legacy "ucorrection" /
        // "faupre" ids (single-phase starts), so existing app.* steps keep working.
        _apps.Add(new AlignmentApp(align));
        _apps.Add(new UCorrectionApp(align));
        _apps.Add(new FauPreAlignApp(align));
        _apps.Add(new HillClimbApp(hill));
        _apps.Add(new AutoWApp(autoW));
    }

    // --- IUtilityAppHost (Layer 2 → Layer 3 command seam, driven by app.* steps) ---
    // Every panel is always visible, so open/home are no-ops; start/stop resolve a panel
    // by id. StepDispatcher may call these off the UI thread, so each marshals onto it.

    public Result Open(string appId) => OnUi(() =>
        Find(appId) == null
            ? Result.Fail(ErrorCode.InvalidParameter, $"app.open: unknown app '{appId}'.")
            : Result.Ok());

    public Result Home() => Result.Ok();

    public Task<Result> StartAsync(string appId, CancellationToken ct) => Task.FromResult(OnUi(() =>
    {
        var app = Find(appId);
        if (app == null) return Result.Fail(ErrorCode.InvalidParameter, $"app.start: unknown app '{appId}'.");
        _ = app.StartAsync(ct);        // begin routine — fire (does not await completion)
        return Result.Ok();
    }));

    public Task<Result> StopAsync(string appId) => Task.FromResult(OnUi(() =>
    {
        var app = Find(appId);
        if (app == null) return Result.Fail(ErrorCode.InvalidParameter, $"app.stop: unknown app '{appId}'.");
        _ = app.StopAsync();
        return Result.Ok();
    }));

    private IUtilityApp? Find(string id)
        => _apps.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Runs <paramref name="fn"/> on the UI thread and returns its result.</summary>
    private Result OnUi(Func<Result> fn)
        => Dispatcher.CheckAccess() ? fn() : Dispatcher.Invoke(fn);
}
