using System;
using System.Windows;
using System.Windows.Threading;

namespace FAAM.UI;

/// <summary>
/// Non-modal, always-on-top yellow notice — the WPF face of the <c>io.alertBuzzer</c>
/// step. The runner sounds the step's buzzer while this is open; <see cref="Acknowledged"/>
/// fires exactly once when the operator presses Acknowledge (or the window is closed),
/// and the owner then silences the buzzer and resumes the run. Shows how long the alert
/// has been waiting.
/// </summary>
public partial class AlertWindow : Window
{
    private readonly DispatcherTimer _timer;
    private readonly DateTime _shownAt = DateTime.Now;
    private bool _done;

    /// <summary>Raised once when the operator acknowledges (or the window closes).</summary>
    public event Action? Acknowledged;

    public AlertWindow(string title, string message)
    {
        InitializeComponent();
        AlertTitle.Text = string.IsNullOrWhiteSpace(title) ? "Process complete" : title;
        AlertMessage.Text = message;
        AlertMessage.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;

        UpdateElapsed();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => UpdateElapsed();
        _timer.Start();
    }

    private void UpdateElapsed()
    {
        var waited = DateTime.Now - _shownAt;
        ElapsedText.Text = $"Waiting {(int)waited.TotalMinutes}:{waited.Seconds:00}";
    }

    private void OnAcknowledge(object sender, RoutedEventArgs e) => Finish();

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        // Closed by the owner / run-Stop without a click — still release the waiter.
        Finish();
    }

    private void Finish()
    {
        if (_done) return;
        _done = true;
        _timer.Stop();
        Acknowledged?.Invoke();
    }
}
