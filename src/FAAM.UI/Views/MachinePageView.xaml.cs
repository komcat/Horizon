using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FAAM.UI.ViewModels;

namespace FAAM.UI.Views;

/// <summary>The Machine page — engine header, section rail, section content, and the global log.</summary>
public partial class MachinePageView : UserControl
{
    private MachinePageViewModel? _viewModel;

    public MachinePageView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        // Ctrl+C (Copy's default gesture) over the focused log copies the selection.
        LogList.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, _) => CopySelected()));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel = DataContext as MachinePageViewModel;
        if (_viewModel != null)
            ((INotifyCollectionChanged)_viewModel.Log).CollectionChanged += OnLogChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel != null)
            ((INotifyCollectionChanged)_viewModel.Log).CollectionChanged -= OnLogChanged;
        _viewModel = null;
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || _viewModel is null || _viewModel.Log.Count == 0)
            return;
        LogList.ScrollIntoView(_viewModel.Log[^1]);
    }

    // --- log copy / select ---

    private void OnCopyLog(object sender, RoutedEventArgs e) => CopySelected();

    private void OnCopyAllLog(object sender, RoutedEventArgs e)
        => CopyLines(LogList.Items.OfType<LogLineViewModel>());

    private void OnSelectAllLog(object sender, RoutedEventArgs e) => LogList.SelectAll();

    /// <summary>Copies the selected lines (or all, if none selected) in display order.</summary>
    private void CopySelected()
    {
        var selected = new HashSet<object>(LogList.SelectedItems.Cast<object>());
        var lines = selected.Count > 0
            ? LogList.Items.OfType<LogLineViewModel>().Where(selected.Contains)
            : LogList.Items.OfType<LogLineViewModel>();
        CopyLines(lines);
    }

    private static void CopyLines(IEnumerable<LogLineViewModel> lines)
    {
        var sb = new StringBuilder();
        foreach (var l in lines)
            sb.Append(l.Time).Append('\t').Append(l.Level).Append('\t')
              .Append(l.Source).Append('\t').AppendLine(l.Message);
        if (sb.Length == 0) return;
        try { Clipboard.SetText(sb.ToString()); }
        catch { /* clipboard transiently locked by another process — ignore */ }
    }
}
