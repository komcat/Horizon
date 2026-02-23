using System.Windows;
using Horizon.Core;

namespace Horizon.App;

public partial class App : Application
{
    private Horizon.Core.AppContext? _appContext;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _appContext = new Horizon.Core.AppContext();

        var mainWindow = new MainWindow(_appContext);
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _appContext?.Dispose();
        base.OnExit(e);
    }
}
