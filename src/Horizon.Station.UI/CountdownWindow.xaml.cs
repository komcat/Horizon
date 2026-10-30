using System;
using System.Windows;
using System.Windows.Threading;

namespace Horizon.Station.UI;

/// <summary>
/// Non-modal, always-on-top countdown — the WPF face of the <c>flow.waitPopup</c>
/// step. A <see cref="DispatcherTimer"/> ticks down a real-time delay; the operator
/// can press Cancel to abort. <see cref="Finished"/> fires exactly once: with
/// <c>true</c> when the countdown elapses, <c>false</c> when the operator cancels
/// (or the window is closed). The owner waits on that result.
/// </summary>
public partial class CountdownWindow : Window
{
    private const int TickMs = 100;

    private readonly DispatcherTimer _timer;
    private readonly int _totalMs;
    private int _remainingMs;
    private bool _done;

    /// <summary>Raised once when the countdown ends — true = elapsed, false = cancelled/closed.</summary>
    public event Action<bool>? Finished;

    public CountdownWindow(string title, string message, int durationMs)
    {
        InitializeComponent();
        PromptTitle.Text = string.IsNullOrWhiteSpace(title) ? "Please wait" : title;
        PromptMessage.Text = message;
        PromptMessage.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;

        _totalMs = Math.Max(0, durationMs);
        _remainingMs = _totalMs;
        UpdateDisplay();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TickMs) };
        _timer.Tick += OnTick;
        if (_totalMs == 0)
            // Nothing to wait for — complete on the next dispatcher turn.
            Dispatcher.BeginInvoke(() => Finish(true));
        else
            _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _remainingMs -= TickMs;
        if (_remainingMs <= 0)
        {
            UpdateDisplay();
            Finish(true);
            return;
        }
        UpdateDisplay();
    }

    private void UpdateDisplay()
    {
        var secs = Math.Max(0, (int)Math.Ceiling(_remainingMs / 1000.0));
        RemainingText.Text = $"{secs}s";
        Progress.Value = _totalMs == 0 ? 1 : (double)(_totalMs - _remainingMs) / _totalMs;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Finish(false);

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        // Closed by the X / owner / run-Stop without a Cancel click → treat as cancelled.
        Finish(false);
    }

    private void Finish(bool completed)
    {
        if (_done) return;
        _done = true;
        _timer.Stop();
        Finished?.Invoke(completed);
    }
}
