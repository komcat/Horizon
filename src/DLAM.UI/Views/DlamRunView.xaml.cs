using System.Windows;
using System.Windows.Controls;
using Horizon.Engine;

namespace DLAM.UI.Views;

/// <summary>
/// DLAM's Run page — the shared Run-page kit (<see cref="RunStepsPanel"/> driven by the
/// product's deck, <see cref="RunCameraPanel"/>, <see cref="RunLogView"/>) plus the
/// double-lens <see cref="AlignBand"/>. DLAM has no built-in fallback steps: the step list is
/// empty until a deck with Run-page programs is chosen.
/// </summary>
public partial class DlamRunView : UserControl, IRunPage
{
    private const string LayoutFile = "dlam_run_layout";   // { "cameraColumnWidth": 900 } — null = Auto

    private readonly EngineHost _host;
    private bool _active;

    public DlamRunView(EngineHost host)
    {
        _host = host;
        InitializeComponent();
        if (RunPageLayout.LoadCameraWidth(LayoutFile, Log) is double w && w >= CameraColumn.MinWidth)
            CameraColumn.Width = new GridLength(w);

        StepsPanel.Attach(host);
        StepsPanel.InspectMotionRequested += (_, session) => Camera.ShowInspector(session);
        Camera.Attach(host);
        LogView.Attach(host);
        Band.Attach(host);
    }

    /// <summary>Run progress from the steps panel (UI thread) — the shell mirrors it on the RUN header.</summary>
    public event Action<RunProgressSnapshot>? ProgressChanged
    {
        add => StepsPanel.ProgressChanged += value;
        remove => StepsPanel.ProgressChanged -= value;
    }

    /// <summary>A step's "Edit" was chosen — the shell opens that program on the Engineer page.</summary>
    public event Action<string>? EditProgramRequested
    {
        add => StepsPanel.EditProgramRequested += value;
        remove => StepsPanel.EditProgramRequested -= value;
    }

    public void StartProcessTimer() => StepsPanel.StartProcessTimer();

    public void StopProcessTimer() => StepsPanel.StopProcessTimer();

    public void Activate()
    {
        if (_active) return;
        _active = true;
        StepsPanel.SyncDeck();   // pick up a deck edited on the Engineer page
        Camera.Activate();
        LogView.Activate();
        Band.Activate();
    }

    public void Deactivate()
    {
        if (!_active) return;
        Camera.Deactivate();
        LogView.Deactivate();
        Band.Deactivate();
        _active = false;
    }

    private void OnSplitterReset(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        CameraColumn.Width = GridLength.Auto;
        RunPageLayout.SaveCameraWidth(LayoutFile, null, Log);
    }

    private void OnSplitterDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (CameraColumn.Width.IsAbsolute)
            RunPageLayout.SaveCameraWidth(LayoutFile, CameraColumn.Width.Value, Log);
    }

    private void Log(string message)
        => _host.Log.Add(new LogEntry(LogLevel.Info, DateTimeOffset.Now, "Run", message));
}
