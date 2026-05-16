namespace Horizon.UI.ViewModels.Sections;

/// <summary>A Machine-page section that is listed in the nav rail but not yet built.</summary>
public sealed class PlaceholderSectionViewModel
{
    public PlaceholderSectionViewModel(string title, string message)
    {
        Title = title;
        Message = message;
    }

    /// <summary>Label shown in the Machine-page section rail.</summary>
    public string Title { get; }

    public string Message { get; }
}
