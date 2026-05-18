using System.Collections.Specialized;
using System.Windows.Controls;

namespace Horizon.UI.Views;

/// <summary>The Programming page — process-program step editor.</summary>
public partial class ProgramEditorPageView : UserControl
{
    public ProgramEditorPageView()
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
