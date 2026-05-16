namespace Horizon.UI.Navigation;

/// <summary>
/// The hub-and-spoke navigation surface (see <c>docs/layer3-ui-design.md</c> §6).
/// Pages call <see cref="GoHome"/> from their Back button; the home launcher
/// calls <see cref="NavigateTo"/> from its tiles.
/// </summary>
public interface IShellNavigator
{
    /// <summary>Shows the given page view model in the shell content region.</summary>
    void NavigateTo(object page);

    /// <summary>Returns to the home launcher.</summary>
    void GoHome();
}
