using System.Windows.Controls;
using FAAM.UI.Siphog;

namespace FAAM.UI.Views;

/// <summary>
/// The Engineer page — a tab container around the authoring surfaces:
/// <b>Program</b> (the timeline / Gantt step editor, <see cref="TimelineEditorView"/>),
/// <b>Process Deck</b> (a collection of programs with transition rules,
/// <see cref="DeckEditorView"/>), <b>Find First Light</b>, <b>Dispensing</b>, and
/// <b>Testing Ground</b> (<see cref="TestingGroundView"/> — a scratch area for
/// experimental alignment routines such as the spiral search). All share the one
/// engine stack; only the selected tab is activated so background subscriptions
/// don't run for a hidden tab.
/// </summary>
public partial class EngineerPageView : UserControl
{
    private readonly TimelineEditorView _program;
    private readonly DeckEditorView _deck;
    private readonly FindFirstLightSettingsView _findFirstLight;
    private readonly DispenseEditorView _dispense;
    private readonly TestingGroundView _testing;
    private object? _activeChild;
    private bool _pageActive;

    public EngineerPageView(EngineHost host)
    {
        InitializeComponent();
        _program = new TimelineEditorView(host);
        _deck = new DeckEditorView(host);
        _findFirstLight = new FindFirstLightSettingsView(host);
        _dispense = new DispenseEditorView(host);
        _testing = new TestingGroundView(host);
        ProgramHost.Content = _program;
        DeckHost.Content = _deck;
        FindFirstLightHost.Content = _findFirstLight;
        DispenseHost.Content = _dispense;
        TestingHost.Content = _testing;
        Downtime.Attach(host);
    }

    /// <summary>
    /// Hands the shared SiPhOG client (owned by the Run page's utility host) to the Testing
    /// Ground widgets that need it — the raster climb reads the target SAG power from it and
    /// drives the SLED guard over its command channel.
    /// </summary>
    public void AttachSiphog(SiphogClient siphog) => _testing.AttachSiphog(siphog);

    /// <summary>Called when the shell shows the Engineer page — activates the selected tab.</summary>
    public void Activate()
    {
        _pageActive = true;
        ActivateSelected();
    }

    /// <summary>Called when the shell leaves the Engineer page — stops both tabs.</summary>
    public void Deactivate()
    {
        _pageActive = false;
        _program.Deactivate();
        _deck.Deactivate();
        _testing.Deactivate();
        _activeChild = null;
    }

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        // TabControl.SelectionChanged bubbles up from inner selectors (combos / lists);
        // only react to the tab strip's own selection.
        if (!ReferenceEquals(e.OriginalSource, Tabs)) return;
        if (_pageActive) ActivateSelected();
    }

    private void ActivateSelected()
    {
        // The Find First Light tab (index 2) is a static settings form with no background
        // work, so it has no Activate/Deactivate — selecting it just stops the other two.
        object target = Tabs.SelectedIndex switch
        {
            1 => _deck,
            2 => _findFirstLight,
            3 => _dispense,
            4 => _testing,
            _ => _program,
        };
        if (ReferenceEquals(_activeChild, target)) return;

        switch (_activeChild)
        {
            case TimelineEditorView t: t.Deactivate(); break;
            case DeckEditorView d: d.Deactivate(); break;
            case TestingGroundView tg: tg.Deactivate(); break;
        }
        switch (target)
        {
            case TimelineEditorView t: t.Activate(); break;
            case DeckEditorView d: d.Activate(); break;
            case DispenseEditorView dv: dv.Activate(); break;
            case TestingGroundView tg: tg.Activate(); break;
        }
        _activeChild = target;
    }
}
