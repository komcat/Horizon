using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FAAM.UI.ViewModels;
using FAAM.UI.ViewModels.Timeline;

namespace FAAM.UI.Views;

/// <summary>
/// The Engineer page — a horizontal timeline (Gantt) editor. Device lanes run down
/// the left; ordered stages across the top. Drop palette commands onto a lane's
/// stage cell; commands in the same stage on different lanes run concurrently (a
/// barrier ends each stage). Builds / runs Layer 2 programs via the
/// <see cref="TimelineEditorViewModel"/> (compiled through <c>flow.parallel</c>).
/// </summary>
public partial class TimelineEditorView : UserControl
{
    private readonly TimelineEditorViewModel _vm;
    private readonly EngineHost _host;

    public TimelineEditorView(EngineHost host)
    {
        InitializeComponent();
        _host = host;
        _vm = new TimelineEditorViewModel(host);
        DataContext = _vm;

        // Keep the newest log line in view (deferred — calling ScrollIntoView inside
        // the CollectionChanged notification re-enters the items pipeline and throws).
        _vm.Log.CollectionChanged += (_, _) =>
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                var n = LogList.Items.Count;
                if (n > 0) LogList.ScrollIntoView(LogList.Items[n - 1]);
            }));
    }

    public void Activate() => _vm.Activate();

    public void Deactivate() => _vm.Deactivate();

    // --- palette drag: double-click adds to the selected cell, or drag onto a cell ---

    private Point _paletteDragStart;
    private PaletteItemViewModel? _paletteDragItem;

    private void OnPaletteMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PaletteItemViewModel item })
            return;

        if (e.ClickCount >= 2)
        {
            item.AddCommand.Execute(null);   // adds to the selected cell
            _paletteDragItem = null;
            return;
        }

        _paletteDragStart = e.GetPosition(null);
        _paletteDragItem = item;
    }

    private void OnPaletteMouseMove(object sender, MouseEventArgs e)
    {
        if (_paletteDragItem == null || e.LeftButton != MouseButtonState.Pressed)
            return;

        var p = e.GetPosition(null);
        if (System.Math.Abs(p.X - _paletteDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            System.Math.Abs(p.Y - _paletteDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var spec = _paletteDragItem.Spec;
        _paletteDragItem = null;
        DragDrop.DoDragDrop((DependencyObject)sender, spec, DragDropEffects.Copy);
    }

    // --- cell drag-drop ---

    private void OnCellDragOver(object sender, DragEventArgs e)
    {
        e.Effects =
            sender is FrameworkElement { DataContext: CellViewModel cell } &&
            e.Data.GetData(typeof(OpSpec)) is OpSpec spec && cell.Lane.Accepts(spec.DeviceKind)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnCellDrop(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CellViewModel cell } &&
            e.Data.GetData(typeof(OpSpec)) is OpSpec spec)
        {
            _vm.DropOp(spec, cell.Lane, cell.Lane.Cells.IndexOf(cell));
            e.Handled = true;
        }
    }

    // --- selection ---

    private void OnCellClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CellViewModel cell })
            _vm.SelectCell(cell);
    }

    private void OnBlockClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ProcessBlockViewModel block })
        {
            _vm.SelectedBlock = block;
            e.Handled = true;   // don't also trigger the cell click
        }
    }

    private void OnRemoveStage(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: StageViewModel stage })
            _vm.RemoveStage(stage);
    }

    private void OnHistory(object sender, RoutedEventArgs e)
        => new ProgramHistoryWindow(_host, _vm.ProgramName) { Owner = Window.GetWindow(this) }.Show();
}
