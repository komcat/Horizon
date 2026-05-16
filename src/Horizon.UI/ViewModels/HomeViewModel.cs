using System.Windows.Input;

namespace Horizon.UI.ViewModels;

/// <summary>One launcher tile on the home screen.</summary>
public sealed record HomeTile(string Title, string Description, ICommand Open);

/// <summary>
/// The home launcher — the first screen after startup. A grid of tiles, each
/// navigating to a full-screen page (see <c>docs/layer3-ui-design.md</c> §6).
/// </summary>
public sealed class HomeViewModel
{
    public HomeViewModel(IReadOnlyList<HomeTile> tiles)
    {
        Tiles = tiles;
    }

    public IReadOnlyList<HomeTile> Tiles { get; }
}
