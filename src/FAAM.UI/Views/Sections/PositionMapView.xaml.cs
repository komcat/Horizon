using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FAAM.UI.ViewModels.Sections;

namespace FAAM.UI.Views.Sections;

/// <summary>
/// The Machine › Motion position map canvas (<see cref="PositionMapViewModel"/>). Mouse
/// handling only: drag a node to move it, click nodes in Link mode, right-click to remove,
/// Delete / Esc, and dropping a position name dragged from the list.
/// </summary>
public partial class PositionMapView : UserControl
{
    /// <summary>Drag-and-drop format carrying a position name from the list onto the map.</summary>
    public const string DragFormat = "FAAM.PositionName";

    private PositionNodeViewModel? _dragNode;
    private Vector _dragOffset;
    private bool _dragMoved;

    public PositionMapView() => InitializeComponent();

    private PositionMapViewModel? Vm => DataContext as PositionMapViewModel;

    private void OnNodeDown(object sender, MouseButtonEventArgs e)
    {
        if (Vm is not { } vm || (sender as FrameworkElement)?.DataContext is not PositionNodeViewModel node) return;
        Focus();
        vm.ClickNode(node);
        e.Handled = true;
        if (vm.LinkMode) return;

        var p = e.GetPosition(Surface);
        _dragNode = node;
        _dragOffset = new Vector(p.X - node.X, p.Y - node.Y);
        _dragMoved = false;
        Surface.CaptureMouse();
    }

    private void OnSurfaceDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        Vm?.ClearSelection();
    }

    private void OnSurfaceMove(object sender, MouseEventArgs e)
    {
        if (_dragNode == null || Vm is not { } vm || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(Surface);
        vm.MoveNode(_dragNode, p.X - _dragOffset.X, p.Y - _dragOffset.Y);
        _dragMoved = true;
    }

    private void OnSurfaceUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragNode != null) Surface.ReleaseMouseCapture();   // → OnSurfaceLostCapture ends the drag
    }

    /// <summary>Ends a drag however capture was lost (button up, Alt-Tab, …) and saves a moved node.</summary>
    private void OnSurfaceLostCapture(object sender, MouseEventArgs e)
    {
        if (_dragNode == null) return;
        _dragNode = null;
        if (_dragMoved) Vm?.CommitMove();
        _dragMoved = false;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (e.Key == Key.Delete && vm.RemoveSelectedCommand.CanExecute(null))
        {
            vm.RemoveSelectedCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            vm.ClearSelection();
            e.Handled = true;
        }
    }

    private void OnRemoveNode(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PositionNodeViewModel node)
            Vm?.RemoveNode(node);
    }

    private void OnRemoveEdge(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PositionEdgeViewModel edge)
            Vm?.RemoveEdge(edge);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DragFormat) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (Vm is not { } vm || e.Data.GetData(DragFormat) is not string name) return;
        var p = e.GetPosition(Surface);
        vm.AddNode(name, p.X, p.Y);
        e.Handled = true;
    }
}
