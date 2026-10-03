using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FAAM.UI.ViewModels;
using FAAM.UI.ViewModels.Timeline;

namespace FAAM.UI.Views;

/// <summary>
/// The Engineer page — a vertical timeline (Gantt) editor. Device lanes run across
/// the top; ordered stages down the left as rows. Drop palette commands onto a lane's
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
        _vm.ShowHelp(item.Spec);   // single click → show this command's help in the right panel
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
        ClearDropTarget();   // drag finished (dropped or cancelled) — drop the highlight
    }

    // --- block drag: move an already-placed block to another cell ---

    private Point _blockDragStart;
    private ProcessBlockViewModel? _blockDragItem;

    private void OnBlockMouseMove(object sender, MouseEventArgs e)
    {
        if (_blockDragItem == null || e.LeftButton != MouseButtonState.Pressed)
            return;

        var p = e.GetPosition(null);
        if (System.Math.Abs(p.X - _blockDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            System.Math.Abs(p.Y - _blockDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var block = _blockDragItem;
        _blockDragItem = null;
        DragDrop.DoDragDrop((DependencyObject)sender, block, DragDropEffects.Move);
        ClearDropTarget();   // drag finished (dropped or cancelled) — drop the highlight
    }

    // --- cell drag-drop: palette op = copy (OpSpec); placed block = move (ProcessBlockViewModel) ---

    /// <summary>The cell currently lit as the drop target, so we can clear it when the drag moves on.</summary>
    private CellViewModel? _dropTargetCell;

    private void SetDropTarget(CellViewModel? cell)
    {
        if (ReferenceEquals(_dropTargetCell, cell)) return;
        if (_dropTargetCell != null) _dropTargetCell.IsDropTarget = false;
        _dropTargetCell = cell;
        if (cell != null) cell.IsDropTarget = true;
    }

    private void ClearDropTarget() => SetDropTarget(null);

    private void OnCellDragOver(object sender, DragEventArgs e)
    {
        var effects = DragDropEffects.None;
        CellViewModel? target = null;
        if (sender is FrameworkElement { DataContext: CellViewModel cell })
        {
            if (e.Data.GetData(typeof(OpSpec)) is OpSpec spec && cell.Lane.Accepts(spec))
            {
                effects = DragDropEffects.Copy;
                target = cell;
            }
            else if (e.Data.GetData(typeof(ProcessBlockViewModel)) is ProcessBlockViewModel block
                     && cell.Lane.Accepts(block.Spec))
            {
                effects = DragDropEffects.Move;
                target = cell;
            }
        }
        e.Effects = effects;
        SetDropTarget(target);   // highlight only the cell a compatible block can drop into
        e.Handled = true;
    }

    private void OnCellDragLeave(object sender, DragEventArgs e)
    {
        // Clear the highlight when the drag leaves the lit cell (e.g. into the gap
        // between cells); moving onto another cell re-lights it via OnCellDragOver.
        if (sender is FrameworkElement { DataContext: CellViewModel cell } && ReferenceEquals(cell, _dropTargetCell))
            ClearDropTarget();
    }

    private void OnCellDrop(object sender, DragEventArgs e)
    {
        ClearDropTarget();
        if (sender is not FrameworkElement { DataContext: CellViewModel cell }) return;

        if (e.Data.GetData(typeof(OpSpec)) is OpSpec spec)
            _vm.DropOp(spec, cell.Lane, cell.Lane.Cells.IndexOf(cell));
        else if (e.Data.GetData(typeof(ProcessBlockViewModel)) is ProcessBlockViewModel block)
            _vm.MoveBlock(block, cell.Lane, cell.Lane.Cells.IndexOf(cell));
        else
            return;
        e.Handled = true;
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
            _blockDragStart = e.GetPosition(null);
            _blockDragItem = block;   // armed; a drag past the threshold moves it to another cell
            e.Handled = true;   // don't also trigger the cell click
        }
    }

    private void OnRemoveStage(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: StageViewModel stage })
            _vm.RemoveStage(stage);
    }

    private void OnStageHeaderClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: StageViewModel stage }) return;

        // Shift extends the selection from the anchor to here; a plain click selects one.
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            _vm.SelectStageRange(stage);
        else
            _vm.SelectStage(stage);

        Focus();   // pull keyboard focus here so Ctrl+C / Ctrl+V reach OnPreviewKeyDown
    }

    // --- stage context menu (insert / copy / paste) ---

    private void OnInsertStageLeft(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: StageViewModel stage })
            _vm.InsertStageRelative(stage, toRight: false);
    }

    private void OnInsertStageRight(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: StageViewModel stage })
            _vm.InsertStageRelative(stage, toRight: true);
    }

    private void OnCopyStages(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: StageViewModel stage })
            _vm.CopyStagesFor(stage);
    }

    private void OnPasteStages(object sender, RoutedEventArgs e) => _vm.PasteStages();

    // --- stage keyboard shortcuts: Ctrl+C copies the selection, Ctrl+V pastes ---

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Don't hijack copy/paste while the operator is editing a parameter field.
        if (e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase) return;
        if (Keyboard.Modifiers != ModifierKeys.Control) return;

        if (e.Key == Key.C) { _vm.CopySelectedStages(); e.Handled = true; }
        else if (e.Key == Key.V) { _vm.PasteStages(); e.Handled = true; }
    }

    // Keep the frozen lane-header row and stage-label column aligned with the body's
    // scroll position (the body owns both scrollbars; these two are slaved to it).
    private void OnBodyScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (HeaderScroll != null) HeaderScroll.ScrollToHorizontalOffset(e.HorizontalOffset);
        if (LabelScroll != null) LabelScroll.ScrollToVerticalOffset(e.VerticalOffset);
    }

    // A wheel over the frozen stage-label column drives the body's vertical scroll
    // (which then re-syncs the label column), so the two panes can't drift apart.
    private void OnFrozenPaneWheel(object sender, MouseWheelEventArgs e)
    {
        if (BodyScroll == null) return;
        BodyScroll.ScrollToVerticalOffset(BodyScroll.VerticalOffset - e.Delta);
        e.Handled = true;
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
    private void OnProgramItemClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: ProgramListItem item }) return;
        for (var d = e.OriginalSource as DependencyObject; d != null && d != sender;
             d = System.Windows.Media.VisualTreeHelper.GetParent(d))
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

    private void OnQueryUid(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedBlock is { } block)
            new ProgramHistoryWindow(_host, _vm.ProgramName, block.Id) { Owner = Window.GetWindow(this) }.Show();
    }
}
