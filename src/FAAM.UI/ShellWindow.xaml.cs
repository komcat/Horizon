using System.ComponentModel;
using System.Windows;
using FAAM.UI.ViewModels;
using FAAM.UI.Views;

namespace FAAM.UI;

/// <summary>
/// The application shell — fixed chrome (Home button, left rail, headers) around
/// a swappable page host. Home shows the launcher; the Machine tile navigates to
/// the Machine page. Pages are created once and reused.
///
/// Closing is intercepted: the operator confirms, then a <see cref="ShutdownWindow"/>
/// runs the ordered hardware teardown with progress. This window stays alive
/// (hidden) until that completes, so the process never ends mid-disconnect.
/// </summary>
public partial class ShellWindow : Window
{
    private readonly EngineHost _host;
    private readonly HomeView _home = new();
    private readonly MachinePageViewModel _machineVm;
    private readonly MachinePageView _machine;
    private readonly RunView _run;
    private readonly EngineerView _engineer;
    private bool _exiting;

    public ShellWindow(EngineHost host)
    {
        _host = host;
        _machineVm = new MachinePageViewModel(host);
        _machine = new MachinePageView { DataContext = _machineVm };
        _run = new RunView(host);
        _engineer = new EngineerView(host);
        InitializeComponent();

        // The shell top-bar engine-status panel binds to the Machine page VM
        // (whose sampler supplies EngineState / DeviceSummary / Reconnect).
        EngineHeader.DataContext = _machineVm;

        // The global jog panel (left rail) drives the connected motion devices.
        JogPanelControl.Attach(host);

        // Register the operator-prompt handler — a workflow flow.askUser step
        // blocks on this until the operator answers a modal dialog.
        host.Process.OperatorPrompt = new OperatorPrompt();

        _home.MachineRequested += ShowMachine;
        _home.RunRequested += ShowRun;
        _home.EngineerRequested += ShowEngineer;

        ShowHome();

        Closing += OnClosing;
    }

    /// <summary>Stops a page's background work (camera pipeline, status sampler) when leaving it.</summary>
    private void LeaveCurrent()
    {
        if (ReferenceEquals(PageHost.Content, _run))
            _run.Deactivate();
        else if (ReferenceEquals(PageHost.Content, _machine))
            _machineVm.OnDeactivated();
    }

    private void ShowHome()
    {
        LeaveCurrent();
        Header1Text.Text = "HOME";
        EngineHeader.Visibility = Visibility.Collapsed;
        PageHost.Content = _home;
    }

    private void ShowMachine()
    {
        LeaveCurrent();
        Header1Text.Text = "MACHINE";
        _machineVm.OnActivated();   // starts the ~5 Hz status sampler
        EngineHeader.Visibility = Visibility.Visible;
        PageHost.Content = _machine;
    }

    private void ShowRun()
    {
        LeaveCurrent();
        Header1Text.Text = "RUN";
        EngineHeader.Visibility = Visibility.Collapsed;
        _run.Activate();   // starts grabbing + frame routing
        PageHost.Content = _run;
    }

    private void ShowEngineer()
    {
        LeaveCurrent();
        Header1Text.Text = "ENGINEER";
        EngineHeader.Visibility = Visibility.Collapsed;
        PageHost.Content = _engineer;
    }

    private void OnHomeClick(object sender, RoutedEventArgs e) => ShowHome();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting)
            return;   // shutdown sequence already running — let the close proceed

        // Cancel this close; we drive the exit ourselves once confirmed.
        e.Cancel = true;

        var answer = MessageBox.Show(
            this,
            "Disconnect all hardware and exit FAAM?",
            "Exit FAAM",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
            return;   // stay open

        _exiting = true;
        _run.Deactivate();         // stop frame routing before the engine tears down
        _machineVm.Dispose();      // stop the status sampler + detach UI hooks
        JogPanelControl.Stop();    // stop the jog-panel position poll

        // Hand off to the shutdown screen — it runs the ordered teardown with
        // progress, then calls Application.Shutdown(). This window stays alive
        // but hidden so the process stays up until teardown completes.
        new ShutdownWindow(_host).Show();
        Hide();
    }
}
