using System.Collections.Specialized;
using System.Windows.Controls;

namespace Horizon.UI.Views;

/// <summary>The Process Sequence page — deck editor and runner.</summary>
public partial class DeckPageView : UserControl
{
    public DeckPageView()
    {
        InitializeComponent();
        // Keep the execution log scrolled to the newest line.
        ((INotifyCollectionChanged)LogList.Items).CollectionChanged += OnLogChanged;
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && LogList.Items.Count > 0)
            LogList.ScrollIntoView(LogList.Items[^1]);
    }
}
