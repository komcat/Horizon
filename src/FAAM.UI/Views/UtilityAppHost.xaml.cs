using System.Windows;
using System.Windows.Controls;
using FAAM.UI.Apps;
using FAAM.UI.Siphog;
using Horizon.Common;
using Horizon.Workflow;

namespace FAAM.UI.Views;

/// <summary>
/// The Run-page "phone" springboard. Hosts the utility apps (U correction, FAU
/// pre-align, Hill Climber, Hexapod Scan) as a home grid of tiles; tapping a tile
/// swaps that app's panel into the content area with a Back button that returns to the
/// grid. Apps are created once and kept alive — navigation only toggles
/// <see cref="UIElement.Visibility"/>, so a running climb / live SiPhOG stream survives
/// while the operator looks at another app (see <c>docs/utility-app-launcher-design.md</c>).
///
/// Implements <see cref="IUtilityAppHost"/> (Phase 2) so <c>app.open</c> / <c>app.home</c>
/// / <c>app.start</c> / <c>app.stop</c> workflow steps can drive the springboard.
/// </summary>
public partial class UtilityAppHost : UserControl, IUtilityAppHost
{
    private readonly List<IUtilityApp> _apps = new();

    public UtilityAppHost()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Builds and attaches the apps. The SiPhOG client is supplied by the caller — it is
    /// owned by the Run-page SiPhOG tab now, not the springboard — and shared into the
    /// Hill Climber app for its feedback. SiPhOG is left disconnected on load (the
    /// operator connects it from the SiPhOG tab, or a Phase 2 command block does).
    /// </summary>
    public void Attach(EngineHost host, SiphogClient siphog)
    {
        var ucorr = new UCorrectionPanel();
        ucorr.Attach(host);

        var fau = new FauPreAlignPanel();
        fau.Attach(host);

        var hill = new HillClimbPanel();
        hill.Attach(host, siphog);

        var scan = new ScanPanel();
        scan.Attach(host);

        _apps.Add(new UCorrectionApp(ucorr));
        _apps.Add(new FauPreAlignApp(fau));
        _apps.Add(new HillClimbApp(hill));
        _apps.Add(new ScanApp(scan));

        // All the panels live in the content host at once; navigation toggles visibility.
        foreach (var app in _apps)
        {
            app.Content.Visibility = Visibility.Collapsed;
            AppContentHost.Children.Add(app.Content);
        }

        AppGrid.ItemsSource = _apps;
        ShowHome();
    }

    private void OnAppClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: IUtilityApp app })
            OpenApp(app);
    }

    // --- IUtilityAppHost (Layer 2 → Layer 3 command seam, driven by app.* steps) ---
    // StepDispatcher may call these off the UI thread, so each marshals onto it.

    public Result Open(string appId) => OnUi(() =>
    {
        var app = Find(appId);
        if (app == null) return Result.Fail(ErrorCode.InvalidParameter, $"app.open: unknown app '{appId}'.");
        OpenApp(app);
        return Result.Ok();
    });

    public Result Home() => OnUi(() => { ShowHome(); return Result.Ok(); });

    public Task<Result> StartAsync(string appId, CancellationToken ct) => Task.FromResult(OnUi(() =>
    {
        var app = Find(appId);
        if (app == null) return Result.Fail(ErrorCode.InvalidParameter, $"app.start: unknown app '{appId}'.");
        OpenApp(app);                  // foreground it
        _ = app.StartAsync(ct);        // begin routine / connect — fire (does not await completion)
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

    private void OpenApp(IUtilityApp app)
    {
        foreach (var a in _apps)
            a.Content.Visibility = ReferenceEquals(a, app) ? Visibility.Visible : Visibility.Collapsed;

        AppView.DataContext = app;     // header binds to Title / StatusDetail
        HomeView.Visibility = Visibility.Collapsed;
        AppView.Visibility = Visibility.Visible;
    }

    private void OnBack(object sender, RoutedEventArgs e) => ShowHome();

    private void ShowHome()
    {
        // Hide the foreground app but do NOT stop it — background run-state persists.
        AppView.Visibility = Visibility.Collapsed;
        HomeView.Visibility = Visibility.Visible;
    }
}
