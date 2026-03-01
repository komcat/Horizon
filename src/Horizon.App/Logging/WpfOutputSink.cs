using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Data;
using System.Windows.Threading;
using Serilog.Core;
using Serilog.Events;

namespace Horizon.App.Logging;

/// <summary>
/// Serilog sink that populates an ObservableCollection of LogEntry items
/// with filtering and a 5000-entry cap.
/// </summary>
internal sealed class WpfOutputSink : ILogEventSink
{
    private const int MaxEntries = 5000;

    private Dispatcher? _dispatcher;

    public ObservableCollection<LogEntry> Entries { get; } = new();
    public CollectionViewSource ViewSource { get; } = new();

    private bool _showInfo = true;
    private bool _showWarning = true;
    private bool _showError = true;

    public bool ShowInfo
    {
        get => _showInfo;
        set { _showInfo = value; ViewSource.View?.Refresh(); }
    }

    public bool ShowWarning
    {
        get => _showWarning;
        set { _showWarning = value; ViewSource.View?.Refresh(); }
    }

    public bool ShowError
    {
        get => _showError;
        set { _showError = value; ViewSource.View?.Refresh(); }
    }

    public void Initialize(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        ViewSource.Source = Entries;
        ViewSource.Filter += OnFilter;

        // Seed initial entry
        Entries.Add(new LogEntry(LogEventLevel.Information, "Horizon ready.", DateTimeOffset.Now));
    }

    public void Emit(LogEvent logEvent)
    {
        if (_dispatcher is null)
            return;

        var level = logEvent.Level;
        var message = logEvent.RenderMessage();
        var timestamp = logEvent.Timestamp;

        _dispatcher.BeginInvoke(() =>
        {
            AddEntry(new LogEntry(level, message, timestamp));
        }, DispatcherPriority.Background);
    }

    public void Clear()
    {
        Entries.Clear();
    }

    private void AddEntry(LogEntry entry)
    {
        if (Entries.Count >= MaxEntries)
            Entries.RemoveAt(0);

        Entries.Add(entry);
    }

    private void OnFilter(object sender, FilterEventArgs e)
    {
        if (e.Item is not LogEntry entry)
        {
            e.Accepted = false;
            return;
        }

        e.Accepted = entry.Level switch
        {
            LogEventLevel.Debug or LogEventLevel.Verbose or LogEventLevel.Information => _showInfo,
            LogEventLevel.Warning => _showWarning,
            LogEventLevel.Error or LogEventLevel.Fatal => _showError,
            _ => true
        };
    }
}
