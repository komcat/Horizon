using System.Windows;
using FAAM.UI.Mvvm;
using FAAM.UI.Views;

namespace FAAM.UI.Apps;

/// <summary>
/// Thin <see cref="IUtilityApp"/> adapters over the existing Run-page panels. Each adds
/// only the launcher metadata + a uniform observable run-state; the panels are unchanged
/// beyond the small <c>RunStateChanged</c>/<c>IsRunning</c> surface they expose. State
/// notifications are coalesced to <see cref="ObservableObject.OnPropertyChanged"/> on the
/// UI thread so the home-grid tiles repaint via binding.
/// </summary>
internal static class UtilityAppUi
{
    /// <summary>Marshals an action onto the UI thread (panel events may fire on worker threads).</summary>
    public static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}

/// <summary>
/// Chained alignment app — runs U correction then FAU pre-align automatically
/// (the combined <see cref="AlignmentPanel"/>'s Start button).
/// </summary>
public sealed class AlignmentApp : ObservableObject, IUtilityApp
{
    private readonly AlignmentPanel _panel;
    public AlignmentApp(AlignmentPanel panel)
    {
        _panel = panel;
        _panel.RunStateChanged += Refresh;
    }

    public string Id => "align";
    public string Title => "U → FAU";
    public string Glyph => "U→F";
    public UIElement Content => _panel;
    public AppRunState State => _panel.IsRunning ? AppRunState.Running : AppRunState.Idle;
    public string? StatusDetail => _panel.RunStatusDetail;

    public Task StartAsync(CancellationToken ct) { _panel.RequestStart(); return Task.CompletedTask; }
    public Task StopAsync() { _panel.RequestStop(); return Task.CompletedTask; }

    private void Refresh() => UtilityAppUi.OnUi(() =>
    {
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(StatusDetail));
    });
}

/// <summary>
/// U-correction app — the legacy <c>ucorrection</c> id, now backed by the combined
/// <see cref="AlignmentPanel"/> and running its U-correction phase only.
/// </summary>
public sealed class UCorrectionApp : ObservableObject, IUtilityApp
{
    private readonly AlignmentPanel _panel;
    public UCorrectionApp(AlignmentPanel panel)
    {
        _panel = panel;
        _panel.RunStateChanged += Refresh;
    }

    public string Id => "ucorrection";
    public string Title => "U Correction";
    public string Glyph => "U°";
    public UIElement Content => _panel;
    public AppRunState State => _panel.IsRunning ? AppRunState.Running : AppRunState.Idle;
    public string? StatusDetail => _panel.RunStatusDetail;

    public Task StartAsync(CancellationToken ct) { _panel.RequestStartU(); return Task.CompletedTask; }
    public Task StopAsync() { _panel.RequestStop(); return Task.CompletedTask; }

    private void Refresh() => UtilityAppUi.OnUi(() =>
    {
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(StatusDetail));
    });
}

/// <summary>
/// FAU pre-align app — the legacy <c>faupre</c> id, now backed by the combined
/// <see cref="AlignmentPanel"/> and running its FAU pre-align phase only.
/// </summary>
public sealed class FauPreAlignApp : ObservableObject, IUtilityApp
{
    private readonly AlignmentPanel _panel;
    public FauPreAlignApp(AlignmentPanel panel)
    {
        _panel = panel;
        _panel.RunStateChanged += Refresh;
    }

    public string Id => "faupre";
    public string Title => "FAU Pre-align";
    public string Glyph => "FAU";
    public UIElement Content => _panel;
    public AppRunState State => _panel.IsRunning ? AppRunState.Running : AppRunState.Idle;
    public string? StatusDetail => _panel.RunStatusDetail;

    public Task StartAsync(CancellationToken ct) { _panel.RequestStartFau(); return Task.CompletedTask; }
    public Task StopAsync() { _panel.RequestStop(); return Task.CompletedTask; }

    private void Refresh() => UtilityAppUi.OnUi(() =>
    {
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(StatusDetail));
    });
}

/// <summary>
/// <b>DEPRECATED — do not use.</b> No longer registered: the Raster Climb panel was removed from the Run page.
/// Raster Climb app (Z+ step → XY plane search → move to the plane's max). It took the
/// hill climber's Run-page slot under the <c>hillclimb</c> id; <c>app.start hillclimb</c> /
/// <c>app.stop hillclimb</c> steps now fail with a deprecation message.
/// </summary>
public sealed class RasterClimbApp : ObservableObject, IUtilityApp
{
    private readonly RasterClimbPanel _panel;
    public RasterClimbApp(RasterClimbPanel panel)
    {
        _panel = panel;
        _panel.RunStateChanged += Refresh;
    }

    public string Id => "hillclimb";
    public string Title => "Raster Climb";
    public string Glyph => "⛰";
    public UIElement Content => _panel;
    public AppRunState State =>
        _panel.IsError ? AppRunState.Error
        : _panel.IsRunning ? AppRunState.Running
        : AppRunState.Idle;
    public string? StatusDetail => _panel.RunStatusDetail;

    public Task StartAsync(CancellationToken ct) { _panel.RequestStart(); return Task.CompletedTask; }
    public Task StopAsync() { _panel.RequestStop(); return Task.CompletedTask; }

    private void Refresh() => UtilityAppUi.OnUi(() =>
    {
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(StatusDetail));
    });
}

/// <summary><b>DEPRECATED — do not use.</b> No longer registered (Auto W panel removed). Auto W maximizer app (1-D W-axis line search re-peaking XY at each angle).</summary>
public sealed class AutoWApp : ObservableObject, IUtilityApp
{
    private readonly AutoWPanel _panel;
    public AutoWApp(AutoWPanel panel)
    {
        _panel = panel;
        _panel.RunStateChanged += Refresh;
    }

    public string Id => "autow";
    public string Title => "Auto W";
    public string Glyph => "W↻";
    public UIElement Content => _panel;
    public AppRunState State =>
        _panel.IsError ? AppRunState.Error
        : _panel.IsRunning ? AppRunState.Running
        : AppRunState.Idle;
    public string? StatusDetail => _panel.RunStatusDetail;

    public Task StartAsync(CancellationToken ct) { _panel.RequestStart(); return Task.CompletedTask; }
    public Task StopAsync() { _panel.RequestStop(); return Task.CompletedTask; }

    private void Refresh() => UtilityAppUi.OnUi(() =>
    {
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(StatusDetail));
    });
}
