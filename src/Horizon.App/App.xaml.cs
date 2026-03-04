using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Horizon.App.Logging;
using Horizon.Core;
using Serilog;
using Serilog.Events;

namespace Horizon.App;

public partial class App : Application
{
    private Horizon.Core.AppContext? _appContext;

    internal static WpfOutputSink UiSink { get; } = new();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool AddDllDirectory(string lpPathName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDefaultDllDirectories(uint directoryFlags);

    private const uint LOAD_LIBRARY_SEARCH_DEFAULT_DIRS = 0x00001000;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Configure Serilog
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Horizon", "logs");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(logDir, "horizon-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                restrictedToMinimumLevel: LogEventLevel.Debug,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.Sink(UiSink, LogEventLevel.Information)
            .CreateLogger();

        InitializePylonEnvironment();

        _appContext = new Horizon.Core.AppContext();
        _appContext.LogSink = new SerilogLogSink();

        // Show splash screen and connect all hardware
        var splash = new SplashWindow(_appContext);
        splash.Show();

        await splash.RunConnectionsAsync();

        // Show main window, then close splash
        var mainWindow = new MainWindow(_appContext);
        mainWindow.Show();
        splash.Close();
    }

    private static void InitializePylonEnvironment()
    {
        // Find the Pylon installation directory
        var pylonRoot = Environment.GetEnvironmentVariable("PYLON_ROOT")
                        ?? FindPylonInstallation();

        if (string.IsNullOrEmpty(pylonRoot))
            return;

        var runtimeDir = Path.Combine(pylonRoot, "Runtime", "x64");
        if (!Directory.Exists(runtimeDir))
            return;

        // Set PYLON_ROOT if not already set
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PYLON_ROOT")))
            Environment.SetEnvironmentVariable("PYLON_ROOT", pylonRoot);

        // Add runtime dir to PATH so native DLLs are found
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        if (!path.Contains(runtimeDir, StringComparison.OrdinalIgnoreCase))
            Environment.SetEnvironmentVariable("PATH", $"{runtimeDir};{path}");

        // Set GENICAM_GENTL64_PATH so Pylon finds the GigE transport layer
        var gentlPath = Environment.GetEnvironmentVariable("GENICAM_GENTL64_PATH") ?? "";
        if (!gentlPath.Contains(runtimeDir, StringComparison.OrdinalIgnoreCase))
        {
            gentlPath = string.IsNullOrEmpty(gentlPath) ? runtimeDir : $"{runtimeDir};{gentlPath}";
            Environment.SetEnvironmentVariable("GENICAM_GENTL64_PATH", gentlPath);
        }

        // Add to Windows DLL search path
        try
        {
            SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
            AddDllDirectory(runtimeDir);
            AddDllDirectory(AppDomain.CurrentDomain.BaseDirectory);
        }
        catch { }
    }

    private static string? FindPylonInstallation()
    {
        // Check common Pylon installation paths
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var baslerDir = Path.Combine(programFiles, "Basler");

        if (!Directory.Exists(baslerDir))
            return null;

        // Find the latest pylon version (e.g. "pylon 8", "pylon 7")
        var pylonDirs = Directory.GetDirectories(baslerDir, "pylon*")
            .OrderByDescending(d => d)
            .ToList();

        foreach (var dir in pylonDirs)
        {
            if (Directory.Exists(Path.Combine(dir, "Runtime", "x64")))
                return dir;
        }

        return null;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _appContext?.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
