using System.Collections.Specialized;
using System.Windows;
using Horizon.UI.ViewModels;

namespace Horizon.UI;

/// <summary>
/// The operator shell. Phase 1: a hardware status dashboard sampled from the
/// engine plus a combined engine/workflow log. Manual control surfaces, the
/// camera display windows, and the product/run UI arrive in later phases
/// (see <c>docs/layer3-ui-design.md</c> §11).
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(EngineHost host)
    {
        _viewModel = new MainViewModel(host);
        DataContext = _viewModel;
        InitializeComponent();

        Loaded += (_, _) => _viewModel.Start();
        Closed += (_, _) => _viewModel.Dispose();

        // Keep the newest log line in view as lines arrive.
        ((INotifyCollectionChanged)_viewModel.Log).CollectionChanged += OnLogChanged;
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || _viewModel.Log.Count == 0)
            return;
        LogList.ScrollIntoView(_viewModel.Log[^1]);
    }
}
