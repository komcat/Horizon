using System.Collections.ObjectModel;

namespace Horizon.Station.UI.ViewModels;

/// <summary>One command in the left palette — clicking <see cref="AddCommand"/> drops the op into the sequence.</summary>
public sealed class PaletteItemViewModel
{
    public PaletteItemViewModel(OpSpec spec, Action<OpSpec> add)
    {
        Spec = spec;
        AddCommand = new RelayCommand(() => add(spec));
    }

    public OpSpec Spec { get; }
    public string Display => Spec.Display;
    public RelayCommand AddCommand { get; }
}

/// <summary>A palette category (Motion, IO, …) with its commands.</summary>
public sealed class PaletteCategoryViewModel
{
    public PaletteCategoryViewModel(string category)
    {
        Category = category;
    }

    public string Category { get; }
    public ObservableCollection<PaletteItemViewModel> Items { get; } = new();
}
