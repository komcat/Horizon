using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Horizon.Station.UI;

/// <summary>How one diagnostic line turned out.</summary>
public enum HalconCheckState { Ok, Info, Warn, Fail }

/// <summary>One line of the HALCON diagnosis: what was checked, how it turned out, and the evidence.</summary>
public sealed record HalconCheck(string Name, HalconCheckState State, string Detail);

/// <summary>Which part of the HALCON setup stopped it from starting.</summary>
public enum HalconFailure { None, Sdk, SdkVersion, LicenseFile, Dongle, License, Unknown }

/// <summary>The full diagnosis shown on Machine › Vision license.</summary>
public sealed record HalconReport(
    bool Available, HalconFailure Failure, string Verdict, string? HalconMessage,
    IReadOnlyList<HalconCheck> Sdk, IReadOnlyList<HalconCheck> License, IReadOnlyList<HalconCheck> Dongle);

/// <summary>
/// Works out why HALCON is or is not available on this machine. The engine starts HALCON once and
/// keeps its answer (<c>IVisionFacet.HalconAvailable</c> / <c>HalconInitError</c>) — that answer is
/// the verdict. The checks here only gather evidence around it, in three groups: the installed SDK
/// (environment, native library, version match with the managed wrapper), the license file, and
/// the USB dongle. They read the file system, the registry and the process list; nothing here
/// calls HALCON, so it is safe to run when HALCON is broken.
///
/// The dongle check is a heuristic: it looks for a USB device from the two dongle makers MVTec has
/// used (WIBU CodeMeter, Sentinel / HASP) among the devices Windows currently has enumerated.
/// </summary>
public static class HalconDiagnostics
{
    private const string NativeName = "halcon.dll";
    private const string WibuVendor = "VID_064F";       // WIBU-SYSTEMS (CodeMeter)
    private const string SentinelVendor = "VID_0529";   // Aladdin / SafeNet (Sentinel HASP)

    public static HalconReport Run(bool halconAvailable, string halconVersion, string? initError)
    {
        var sdk = new List<HalconCheck>();
        var license = new List<HalconCheck>();
        var dongle = new List<HalconCheck>();

        string? root = Environment.GetEnvironmentVariable("HALCONROOT");
        bool rootOk = !string.IsNullOrWhiteSpace(root) && Directory.Exists(root);

        var wrapper = WrapperVersion();
        var natives = FindNative(root);
        string? loaded = LoadedNative();
        bool versionMismatch = CheckSdk(sdk, root, rootOk, wrapper, natives, loaded, halconAvailable, halconVersion);
        bool licenseFileFound = CheckLicense(license, root, rootOk);
        bool dongleSeen = CheckDongle(dongle);

        var (failure, verdict) = Verdict(halconAvailable, halconVersion, initError,
            natives.Count > 0 || loaded != null, versionMismatch, licenseFileFound, dongleSeen);
        return new HalconReport(halconAvailable, failure, verdict, initError, sdk, license, dongle);
    }

    // --- installed SDK ---

    private static bool CheckSdk(
        List<HalconCheck> sdk, string? root, bool rootOk, Version? wrapper,
        IReadOnlyList<(string Path, string Version)> natives, string? loaded, bool available, string halconVersion)
    {
        sdk.Add(available
            ? new HalconCheck("HALCON runtime", HalconCheckState.Ok, $"Started — version {halconVersion}.")
            : new HalconCheck("HALCON runtime", HalconCheckState.Fail, "Did not start when the application launched."));

        sdk.Add(wrapper != null
            ? new HalconCheck("Managed wrapper (MVTec.HalconDotNet)", HalconCheckState.Info, $"Version {wrapper} — this is the HALCON version the application was built for.")
            : new HalconCheck("Managed wrapper (MVTec.HalconDotNet)", HalconCheckState.Warn, "Not loaded in this process."));

        sdk.Add(string.IsNullOrWhiteSpace(root)
            ? new HalconCheck("HALCONROOT", HalconCheckState.Warn, "Environment variable not set — HALCON is not installed, or the application was started before the installer set it.")
            : rootOk
                ? new HalconCheck("HALCONROOT", HalconCheckState.Ok, root!)
                : new HalconCheck("HALCONROOT", HalconCheckState.Fail, $"{root} — this folder does not exist."));

        string? arch = Environment.GetEnvironmentVariable("HALCONARCH");
        sdk.Add(new HalconCheck("HALCONARCH", string.IsNullOrWhiteSpace(arch) ? HalconCheckState.Info : HalconCheckState.Ok,
            string.IsNullOrWhiteSpace(arch) ? "Not set." : arch!));

        sdk.Add(new HalconCheck("Process", Environment.Is64BitProcess ? HalconCheckState.Ok : HalconCheckState.Fail,
            Environment.Is64BitProcess ? "64-bit (HALCON is x64 only)." : "32-bit — HALCON is x64 only."));

        if (natives.Count == 0)
            sdk.Add(new HalconCheck($"Native library ({NativeName})", HalconCheckState.Fail,
                "Not found in the application folder, under HALCONROOT, or on PATH."));
        foreach (var (path, version) in natives)
            sdk.Add(new HalconCheck($"Native library ({NativeName})", HalconCheckState.Info, $"{path}   version {version}"));

        sdk.Add(loaded != null
            ? new HalconCheck("Loaded by this process", HalconCheckState.Ok, loaded)
            : new HalconCheck("Loaded by this process", available ? HalconCheckState.Info : HalconCheckState.Warn,
                $"{NativeName} is not loaded in this process."));

        // The native engine must be the same HALCON release as the wrapper; a different one misroutes operators.
        bool mismatch = false;
        string? used = loaded ?? (natives.Count > 0 ? natives[0].Path : null);
        if (wrapper != null && used != null && FileVersion(used) is { } nativeVersion && WrapperRelease(wrapper) is { } want)
        {
            // Compare on digits only, so "25.11.0.0" and "25110" both match a wanted "25.11".
            static string Digits(string s) => new(s.Where(char.IsDigit).ToArray());
            mismatch = !Digits(nativeVersion).StartsWith(Digits(want), StringComparison.Ordinal);
            sdk.Add(mismatch
                ? new HalconCheck("Version match", HalconCheckState.Fail,
                    $"The application needs HALCON {want}, but {used} is version {nativeVersion}. Install HALCON {want} (or remove the other one from PATH).")
                : new HalconCheck("Version match", HalconCheckState.Ok, $"Native library {nativeVersion} matches the wrapper ({want})."));
        }
        return mismatch;
    }

    /// <summary>The loaded halcondotnet assembly's version, or null when it is not loaded.</summary>
    private static Version? WrapperVersion()
    {
        try
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                // The NuGet wrapper is MVTec.HalconDotNet.dll; a classic install's is halcondotnet.dll.
                .FirstOrDefault(a => (a.GetName().Name ?? string.Empty).Contains("halcondotnet", StringComparison.OrdinalIgnoreCase))
                ?.GetName().Version;
        }
        catch (Exception) { return null; }
    }

    /// <summary>"25.11" from a wrapper version such as 25.11.0.0 or 25110.0.0.0; null when it cannot be read.</summary>
    private static string? WrapperRelease(Version wrapper)
    {
        if (wrapper.Major is > 0 and < 1000) return $"{wrapper.Major}.{wrapper.Minor:00}";
        if (wrapper.Major >= 10000) return $"{wrapper.Major / 1000}.{wrapper.Major % 1000 / 10:00}";   // 25110 → 25.11
        return null;
    }

    /// <summary>Every halcon.dll the loader could pick up: application folder, HALCONROOT, PATH — in that order.</summary>
    private static IReadOnlyList<(string Path, string Version)> FindNative(string? root)
    {
        var folders = new List<string>();
        string app = AppContext.BaseDirectory;
        folders.Add(app);
        folders.Add(Path.Combine(app, "runtimes", "win-x64", "native"));
        if (!string.IsNullOrWhiteSpace(root))
        {
            string arch = Environment.GetEnvironmentVariable("HALCONARCH") is { Length: > 0 } a ? a : "x64-win64";
            folders.Add(Path.Combine(root, "bin", arch));
        }
        folders.AddRange((Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        var found = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            try
            {
                string path = Path.GetFullPath(Path.Combine(folder, NativeName));
                if (seen.Add(path) && File.Exists(path))
                    found.Add((path, FileVersion(path) ?? "unknown"));
            }
            catch (Exception) { /* a malformed PATH entry — skip it */ }
        }
        return found;
    }

    private static string? FileVersion(string path)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path);
            return string.IsNullOrWhiteSpace(v.ProductVersion) ? v.FileVersion : v.ProductVersion;
        }
        catch (Exception) { return null; }
    }

    /// <summary>The halcon.dll this process actually loaded, or null.</summary>
    private static string? LoadedNative()
    {
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            foreach (ProcessModule module in process.Modules)
                if (string.Equals(module.ModuleName, NativeName, StringComparison.OrdinalIgnoreCase))
                    return module.FileName;
        }
        catch (Exception) { /* module list not readable — report "not loaded" */ }
        return null;
    }

    // --- license file ---

    /// <summary>The folder HALCON reads its license file from, or null without a usable HALCONROOT.</summary>
    public static string? LicenseFolder()
    {
        string? root = Environment.GetEnvironmentVariable("HALCONROOT");
        return string.IsNullOrWhiteSpace(root) ? null : Path.Combine(root, "license");
    }

    private static bool CheckLicense(List<HalconCheck> license, string? root, bool rootOk)
    {
        if (!rootOk)
        {
            license.Add(new HalconCheck("License folder", HalconCheckState.Fail,
                "HALCONROOT is not usable, so there is no <HALCONROOT>\\license folder to hold a license file."));
            return false;
        }

        string folder = Path.Combine(root!, "license");
        if (!Directory.Exists(folder))
        {
            license.Add(new HalconCheck("License folder", HalconCheckState.Fail, $"{folder} does not exist."));
            return false;
        }
        license.Add(new HalconCheck("License folder", HalconCheckState.Ok, folder));

        string[] files;
        try { files = Directory.GetFiles(folder, "*.dat"); }
        catch (Exception ex)
        {
            license.Add(new HalconCheck("License file", HalconCheckState.Fail, $"The folder cannot be read — {ex.Message}"));
            return false;
        }
        if (files.Length == 0)
        {
            license.Add(new HalconCheck("License file", HalconCheckState.Fail,
                "No license file (*.dat) in the folder. Copy the license file from MVTec there."));
            return false;
        }
        foreach (var file in files)
        {
            var info = new FileInfo(file);
            license.Add(new HalconCheck("License file", HalconCheckState.Info,
                $"{info.Name}   {info.Length:N0} bytes   modified {info.LastWriteTime:yyyy-MM-dd}"));
        }
        license.Add(new HalconCheck("Validity", HalconCheckState.Info,
            "Only HALCON can tell whether the file is valid for this machine / dongle and not expired — see HALCON's message above."));
        return true;
    }

    // --- dongle ---

    private static bool CheckDongle(List<HalconCheck> dongle)
    {
        var devices = ConnectedUsb();
        var wibu = devices.Where(d => d.Contains(WibuVendor, StringComparison.OrdinalIgnoreCase)).ToList();
        var sentinel = devices.Where(d => d.Contains(SentinelVendor, StringComparison.OrdinalIgnoreCase)).ToList();

        foreach (var d in wibu)
            dongle.Add(new HalconCheck("USB dongle", HalconCheckState.Ok, $"WIBU CodeMeter device connected — {d}"));
        foreach (var d in sentinel)
            dongle.Add(new HalconCheck("USB dongle", HalconCheckState.Ok, $"Sentinel / HASP device connected — {d}"));
        bool seen = wibu.Count + sentinel.Count > 0;
        if (!seen)
            dongle.Add(new HalconCheck("USB dongle", HalconCheckState.Warn,
                "No WIBU CodeMeter or Sentinel / HASP USB device is connected. That is expected for a license tied to this computer, and the cause of the failure for a dongle license."));

        bool codeMeter = IsRunning("CodeMeter");
        dongle.Add(new HalconCheck("CodeMeter runtime", codeMeter ? HalconCheckState.Ok : wibu.Count > 0 ? HalconCheckState.Fail : HalconCheckState.Info,
            codeMeter ? "CodeMeter.exe is running."
                : wibu.Count > 0 ? "A CodeMeter dongle is connected but CodeMeter.exe is not running — install / start the CodeMeter runtime."
                : "CodeMeter.exe is not running (only needed for a CodeMeter dongle)."));

        if (sentinel.Count > 0 || IsRunning("hasplms"))
            dongle.Add(new HalconCheck("Sentinel runtime", IsRunning("hasplms") ? HalconCheckState.Ok : HalconCheckState.Fail,
                IsRunning("hasplms") ? "Sentinel license manager (hasplms.exe) is running."
                    : "A Sentinel dongle is connected but its license manager (hasplms.exe) is not running — install the Sentinel runtime."));

        dongle.Add(new HalconCheck("How this is detected", HalconCheckState.Info,
            "By USB vendor id among the devices Windows has enumerated — it shows a dongle is plugged in, not that it carries a HALCON license."));
        return seen;
    }

    /// <summary>
    /// Instance ids of the USB devices Windows currently has enumerated — each driver service lists
    /// the devices it is driving under <c>Services\{name}\Enum</c>. Empty when the registry cannot be read.
    /// </summary>
    private static IReadOnlyList<string> ConnectedUsb()
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (services == null) return Array.Empty<string>();
            foreach (var name in services.GetSubKeyNames())
            {
                try
                {
                    using var e = services.OpenSubKey(name + @"\Enum");
                    if (e == null) continue;
                    foreach (var valueName in e.GetValueNames())
                        if (e.GetValue(valueName) is string id && id.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase))
                            found.Add(id);
                }
                catch (Exception) { /* a service key we may not read — skip it */ }
            }
        }
        catch (Exception) { /* registry not readable — report nothing seen */ }
        return found.ToList();
    }

    private static bool IsRunning(string processName)
    {
        try
        {
            var processes = System.Diagnostics.Process.GetProcessesByName(processName);
            foreach (var p in processes) p.Dispose();
            return processes.Length > 0;
        }
        catch (Exception) { return false; }
    }

    // --- verdict ---

    private static (HalconFailure, string) Verdict(
        bool available, string version, string? initError, bool nativeFound, bool versionMismatch,
        bool licenseFileFound, bool dongleSeen)
    {
        if (available)
            return versionMismatch
                ? (HalconFailure.SdkVersion, $"HALCON {version} started, but the native library is a different release than the application was built for — vision results may be wrong. See Installed SDK.")
                : (HalconFailure.None, $"HALCON {version} is working: SDK, license and (if used) dongle are all fine.");

        string message = initError ?? string.Empty;
        bool loadError = message.Contains("DllNotFoundException", StringComparison.Ordinal)
                      || message.Contains("BadImageFormatException", StringComparison.Ordinal)
                      || message.Contains("EntryPointNotFoundException", StringComparison.Ordinal);
        bool licenseError = Regex.IsMatch(message, "licen[sc]e|dongle|host ?id", RegexOptions.IgnoreCase);

        if (!licenseError && (loadError || !nativeFound))
            return (HalconFailure.Sdk,
                "FAILED AT: INSTALLED SDK — the HALCON native library could not be loaded. Install the HALCON runtime (same release as the wrapper) and restart the application.");
        if (!licenseError && versionMismatch)
            return (HalconFailure.SdkVersion,
                "FAILED AT: INSTALLED SDK — the installed HALCON is a different release than the application was built for.");
        if (licenseError && !licenseFileFound)
            return (HalconFailure.LicenseFile,
                "FAILED AT: LICENSE FILE — HALCON reports a license problem and there is no license file in the license folder.");
        if (licenseError && !dongleSeen)
            return (HalconFailure.Dongle,
                "FAILED AT: LICENSE / DONGLE — HALCON reports a license problem. A license file is present but no dongle is connected: plug the dongle in if this is a dongle license; otherwise the file is not valid for this computer or has expired.");
        if (licenseError)
            return (HalconFailure.License,
                "FAILED AT: LICENSE — a license file and a dongle are both present, but HALCON rejects the license (wrong dongle, wrong HALCON release, or expired). HALCON's message below says which.");
        return (HalconFailure.Unknown,
            "HALCON did not start, and the cause is not one this page recognises. HALCON's message below is the evidence.");
    }

    /// <summary>The report as plain text, for pasting into an e-mail to support.</summary>
    public static string ToText(HalconReport report)
    {
        var lines = new List<string>
        {
            $"HALCON diagnosis — {DateTime.Now:yyyy-MM-dd HH:mm:ss} — {Environment.MachineName}",
            report.Verdict,
            string.Empty,
            "HALCON's message:",
            string.IsNullOrWhiteSpace(report.HalconMessage) ? "  (none — HALCON started)" : "  " + report.HalconMessage,
        };
        void Group(string title, IReadOnlyList<HalconCheck> checks)
        {
            lines.Add(string.Empty);
            lines.Add(title);
            foreach (var c in checks) lines.Add($"  [{c.State}] {c.Name}: {c.Detail}");
        }
        Group("Installed SDK", report.Sdk);
        Group("License", report.License);
        Group("Dongle", report.Dongle);
        return string.Join(Environment.NewLine, lines);
    }
}
