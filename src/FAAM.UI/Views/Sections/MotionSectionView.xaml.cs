using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using FAAM.UI.ViewModels.Sections;

namespace FAAM.UI.Views.Sections;

/// <summary>
/// Machine-page Motion section — live per-axis position, jog, velocity, and Stop, plus the
/// position list beside the position map. The list / map split is remembered in
/// <c>faam_position_map.json</c>.
/// </summary>
public partial class MotionSectionView : UserControl
{
    public MotionSectionView()
    {
        InitializeComponent();
        if (PositionMapStore.LoadListWidth() is double w && double.IsFinite(w) && w >= ListColumn.MinWidth)
            ListColumn.Width = new GridLength(w);
    }

    /// <summary>Splitter drag finished — remember the list column's new pixel width.</summary>
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
