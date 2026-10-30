using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Horizon.Station.UI.ViewModels.Sections;

namespace Horizon.Station.UI.Views.Sections;

/// <summary>
/// The position map canvas (<see cref="PositionMapViewModel"/>) — Machine › Motion and the jog
/// rail's Move map tab. Mouse handling only: drag a node to move it, click nodes in Link mode,
/// right-click to remove, Delete / Esc, and dropping a position name dragged from the list —
/// all editing is off when the view-model is not <see cref="PositionMapViewModel.IsEditable"/>.
/// <see cref="Compact"/> lays it out for the narrow rail: the hint wraps on its own row and the
/// canvas is scaled down to fit its nodes.
/// </summary>
public partial class PositionMapView : UserControl
{
    /// <summary>Drag-and-drop format carrying a position name from the list onto the map.</summary>
    public const string DragFormat = "FAAM.PositionName";

    // Compact fit: never shrink labels below this; space kept right / below the outermost nodes.
    private const double MinFitScale = 0.6;
    private const double FitPad = 24;

    public static readonly DependencyProperty CompactProperty = DependencyProperty.Register(
        nameof(Compact), typeof(bool), typeof(PositionMapView),
        new PropertyMetadata(false, (d, _) => ((PositionMapView)d).ApplyCompact()));

    private PositionNodeViewModel? _dragNode;
    private Vector _dragOffset;
    private bool _dragMoved;
    private PositionMapViewModel? _fitVm;

    public PositionMapView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => OnVmChanged();
    }

    /// <summary>Narrow layout for the jog rail (hint on its own row, canvas scaled to its nodes).</summary>
    public bool Compact
    {
        get => (bool)GetValue(CompactProperty);
        set => SetValue(CompactProperty, value);
    }

    private PositionMapViewModel? Vm => DataContext as PositionMapViewModel;

    private void ApplyCompact()
    {
        bool c = Compact;
        MapLabel.Visibility = c ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetRow(HintText, c ? 1 : 0);
        Grid.SetColumn(HintText, c ? 0 : 1);
        Grid.SetColumnSpan(HintText, c ? 3 : 1);
        HintText.TextWrapping = c ? TextWrapping.Wrap : TextWrapping.NoWrap;
        HintText.Margin = c ? new Thickness(0, 4, 0, 0) : new Thickness(0);
        FitToView();
    }

    private void OnVmChanged()
    {
        if (_fitVm != null) _fitVm.Nodes.CollectionChanged -= OnNodesChanged;
        _fitVm = Vm;
        if (_fitVm != null) _fitVm.Nodes.CollectionChanged += OnNodesChanged;
        FitToView();
    }

    private void OnNodesChanged(object? sender, NotifyCollectionChangedEventArgs e) => FitToView();

    private void OnScrollerSizeChanged(object sender, SizeChangedEventArgs e) => FitToView();

    /// <summary>
    /// Compact only: sizes the surface to the nodes' extent and scales it to fit the viewport
    /// (down to <see cref="MinFitScale"/>, then it scrolls). Mouse maths still reads positions
    /// in surface coordinates, so the scale is transparent to it.
    /// </summary>
    private void FitToView()
    {
        if (!Compact) return;
        if (Vm is not { } vm || vm.Nodes.Count == 0)
        {
            Surface.LayoutTransform = Transform.Identity;
            return;
        }

        double w = vm.Nodes.Max(n => n.X) + PositionNodeViewModel.BoxWidth / 2 + FitPad;
        double h = vm.Nodes.Max(n => n.Y) + PositionNodeViewModel.Diameter + FitPad;
        Surface.Width = w;
        Surface.Height = h;

        double vw = Scroller.ActualWidth - 2, vh = Scroller.ActualHeight - 2;
        if (vw <= 0 || vh <= 0) return;
        double s = Math.Clamp(Math.Min(vw / w, vh / h), MinFitScale, 1.0);
        Surface.LayoutTransform = new ScaleTransform(s, s);
    }

    private void OnNodeDown(object sender, MouseButtonEventArgs e)
    {
        if (Vm is not { } vm || (sender as FrameworkElement)?.DataContext is not PositionNodeViewModel node) return;
        Focus();
        vm.ClickNode(node);
        e.Handled = true;
        if (vm.LinkMode || !vm.IsEditable) return;

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
        e.Effects = Vm is { IsEditable: true } && e.Data.GetDataPresent(DragFormat) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (Vm is not { IsEditable: true } vm || e.Data.GetData(DragFormat) is not string name) return;
        var p = e.GetPosition(Surface);
        vm.AddNode(name, p.X, p.Y);
        e.Handled = true;
    }
}
