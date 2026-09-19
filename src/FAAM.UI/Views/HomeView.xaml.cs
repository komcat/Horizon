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

    // Each gated tile's designed tooltip, captured before the access gate starts
    // swapping in "needs level X" text, so it comes back when the tile is unlocked.
    private readonly Dictionary<Button, object?> _tileTips = new();

    /// <summary>Raised when the operator clicks RUN.</summary>
    public event Action? RunRequested;

    /// <summary>Raised when the operator clicks Machine.</summary>
    public event Action? MachineRequested;

    /// <summary>Raised when the operator clicks Engineer.</summary>
    public event Action? EngineerRequested;

    /// <summary>Raised when the operator clicks Vision Alignment (vision profiles + gripped FAU).</summary>
    public event Action? VisionAlignmentRequested;

    /// <summary>Raised when the operator clicks Script.</summary>
    public event Action? ScriptRequested;

    /// <summary>Raised when the operator clicks Calibration.</summary>
    public event Action? CalibrationRequested;

    /// <summary>Raised when the operator clicks Repository.</summary>
    public event Action? RepositoryRequested;

    /// <summary>Raised when the operator clicks DUT Data (per-serial UV signal snapshots).</summary>
    public event Action? DutDataRequested;


    public HomeView() => InitializeComponent();

    /// <summary>Supplies the engine host and populates the product bar.</summary>
    public void Attach(EngineHost host)
    {
        _host = host;
        _host.Process.ProductChanged += _ => RefreshProducts();
        _host.Session.Changed += ApplyAccess;
        RefreshProducts();
        ApplyAccess();
    }

    /// <summary>
    /// Enables each tile for the signed-in level (see <see cref="AccessPolicy"/>) and
    /// tells a locked-out operator what they would need. Logged out, everything is off —
    /// including the product bar, since switching products re-scopes the whole station.
    /// Re-run on every <see cref="Session.Changed"/>.
    /// </summary>
    private void ApplyAccess()
    {
        if (_host == null) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(ApplyAccess); return; }

        var session = _host.Session;
        Gate(RunTile, AccessPolicy.Run);
        Gate(EngineerTile, AccessPolicy.Engineer);
        Gate(MachineTile, AccessPolicy.Machine);
        Gate(VisionAlignmentTile, AccessPolicy.VisionAlignment);
        Gate(ScriptTile, AccessPolicy.Script);
        Gate(CalibrationTile, AccessPolicy.Calibration);
        Gate(RepositoryTile, AccessPolicy.Repository);
        Gate(DutDataTile, AccessPolicy.DutData);

        // Switching to an existing product is floor work; creating one is authoring.
        var canSwitch = session.Has(AccessPolicy.ProductSwitch);
        ProductCombo.IsEnabled = canSwitch;
        OpenButton.IsEnabled = canSwitch;
        SaveAsButton.IsEnabled = session.Has(AccessPolicy.ProductCreate);

        void Gate(Button tile, AccessLevel required)
        {
            if (!_tileTips.ContainsKey(tile)) _tileTips[tile] = tile.ToolTip;

            var allowed = session.Has(required);
            tile.IsEnabled = allowed;
            tile.ToolTip = allowed
                ? _tileTips[tile]
                : session.IsLoggedIn
                    ? $"Needs {required} level — you are signed in as {session.Level}."
                    : "Sign in to use this station.";
        }
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

    /// <summary>
    /// Clones the active product into a new name — copying its taught positions +
    /// vision profiles so a new unit/part starts from the existing dataset instead of
    /// an empty product. Programs are shared (not copied); DUT history starts empty.
    /// Prompts for the new name in a dialog (not the product box) and, on success,
    /// loads the new product.
    /// </summary>
    private void OnSaveAsProduct(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;

        var owner = Window.GetWindow(this);
        var source = _host.Process.ActiveProduct;
        if (string.IsNullOrWhiteSpace(source))
        {
            MessageBox.Show(owner, "Load a product first, then Save As to clone it.",
                "Save product as", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var newName = PromptForName(
            "Save product as",
            $"Clone '{source}' to a new product.\n\n" +
            "Positions, vision profiles, and programs / decks copy over so you can " +
            "edit them for the variation. DUT history starts empty.\n\nNew product name:",
            SuggestProductName(source));
        if (string.IsNullOrWhiteSpace(newName)) return;   // cancelled or blank
        newName = newName.Trim();

        if (string.Equals(newName, source, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(owner, "Pick a name different from the active product.",
                "Save product as", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_host.Process.Products.Contains(newName, StringComparer.OrdinalIgnoreCase))
        {
            MessageBox.Show(owner, $"Product '{newName}' already exists — pick a new name.",
                "Save product as", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var cloned = _host.Process.CloneProduct(source, newName);
        if (!cloned.IsSuccess)
        {
            MessageBox.Show(owner, $"Save As failed: {cloned.Message}",
                "Save product as", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var loaded = _host.Process.LoadProduct(newName);
        if (!loaded.IsSuccess)
            Log(LogLevel.Warning, $"Open '{newName}' failed: {loaded.Message}");
        Log(LogLevel.Info, $"Saved product as '{newName}' (cloned from '{source}').");
        RefreshProducts();
    }

    /// <summary>Suggests a free product name based on <paramref name="source"/> (e.g. "FAAM-copy").</summary>
    private string SuggestProductName(string source)
    {
        var baseName = $"{source}-copy";
        var name = baseName;
        for (int i = 2; _host!.Process.Products.Contains(name, StringComparer.OrdinalIgnoreCase); i++)
            name = $"{baseName}{i}";
        return name;
    }

    /// <summary>A minimal modal text prompt (WPF has none built in). Returns null if cancelled.</summary>
    private string? PromptForName(string title, string message, string initial)
    {
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 8, 0, 12), MinWidth = 280 };
        var ok = new Button { Content = "OK", Width = 80, Height = 26, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", Width = 80, Height = 26, IsCancel = true };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 });
        panel.Children.Add(box);
        panel.Children.Add(buttons);

        var dlg = new Window
        {
            Title = title,
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this),
            ShowInTaskbar = false,
        };
        ok.Click += (_, _) => dlg.DialogResult = true;
        dlg.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };

        return dlg.ShowDialog() == true ? box.Text : null;
    }

    private void Log(LogLevel level, string message)
        => _host?.Log.Add(new LogEntry(level, DateTimeOffset.Now, "FAAM", message));

    private void OnRunClick(object sender, RoutedEventArgs e) => RunRequested?.Invoke();

    private void OnMachineClick(object sender, RoutedEventArgs e) => MachineRequested?.Invoke();

    private void OnEngineerClick(object sender, RoutedEventArgs e) => EngineerRequested?.Invoke();

    private void OnVisionAlignmentClick(object sender, RoutedEventArgs e) => VisionAlignmentRequested?.Invoke();

    private void OnScriptClick(object sender, RoutedEventArgs e) => ScriptRequested?.Invoke();

    private void OnCalibrationClick(object sender, RoutedEventArgs e) => CalibrationRequested?.Invoke();

    private void OnRepositoryClick(object sender, RoutedEventArgs e) => RepositoryRequested?.Invoke();

    private void OnDutDataClick(object sender, RoutedEventArgs e) => DutDataRequested?.Invoke();
}
