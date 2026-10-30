using System.Windows;
using System.Windows.Controls;

namespace Horizon.Station.UI.Views;

/// <summary>
/// The <b>Vision Alignment</b> page — one tabbed home for the camera-side authoring:
/// <b>Vision Profile</b> (per-position vision recipes) and <b>Gripped FAU</b> (the object
/// held by the registered gripper, taught from two points). Each tab keeps its own view;
/// only the selected tab's camera pipeline / timers run, the other is deactivated, so
/// switching tabs costs nothing and leaving the page stops both.
///
/// The page opens at the Vision Profile level (<see cref="AccessPolicy.VisionAlignment"/>);
/// the Gripped FAU tab is additionally gated at <see cref="AccessPolicy.GrippedObject"/>
/// (Engineer) and is disabled for a lower level, re-checked on every sign-in change.
/// </summary>
public partial class VisionAlignmentView : UserControl
{
    private readonly EngineHost _host;
    private readonly VisionProfileView _profile;
    private readonly GrippedObjectView _gripped;
    private readonly PartAlignmentView _part;
    private bool _pageActive;

    public VisionAlignmentView(EngineHost host)
    {
        _host = host;
        InitializeComponent();
        _profile = new VisionProfileView(host);
        _gripped = new GrippedObjectView(host);
        _part = new PartAlignmentView(host);
        ProfileHost.Content = _profile;
        GrippedHost.Content = _gripped;
        PartHost.Content = _part;

        _host.Session.Changed += ApplyAccess;
        ApplyAccess();
    }

    /// <summary>Starts the selected tab's view. Called by the shell when the page is shown.</summary>
    public void Activate()
    {
        _pageActive = true;
        ActivateSelected();
    }

    /// <summary>Stops both tabs' views (camera leases, timers, a running procedure). Idempotent.</summary>
    public void Deactivate()
    {
        _pageActive = false;
        _profile.Deactivate();
        _gripped.Deactivate();
        _part.Deactivate();
    }

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        // TabControl.SelectionChanged bubbles up from inner selectors (combos / lists) —
        // only react to the tab strip itself.
        if (!ReferenceEquals(e.OriginalSource, Tabs)) return;
        if (_pageActive) ActivateSelected();
    }

    private void ActivateSelected()
    {
        bool gripped = ReferenceEquals(Tabs.SelectedItem, GrippedTab);
        bool part = ReferenceEquals(Tabs.SelectedItem, PartTab);
        if (gripped) { _profile.Deactivate(); _part.Deactivate(); _gripped.Activate(); }
        else if (part) { _profile.Deactivate(); _gripped.Deactivate(); _part.Activate(); }
        else { _gripped.Deactivate(); _part.Deactivate(); _profile.Activate(); }
    }

    /// <summary>Gates the Gripped FAU tab on its own level; falls back to the profile tab when it locks.</summary>
    private void ApplyAccess()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(ApplyAccess); return; }
        bool allowed = _host.Session.Has(AccessPolicy.GrippedObject);
        GrippedTab.IsEnabled = allowed;
        GrippedTab.ToolTip = allowed ? null : $"Needs {AccessPolicy.GrippedObject} level.";
        bool partAllowed = _host.Session.Has(AccessPolicy.PartAlignment);
        PartTab.IsEnabled = partAllowed;
        PartTab.ToolTip = partAllowed ? null : $"Needs {AccessPolicy.PartAlignment} level.";
        if ((!allowed && ReferenceEquals(Tabs.SelectedItem, GrippedTab)) || (!partAllowed && ReferenceEquals(Tabs.SelectedItem, PartTab)))
            Tabs.SelectedIndex = 0;
    }
}
