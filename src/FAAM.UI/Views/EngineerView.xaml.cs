using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using FAAM.UI.ViewModels;

namespace FAAM.UI.Views;

/// <summary>
/// The Engineer page — a non-coder visual process-sequence editor (command
/// palette, flow-chart of step blocks, per-block parameter panel). Builds, saves
/// and runs Layer 2 <c>.hwf</c> programs via the <see cref="ProcessEditorViewModel"/>.
/// </summary>
public partial class EngineerView : UserControl
{
    private readonly ProcessEditorViewModel _vm;

    public EngineerView(EngineHost host)
    {
        InitializeComponent();
        _vm = new ProcessEditorViewModel(host);
        DataContext = _vm;

        // Keep the newest log line in view. OnLog marshals to the UI thread before
        // mutating Log, so CollectionChanged fires here on the dispatcher — but
        // ScrollIntoView must be DEFERRED: calling it inside the CollectionChanged
        // notification re-enters the items pipeline (container generation) while the
        // tree walk is in progress and throws. Background priority runs it after.
        _vm.Log.CollectionChanged += (_, _) =>
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                var n = LogList.Items.Count;
                if (n > 0)
                    LogList.ScrollIntoView(LogList.Items[n - 1]);
            }));
    }

    /// <summary>Subscribes the run/log hooks and refreshes the program list — called by the shell on show.</summary>
    public void Activate() => _vm.Activate();

    /// <summary>Detaches the run/log hooks — called by the shell when leaving the page.</summary>
    public void Deactivate() => _vm.Deactivate();

    // --- Palette: double-click to append, or drag onto the flow chart ---

    private Point _paletteDragStart;
    private PaletteItemViewModel? _paletteDragItem;

    private void OnPaletteMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PaletteItemViewModel item })
            return;

        if (e.ClickCount >= 2)
        {
            // Double-click → append under the selected block (or at the end).
            item.AddCommand.Execute(null);
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
        RemoveDragAdorner();   // DoDragDrop is modal; clean up if the drag was cancelled
    }

    private DragInsertionAdorner? _dragAdorner;

    private void OnFlowDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(OpSpec)) is not OpSpec spec)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Copy;
        var p = e.GetPosition(FlowScroller);
        var index = FlowDropIndex(p);
        EnsureDragAdorner();
        _dragAdorner?.Update(LineYForIndex(index), p, spec.Display);
        e.Handled = true;
    }

    private void OnFlowDragLeave(object sender, DragEventArgs e)
    {
        // DragLeave also fires when crossing child boundaries — only tear down the
        // feedback when the cursor has truly left the flow area.
        var p = e.GetPosition(FlowScroller);
        if (p.X >= 0 && p.Y >= 0 && p.X <= FlowScroller.ActualWidth && p.Y <= FlowScroller.ActualHeight)
            return;
        RemoveDragAdorner();
    }

    private void OnFlowDrop(object sender, DragEventArgs e)
    {
        RemoveDragAdorner();
        if (e.Data.GetData(typeof(OpSpec)) is not OpSpec spec)
            return;
        _vm.InsertBlock(spec, FlowDropIndex(e.GetPosition(FlowScroller)));
        e.Handled = true;
    }

    private void EnsureDragAdorner()
    {
        if (_dragAdorner != null) return;
        var layer = AdornerLayer.GetAdornerLayer(FlowScroller);
        if (layer == null) return;
        _dragAdorner = new DragInsertionAdorner(FlowScroller);
        layer.Add(_dragAdorner);
    }

    private void RemoveDragAdorner()
    {
        if (_dragAdorner == null) return;
        AdornerLayer.GetAdornerLayer(FlowScroller)?.Remove(_dragAdorner);
        _dragAdorner = null;
    }

    /// <summary>Insertion index for a drop at <paramref name="p"/> (viewport coords): before the first block whose vertical midpoint is below the drop, else append.</summary>
    private int FlowDropIndex(Point p)
    {
        for (int i = 0; i < FlowList.Items.Count; i++)
        {
            if (FlowList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement c)
                continue;
            var top = c.TransformToAncestor(FlowScroller).Transform(new Point(0, 0)).Y;
            if (p.Y < top + c.ActualHeight / 2)
                return i;
        }
        return FlowList.Items.Count;
    }

    /// <summary>Y (viewport coords) of the insertion line for <paramref name="index"/> — the top of that block, or the bottom of the last block when appending.</summary>
    private double LineYForIndex(int index)
    {
        var count = FlowList.Items.Count;
        if (count == 0) return 14;

        if (index < count &&
            FlowList.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement c)
            return c.TransformToAncestor(FlowScroller).Transform(new Point(0, 0)).Y;

        if (FlowList.ItemContainerGenerator.ContainerFromIndex(count - 1) is FrameworkElement last)
            return last.TransformToAncestor(FlowScroller).Transform(new Point(0, 0)).Y + last.ActualHeight;

        return 14;
    }
}
