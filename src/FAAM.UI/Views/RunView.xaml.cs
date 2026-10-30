using System.Windows;
using System.Windows.Controls;
using FAAM.UI.Siphog;
using Horizon.Engine;

namespace FAAM.UI.Views;

/// <summary>
/// FAAM's Run page — the shared Run-page kit (<see cref="RunStepsPanel"/> with FAAM's
/// built-in FAU-attach steps, <see cref="RunCameraPanel"/>, <see cref="RunLogView"/>) plus the
/// FAU utilities: the PSU panel and the alignment / SiPhOG utility apps, whose SiPhOG client
/// this page wires into the engine (signal catalog, hill-climb feedback, sheet sink).
/// </summary>
public partial class RunView : UserControl, IRunPage
{
    private const string LayoutFile = "faam_run_layout";   // { "cameraColumnWidth": 900 } — null = Auto

    private readonly EngineHost _host;
    private bool _active;

    /// <summary>
    /// The shared SiPhOG client owned by the Run page's utility host — exposed so other
    /// pages (the Engineer Testing Ground) reuse this one connection instead of opening a
    /// second one. Valid once the constructor has run.
    /// </summary>
    public Siphog.SiphogClient Siphog => AppHost.SiphogClient;

    public RunView(EngineHost host)
    {
        _host = host;
        InitializeComponent();
        RestoreLayout();   // camera column width the operator dragged to last session

        StepsPanel.BuiltIns = new FaamBuiltInSteps();   // FAU-attach fallback while no deck is chosen
        StepsPanel.Attach(host);   // process panel logs + STOP via the engine host
        StepsPanel.InspectMotionRequested += (_, session) => Camera.ShowInspector(session);

        Camera.Attach(host);       // live feed, MoveClick, capture prompts, snapshot box
        LogView.Attach(host);

        PsuPanel.Attach(host);   // power-supply channel ON/OFF + live V / A

        // The alignment-utilities grid owns the SiPhOG connection panel (and its client).
        AppHost.Attach(host);
        var siphog = AppHost.SiphogClient;

        // Let the U/FAU capture prompts mirror onto the camera feed's overlays (registered on
        // the shared hub by the camera panel), so the operator can Capture/Apply from the
        // camera view without looking at the side panel.
        AppHost.SetCapturePromptSurface(host.CapturePrompts);

        // Enrich jog activities in the DUT log with the live Sagnac value. Installed here —
        // where the SiPhOG client lives — so the generic activity/DUT layer stays signal-agnostic
        // (Sagnac is application-specific to FAAM). Reuses the same SAG-PWR read as the hill
        // climber; yields null when the SiPhOG isn't streaming so no stale value is recorded.
        var sagProbe = new SiphogHillClimbFeedback(siphog);
        host.SetActivitySignalProbe(() => sagProbe.HasSample
            ? new KeyValuePair<string, double>("sagnacV", sagProbe.Read())
            : (KeyValuePair<string, double>?)null);

        // Let app.* workflow steps drive the utility panels (start/stop a panel by id).
        host.Process.SetUtilityAppHost(AppHost);

        // Make that same SiPhOG signal available to hillclimb.* workflow steps (Engineer page).
        host.Process.SetHillClimbFeedbackProvider(() => new SiphogHillClimbFeedback(siphog));

        // Publish the live SiPhOG frame into the unified signal.read catalog so Engineer
        // blocks can pull siphog.* values (alongside the built-in SMU/PSU/Measurement/…).
        host.Process.AddSignalSource(new SiphogSignalSource(siphog));

        // Let siphog.updateSheet workflow steps write a staged SAG-PWR measurement to the
        // SiPhOG server's Google Sheet (V11 UPDATE_SAG) over that same client/connection.
        host.Process.SetSiphogSheetSink(new SiphogSheetSink(siphog));
    }

    /// <summary>Run progress from the steps panel (UI thread) — the shell mirrors it on the RUN header.</summary>
    public event Action<RunProgressSnapshot>? ProgressChanged
    {
        add => StepsPanel.ProgressChanged += value;
        remove => StepsPanel.ProgressChanged -= value;
    }

    /// <summary>A step's "Edit" was chosen — carries its program name; the shell opens it on the Engineer page.</summary>
    public event Action<string>? EditProgramRequested
    {
        add => StepsPanel.EditProgramRequested += value;
        remove => StepsPanel.EditProgramRequested -= value;
    }

    /// <summary>Starts the process timer — called by the shell header when a DUT run begins.</summary>
    public void StartProcessTimer() => StepsPanel.StartProcessTimer();

    /// <summary>Stops the process timer — called by the shell header when a DUT run ends.</summary>
    public void StopProcessTimer() => StepsPanel.StopProcessTimer();

    /// <summary>Double-click on the left/camera splitter — drop the dragged pixel width and
    /// return the camera column to Auto (full aspect-correct feed width).</summary>
    private void OnSplitterReset(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        CameraColumn.Width = GridLength.Auto;
        RunPageLayout.SaveCameraWidth(LayoutFile, null, Log);
    }

    /// <summary>Splitter drag finished — remember the camera column's new pixel width.</summary>
    private void OnSplitterDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (CameraColumn.Width.IsAbsolute)
            RunPageLayout.SaveCameraWidth(LayoutFile, CameraColumn.Width.Value, Log);
    }

    /// <summary>Re-applies the camera column width saved by a previous session, if any.</summary>
    private void RestoreLayout()
    {
        if (RunPageLayout.LoadCameraWidth(LayoutFile, Log) is double w && w >= CameraColumn.MinWidth)
            CameraColumn.Width = new GridLength(w);
    }

    /// <summary>
    /// Starts all cameras grabbing, re-syncs the deck and follows the log. Called by the shell
    /// when the Run page is shown.
    /// </summary>
    public void Activate()
    {
        if (_active) return;
        _active = true;

        // Re-sync the process-steps deck from disk: a deck edited and saved in the
        // Engineer page (same product, same name) won't have refreshed the Run-page
        // buttons, so reload the selected deck's current Run-page entries on every show.
        StepsPanel.SyncDeck();
        Camera.Activate();
        LogView.Activate();
    }

    /// <summary>Stops the cameras and the log follow. Called when navigating away (idempotent).</summary>
    public void Deactivate()
    {
        if (!_active) return;
        Camera.Deactivate();
        LogView.Deactivate();
        _active = false;
    }

    /// <summary>Writes a UI-side note into the shared engine log.</summary>
    private void Log(string message)
        => _host.Log.Add(new LogEntry(LogLevel.Info, DateTimeOffset.Now, "Run", message));
}
