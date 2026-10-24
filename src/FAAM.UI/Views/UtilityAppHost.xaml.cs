using System.Windows.Controls;
using FAAM.UI.Apps;
using FAAM.UI.Siphog;
using Horizon.Common;
using Horizon.Workflow;

namespace FAAM.UI.Views;

/// <summary>
/// The Run-page alignment utilities — the combined U correction / FAU pre-align panel, the
/// SiPhOG connection panel and the Scanning panel — laid out directly and always ready to use.
/// The Raster Climb and Auto W panels are DEPRECATED (do not use) and no longer shown; their
/// app ids report as deprecated. The old "phone" springboard (tiles +
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
    /// Builds and attaches the panels into the fixed grid. The SiPhOG panel owns the
    /// shared client (exposed via <see cref="SiphogClient"/> for the chart) and feeds the
    /// grid search. SiPhOG is left disconnected on load — the operator connects it from
    /// its panel, or a Phase 2 command block does.
    /// </summary>
    public void Attach(EngineHost host)
    {
        // U correction + FAU pre-align merged into one panel that chains the two routines.
        var align = new AlignmentPanel();
        align.Attach(host);
        _align = align;

        _siphogPanel = new SiphogPanel();          // self-attached (owns its client)

        // The Raster Climb + Auto W panels are DEPRECATED (do not use) and no longer built here —
        // the Scanning panel's script presets (FAU_PowerClimb) replace them. After FAU pre-align
        // the alignment panel still runs the XY grid signal search; with no climb to hand off to,
        // it stops at the found signal.
        align.EnableGridSearch(_siphogPanel.Client);

        AlignHost.Content = align;
        SiphogHost.Content = _siphogPanel;

        // Saved hex-right poses + assignable .moon scripts (faam_scan_panel.json).
        var scan = new ScanningPanel();
        scan.Attach(host);
        ScanHost.Content = scan;

        // Thin adapters keep the id → panel + Start/Stop mapping for the app.* command seam.
        // The combined panel backs the chained "align" id plus the legacy "ucorrection" /
        // "faupre" ids (single-phase starts), so existing app.* steps keep working.
        _apps.Add(new AlignmentApp(align));
        _apps.Add(new UCorrectionApp(align));
        _apps.Add(new FauPreAlignApp(align));
    }

    // App ids of the removed (DEPRECATED) Raster Climb / Auto W panels — an app.* step naming one
    // fails with this explanation instead of a bare "unknown app".
    private static readonly HashSet<string> DeprecatedIds = new(StringComparer.OrdinalIgnoreCase) { "hillclimb", "autow" };

    private static Result Unknown(string op, string appId)
        => Result.Fail(ErrorCode.InvalidParameter, DeprecatedIds.Contains(appId)
            ? $"{op}: '{appId}' (Raster Climb / Auto W) is deprecated and removed from the Run page — do not use; run a Scanning script preset (e.g. FAU_PowerClimb) instead."
            : $"{op}: unknown app '{appId}'.");

    // --- IUtilityAppHost (Layer 2 → Layer 3 command seam, driven by app.* steps) ---
    // Every panel is always visible, so open/home are no-ops; start/stop resolve a panel
    // by id. StepDispatcher may call these off the UI thread, so each marshals onto it.

    public Result Open(string appId) => OnUi(() =>
        Find(appId) == null ? Unknown("app.open", appId) : Result.Ok());

    public Result Home() => Result.Ok();

    public Task<Result> StartAsync(string appId, CancellationToken ct) => Task.FromResult(OnUi(() =>
    {
        var app = Find(appId);
        if (app == null) return Unknown("app.start", appId);
        _ = app.StartAsync(ct);        // begin routine — fire (does not await completion)
        return Result.Ok();
    }));

    public Task<Result> StopAsync(string appId) => Task.FromResult(OnUi(() =>
    {
        var app = Find(appId);
        if (app == null) return Unknown("app.stop", appId);
        _ = app.StopAsync();
        return Result.Ok();
    }));

    /// <summary>
    /// Runs an alignment id to completion (the <c>align.correction</c> step): <c>align</c> = U → FAU,
    /// <c>ucorrection</c> / <c>faupre</c> = that phase only (the <c>align.uCorrection</c> /
    /// <c>align.placeFau</c> blocks). <paramref name="settings"/> carries the block's points /
    /// target / guards. Marshalled onto the UI thread without blocking it, so the operator can
    /// Capture / MoveClick while the program awaits.
    /// </summary>
    public Task<Result> RunAsync(string appId, AlignmentRunOverrides? settings, CancellationToken ct)
    {
        (bool U, bool Fau)? phases = appId.ToLowerInvariant() switch
        {
            "align" => (true, true),
            "ucorrection" => (true, false),
            "faupre" => (false, true),
            _ => null,
        };
        if (phases is not { } p) return Task.FromResult(Unknown("align.correction", appId));

        return Dispatcher.InvokeAsync(() => _align == null
                ? Task.FromResult(Result.Fail(ErrorCode.NotSupported, "Alignment panel not attached."))
                : _align.RunFromProgramAsync(p.U, p.Fau, settings, ct))
            .Task.Unwrap();
    }

    private IUtilityApp? Find(string id)
        => _apps.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Runs <paramref name="fn"/> on the UI thread and returns its result.</summary>
    private Result OnUi(Func<Result> fn)
        => Dispatcher.CheckAccess() ? fn() : Dispatcher.Invoke(fn);
}
