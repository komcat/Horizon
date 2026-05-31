using System.Windows;
using System.Windows.Controls;
using Horizon.Common;
using Horizon.Engine;

namespace FAAM.UI.Views;

/// <summary>
/// The home launcher — a product bar plus a tile grid. Tiles raise events the shell
/// turns into navigation; the shell owns the pages, the view just signals intent.
/// The product bar loads / creates the active product (defaulting to FAAM); a
/// switch re-scopes programs and positions together via Layer 2.
/// </summary>
public partial class HomeView : UserControl
{
    private EngineHost? _host;

    /// <summary>Raised when the operator clicks RUN.</summary>
    public event Action? RunRequested;

    /// <summary>Raised when the operator clicks Machine.</summary>
    public event Action? MachineRequested;

    /// <summary>Raised when the operator clicks Engineer.</summary>
    public event Action? EngineerRequested;

    public HomeView() => InitializeComponent();

    /// <summary>Supplies the engine host and populates the product bar.</summary>
    public void Attach(EngineHost host)
    {
        _host = host;
        _host.Process.ProductChanged += _ => RefreshProducts();
        RefreshProducts();
    }

    /// <summary>Repopulates the product list and shows the active one.</summary>
    private void RefreshProducts()
    {
        if (_host == null) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(RefreshProducts); return; }

        var active = _host.Process.ActiveProduct;
        ProductCombo.ItemsSource = _host.Process.Products;
        ProductCombo.Text = active ?? string.Empty;
        ActiveProductText.Text = active == null ? "(no product loaded)" : $"Active: {active}";
    }

    private void OnOpenProduct(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;

        var name = (ProductCombo.SelectedItem as string ?? ProductCombo.Text)?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            Log(LogLevel.Warning, "Enter or pick a product name first.");
            return;
        }

        if (!_host.Process.Products.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            var ask = MessageBox.Show(
                Window.GetWindow(this),
                $"Product '{name}' does not exist. Create it?",
                "New product", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (ask != MessageBoxResult.Yes) return;

            var created = _host.Process.CreateProduct(name);
            if (!created.IsSuccess)
            {
                Log(LogLevel.Warning, $"Create '{name}' failed: {created.Message}");
                return;
            }
        }

        var loaded = _host.Process.LoadProduct(name);
        if (!loaded.IsSuccess)
            Log(LogLevel.Warning, $"Open '{name}' failed: {loaded.Message}");
        RefreshProducts();
    }

    private void Log(LogLevel level, string message)
        => _host?.Log.Add(new LogEntry(level, DateTimeOffset.Now, "FAAM", message));

    private void OnRunClick(object sender, RoutedEventArgs e) => RunRequested?.Invoke();

    private void OnMachineClick(object sender, RoutedEventArgs e) => MachineRequested?.Invoke();

    private void OnEngineerClick(object sender, RoutedEventArgs e) => EngineerRequested?.Invoke();
}
