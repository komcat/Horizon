using System.Windows;
using FAAM.UI.Mvvm;
using FAAM.UI.Siphog;
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

/// <summary>U-correction app (gantry reference-point U-axis correction).</summary>
public sealed class UCorrectionApp : ObservableObject, IUtilityApp
{
    private readonly UCorrectionPanel _panel;
    public UCorrectionApp(UCorrectionPanel panel)
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

    public Task StartAsync(CancellationToken ct) { _panel.RequestStart(); return Task.CompletedTask; }
    public Task StopAsync() { _panel.RequestStop(); return Task.CompletedTask; }

    private void Refresh() => UtilityAppUi.OnUi(() =>
    {
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(StatusDetail));
    });
}

/// <summary>FAU pre-align app (gantry reference-point Y/Z pre-alignment).</summary>
public sealed class FauPreAlignApp : ObservableObject, IUtilityApp
{
    private readonly FauPreAlignPanel _panel;
    public FauPreAlignApp(FauPreAlignPanel panel)
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

    public Task StartAsync(CancellationToken ct) { _panel.RequestStart(); return Task.CompletedTask; }
    public Task StopAsync() { _panel.RequestStop(); return Task.CompletedTask; }

    private void Refresh() => UtilityAppUi.OnUi(() =>
    {
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(StatusDetail));
    });
}

/// <summary>Hill Climber app (coordinate-descent FAU→PIC active alignment).</summary>
public sealed class HillClimbApp : ObservableObject, IUtilityApp
{
    private readonly HillClimbPanel _panel;
    public HillClimbApp(HillClimbPanel panel)
    {
        _panel = panel;
        _panel.RunStateChanged += Refresh;
    }

    public string Id => "hillclimb";
    public string Title => "Hill Climber";
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

/// <summary>SiPhOG connection app (TCP client + value table). Passive — Connected, not Running.</summary>
public sealed class SiphogApp : ObservableObject, IUtilityApp
{
    private readonly SiphogPanel _panel;
    public SiphogApp(SiphogPanel panel)
    {
        _panel = panel;
        _panel.Client.ConnectionChanged += _ => Refresh();
    }

    public string Id => "siphog";
    public string Title => "SiPhOG";
    public string Glyph => "((•))";
    public UIElement Content => _panel;
    public AppRunState State => _panel.Client.IsConnected ? AppRunState.Connected : AppRunState.Idle;
    public string? StatusDetail => _panel.Client.IsConnected ? "Connected" : "Disconnected";

    public Task StartAsync(CancellationToken ct) { _panel.EnsureConnected(); return Task.CompletedTask; }
    public Task StopAsync() { _panel.Client.Disconnect(); return Task.CompletedTask; }

    private void Refresh() => UtilityAppUi.OnUi(() =>
    {
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(StatusDetail));
    });
}
