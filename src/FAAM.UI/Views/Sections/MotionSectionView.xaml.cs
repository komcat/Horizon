using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using FAAM.UI.ViewModels.Sections;

namespace FAAM.UI.Views.Sections;

/// <summary>
/// Machine-page Motion section — live per-axis position, jog, velocity, and Stop, plus the
/// position grid beside the position map. The grid gets one value column per installed axis
/// of the selected device (rebuilt on device change). The grid / map split is remembered in
/// <c>faam_position_map.json</c>.
/// </summary>
public partial class MotionSectionView : UserControl
{
    /// <summary>Columns defined in XAML (Map, Name) — axis columns follow them.</summary>
    private const int FixedColumns = 2;
    private const double AxisColumnWidth = 84;

    private MotionSectionViewModel? _vm;

    public MotionSectionView()
    {
        InitializeComponent();
        if (PositionMapStore.LoadListWidth() is double w && double.IsFinite(w) && w >= ListColumn.MinWidth)
            ListColumn.Width = new GridLength(w);

        DataContextChanged += (_, _) => Attach(IsLoaded ? DataContext as MotionSectionViewModel : null);
        Loaded += (_, _) => Attach(DataContext as MotionSectionViewModel);
        Unloaded += (_, _) => Attach(null);
    }

    /// <summary>Follows the view-model's axis set while loaded (the view is recreated per visit, the VM is not).</summary>
    private void Attach(MotionSectionViewModel? vm)
    {
        if (_vm == vm) return;
        if (_vm != null) _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _vm = vm;
        if (_vm == null) return;
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        RebuildAxisColumns();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MotionSectionViewModel.AxisNames)) RebuildAxisColumns();
    }

    /// <summary>Replaces the axis columns with one per installed axis of the selected device.</summary>
    private void RebuildAxisColumns()
    {
        while (PositionGrid.Columns.Count > FixedColumns)
            PositionGrid.Columns.RemoveAt(PositionGrid.Columns.Count - 1);
        if (_vm == null) return;

        var cellStyle = (Style)PositionGrid.FindResource("AxisCell");
        foreach (var axis in _vm.AxisNames)
        {
            PositionGrid.Columns.Add(new DataGridTextColumn
            {
                Header = axis,
                Binding = new Binding($"Values[{axis}]"),
                ElementStyle = cellStyle,
                Width = AxisColumnWidth,
                CanUserSort = false,
            });
        }
    }

    /// <summary>Double-clicking a row opens its editor (same as the Edit… button).</summary>
    private void OnPositionGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(PositionGrid, e.OriginalSource as DependencyObject) is not DataGridRow) return;
        // A fast double toggle of the Map box is not a request to edit.
        for (var d = e.OriginalSource as DependencyObject; d != null && d != PositionGrid;
             d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is CheckBox) return;
        }
        if (_vm?.EditSelectedPositionCommand.CanExecute(null) == true)
            _vm.EditSelectedPositionCommand.Execute(null);
    }

    /// <summary>Splitter drag finished — remember the grid column's new pixel width.</summary>
    private void OnSplitterDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (!ListColumn.Width.IsAbsolute) return;
        try
        {
            PositionMapStore.SaveListWidth(ListColumn.Width.Value);
        }
        catch
        {
            // Cosmetic preference — the default width is used next time.
        }
    }

    /// <summary>Dragging a position's name starts a drag-and-drop onto the map.</summary>
    private void OnPositionNameDrag(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed
            || sender is not FrameworkElement { DataContext: NamedPositionViewModel row } source) return;
        DragDrop.DoDragDrop(source, new DataObject(PositionMapView.DragFormat, row.Name), DragDropEffects.Copy);
    }
}
