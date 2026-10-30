using System;
using System.Windows;
using System.Windows.Threading;

namespace Horizon.Station.UI;

/// <summary>
/// Non-modal, always-on-top window with one big STOP button — the WPF face of the
/// <c>motion.slowApproach</c> crawl. <see cref="Finished"/> fires exactly once with
/// <c>true</c> when the operator presses STOP or the window is closed some other way; the
/// owner resolves its waiter <c>false</c> before closing it when the move ends on its own.
/// Shows how long the move has been running.
/// </summary>
public partial class MotionStopWindow : Window
{
    private readonly DispatcherTimer _timer;
    private readonly DateTime _shownAt = DateTime.Now;
    private bool _done;

    /// <summary>Raised once — <c>true</c> = STOP pressed (or window closed).</summary>
    public event Action<bool>? Finished;

    public MotionStopWindow(string title, string message)
    {
        InitializeComponent();
        StopTitle.Text = string.IsNullOrWhiteSpace(title) ? "Moving" : title;
        StopMessage.Text = message;
        StopMessage.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;

        UpdateElapsed();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => UpdateElapsed();
        _timer.Start();
    }

    private void UpdateElapsed()
    {
        var t = DateTime.Now - _shownAt;
        ElapsedText.Text = $"Moving {(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    private void OnStop(object sender, RoutedEventArgs e) => Finish(true);

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        // Fail-safe: a close nobody asked for (Alt+F4, shutdown) counts as STOP. When the
        // owner closes it because the move ended, its waiter is already resolved (false),
        // so this late "true" is ignored there.
        Finish(true);
    }

    private void Finish(bool stop)
    {
        if (_done) return;
        _done = true;
        _timer.Stop();
        Finished?.Invoke(stop);
    }
}
