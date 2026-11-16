using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Horizon.Station.UI.ViewModels.Timeline;

/// <summary>What a canvas node is: the fixed Start marker, one command block, or a parallel envelope.</summary>
public enum CanvasNodeKind { Start, Block, Parallel }

/// <summary>
/// Which conditional branch a node falls in, for the canvas tint:
/// <see cref="Then"/> (between an If and its Else/End If) shows light pink,
/// <see cref="Else"/> (between Else and End If) light yellow, <see cref="None"/> untinted.
/// </summary>
public enum ConditionRegion { None, Then, Else }

/// <summary>
/// Branch keys of a parallel envelope. A branch is keyed by the "lane" its blocks run on —
/// the device, or one of the two device-less lanes — exactly as the old lane grid did, so a
/// device never runs two commands at once inside one envelope.
/// </summary>
public static class LaneIds
{
    /// <summary>Lane id for the device-less read-out (signal.read) block.</summary>
    public const string Signal = "signal";

    /// <summary>Lane id for device-less flow ops.</summary>
    public const string Control = "control";

    /// <summary>The lane a block belongs to: its device, else Signal (category "Signal") or Control.</summary>
    public static string For(ProcessBlockViewModel block)
    {
        if (block.Spec.DeviceKind != DeviceKind.None && !string.IsNullOrEmpty(block.SelectedDevice))
            return block.SelectedDevice!;
        return block.Spec.Category.Equals("Signal", StringComparison.OrdinalIgnoreCase) ? Signal : Control;
    }

    /// <summary>Caption shown over a branch column.</summary>
    public static string Display(string laneId)
        => laneId.Equals(Signal, StringComparison.OrdinalIgnoreCase) ? "Signal"
         : laneId.Equals(Control, StringComparison.OrdinalIgnoreCase) ? "Flow / Control"
         : laneId;
}

/// <summary>
/// One concurrent branch of a parallel envelope: an ordered sub-sequence (top to bottom) that
/// runs alongside the envelope's other branches. Compiles to a <c>WorkflowBranch</c>.
/// </summary>
public sealed class ParallelBranchViewModel
{
    public ParallelBranchViewModel(string laneId)
    {
        LaneId = laneId;
        Display = LaneIds.Display(laneId);
    }

    /// <summary>The saved <c>WorkflowBranch.Lane</c> — kept verbatim across load / save.</summary>
    public string LaneId { get; }

    public string Display { get; }

    public ObservableCollection<ProcessBlockViewModel> Blocks { get; } = new();
}

/// <summary>
/// One node on the free canvas. Execution order is the chain of <see cref="Next"/> links
/// starting at the Start node; the position (<see cref="X"/> + <see cref="Row"/>) is layout
/// only and is never written to the program file.
/// </summary>
public sealed class CanvasNodeViewModel : ObservableObject
{
    public const string StartUid = "start";

    private CanvasNodeViewModel(CanvasNodeKind kind, string uid, ProcessBlockViewModel? block)
    {
        Kind = kind;
        Uid = uid;
        Block = block;
        _width = kind == CanvasNodeKind.Start ? 96 : 180;
        _height = kind switch { CanvasNodeKind.Start => 34, CanvasNodeKind.Block => 58, _ => 110 };
        Branches.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoBranches));
    }

    public static CanvasNodeViewModel Start() => new(CanvasNodeKind.Start, StartUid, null);

    public static CanvasNodeViewModel ForBlock(ProcessBlockViewModel block) => new(CanvasNodeKind.Block, block.Id, block);

    /// <summary><paramref name="uid"/> is the saved <c>flow.parallel</c> step id; null mints a new one.</summary>
    public static CanvasNodeViewModel ForParallel(string? uid = null)
        => new(CanvasNodeKind.Parallel, string.IsNullOrEmpty(uid) ? "stg-" + Guid.NewGuid().ToString("N") : uid, null);

    public CanvasNodeKind Kind { get; }

    /// <summary>
    /// Stable id — the block id, the parallel step id, or <see cref="StartUid"/>. Keys the
    /// saved canvas layout, and (for an envelope) is the compiled <c>flow.parallel</c> step id.
    /// </summary>
    public string Uid { get; }

    /// <summary>The command of a Block node; null for Start and Parallel.</summary>
    public ProcessBlockViewModel? Block { get; }

    /// <summary>The branches of a Parallel envelope; empty for the other kinds.</summary>
    public ObservableCollection<ParallelBranchViewModel> Branches { get; } = new();

    public bool HasNoBranches => Branches.Count == 0;

    /// <summary>Next node in execution order (the outgoing arrow), or null.</summary>
    public CanvasNodeViewModel? Next { get; set; }

    /// <summary>The node whose arrow points here, or null.</summary>
    public CanvasNodeViewModel? Prev { get; set; }

    private double _x;
    private double _y;
    private double _width;
    private double _height;
    private int _row;

    public double X { get => _x; set => SetField(ref _x, value); }

    /// <summary>Derived from <see cref="Row"/> by the editor's layout pass (free only while dragging).</summary>
    public double Y { get => _y; set => SetField(ref _y, value); }

    /// <summary>Measured size, pushed in by the view.</summary>
    public double Width { get => _width; set => SetField(ref _width, value); }
    public double Height { get => _height; set => SetField(ref _height, value); }

    /// <summary>The stage row this node sits in (0 = the Start row).</summary>
    public int Row { get => _row; set => SetField(ref _row, value); }

    public Rect Bounds => new(X, Y, Width, Height);

    private bool _isSelected;
    private bool _isOrphan;
    private bool _isDropTarget;
    private ConditionRegion _region;
    private string _runStatus = "—";
    private string _ordinal = string.Empty;

    public bool IsSelected { get => _isSelected; set => SetField(ref _isSelected, value); }

    /// <summary>True when no chain of arrows from Start reaches this node — it would not run.</summary>
    public bool IsOrphan { get => _isOrphan; set => SetField(ref _isOrphan, value); }

    /// <summary>True while a drag hovers this envelope — drives the drop highlight.</summary>
    public bool IsDropTarget { get => _isDropTarget; set => SetField(ref _isDropTarget, value); }

    public ConditionRegion Region { get => _region; set => SetField(ref _region, value); }

    /// <summary>Run verdict of a Parallel envelope as a whole (its blocks carry their own).</summary>
    public string RunStatus { get => _runStatus; set => SetField(ref _runStatus, value); }

    /// <summary>1-based position in the execution chain ("" for Start and unconnected nodes).</summary>
    public string Ordinal { get => _ordinal; set => SetField(ref _ordinal, value); }

    /// <summary>Every command this node carries.</summary>
    public IEnumerable<ProcessBlockViewModel> AllBlocks()
        => Block != null ? new[] { Block } : Branches.SelectMany(b => b.Blocks);
}

/// <summary>An arrow between two nodes; the geometry is re-routed whenever either end moves.</summary>
public sealed class LinkViewModel : ObservableObject
{
    public LinkViewModel(CanvasNodeViewModel from, CanvasNodeViewModel to)
    {
        From = from;
        To = to;
    }

    public CanvasNodeViewModel From { get; }
    public CanvasNodeViewModel To { get; }

    private Geometry? _data;
    private Geometry? _head;
    private bool _isSelected;

    public Geometry? Data { get => _data; set => SetField(ref _data, value); }
    public Geometry? Head { get => _head; set => SetField(ref _head, value); }
    public bool IsSelected { get => _isSelected; set => SetField(ref _isSelected, value); }
}

/// <summary>One stage row of the background grid: a number and a dashed line under it.</summary>
public sealed class GridRowViewModel : ObservableObject
{
    private string _label = string.Empty;
    private double _top;
    private double _height;

    public string Label { get => _label; set => SetField(ref _label, value); }
    public double Top { get => _top; set => SetField(ref _top, value); }
    public double Height { get => _height; set => SetField(ref _height, value); }
}

/// <summary>
/// Routes an arrow between two nodes. Every node has two out ports (bottom, right) and two
/// in ports (top, left); the arrow snaps to the pair that gives the shortest forward curve.
/// </summary>
public static class LinkRouter
{
    private static readonly Vector Down = new(0, 1);
    private static readonly Vector Right = new(1, 0);

    /// <summary>The out port of <paramref name="node"/>: its right-middle, or its bottom-middle.</summary>
    public static (Point Point, Vector Normal) OutPort(CanvasNodeViewModel node, bool right)
        => right
            ? (new Point(node.X + node.Width, node.Y + node.Height / 2), Right)
            : (new Point(node.X + node.Width / 2, node.Y + node.Height), Down);

    public static (Geometry Data, Geometry Head) Route(CanvasNodeViewModel from, CanvasNodeViewModel to)
    {
        var outs = new[] { OutPort(from, right: false), OutPort(from, right: true) };
        var ins = new[]
        {
            (Point: new Point(to.X + to.Width / 2, to.Y), Normal: Down),    // top, entered going down
            (Point: new Point(to.X, to.Y + to.Height / 2), Normal: Right),  // left, entered going right
        };

        var best = (Out: outs[0], In: ins[0]);
        double bestScore = double.MaxValue;
        foreach (var o in outs)
            foreach (var i in ins)
            {
                var d = i.Point - o.Point;
                // A port that would have to curl back on itself is a poor choice.
                double score = d.Length + (d * o.Normal < 8 ? 400 : 0) + (d * i.Normal < 8 ? 400 : 0);
                if (score < bestScore) { bestScore = score; best = (o, i); }
            }

        return Build(best.Out.Point, best.Out.Normal, best.In.Point, best.In.Normal);
    }

    /// <summary>The curve drawn while a new arrow is being dragged out of a port.</summary>
    public static Geometry Rubber(Point from, Vector normal, Point to)
    {
        double reach = Math.Clamp((to - from).Length * 0.45, 20, 140);
        var fig = new PathFigure { StartPoint = from, IsFilled = false };
        fig.Segments.Add(new BezierSegment(from + normal * reach, to, to, true));
        var g = new PathGeometry(new[] { fig });
        g.Freeze();
        return g;
    }

    private static (Geometry, Geometry) Build(Point p1, Vector n1, Point p2, Vector n2)
    {
        const double headLength = 9, headHalfWidth = 4.5;
        var end = p2 - n2 * (headLength - 1);
        double reach = Math.Clamp((p2 - p1).Length * 0.45, 28, 140);

        var fig = new PathFigure { StartPoint = p1, IsFilled = false };
        fig.Segments.Add(new BezierSegment(p1 + n1 * reach, end - n2 * reach, end, true));
        var data = new PathGeometry(new[] { fig });
        data.Freeze();

        var perp = new Vector(-n2.Y, n2.X);
        var tail = p2 - n2 * headLength;
        var headFig = new PathFigure { StartPoint = p2, IsClosed = true, IsFilled = true };
        headFig.Segments.Add(new LineSegment(tail + perp * headHalfWidth, true));
        headFig.Segments.Add(new LineSegment(tail - perp * headHalfWidth, true));
        var head = new PathGeometry(new[] { headFig });
        head.Freeze();
        return (data, head);
    }
}

/// <summary>Picks the canvas node template by <see cref="CanvasNodeViewModel.Kind"/>.</summary>
public sealed class CanvasNodeTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Start { get; set; }
    public DataTemplate? Block { get; set; }
    public DataTemplate? Parallel { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
        => (item as CanvasNodeViewModel)?.Kind switch
        {
            CanvasNodeKind.Start => Start,
            CanvasNodeKind.Parallel => Parallel,
            CanvasNodeKind.Block => Block,
            _ => base.SelectTemplate(item, container),
        };
}
