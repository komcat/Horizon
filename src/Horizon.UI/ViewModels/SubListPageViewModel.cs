using Horizon.UI.Mvvm;

namespace Horizon.UI.ViewModels;

/// <summary>
/// A page with a left sub-list and a content area — the shared layout of the
/// Process Trainer and Process Sequence pages (see <c>docs/layer3-ui-design.md</c>
/// §6). Phase 2 ships the shell only; the per-item content is a placeholder.
/// </summary>
public sealed class SubListPageViewModel : PageViewModel
{
    private string? _selectedItem;

    public SubListPageViewModel(string title, IReadOnlyList<string> items, string placeholder)
    {
        Title = title;
        Items = items;
        Placeholder = placeholder;
        _selectedItem = items.Count > 0 ? items[0] : null;
    }

    public override string Title { get; }

    /// <summary>Left sub-list entries.</summary>
    public IReadOnlyList<string> Items { get; }

    /// <summary>Message shown in the content area until the real UI is built.</summary>
    public string Placeholder { get; }

    public string? SelectedItem
    {
        get => _selectedItem;
        set => SetField(ref _selectedItem, value);
    }
}
