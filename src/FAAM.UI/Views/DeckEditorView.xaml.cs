using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FAAM.UI.ViewModels;

namespace FAAM.UI.Views;

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
}
