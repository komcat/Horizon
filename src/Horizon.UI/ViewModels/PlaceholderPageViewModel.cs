namespace Horizon.UI.ViewModels;

/// <summary>A page that is reachable but not yet built — shows a "coming later" note.</summary>
public sealed class PlaceholderPageViewModel : PageViewModel
{
    public PlaceholderPageViewModel(string title, string message)
    {
        Title = title;
        Message = message;
    }

    public override string Title { get; }

    public string Message { get; }
}
