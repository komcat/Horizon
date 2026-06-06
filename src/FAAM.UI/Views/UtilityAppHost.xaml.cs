using System.Windows;
using System.Windows.Controls;
using FAAM.UI.Apps;
using FAAM.UI.Siphog;

namespace FAAM.UI.Views;

/// <summary>
/// The Run-page "phone" springboard. Hosts the four utility apps (U correction, FAU
/// pre-align, Hill Climber, SiPhOG connection) as a home grid of tiles; tapping a tile
/// swaps that app's panel into the content area with a Back button that returns to the
/// grid. Apps are created once and kept alive — navigation only toggles
/// <see cref="UIElement.Visibility"/>, so a running climb / live SiPhOG stream survives
/// while the operator looks at another app (see <c>docs/utility-app-launcher-design.md</c>).
///
/// Phase 1: navigation + status badges only. The Layer 2 → Layer 3 command seam
/// (open/close/start/stop via workflow steps) is Phase 2.
/// </summary>
public partial class UtilityAppHost : UserControl
{
    private readonly List<IUtilityApp> _apps = new();
    private SiphogPanel? _siphogPanel;

    public UtilityAppHost()
    {
        InitializeComponent();
    }

    /// <summary>The shared SiPhOG client — used by the Run-page chart + the Layer 2 signal/feedback seams.</summary>
    public SiphogClient SiphogClient =>
        _siphogPanel?.Client ?? throw new InvalidOperationException("Attach(host) must be called first.");

    /// <summary>
    /// Builds and attaches the four apps. Must be called once before <see cref="SiphogClient"/>
    /// is read. SiPhOG is left disconnected on load (as before — the operator connects it
    /// from the SiPhOG app, or a Phase 2 command block does).
    /// </summary>
    public void Attach(EngineHost host)
    {
        var ucorr = new UCorrectionPanel();
        ucorr.Attach(host);

        var fau = new FauPreAlignPanel();
        fau.Attach(host);

        _siphogPanel = new SiphogPanel();          // self-attached (owns its client)

        var hill = new HillClimbPanel();
        hill.Attach(host, _siphogPanel.Client);

        _apps.Add(new UCorrectionApp(ucorr));
        _apps.Add(new FauPreAlignApp(fau));
        _apps.Add(new HillClimbApp(hill));
        _apps.Add(new SiphogApp(_siphogPanel));

        // All four panels live in the content host at once; navigation toggles visibility.
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

    /// <summary>Brings an app to the foreground by id (used by the Phase 2 command seam). Returns false if unknown.</summary>
    public bool Open(string id)
    {
        var app = _apps.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
        if (app == null) return false;
        OpenApp(app);
        return true;
    }

    /// <summary>Returns to the home springboard (apps keep running in the background).</summary>
    public void Home() => ShowHome();

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
