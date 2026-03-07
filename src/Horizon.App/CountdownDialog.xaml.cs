using System.Windows;
using System.Windows.Threading;

namespace Horizon.App;

public enum CountdownResult { Continue, Stop }

public partial class CountdownDialog : Window
{
    private readonly DispatcherTimer _timer;
    private double _remainingMs;
    private readonly double _totalMs;

    public CountdownResult Result { get; private set; } = CountdownResult.Stop;

    public CountdownDialog(string nextProgramName, int delaySeconds)
    {
        InitializeComponent();

        NextProgramText.Text = $"Next: {nextProgramName}";
        _totalMs = delaySeconds * 1000.0;
        _remainingMs = _totalMs;

        UpdateDisplay();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += Timer_Tick;
        _timer.Start();
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        _remainingMs -= 50;
        if (_remainingMs <= 0)
        {
            _timer.Stop();
            Result = CountdownResult.Continue;
            DialogResult = true;
            return;
        }
        UpdateDisplay();
    }

    private void UpdateDisplay()
    {
        var seconds = Math.Ceiling(_remainingMs / 1000.0);
        CountdownText.Text = $"Starting in {seconds:0}...";
        CountdownBar.Value = (_remainingMs / _totalMs) * 100.0;
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        Result = CountdownResult.Continue;
        DialogResult = true;
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        Result = CountdownResult.Stop;
        DialogResult = false;
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }
}
