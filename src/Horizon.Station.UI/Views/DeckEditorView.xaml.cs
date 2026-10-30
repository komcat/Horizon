using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Horizon.Station.UI.ViewModels;

namespace Horizon.Station.UI.Views;

/// <summary>
/// The Engineer page's <b>Process Deck</b> tab — a create/save/delete/edit surface
/// for a deck (an ordered collection of programs with a transition rule between
/// each). Backed by <see cref="DeckEditorViewModel"/>.
/// </summary>
public partial class DeckEditorView : UserControl
{
    private readonly DeckEditorViewModel _vm;
    private readonly EngineHost _host;

    public DeckEditorView(EngineHost host)
    {
        InitializeComponent();
        _host = host;
        _vm = new DeckEditorViewModel(host);
        DataContext = _vm;

        // Keep the newest log line in view (deferred — scrolling inside the
        // CollectionChanged notification re-enters the items pipeline and throws).
        _vm.Log.CollectionChanged += (_, _) =>
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                var n = LogList.Items.Count;
                if (n > 0) LogList.ScrollIntoView(LogList.Items[n - 1]);
            }));
    }

    public void Activate() => _vm.Activate();

    public void Deactivate() => _vm.Deactivate();

    private void OnHistory(object sender, RoutedEventArgs e)
        => new DeckHistoryWindow(_host, _vm.DeckUid, _vm.DeckName) { Owner = Window.GetWindow(this) }.Show();

    /// <summary>Opens the deck picker (name + version + last-commit) and loads the chosen deck.</summary>
    private void OnLoad(object sender, RoutedEventArgs e)
    {
        var dlg = new DeckPickerWindow(_host) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true && dlg.SelectedDeckName is { } name)
            _vm.LoadDeckByName(name);
    }

    private void OnSaveAs(object sender, RoutedEventArgs e)
    {
        var name = PromptForName(
            "Save deck as",
            "Save the current deck as a new, independent deck (its own history) you " +
            "can edit for a product variation.\n\nNew deck name:",
            SuggestDeckName());
        if (string.IsNullOrWhiteSpace(name)) return;   // cancelled
        _vm.SaveDeckAs(name);                          // VM validates + reports via Status
    }

    /// <summary>Suggests a free deck name based on the current one (e.g. "Cure-copy").</summary>
    private string SuggestDeckName()
    {
        var existing = _host.Process.ListDecks();
        var baseName = $"{_vm.DeckName}-copy";
        var name = baseName;
        for (int i = 2; existing.Contains(name, StringComparer.OrdinalIgnoreCase); i++)
            name = $"{baseName}{i}";
        return name;
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        var others = _host.Process.Products
            .Where(p => !string.Equals(p, _host.Process.ActiveProduct, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (others.Count == 0)
        {
            MessageBox.Show(Window.GetWindow(this), "No other product to import a deck from.",
                "Import deck", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var choice = PromptImport(others);
        if (choice is not { } c) return;
        _vm.ImportDeck(c.Product, c.Deck, c.Name);   // VM validates + reports via Status
    }

    /// <summary>Suggests a free deck name in the active product, defaulting to the source name.</summary>
    private string SuggestDeckNameFrom(string baseName)
    {
        var existing = _host.Process.ListDecks();
        var name = baseName;
        for (int i = 2; existing.Contains(name, StringComparer.OrdinalIgnoreCase); i++)
            name = $"{baseName}-{i}";
        return name;
    }

    /// <summary>
    /// Modal import picker: choose a source product, one of its decks, and the name to
    /// save it under here. Returns null if cancelled or incomplete.
    /// </summary>
    private (string Product, string Deck, string Name)? PromptImport(List<string> others)
    {
        var productCombo = new ComboBox { Height = 26, Margin = new Thickness(0, 4, 0, 10), ItemsSource = others };
        var deckCombo = new ComboBox { Height = 26, Margin = new Thickness(0, 4, 0, 10) };
        var nameBox = new TextBox { Height = 26, Margin = new Thickness(0, 4, 0, 14), MinWidth = 300, VerticalContentAlignment = VerticalAlignment.Center };

        productCombo.SelectionChanged += (_, _) =>
        {
            var decks = productCombo.SelectedItem is string prod
                ? _host.Process.ListDecksOf(prod).ToList()
                : new List<string>();
            deckCombo.ItemsSource = decks;
            deckCombo.SelectedItem = decks.FirstOrDefault();
        };
        deckCombo.SelectionChanged += (_, _) =>
        {
            if (deckCombo.SelectedItem is string deck && !string.IsNullOrWhiteSpace(deck))
                nameBox.Text = SuggestDeckNameFrom(deck);
        };
        productCombo.SelectedItem = others[0];   // fires the handlers → fills decks + name

        var ok = new Button { Content = "Import", Width = 90, Height = 26, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", Width = 80, Height = 26, IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(16), MinWidth = 320 };
        panel.Children.Add(new TextBlock
        {
            Text = "Import a deck from another product into the active product. The programs it references are copied too.",
            TextWrapping = TextWrapping.Wrap, MaxWidth = 320, Margin = new Thickness(0, 0, 0, 10),
        });
        panel.Children.Add(new TextBlock { Text = "Source product" });
        panel.Children.Add(productCombo);
        panel.Children.Add(new TextBlock { Text = "Deck" });
        panel.Children.Add(deckCombo);
        panel.Children.Add(new TextBlock { Text = "Save as deck name" });
        panel.Children.Add(nameBox);
        panel.Children.Add(buttons);

        var dlg = new Window
        {
            Title = "Import deck",
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this),
            ShowInTaskbar = false,
        };
        ok.Click += (_, _) =>
        {
            if (productCombo.SelectedItem is string && deckCombo.SelectedItem is string
                && !string.IsNullOrWhiteSpace(nameBox.Text))
                dlg.DialogResult = true;   // else keep the dialog open
        };

        if (dlg.ShowDialog() != true) return null;
        return ((string)productCombo.SelectedItem, (string)deckCombo.SelectedItem, nameBox.Text.Trim());
    }

    /// <summary>A minimal modal text prompt (mirrors HomeView's). Returns null if cancelled.</summary>
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
}
