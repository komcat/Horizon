using System.ComponentModel;
using System.Windows;
using Horizon.UI.ViewModels;

namespace Horizon.UI;

/// <summary>
/// The application shell window. Hosts the home launcher and the five pages
/// via <see cref="ShellViewModel.CurrentView"/> (see <c>docs/layer3-ui-design.md</c>
/// §6).
///
/// Closing is intercepted: the operator confirms, then a <see cref="ShutdownWindow"/>
/// runs the ordered hardware teardown with progress. This window stays alive
/// (hidden) until that completes, so the process never ends mid-disconnect.
/// </summary>
public partial class ShellWindow : Window
{
    private readonly EngineHost _host;
    private readonly ShellViewModel _viewModel;
    private bool _exiting;

    public ShellWindow(EngineHost host)
    {
        _host = host;
        _viewModel = new ShellViewModel(host);
        DataContext = _viewModel;
        InitializeComponent();

        Closing += OnClosing;
        Closed += (_, _) => _viewModel.Dispose();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting)
            return;   // shutdown sequence already running — let the close proceed

        // Cancel this close; we drive the exit ourselves once confirmed.
        e.Cancel = true;

        var answer = MessageBox.Show(
            this,
            "Disconnect all hardware and exit Horizon?",
            "Exit Horizon",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
            return;   // stay open

        _exiting = true;

        // Stop the UI sampler now, before the engine is torn down — its next
        // tick would otherwise touch a nulled facet (Engine.Smu/Measurement
        // throw once shut down). Dispose is guarded, so the Closed handler's
        // call below is a harmless no-op.
        _viewModel.Dispose();

        // Hand off to the shutdown screen — it runs the ordered teardown with
        // progress, then calls Application.Shutdown(). This window stays alive
        // but hidden so the process stays up until teardown completes.
        new ShutdownWindow(_host).Show();
        Hide();
    }
}
