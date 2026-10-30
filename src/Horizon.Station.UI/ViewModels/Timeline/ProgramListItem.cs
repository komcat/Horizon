using Horizon.Station.UI.Mvvm;

namespace Horizon.Station.UI.ViewModels.Timeline;

/// <summary>One row of the Engineer page's program list — a program name, whether it is
/// hidden (view preference), and whether it is the one open in the editor.</summary>
public sealed class ProgramListItem : ObservableObject
{
    private bool _isHidden;
    private bool _isCurrent;

    public ProgramListItem(string name, bool isHidden, Action<ProgramListItem> toggleHidden)
    {
        Name = name;
        _isHidden = isHidden;
        ToggleHiddenCommand = new RelayCommand(() => toggleHidden(this));
    }

    public string Name { get; }

    public bool IsHidden { get => _isHidden; set => SetField(ref _isHidden, value); }

    public bool IsCurrent { get => _isCurrent; set => SetField(ref _isCurrent, value); }

    public RelayCommand ToggleHiddenCommand { get; }
}
