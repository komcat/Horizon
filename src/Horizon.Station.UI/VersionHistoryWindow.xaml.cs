using System.ComponentModel;
using System.Globalization;
using System.Windows;
using Horizon.Engine;
using Velopack;

namespace Horizon.Station.UI;

/// <summary>
/// Home › Version — a brief build identity: this build's release version, git id, branch
/// and build time, then every release (v* tag) with its date and git id. The data is
/// <see cref="AppVersionInfo.Current"/>, embedded at build time.
///
/// <para>Also the software-update surface (<see cref="AppUpdater"/>): on open it asks GitHub
/// for a newer release; <b>Update…</b> (<see cref="AccessPolicy.SoftwareUpdate"/>, refused while
/// anything runs) confirms, downloads, and closes with <see cref="AppUpdater.IsPending"/> set —
/// the caller then drives the normal disconnect-and-exit, after which the update installs
/// and FAAM restarts.</para>
/// </summary>
public partial class VersionHistoryWindow : Window
{
    private sealed record Row(string Tag, string DateText, string Commit, string Title);

    private readonly AppVersionInfo _info = AppVersionInfo.Current;
    private readonly EngineHost? _host;
    private readonly CancellationTokenSource _closing = new();
    private UpdateInfo? _update;
    private bool _downloading;

    public VersionHistoryWindow(EngineHost? host)
    {
        _host = host;
        InitializeComponent();
        var i = _info;

        VersionText.Text = $"{StationApp.AppName}  v{i.Version}";
        CommitText.Text = string.IsNullOrEmpty(i.ShortCommit)
            ? "git id unknown"
            : $"git {i.ShortCommit}" + (string.IsNullOrEmpty(i.Branch) ? "" : $"  ·  {i.Branch}")
              + (string.IsNullOrEmpty(i.Describe) ? "" : $"  ·  {i.Describe}");
        BuiltText.Text = string.Join("   ", new[]
        {
            i.BuiltLocal is { } b ? $"Built {b:yyyy-MM-dd HH:mm}" : null,
            i.CommitDate is { } c ? $"Committed {c.ToLocalTime():yyyy-MM-dd HH:mm}" : null,
            i.CommitsSinceRelease > 0 && i.Releases.Count > 0
                ? $"{i.CommitsSinceRelease} commit(s) after {i.Releases[0].Tag}" : null,
        }.Where(s => s != null));

        var notes = new List<string>();
        if (i.Dirty) notes.Add("Built with uncommitted changes — not an exact copy of the git id above.");
        if (!i.FromGit) notes.Add("Release history unavailable (this build was made without git).");
        if (notes.Count > 0)
        {
            NoteText.Text = string.Join("\n", notes);
            NoteText.Visibility = Visibility.Visible;
        }

        ReleaseGrid.ItemsSource = i.Releases
            .Select(r => new Row(r.Tag,
                r.Date is { } d ? d.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "",
                r.Commit, r.Title))
            .ToList();

        Loaded += async (_, _) => await CheckForUpdateAsync();
        Closing += OnWindowClosing;
        Closed += (_, _) => _closing.Dispose();
    }

    // ---- software update -------------------------------------------------------

    /// <summary>Asks GitHub for a newer release and shows the result.</summary>
    private async Task CheckForUpdateAsync()
    {
        _update = null;
        UpdateButton.IsEnabled = false;
        if (!AppUpdater.IsInstalled)
        {
            UpdateStatusText.Text = AppUpdater.NotInstalledMessage;
            CheckButton.IsEnabled = false;
            return;
        }

        CheckButton.IsEnabled = false;
        UpdateStatusText.Text = "Checking GitHub for a newer release…";
        string status;
        try
        {
            _update = await AppUpdater.CheckAsync();
            status = _update is { } u
                ? $"v{u.TargetFullRelease.Version} is available (this copy is v{_info.Version})."
                : $"Up to date — v{_info.Version} is the latest release.";
        }
        catch (Exception ex)
        {
            status = $"Could not check for updates: {ex.Message}";
        }
        if (_closing.IsCancellationRequested) return;

        UpdateStatusText.Text = status;
        CheckButton.IsEnabled = true;
        ApplyUpdateAccess();
    }

    /// <summary>Enables Update… only when a newer release exists and the level allows it.</summary>
    private void ApplyUpdateAccess()
    {
        var allowed = _host?.Session.Has(AccessPolicy.SoftwareUpdate) ?? false;
        UpdateButton.IsEnabled = _update != null && allowed && !_downloading;
        if (_update != null && !allowed)
            UpdateButton.ToolTip = $"Needs {AccessPolicy.SoftwareUpdate} level or higher";
    }

    /// <summary>Why an update cannot start now, or null when the station is idle.</summary>
    private string? BusyReason()
    {
        if (_host == null) return "the engine is not available";
        if (_host.Process.IsRunning) return "a program / deck is running";
        if (_host.Activity.ProcessBusy) return "a Run-page process step is running";
        if (_host.Activity.AlignmentBusy) return "an alignment / scan is running";
        return null;
    }

    private async void OnCheckUpdate(object sender, RoutedEventArgs e) => await CheckForUpdateAsync();

    private async void OnUpdate(object sender, RoutedEventArgs e)
    {
        if (_update is not { } info || _host == null) return;
        if (!_host.Session.Has(AccessPolicy.SoftwareUpdate)) return;

        if (BusyReason() is { } busy)
        {
            MessageBox.Show(this, $"Cannot update while {busy}. Wait for it to finish, then try again.",
                "Software update", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var target = info.TargetFullRelease.Version;
        var answer = MessageBox.Show(this,
            $"Update {StationApp.AppName} v{_info.Version} → v{target}?\n\n" +
            "The update downloads first. Then FAAM disconnects all hardware, exits, installs " +
            "the update and restarts by itself.\n\n" +
            @"Settings, products and data in %APPDATA%\Horizon are not changed.",
            "Software update", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        _downloading = true;
        CheckButton.IsEnabled = false;
        UpdateButton.IsEnabled = false;
        UpdateProgress.Value = 0;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateStatusText.Text = $"Downloading v{target}…";
        _host.Log.Add(new LogEntry(LogLevel.Info, DateTimeOffset.Now, StationApp.Title,
            $"Software update v{_info.Version} → v{target} started by {_host.Session.Operator?.Id ?? "?"}"));

        string? failure = null;
        try
        {
            await AppUpdater.DownloadAsync(info,
                pct => Dispatcher.BeginInvoke(() => UpdateProgress.Value = pct),
                _closing.Token);
        }
        catch (Exception ex)
        {
            failure = ex is OperationCanceledException ? "Download cancelled." : $"Download failed: {ex.Message}";
        }
        _downloading = false;
        if (_closing.IsCancellationRequested) return;

        UpdateProgress.Visibility = Visibility.Collapsed;
        if (failure != null)
        {
            UpdateStatusText.Text = failure;
            CheckButton.IsEnabled = true;
            ApplyUpdateAccess();
            return;
        }

        // Downloaded. Hand back to Home, which starts the normal disconnect-and-exit.
        UpdateStatusText.Text = $"v{target} downloaded — disconnecting and restarting…";
        UpdateReady = true;
        Close();
    }

    /// <summary>
    /// True when the window closed with an update downloaded and confirmed — the caller
    /// should now run the disconnect-and-exit that installs it.
    /// </summary>
    public bool UpdateReady { get; private set; }

    /// <summary>Closing mid-download cancels it; the station keeps running the current version.</summary>
    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_downloading)
        {
            var answer = MessageBox.Show(this, "Cancel the update download?", "Software update",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) { e.Cancel = true; return; }
        }
        _closing.Cancel();
        // A download that finished just as it was cancelled must not install at the next exit.
        if (!UpdateReady) AppUpdater.DiscardPending();
    }

    // ---- build identity --------------------------------------------------------

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        var i = _info;
        var text = $"{StationApp.AppName} v{i.Version} ({(string.IsNullOrEmpty(i.Describe) ? i.ShortCommit : i.Describe)})"
                   + (string.IsNullOrEmpty(i.Commit) ? "" : $" commit {i.Commit}")
                   + (i.BuiltLocal is { } b ? $" built {b:yyyy-MM-dd HH:mm}" : "");
        try { Clipboard.SetText(text); } catch { /* clipboard busy — ignore */ }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
