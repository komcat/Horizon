using System.Windows.Controls;
using System.Windows.Threading;
using Horizon.Engine;

namespace Horizon.Station.UI.Views;

/// <summary>
/// The Run page's log box — the last <see cref="MaxLogLines"/> engine log lines. Loads the
/// buffer and follows new lines while the page is shown (<see cref="Activate"/>).
/// </summary>
public partial class RunLogView : UserControl
{
    private const int MaxLogLines = 200;

    private readonly Dispatcher _dispatcher;
    private readonly Queue<string> _logLines = new();
    private EngineHost? _host;
    private bool _subscribed;

    public RunLogView()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        InitializeComponent();
    }

    public void Attach(EngineHost host) => _host = host;

    /// <summary>Shows the current log buffer, then follows new lines live.</summary>
    public void Activate()
    {
        if (_host == null) return;
        _logLines.Clear();
        foreach (var entry in _host.Log.Snapshot())
            _logLines.Enqueue(Format(entry));
        while (_logLines.Count > MaxLogLines) _logLines.Dequeue();
        Flush();

        if (!_subscribed)
        {
            _host.Log.Appended += OnLogAppended;
            _subscribed = true;
        }
    }

    public void Deactivate()
    {
        if (_host == null || !_subscribed) return;
        _host.Log.Appended -= OnLogAppended;
        _subscribed = false;
    }

    private void OnLogAppended(LogEntry entry)
    {
        // Fires on engine threads — marshal to the UI thread.
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => OnLogAppended(entry));
            return;
        }
        _logLines.Enqueue(Format(entry));
        while (_logLines.Count > MaxLogLines) _logLines.Dequeue();
        Flush();
    }

    private void Flush()
    {
        LogBox.Text = string.Join(Environment.NewLine, _logLines);
        LogBox.ScrollToEnd();
    }

    private static string Format(LogEntry e)
        => $"{e.Timestamp:HH:mm:ss} [{e.Source}] {e.Message}";
}
