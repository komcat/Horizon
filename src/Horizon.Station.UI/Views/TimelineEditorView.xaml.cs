using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Horizon.Station.UI.ViewModels;
using Horizon.Station.UI.ViewModels.Timeline;

namespace Horizon.Station.UI.Views;

/// <summary>
/// The Engineer page — a free-canvas sequence editor. Blocks are dragged anywhere on the
/// canvas (free in X, snapping to a numbered stage row on release) and joined by arrows
/// drawn from a block's out port to the next block; the chain of arrows from START is the
/// execution order. A parallel envelope is one node whose branches run concurrently.
/// Builds / runs Layer 2 programs via the <see cref="TimelineEditorViewModel"/>.
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

    /// <summary>
    /// Opens a program by name (Run-page step "Edit"). Already open and clean → left as-is;
    /// unsaved edits → asks before discarding them.
    /// </summary>
    public void OpenProgram(string name)
    {
        bool same = string.Equals(_vm.ProgramName, name, StringComparison.OrdinalIgnoreCase);
        if (same && !_vm.IsDirty) return;
        if (_vm.IsDirty && MessageBox.Show(Window.GetWindow(this)!,
                same
                    ? $"'{name}' has unsaved changes in the editor. Discard them and reload the saved program?"
                    : $"Discard unsaved changes to '{_vm.ProgramName}' and open '{name}'?",
                "Open program", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        _vm.LoadProgramByName(name);
    }

    // --- palette drag: double-click adds after the selected block, or drag onto the canvas ---

    private Point _paletteDragStart;
    private PaletteItemViewModel? _paletteDragItem;

    /// <summary>A param combo opened — re-read live options (e.g. the device's saved positions).</summary>
    private void OnParamDropDownOpened(object sender, EventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BlockParamViewModel p)
            p.RefreshOptions();
    }

    private void OnPaletteMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PaletteItemViewModel item })
            return;

        if (e.ClickCount >= 2)
        {
            item.AddCommand.Execute(null);   // adds after the selected block (or into the selected parallel group)
            _paletteDragItem = null;
            return;
        }

        _paletteDragStart = e.GetPosition(null);
        _paletteDragItem = item;
        _vm.ShowHelp(item.Spec);   // single click → show this command's help in the right panel
    }

    private void OnPaletteMouseMove(object sender, MouseEventArgs e)
    {
        if (_paletteDragItem == null || e.LeftButton != MouseButtonState.Pressed)
            return;

        var p = e.GetPosition(null);
        if (Math.Abs(p.X - _paletteDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(p.Y - _paletteDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var spec = _paletteDragItem.Spec;
        _paletteDragItem = null;
        DragDrop.DoDragDrop((DependencyObject)sender, spec, DragDropEffects.Copy);
        _vm.SetDropTarget(null);   // drag finished (dropped or cancelled) — drop the highlight
    }

    // --- palette drop on the canvas: onto a parallel envelope joins it, elsewhere makes a new block ---

    /// <summary>The canvas node an event came from (walks up to the node template's root).</summary>
    private static CanvasNodeViewModel? NodeOf(object? source)
    {
        for (var d = source as DependencyObject; d != null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is FrameworkElement { DataContext: CanvasNodeViewModel node })
                return node;
        return null;
    }

    private void OnCanvasDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(OpSpec)) is OpSpec)
        {
            e.Effects = DragDropEffects.Copy;
            _vm.SetDropTarget(_vm.ParallelAt(e.GetPosition(CanvasRoot)));
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void OnCanvasDragLeave(object sender, DragEventArgs e) => _vm.SetDropTarget(null);

    private void OnCanvasDrop(object sender, DragEventArgs e)
    {
        _vm.SetDropTarget(null);
        if (e.Data.GetData(typeof(OpSpec)) is not OpSpec spec) return;

        var p = e.GetPosition(CanvasRoot);
        if (_vm.ParallelAt(p) is { } envelope)
            _vm.AddToParallel(envelope, spec);
        else
            _vm.DropOpAt(spec, p.X - 90, p.Y);   // centre the new block under the pointer
        Focus();
        e.Handled = true;
    }

    // --- canvas gestures: drag a node, draw an arrow, pull a block out of an envelope, pan ---

    private enum Gesture { None, DragNode, Link, InnerArmed, Pan }

    private Gesture _gesture;
    private CanvasNodeViewModel? _gNode;        // node being dragged, or the arrow's source
    private ProcessBlockViewModel? _gBlock;     // block armed for pulling out of its envelope
    private Point _gStart;                      // canvas point where the gesture began
    private Vector _gOffset;                    // pointer − node origin, for DragNode
    private bool _gMoved;
    private bool _gRightPort;                   // arrow leaves the right port (else the bottom one)
    private Point _panStart;                    // pointer in the scroll viewer, for Pan
    private Point _panOrigin;                   // scroll offsets when the pan began

    private static bool PastDragThreshold(Point a, Point b)
        => Math.Abs(a.X - b.X) >= SystemParameters.MinimumHorizontalDragDistance
        || Math.Abs(a.Y - b.Y) >= SystemParameters.MinimumVerticalDragDistance;

    private void Begin(Gesture gesture, Point start)
    {
        _gesture = gesture;
        _gStart = start;
        _gMoved = false;
        CanvasRoot.CaptureMouse();
    }

    /// <summary>Ends the gesture in progress, finishing a node drag (snap to row) but dropping an unfinished arrow.</summary>
    private void EndGesture()
    {
        var gesture = _gesture;
        var node = _gNode;
        var moved = _gMoved;
        _gesture = Gesture.None;
        _gNode = null;
        _gBlock = null;

        RubberBand.Visibility = Visibility.Collapsed;
        RubberBand.Data = null;
        _vm.IsConnecting = _vm.IsLinkMode;
        if (gesture == Gesture.DragNode && node != null && moved)
            _vm.EndDrag(node);
        else
            _vm.DraggingNode = null;
        if (CanvasRoot.IsMouseCaptured) CanvasRoot.ReleaseMouseCapture();
    }

    private void OnNodeMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CanvasNodeViewModel node }) return;
        Focus();
        if (_vm.LinkClick(node)) { e.Handled = true; return; }   // arrow mode: the click picks from / to
        _vm.SelectNode(node, (Keyboard.Modifiers & ModifierKeys.Shift) != 0);

        var p = e.GetPosition(CanvasRoot);
        _gNode = node;
        _gOffset = p - new Point(node.X, node.Y);
        Begin(Gesture.DragNode, p);
        e.Handled = true;
    }

    private void OnOutPortMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CanvasNodeViewModel node } port) return;
        Focus();
        _gNode = node;
        _gRightPort = port.Tag as string == "R";
        _vm.IsConnecting = true;
        RubberBand.Visibility = Visibility.Visible;
        Begin(Gesture.Link, e.GetPosition(CanvasRoot));
        e.Handled = true;
    }

    private void OnInnerBlockMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ProcessBlockViewModel block }) return;
        Focus();
        if (_vm.LinkClick(block)) { e.Handled = true; return; }
        _vm.SelectInnerBlock(block);
        _gBlock = block;   // armed; a drag past the threshold pulls it out of the envelope
        Begin(Gesture.InnerArmed, e.GetPosition(CanvasRoot));
        e.Handled = true;
    }

    private void OnLinkMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LinkViewModel link }) return;
        Focus();
        _vm.SelectLink(link);
        e.Handled = true;
    }

    /// <summary>Right-click on an arrow: select it, so the menu that opens is clearly about that arrow.</summary>
    private void OnLinkRightMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LinkViewModel link }) return;
        Focus();
        _vm.SelectLink(link);   // not handled — the context menu still opens
    }

    private void OnRemoveLink(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LinkViewModel link }) _vm.RemoveLink(link);
    }

    /// <summary>Press on the empty canvas: clear the selection and pan with the drag.</summary>
    private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        _vm.CancelLinkMode();
        _vm.ClearSelection();
        _panStart = e.GetPosition(CanvasScroll);
        _panOrigin = new Point(CanvasScroll.HorizontalOffset, CanvasScroll.VerticalOffset);
        Begin(Gesture.Pan, e.GetPosition(CanvasRoot));
        e.Handled = true;
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (_gesture == Gesture.None) return;
        if (e.LeftButton != MouseButtonState.Pressed) { EndGesture(); return; }
        var p = e.GetPosition(CanvasRoot);

        switch (_gesture)
        {
            case Gesture.DragNode when _gNode != null:
                if (!_gMoved && !PastDragThreshold(p, _gStart)) return;
                _gMoved = true;
                _vm.DraggingNode = _gNode;
                _gNode.X = Math.Max(TimelineEditorViewModel.RowGutter, p.X - _gOffset.X);
                _gNode.Y = Math.Max(0, p.Y - _gOffset.Y);
                _vm.UpdateLinks();
                // A command block hovering a parallel envelope would move into it.
                _vm.SetDropTarget(_gNode.Kind == CanvasNodeKind.Block
                    ? _vm.ParallelAt(new Point(_gNode.X + _gNode.Width / 2, _gNode.Y + _gNode.Height / 2), _gNode)
                    : null);
                break;

            case Gesture.Link when _gNode != null:
                var (from, normal) = LinkRouter.OutPort(_gNode, _gRightPort);
                RubberBand.Data = LinkRouter.Rubber(from, normal, p);
                break;

            case Gesture.InnerArmed when _gBlock != null:
                if (!PastDragThreshold(p, _gStart)) return;
                // Pull the block out as a free node and carry on dragging that node.
                var node = _vm.PopOut(_gBlock, p.X - 90, p.Y - 20);
                _gBlock = null;
                if (node == null) { EndGesture(); return; }
                _gNode = node;
                _gOffset = new Vector(90, 20);
                _gesture = Gesture.DragNode;
                _gMoved = true;
                _vm.DraggingNode = node;
                break;

            case Gesture.Pan:
                var s = e.GetPosition(CanvasScroll);
                CanvasScroll.ScrollToHorizontalOffset(_panOrigin.X - (s.X - _panStart.X));
                CanvasScroll.ScrollToVerticalOffset(_panOrigin.Y - (s.Y - _panStart.Y));
                break;
        }
    }

    private void OnCanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_gesture == Gesture.None) return;

        if (_gesture == Gesture.Link && _gNode != null)
        {
            var from = _gNode;
            var target = _vm.NodeAt(e.GetPosition(CanvasRoot), from);
            EndGesture();
            if (target != null) _vm.Connect(from, target);
        }
        else
        {
            EndGesture();
        }
        e.Handled = true;
    }

    private void OnCanvasLostCapture(object sender, MouseEventArgs e)
    {
        if (_gesture != Gesture.None) EndGesture();
    }

    /// <summary>Ctrl + wheel zooms the canvas; a plain wheel scrolls it as usual.</summary>
    private void OnCanvasWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        _vm.Zoom += e.Delta > 0 ? 0.1 : -0.1;
        e.Handled = true;
    }

    // --- node layout: the view reports each node's measured size, then the rows are re-laid ---

    private bool _relayoutQueued;

    private void OnNodeSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CanvasNodeViewModel node } el) return;
        if (el.ActualWidth <= 0 || el.ActualHeight <= 0) return;   // folded away — keep its last real size
        node.Width = el.ActualWidth;
        node.Height = el.ActualHeight;
        if (_relayoutQueued) return;
        _relayoutQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _relayoutQueued = false;
            _vm.Relayout();
        }));
    }

    // --- node context menus ---

    private void OnRemoveNode(object sender, RoutedEventArgs e)
    {
        if (NodeOf(sender) is { } node) _vm.RemoveNode(node);
    }

    private void OnDisconnectNode(object sender, RoutedEventArgs e)
    {
        if (NodeOf(sender) is { } node) _vm.Disconnect(node);
    }

    private void OnToggleCollapse(object sender, RoutedEventArgs e)
    {
        if (NodeOf(sender) is { } node) _vm.ToggleCollapse(node);
        Focus();
    }

    private void OnToggleColumnBreak(object sender, RoutedEventArgs e)
    {
        if (NodeOf(sender) is { } node) _vm.ToggleColumnBreak(node);
    }

    private void OnCopyNode(object sender, RoutedEventArgs e)
    {
        if (NodeOf(sender) is not { } node) return;
        if (!node.IsSelected) _vm.SelectNode(node, range: false);   // right-click outside the selection copies that node
        _vm.CopySelection();
    }

    private void OnPasteAfterNode(object sender, RoutedEventArgs e)
    {
        if (NodeOf(sender) is not { } node) return;
        _vm.SelectNode(node, range: false);
        _vm.Paste();
    }

    private void OnMoveOutBlock(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ProcessBlockViewModel block })
            _vm.MoveOut(block);
    }

    // --- keyboard: Delete removes the selection, Ctrl+C / Ctrl+V copy / paste, Esc drops an unfinished arrow ---

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Don't hijack keys while the operator is editing a parameter field.
        if (e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase) return;

        if (e.Key == Key.Escape && _gesture != Gesture.None) { EndGesture(); e.Handled = true; return; }
        if (e.Key == Key.Escape && _vm.IsLinkMode) { _vm.ToggleLinkMode(); e.Handled = true; return; }
        if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
        {
            if (e.OriginalSource is ComboBox or ComboBoxItem or ListBoxItem) return;
            _vm.DeleteSelection();
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        if (e.Key == Key.C) { _vm.CopySelection(); e.Handled = true; }
        else if (e.Key == Key.V) { _vm.Paste(); e.Handled = true; }
    }

    private void OnHistory(object sender, RoutedEventArgs e)
        => new ProgramHistoryWindow(_host, _vm.ProgramName) { Owner = Window.GetWindow(this) }.Show();

    /// <summary>Opens the program picker (name + version + last-commit) and loads the chosen program.</summary>
    private void OnLoad(object sender, RoutedEventArgs e)
    {
        var dlg = new ProgramPickerWindow(_host) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true && dlg.SelectedProgramName is { } name)
            _vm.LoadProgramByName(name);
    }

    /// <summary>
    /// Program list row click — opens that program (asking first if the editor has unsaved
    /// changes). Clicks on the row's hide/show eye button are left to the button.
    /// </summary>
    private void OnProgramItemClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: ProgramListItem item }) return;
        for (var d = e.OriginalSource as DependencyObject; d != null && d != sender;
             d = VisualTreeHelper.GetParent(d))
            if (d is System.Windows.Controls.Primitives.ButtonBase) return;   // the eye toggle

        if (item.IsCurrent && !_vm.IsDirty) return;
        if (_vm.IsDirty && MessageBox.Show(Window.GetWindow(this)!,
                $"Discard unsaved changes to '{_vm.ProgramName}' and open '{item.Name}'?",
                "Open program", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        _vm.LoadProgramByName(item.Name);
    }

    private void OnCopyUid(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedBlock is { } block)
            try { Clipboard.SetText(block.Id); } catch { /* clipboard busy — ignore */ }
    }

    /// <summary>Copies every log line (the list is capped at 500) to the clipboard.</summary>
    private void OnCopyLog(object sender, RoutedEventArgs e)
    {
        if (_vm.Log.Count == 0) return;
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, _vm.Log));
            CopyLogButton.Content = $"Copied {_vm.Log.Count}";
        }
        catch
        {
            CopyLogButton.Content = "Clipboard busy";
        }

        // Put the caption back after a moment.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            CopyLogButton.Content = "Copy";
        };
        timer.Start();
    }

    private void OnQueryUid(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedBlock is { } block)
            new ProgramHistoryWindow(_host, _vm.ProgramName, block.Id) { Owner = Window.GetWindow(this) }.Show();
    }
}
