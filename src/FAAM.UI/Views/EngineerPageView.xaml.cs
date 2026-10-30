using System.Windows.Controls;

namespace FAAM.UI.Views;

/// <summary>
/// The Engineer page — a tab container around the authoring surfaces:
/// <b>Program</b> (the timeline / Gantt step editor, <see cref="TimelineEditorView"/>),
/// <b>Process Deck</b> (a collection of programs with transition rules,
/// <see cref="DeckEditorView"/>) and <b>Dispensing</b>, followed by the head's own tabs
/// (<see cref="IStationHead.CreateEngineerTabs"/> — FAAM: Find First Light, Testing Ground).
/// All share the one engine stack; only the selected tab is activated so background
/// subscriptions don't run for a hidden tab.
/// </summary>
public partial class EngineerPageView : UserControl
{
    private readonly TimelineEditorView _program;
    private readonly DeckEditorView _deck;
    private readonly DispenseEditorView _dispense;
    private readonly List<object> _tabContent = new();   // by tab index
    private object? _activeChild;
    private bool _pageActive;

    public EngineerPageView(EngineHost host)
    {
        InitializeComponent();
        _program = new TimelineEditorView(host);
        _deck = new DeckEditorView(host);
        _dispense = new DispenseEditorView(host);
        ProgramHost.Content = _program;
        DeckHost.Content = _deck;
        DispenseHost.Content = _dispense;
        _tabContent.AddRange(new object[] { _program, _deck, _dispense });
        foreach (var tab in host.Head.CreateEngineerTabs(host))
        {
            Tabs.Items.Add(new TabItem { Header = tab.Header, Content = tab.Content });
            _tabContent.Add(tab.Content);
        }
        Downtime.Attach(host);
    }

    /// <summary>Called when the shell shows the Engineer page — activates the selected tab.</summary>
    public void Activate()
    {
        _pageActive = true;
        ActivateSelected();
    }

    /// <summary>Switches to the Program tab and opens <paramref name="name"/> in the timeline editor.</summary>
    public void OpenProgram(string name)
    {
        Tabs.SelectedIndex = 0;   // OnTabChanged activates the Program tab
        _program.OpenProgram(name);
    }

    /// <summary>Called when the shell leaves the Engineer page — stops every tab with background work.</summary>
    public void Deactivate()
    {
        _pageActive = false;
        _program.Deactivate();
        _deck.Deactivate();
        foreach (var page in _tabContent.OfType<IStationPage>()) page.Deactivate();
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
        // A tab with no background work (e.g. a static settings form) has no Activate /
        // Deactivate — selecting it just stops the previous one.
        int i = Tabs.SelectedIndex;
        object target = i >= 0 && i < _tabContent.Count ? _tabContent[i] : _program;
        if (ReferenceEquals(_activeChild, target)) return;

        switch (_activeChild)
        {
            case TimelineEditorView t: t.Deactivate(); break;
            case DeckEditorView d: d.Deactivate(); break;
            case IStationPage p: p.Deactivate(); break;
        }
        switch (target)
        {
            case TimelineEditorView t: t.Activate(); break;
            case DeckEditorView d: d.Activate(); break;
            case DispenseEditorView dv: dv.Activate(); break;
            case IStationPage p: p.Activate(); break;
        }
        _activeChild = target;
    }
}
