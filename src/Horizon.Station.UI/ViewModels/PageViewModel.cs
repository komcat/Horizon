using Horizon.Station.UI.Mvvm;

namespace Horizon.Station.UI.ViewModels;

/// <summary>
/// Base for a full-screen shell page. The shell draws the page chrome itself;
/// a page supplies its <see cref="Title"/> and the activate / deactivate hooks
/// the shell calls on navigation — a page does background work (sampling,
/// subscriptions) only while it is the visible page.
/// </summary>
public abstract class PageViewModel : ObservableObject
{
    /// <summary>Title shown in the shell page header.</summary>
    public abstract string Title { get; }

    /// <summary>Called by the shell when this page becomes visible.</summary>
    public virtual void OnActivated() { }

    /// <summary>Called by the shell when this page is navigated away from.</summary>
    public virtual void OnDeactivated() { }
}
