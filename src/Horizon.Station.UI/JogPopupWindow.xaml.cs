using System;
using System.Windows;
using System.Windows.Threading;
using Horizon.Workflow;

namespace Horizon.Station.UI;

/// <summary>
/// Non-modal, always-on-top manual-jog window — the WPF face of the
/// <c>flow.jogPopup</c> step. Embeds the reusable <see cref="Views.JogPad"/> so the
/// operator can nudge the configured devices/axes, while a <see cref="DispatcherTimer"/>
/// counts elapsed seconds UP to <see cref="JogPopupRequest.MaxSeconds"/>.
/// <see cref="Finished"/> fires exactly once: <c>true</c> when the operator presses
/// Continue, <c>false</c> on Cancel / window-close / timeout. The owner waits on it.
/// </summary>
public partial class JogPopupWindow : Window
{
    private readonly DispatcherTimer _timer;
    private readonly int _maxSeconds;
    private int _elapsed;
    private bool _done;

    /// <summary>Raised once when the window resolves — true = Continue, false = Cancel/timeout/closed.</summary>
    public event Action<bool>? Finished;

    public JogPopupWindow(JogPopupRequest request, EngineHost host)
    {
        InitializeComponent();

        PromptTitle.Text = string.IsNullOrWhiteSpace(request.Title) ? "Manual jog" : request.Title;
        PromptMessage.Text = request.Message;
        PromptMessage.Visibility = string.IsNullOrWhiteSpace(request.Message) ? Visibility.Collapsed : Visibility.Visible;

        Pad.Configure(host, request.Devices, request.Axes, request.DefaultStepMm);

        _maxSeconds = Math.Clamp(request.MaxSeconds, 1, 3600);
        UpdateElapsed();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _elapsed++;
        UpdateElapsed();
        if (_elapsed >= _maxSeconds)
            Finish(false);   // timed out → treat as Cancel (fails the run)
    }

    private void UpdateElapsed()
        => ElapsedText.Text = $"Elapsed {_elapsed}s  /  max {_maxSeconds}s";

    private void OnContinue(object sender, RoutedEventArgs e) => Finish(true);

    private void OnCancel(object sender, RoutedEventArgs e) => Finish(false);

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Finish(false);   // closed by X / owner / run-Stop without a button → cancelled
    }

    private void Finish(bool carryOn)
    {
        if (_done) return;
        _done = true;
        _timer.Stop();
        Pad.Stop();
        Finished?.Invoke(carryOn);
    }
}
