using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using Horizon.UI.ViewModels;

namespace Horizon.UI.Views;

/// <summary>The Machine page — engine header, section rail, section content, and the global log.</summary>
public partial class MachinePageView : UserControl
{
    private MachinePageViewModel? _viewModel;

    public MachinePageView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel = DataContext as MachinePageViewModel;
        if (_viewModel != null)
            ((INotifyCollectionChanged)_viewModel.Log).CollectionChanged += OnLogChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel != null)
            ((INotifyCollectionChanged)_viewModel.Log).CollectionChanged -= OnLogChanged;
        _viewModel = null;
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || _viewModel is null || _viewModel.Log.Count == 0)
            return;
        LogList.ScrollIntoView(_viewModel.Log[^1]);
    }
}
