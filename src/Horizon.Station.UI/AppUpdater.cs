using Serilog;
using Velopack;
using Velopack.Sources;

namespace Horizon.Station.UI;

/// <summary>
/// Internet software update (Home › Version › Update) via Velopack, fed from the GitHub
/// Releases that <c>deploy/release.ps1</c> publishes. See <c>docs/software-update-design.md</c>.
///
/// <para>Flow: <see cref="CheckAsync"/> → the operator confirms → <see cref="DownloadAsync"/>
/// (the station stays up and nothing changes on disk but Velopack's packages folder) → the
/// normal exit path disconnects the hardware → <see cref="ShutdownWindow"/> calls
/// <see cref="ApplyPendingAtExit"/>, which starts Velopack's updater. The updater waits for
/// this process to end (at most 60 s, hence only after teardown), replaces the install's
/// <c>current\</c> folder as a whole and relaunches the head. <c>%APPDATA%\Horizon</c> is never
/// touched.</para>
///
/// <para>Only a copy installed by the release's <c>Setup.exe</c> / <c>Portable.zip</c> can
/// update (<see cref="IsInstalled"/>); a plain publish folder or a Visual Studio run cannot.</para>
/// </summary>
public static class AppUpdater
{
    /// <summary>The public repository whose releases carry the update packages.</summary>
#pragma warning disable S1075 // the update feed is this repo by design; release.ps1 publishes to the same URL
    public const string RepoUrl = "https://github.com/komcat/Horizon";
#pragma warning restore S1075

    /// <summary>Shown wherever an update is offered on a copy that cannot update.</summary>
    public const string NotInstalledMessage =
        "This copy was not installed with the release Setup.exe / Portable.zip, so it cannot update itself.";

    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(20);

    // Releases are cut as pre-releases while the heads are 0.x, so they must be searched too.
    private static readonly Lazy<UpdateManager?> Manager = new(CreateManager);

    private static VelopackAsset? _pending;

    /// <summary>The newer release found by the last successful check; null when up to date or never checked.</summary>
    public static UpdateInfo? Available { get; private set; }

    /// <summary>Raised (on a pool thread) when <see cref="Available"/> changes.</summary>
    public static event Action? AvailableChanged;

    /// <summary>True when this copy was installed by Velopack and so can update itself.</summary>
    public static bool IsInstalled => Manager.Value?.IsInstalled ?? false;

    /// <summary>True once an update is downloaded and will be applied when the station exits.</summary>
    public static bool IsPending => _pending != null;

    /// <summary>
    /// Asks GitHub for a newer release. Returns it, or null when this copy is up to date.
    /// Throws on a network failure / timeout, or when <see cref="IsInstalled"/> is false.
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync()
    {
        var mgr = RequireManager();
        var info = await mgr.CheckForUpdatesAsync().WaitAsync(CheckTimeout).ConfigureAwait(false);
        Available = info;
        AvailableChanged?.Invoke();
        return info;
    }

    /// <summary>
    /// Downloads <paramref name="info"/> (the delta package when one applies, else the full
    /// package; checksum-verified by Velopack) and marks it to be applied at exit.
    /// <paramref name="progress"/> reports 0–100 on a pool thread.
    /// </summary>
    public static async Task DownloadAsync(UpdateInfo info, Action<int> progress, CancellationToken ct)
    {
        var mgr = RequireManager();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(DownloadTimeout);
        await mgr.DownloadUpdatesAsync(info, progress, cts.Token).ConfigureAwait(false);
        _pending = info.TargetFullRelease;
        Log.Information("Software update v{Version} downloaded; applies when the station exits",
            info.TargetFullRelease.Version);
    }

    /// <summary>
    /// Called by <see cref="ShutdownWindow"/> once the hardware is disconnected: when an
    /// update is pending, starts Velopack's updater, which waits for this process to end,
    /// installs it and relaunches the head. A no-op otherwise. Never throws — a failure only
    /// means the station exits without updating.
    /// </summary>
    public static void ApplyPendingAtExit()
    {
        if (_pending is not { } asset) return;
        _pending = null;
        try
        {
            RequireManager().WaitExitThenApplyUpdates(asset, silent: false, restart: true);
            Log.Information("Software update v{Version}: updater started, applying after exit", asset.Version);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Software update v{Version} could not be started", asset.Version);
        }
    }

    /// <summary>Forgets a downloaded update so the next exit does not install it.</summary>
    public static void DiscardPending() => _pending = null;

    private static UpdateManager RequireManager()
    {
        var mgr = Manager.Value;
        if (mgr is not { IsInstalled: true })
            throw new InvalidOperationException(NotInstalledMessage);
        return mgr;
    }

    private static UpdateManager? CreateManager()
    {
        try
        {
            return new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: true));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Software update unavailable: Velopack update manager could not be created");
            return null;
        }
    }
}
