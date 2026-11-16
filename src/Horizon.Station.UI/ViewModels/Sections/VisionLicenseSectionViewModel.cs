using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace Horizon.Station.UI.ViewModels.Sections;

/// <summary>
/// The Machine page's <b>Vision license</b> section: says whether HALCON is available and, when it
/// is not, which part failed — the installed SDK, the license file, or the dongle — with the
/// evidence for each (see <see cref="HalconDiagnostics"/>). HALCON itself is started once, when the
/// application launches, so after fixing something the application must be restarted; Re-check only
/// re-reads the surroundings (files, environment, USB devices).
/// </summary>
public sealed class VisionLicenseSectionViewModel : ObservableObject
{
    private readonly EngineHost _host;
    private HalconReport? _report;
    private string _verdict = "Checking…";
    private string _halconMessage = string.Empty;
    private bool _available;
    private bool _busy;
    private string _status = string.Empty;

    public VisionLicenseSectionViewModel(EngineHost host)
    {
        _host = host;
        RecheckCommand = new AsyncRelayCommand(RecheckAsync, () => !_busy);
        CopyReportCommand = new RelayCommand(CopyReport, () => _report != null);
        OpenLicenseFolderCommand = new RelayCommand(OpenLicenseFolder);
        _ = RecheckAsync();
    }

    /// <summary>Label shown in the Machine-page section rail.</summary>
    public string Title => "Vision license";

    public AsyncRelayCommand RecheckCommand { get; }
    public RelayCommand CopyReportCommand { get; }
    public RelayCommand OpenLicenseFolderCommand { get; }

    /// <summary>The one-line answer: working, or which part failed.</summary>
    public string Verdict { get => _verdict; private set => SetField(ref _verdict, value); }

    /// <summary>True when HALCON started — the banner is green; otherwise red.</summary>
    public bool Available { get => _available; private set { if (SetField(ref _available, value)) OnPropertyChanged(nameof(Failed)); } }
    public bool Failed => !_available;

    /// <summary>HALCON's / the runtime's own error text from start-up (empty when it started).</summary>
    public string HalconMessage
    {
        get => _halconMessage;
        private set { if (SetField(ref _halconMessage, value)) OnPropertyChanged(nameof(HasHalconMessage)); }
    }
    public bool HasHalconMessage => !string.IsNullOrWhiteSpace(_halconMessage);

    public string Status { get => _status; private set => SetField(ref _status, value); }

    public ObservableCollection<HalconCheck> Sdk { get; } = new();
    public ObservableCollection<HalconCheck> License { get; } = new();
    public ObservableCollection<HalconCheck> Dongle { get; } = new();

    /// <summary>Group headings carry a mark when that group is where HALCON failed.</summary>
    public string SdkHeading => Heading("INSTALLED SDK", HalconFailure.Sdk, HalconFailure.SdkVersion);
    public string LicenseHeading => Heading("LICENSE", HalconFailure.LicenseFile, HalconFailure.License);
    public string DongleHeading => Heading("DONGLE", HalconFailure.Dongle);

    private string Heading(string title, params HalconFailure[] mine)
        => _report != null && mine.Contains(_report.Failure) ? $"{title}   ← failed here" : title;

    private async Task RecheckAsync()
    {
        if (_busy) return;
        _busy = true;
        RecheckCommand.RaiseCanExecuteChanged();
        try
        {
            var vision = _host.Engine.Vision;
            bool available = vision.HalconAvailable;
            string version = vision.HalconVersion;
            string? error = vision.HalconInitError;

            // File system + registry + process list — off the UI thread.
            var report = await Task.Run(() => HalconDiagnostics.Run(available, version, error));

            _report = report;
            Available = report.Available;
            Verdict = report.Verdict;
            HalconMessage = report.HalconMessage ?? string.Empty;
            Fill(Sdk, report.Sdk);
            Fill(License, report.License);
            Fill(Dongle, report.Dongle);
            OnPropertyChanged(nameof(SdkHeading));
            OnPropertyChanged(nameof(LicenseHeading));
            OnPropertyChanged(nameof(DongleHeading));
            Status = $"Checked {DateTime.Now:HH:mm:ss}. HALCON is started once at launch — after fixing something, restart the application.";
            CopyReportCommand.RaiseCanExecuteChanged();
        }
        catch (Exception ex) { Status = $"Check failed: {ex.Message}"; }
        finally
        {
            _busy = false;
            RecheckCommand.RaiseCanExecuteChanged();
        }
    }

    private static void Fill(ObservableCollection<HalconCheck> target, IReadOnlyList<HalconCheck> source)
    {
        target.Clear();
        foreach (var c in source) target.Add(c);
    }

    private void CopyReport()
    {
        if (_report == null) return;
        try
        {
            Clipboard.SetText(HalconDiagnostics.ToText(_report));
            Status = "Report copied to the clipboard.";
        }
        catch (Exception ex) { Status = $"Copy failed: {ex.Message}"; }
    }

    private void OpenLicenseFolder()
    {
        string? folder = HalconDiagnostics.LicenseFolder();
        if (folder == null || !Directory.Exists(folder))
        {
            Status = folder == null
                ? "HALCONROOT is not set, so there is no license folder to open."
                : $"{folder} does not exist.";
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) { Status = $"Could not open {folder}: {ex.Message}"; }
    }
}
