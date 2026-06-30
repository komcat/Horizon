using System.Windows.Controls;

namespace FAAM.UI.Views;

/// <summary>
/// The Engineer page — a tab container around the two authoring surfaces:
/// <b>Program</b> (the timeline / Gantt step editor, <see cref="TimelineEditorView"/>)
/// and <b>Process Deck</b> (a collection of programs with transition rules,
/// <see cref="DeckEditorView"/>). Both share the one engine stack; only the
/// selected tab is activated so background subscriptions don't run for a hidden tab.
/// </summary>
public partial class EngineerPageView : UserControl
{
    private readonly TimelineEditorView _program;
    private readonly DeckEditorView _deck;
    private readonly FindFirstLightSettingsView _findFirstLight;
    private object? _activeChild;
    private bool _pageActive;

    public EngineerPageView(EngineHost host)
    {
        InitializeComponent();
        _program = new TimelineEditorView(host);
        _deck = new DeckEditorView(host);
        _findFirstLight = new FindFirstLightSettingsView(host);
        ProgramHost.Content = _program;
        DeckHost.Content = _deck;
        FindFirstLightHost.Content = _findFirstLight;
    }

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
            _ => _program,
        };
        if (ReferenceEquals(_activeChild, target)) return;

        switch (_activeChild)
        {
            case TimelineEditorView t: t.Deactivate(); break;
            case DeckEditorView d: d.Deactivate(); break;
        }
        switch (target)
        {
            case TimelineEditorView t: t.Activate(); break;
            case DeckEditorView d: d.Activate(); break;
        }
        _activeChild = target;
    }
}
