using Horizon.UI.Mvvm;

namespace Horizon.UI.ViewModels;

/// <summary>One <c>key = value</c> row of a step's parameter grid.</summary>
public sealed class StepParamViewModel : ObservableObject
{
    private string _key;
    private string _value;

    public StepParamViewModel(string key, string value, Action<StepParamViewModel> remove)
    {
        _key = key;
        _value = value;
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    public string Key { get => _key; set => SetField(ref _key, value); }
    public string Value { get => _value; set => SetField(ref _value, value); }

    public RelayCommand RemoveCommand { get; }
}
