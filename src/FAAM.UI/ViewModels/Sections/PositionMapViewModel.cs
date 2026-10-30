using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using Horizon.Workflow;

namespace FAAM.UI.ViewModels.Sections;

/// <summary>Outcome of one map move leg — <see cref="Arrived"/> only when the device was confirmed standing on the position.</summary>
public readonly record struct MapMoveResult(bool Arrived, string Message);

/// <summary>
/// The position map — the canvas beside the Machine › Motion position list, and (read-only)
/// the jog rail's Move map tab. Each position the operator puts on the map is a circle node
/// they can drag anywhere; <b>Link</b> mode joins two nodes (click the 1st, then the 2nd)
/// with an undirected edge. A node turns green while the device stands on its position.
/// Selecting a node offers <b>Move</b> (recall it) and <b>Update</b> (re-teach it from the
/// current pose). With <see cref="FollowPath"/> on (the default) Move travels along the
/// links — node by node over the route with the fewest hops — instead of straight to the
/// target; arrows show the route from the node the device stands on, dashed as a preview,
/// the running leg solid. One map per product + device, persisted by
/// <see cref="PositionMapStore"/> on every edit.
/// </summary>
public sealed class PositionMapViewModel : ObservableObject
{
    /// <summary>Fixed drawing area (canvas pixels) — scrolls rather than rescales, so saved spots don't drift with the window.</summary>
    public const double CanvasWidth = 2000;
    public const double CanvasHeight = 1400;

    // New nodes added without a drop point fill a grid of free spots from the top-left.
    private const double SpotOriginX = 90, SpotOriginY = 60, SpotStepX = 150, SpotStepY = 100;
    private const int SpotColumns = 6, MaxSpots = 120;
    private const double MinNodeSpacing = 50;

    private readonly Func<string, string, Task<MapMoveResult>> _moveTo;
    private readonly Func<string, Task<string>>? _updateTo;
    private readonly bool _editable;
    private string? _product;
    private string? _device;
    private HashSet<string> _taught = new(StringComparer.OrdinalIgnoreCase);
    private bool _linkMode;
    private bool _canAct;
    private bool _followPath = true;
    private bool _reloadPending;
    private PositionNodeViewModel? _selected;
    private PositionNodeViewModel? _linkSource;
    private PositionNodeViewModel? _currentNode;
    private PositionNodeViewModel? _moveTarget;
    private PositionNodeViewModel? _moveStart;
    private List<PositionNodeViewModel>? _moveStops;
    private int _moveLeg;
    private CancellationTokenSource? _moveCts;
    private string _hint = string.Empty;

    /// <param name="moveTo">Moves (device, position) and confirms arrival — one leg of a Move.</param>
    /// <param name="updateTo">Re-teaches the named position from the device's current pose (confirming first); returns the result line. Null hides Update.</param>
    /// <param name="editable">False = view-only map (jog rail): no drag / link / remove, never saved.</param>
    public PositionMapViewModel(Func<string, string, Task<MapMoveResult>> moveTo,
                                Func<string, Task<string>>? updateTo, bool editable = true)
    {
        _moveTo = moveTo;
        _updateTo = updateTo;
        _editable = editable;
        RemoveSelectedCommand = new RelayCommand(() => { if (_selected != null) RemoveNode(_selected); }, () => _editable && _selected != null);
        MoveSelectedCommand = new AsyncRelayCommand(MoveSelectedAsync, CanActOnSelected);
        UpdateSelectedCommand = new AsyncRelayCommand(UpdateSelectedAsync, () => _updateTo != null && CanActOnSelected());
        PositionMapStore.Saved += OnStoreSaved;
        StopRequested += CancelMove;
        UpdateHint();
    }

    public ObservableCollection<PositionNodeViewModel> Nodes { get; } = new();
    public ObservableCollection<PositionEdgeViewModel> Edges { get; } = new();

    /// <summary>The move arrows: one per leg of the previewed / running route.</summary>
    public ObservableCollection<PositionTrajectoryViewModel> Trajectories { get; } = new();

    public double Width => CanvasWidth;
    public double Height => CanvasHeight;

    /// <summary>Nodes can be dragged / linked / removed (Machine page); false on the jog rail.</summary>
    public bool IsEditable => _editable;

    /// <summary>Shows the Update button.</summary>
    public bool CanUpdate => _updateTo != null;

    public RelayCommand RemoveSelectedCommand { get; }

    /// <summary>Moves the device to the selected node — along the links when <see cref="FollowPath"/> is on.</summary>
    public AsyncRelayCommand MoveSelectedCommand { get; }

    /// <summary>Saves the device's current axis position into the selected node's position.</summary>
    public AsyncRelayCommand UpdateSelectedCommand { get; }

    /// <summary>Raised with a position name when it is added to or removed from the map.</summary>
    public event Action<string>? MembershipChanged;

    /// <summary>
    /// Move travels node by node along the links (the route with the fewest hops) instead of
    /// straight to the target. Needs the device to stand on a node and a linked route to exist.
    /// </summary>
    public bool FollowPath
    {
        get => _followPath;
        set
        {
            if (!SetField(ref _followPath, value)) return;
            UpdateTrajectory();
            if (_selected != null && !_linkMode) Hint = DescribeSelection(_selected);
        }
    }

    /// <summary>Link mode: clicks pick the two ends of a new edge instead of selecting / dragging.</summary>
    public bool LinkMode
    {
        get => _linkMode;
        set
        {
            if (!_editable && value) return;
            if (!SetField(ref _linkMode, value)) return;
            LinkSource = null;
            if (value) Selected = null;
            UpdateHint();
        }
    }

    /// <summary>One-line guidance / last result under the toolbar.</summary>
    public string Hint { get => _hint; private set => SetField(ref _hint, value); }

    public PositionNodeViewModel? Selected
    {
        get => _selected;
        private set
        {
            var old = _selected;
            if (!SetField(ref _selected, value)) return;
            if (old != null) old.IsSelected = false;
            if (value != null) value.IsSelected = true;
            OnPropertyChanged(nameof(HasSelection));
            RemoveSelectedCommand.RaiseCanExecuteChanged();
            RaiseActCommands();
            UpdateTrajectory();
        }
    }

    /// <summary>A node is selected — shows the Move / Update buttons.</summary>
    public bool HasSelection => _selected != null;

    private PositionNodeViewModel? LinkSource
    {
        get => _linkSource;
        set
        {
            if (_linkSource == value) return;
            if (_linkSource != null) _linkSource.IsLinkSource = false;
            _linkSource = value;
            if (value != null) value.IsLinkSource = true;
        }
    }

    /// <summary>
    /// After a move reported done, re-reads the live pose from the controller and checks the
    /// device really stands on <paramref name="name"/>. A STOP mid-move also ends the move
    /// "successfully" (motion simply ceased), so a path must not start its next leg on that alone.
    /// </summary>
    public static async Task<MapMoveResult> ConfirmArrivalAsync(EngineHost host, string device, string name)
    {
        var live = await host.Engine.Motion.GetPositionAsync(device).ConfigureAwait(true);
        if (!live.IsSuccess)
            return new MapMoveResult(false, $"Could not confirm {device} reached {name}: {live.Message}");
        var axes = host.Engine.Motion.GetDevice(device)?.Axes ?? Array.Empty<string>();
        return PositionMatch.Matches(host.Process.GetPosition(device, name)?.Coordinates, live.Value, axes)
            ? new MapMoveResult(true, $"{device} at {name}.")
            : new MapMoveResult(false, $"{device} stopped short of {name}");
    }

    /// <summary>
    /// Shows the map for <paramref name="product"/> / <paramref name="device"/> — reloaded from
    /// disk only when either changes — and flags nodes whose position no longer exists.
    /// </summary>
    public void Show(string? product, string? device, IReadOnlyCollection<string> positionNames)
    {
        if (!string.Equals(product, _product, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(device, _device, StringComparison.OrdinalIgnoreCase))
        {
            _moveCts?.Cancel();   // a route belongs to the map it was planned on
            _product = product;
            _device = device;
            Load();
        }

        _taught = new HashSet<string>(positionNames, StringComparer.OrdinalIgnoreCase);
        ApplyTaught();
        RaiseActCommands();
        UpdateTrajectory();
    }

    /// <summary>True when the map currently shows <paramref name="product"/> / <paramref name="device"/>.</summary>
    public bool IsShowing(string? product, string? device)
        => string.Equals(product, _product, StringComparison.OrdinalIgnoreCase)
           && string.Equals(device, _device, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether Move / Update may run (device connected, no program running) — called each tick, raises only on change.</summary>
    public void SetCanAct(bool canAct)
    {
        if (_canAct == canAct) return;
        _canAct = canAct;
        RaiseActCommands();
    }

    /// <summary>STOP was pressed — a path move in progress starts no further leg.</summary>
    public void CancelMove() => _moveCts?.Cancel();

    /// <summary>Any STOP: every map (Machine page and jog rail) abandons its path move.</summary>
    public static void CancelAllMoves() => StopRequested?.Invoke();

    private static event Action? StopRequested;

    public bool Contains(string name) => Find(name) != null;

    /// <summary>Puts <paramref name="name"/> on the map at (<paramref name="x"/>, <paramref name="y"/>), or the next free spot; an existing node is moved there instead.</summary>
    public void AddNode(string name, double? x = null, double? y = null)
    {
        if (!_editable || !CanEdit()) return;
        var node = Find(name);
        if (node != null)
        {
            if (x.HasValue && y.HasValue) MoveNode(node, x.Value, y.Value);
            Save();
            return;
        }

        var spot = x.HasValue && y.HasValue ? (X: x.Value, Y: y.Value) : FreeSpot();
        node = new PositionNodeViewModel(name, Clamp(spot.X, CanvasWidth), Clamp(spot.Y, CanvasHeight)) { IsTaught = true };
        Nodes.Add(node);
        Save();
        MembershipChanged?.Invoke(name);
    }

    /// <summary>Takes <paramref name="name"/> off the map along with its edges.</summary>
    public void RemoveNode(string name)
    {
        var node = Find(name);
        if (node != null) RemoveNode(node);
    }

    public void RemoveNode(PositionNodeViewModel node)
    {
        if (!_editable || !Nodes.Contains(node)) return;
        foreach (var edge in Edges.Where(e => e.Touches(node)).ToList())
            Edges.Remove(edge);
        Nodes.Remove(node);
        if (Selected == node) Selected = null;
        if (LinkSource == node) LinkSource = null;
        if (_currentNode == node) _currentNode = null;
        UpdateTrajectory();
        Save();
        UpdateHint();
        MembershipChanged?.Invoke(node.Name);
    }

    public void RemoveEdge(PositionEdgeViewModel edge)
    {
        if (!_editable || !Edges.Remove(edge)) return;
        UpdateTrajectory();
        Save();
        Hint = $"Removed link {edge.A.Name} — {edge.B.Name}.";
    }

    /// <summary>Drag in progress — moves the node (clamped to the canvas); persisted by <see cref="CommitMove"/>.</summary>
    public void MoveNode(PositionNodeViewModel node, double x, double y)
    {
        if (!_editable) return;
        node.X = Clamp(x, CanvasWidth);
        node.Y = Clamp(y, CanvasHeight);
    }

    /// <summary>Drag finished — save the new spot.</summary>
    public void CommitMove() => Save();

    /// <summary>
    /// A node was clicked. In link mode the 1st click picks the source and the 2nd adds
    /// the edge (clicking the source again un-picks it); otherwise it selects the node.
    /// </summary>
    public void ClickNode(PositionNodeViewModel node)
    {
        if (!_linkMode)
        {
            Selected = node;
            Hint = DescribeSelection(node);
            return;
        }

        if (LinkSource == null)
        {
            LinkSource = node;
            Hint = $"Link from {node.Name} — click the 2nd node (Esc cancels).";
            return;
        }

        var from = LinkSource;
        LinkSource = null;
        if (from == node)
        {
            UpdateHint();
            return;
        }
        if (Edges.Any(e => e.Joins(from, node)))
        {
            Hint = $"{from.Name} and {node.Name} are already linked.";
            return;
        }
        Edges.Add(new PositionEdgeViewModel(from, node));
        UpdateTrajectory();
        Save();
        Hint = $"Linked {from.Name} — {node.Name}. Click the next 1st node.";
    }

    /// <summary>Empty canvas clicked / Esc — drop the selection and any half-made link.</summary>
    public void ClearSelection()
    {
        Selected = null;
        LinkSource = null;
        UpdateHint();
    }

    /// <summary>Recolours the nodes — <paramref name="isAt"/> answers "is the device on this position" by name.</summary>
    public void UpdateAtPosition(Func<string, bool> isAt)
    {
        PositionNodeViewModel? current = null;
        foreach (var node in Nodes)
        {
            node.IsAtPosition = node.IsTaught && isAt(node.Name);
            if (node.IsAtPosition) current ??= node;
        }
        SetCurrentNode(current);
    }

    /// <summary>Clears every "at position" mark (device disconnected / state unknown).</summary>
    public void ClearAtPosition()
    {
        foreach (var node in Nodes) node.IsAtPosition = false;
        SetCurrentNode(null);
    }

    private void SetCurrentNode(PositionNodeViewModel? node)
    {
        if (_currentNode == node) return;
        _currentNode = node;
        UpdateTrajectory();
    }

    private bool CanActOnSelected() => _canAct && _moveTarget == null && _selected is { IsTaught: true };

    private void RaiseActCommands()
    {
        MoveSelectedCommand.RaiseCanExecuteChanged();
        UpdateSelectedCommand.RaiseCanExecuteChanged();
    }

    private string DescribeSelection(PositionNodeViewModel node)
    {
        if (!node.IsTaught) return $"{node.Name} — this position no longer exists for the device.";
        var update = _updateTo != null ? " · Update saves the current axis position into it." : ".";
        var (start, stops, reason) = Plan(node);
        if (stops == null) return $"{node.Name} — {reason}";
        var via = stops.Count > 1 && start != null
            ? " via " + string.Join(" → ", stops.SkipLast(1).Select(s => s.Name))
            : string.Empty;
        return $"{node.Name} — Move goes there{via}{update}";
    }

    /// <summary>
    /// The stops a Move to <paramref name="target"/> visits in order (ending at the target) and
    /// the node it starts from (null = unknown). Direct: just the target. Follow path: every
    /// node of the fewest-hop linked route after the start; null stops + the reason when the
    /// device isn't on a node or no linked route reaches the target.
    /// </summary>
    private (PositionNodeViewModel? Start, List<PositionNodeViewModel>? Stops, string Reason) Plan(PositionNodeViewModel target)
    {
        var start = _currentNode;
        if (!_followPath || start == target)
            return (start, new List<PositionNodeViewModel> { target }, string.Empty);
        if (start == null)
            return (null, null, $"Follow path: the device isn't on a map node — move it onto one first, or untick Follow path to go straight to {target.Name}.");
        var route = FindRoute(start, target);
        return route == null
            ? (start, null, $"Follow path: no linked route from {start.Name} to {target.Name} — link them, or untick Follow path.")
            : (start, route.Skip(1).ToList(), string.Empty);
    }

    /// <summary>Fewest-hop route from → to over the links, through positions that still exist; null if none.</summary>
    private List<PositionNodeViewModel>? FindRoute(PositionNodeViewModel from, PositionNodeViewModel to)
    {
        var prev = new Dictionary<PositionNodeViewModel, PositionNodeViewModel?> { [from] = null };
        var queue = new Queue<PositionNodeViewModel>();
        queue.Enqueue(from);
        while (queue.Count > 0 && !prev.ContainsKey(to))
        {
            var node = queue.Dequeue();
            foreach (var edge in Edges)
            {
                if (!edge.Touches(node)) continue;
                var next = edge.A == node ? edge.B : edge.A;
                if (!next.IsTaught || prev.ContainsKey(next)) continue;
                prev[next] = node;
                queue.Enqueue(next);
            }
        }
        if (!prev.ContainsKey(to)) return null;

        var route = new List<PositionNodeViewModel>();
        for (PositionNodeViewModel? n = to; n != null; n = prev[n]) route.Add(n);
        route.Reverse();
        return route;
    }

    /// <summary>
    /// Moves to the selected node — straight there, or leg by leg along the planned route. Each
    /// leg must be confirmed arrived before the next starts; STOP (<see cref="CancelMove"/>), a
    /// failed / short leg, or a device / product switch ends the route where it is. The route is
    /// pinned for the whole move, so its arrows stay drawn as the device leaves each node.
    /// </summary>
    private async Task MoveSelectedAsync()
    {
        var target = _selected;
        var device = _device;
        var product = _product;
        if (target == null || device == null) return;

        var (start, stops, reason) = Plan(target);
        if (stops == null)
        {
            Hint = reason;
            return;
        }

        using var cts = new CancellationTokenSource();
        _moveCts = cts;
        _moveStart = start;
        _moveStops = stops;
        _moveLeg = 0;
        _moveTarget = target;
        RaiseActCommands();
        try
        {
            Hint = await RunRouteAsync(product, device, target, stops, cts.Token).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Hint = $"Move to {target.Name} failed: {ex.Message}";
        }
        finally
        {
            _moveCts = null;
            _moveStart = null;
            _moveStops = null;
            _moveTarget = null;
            RaiseActCommands();
            UpdateTrajectory();
            if (_reloadPending) Reload();
        }
    }

    private async Task<string> RunRouteAsync(string? product, string device, PositionNodeViewModel target,
                                             List<PositionNodeViewModel> stops, CancellationToken ct)
    {
        string last = string.Empty;
        for (int i = 0; i < stops.Count; i++)
        {
            var stop = stops[i];
            if (ct.IsCancellationRequested || !IsShowing(product, device))
                return $"Stopped — move to {target.Name} abandoned before {stop.Name}.";

            _moveLeg = i;
            UpdateTrajectory();
            Hint = stops.Count > 1
                ? $"Moving to {stop.Name} ({i + 1}/{stops.Count}) on the way to {target.Name}…"
                : $"Moving to {target.Name}…";

            var r = await _moveTo(device, stop.Name).ConfigureAwait(true);
            if (!r.Arrived)
                return stops.Count > 1 ? $"{r.Message} — path to {target.Name} ended at leg {i + 1}/{stops.Count}." : r.Message;
            last = r.Message;
        }
        return stops.Count > 1
            ? $"{device} at {target.Name} via {string.Join(" → ", stops.SkipLast(1).Select(s => s.Name))}."
            : last;
    }

    private async Task UpdateSelectedAsync()
    {
        var target = _selected;
        if (target == null || _updateTo == null) return;
        try
        {
            Hint = await _updateTo(target.Name).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Hint = $"Update {target.Name} failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Redraws the arrows: during a move the pinned route (running leg solid); otherwise a
    /// dashed preview of what Move would do from the node the device stands on to the
    /// selected node. None when the start is unknown or no route can be planned.
    /// </summary>
    private void UpdateTrajectory()
    {
        PositionNodeViewModel? start;
        List<PositionNodeViewModel>? stops;
        int activeLeg;
        if (_moveTarget != null)
        {
            (start, stops, activeLeg) = (_moveStart, _moveStops, _moveLeg);
        }
        else
        {
            start = _currentNode;
            stops = _selected is { IsTaught: true } s ? Plan(s).Stops : null;
            activeLeg = -1;
        }

        var legs = new List<(PositionNodeViewModel From, PositionNodeViewModel To)>();
        if (start != null && stops != null)
        {
            var from = start;
            foreach (var to in stops)
            {
                if (from != to && Nodes.Contains(from) && Nodes.Contains(to)) legs.Add((from, to));
                from = to;
            }
        }

        bool same = legs.Count == Trajectories.Count
            && legs.Select((l, i) => Trajectories[i].From == l.From && Trajectories[i].To == l.To).All(x => x);
        if (!same)
        {
            foreach (var t in Trajectories) t.Detach();
            Trajectories.Clear();
            foreach (var (from, to) in legs) Trajectories.Add(new PositionTrajectoryViewModel(from, to));
        }
        // Leg index counts stops; a leg skipped above (start == first stop) shifts nothing visible.
        for (int i = 0; i < Trajectories.Count; i++)
            Trajectories[i].IsActive = activeLeg >= 0 && stops != null && activeLeg < stops.Count && Trajectories[i].To == stops[activeLeg];
    }

    private void ApplyTaught()
    {
        foreach (var node in Nodes)
        {
            node.IsTaught = _taught.Contains(node.Name);
            if (!node.IsTaught) node.IsAtPosition = false;
        }
    }

    /// <summary>Another map (the Machine page) saved this product / device — re-read it, keeping the selection.</summary>
    private void OnStoreSaved(string product, string device, object? origin)
    {
        if (ReferenceEquals(origin, this) || !IsShowing(product, device)) return;
        if (_moveTarget != null)
        {
            _reloadPending = true;   // never swap the nodes under a running route
            return;
        }
        Reload();
    }

    private void Reload()
    {
        _reloadPending = false;
        var keep = _selected?.Name;
        Load();
        ApplyTaught();
        if (keep != null && Find(keep) is { } node) Selected = node;
        UpdateTrajectory();
    }

    private void Load()
    {
        foreach (var t in Trajectories) t.Detach();
        Trajectories.Clear();
        Edges.Clear();
        Nodes.Clear();
        Selected = null;
        LinkSource = null;
        _currentNode = null;
        if (_product != null && _device != null)
        {
            var layout = PositionMapStore.Load(_product, _device);
            foreach (var n in layout.Nodes.Where(n => !string.IsNullOrWhiteSpace(n.Name)))
                if (Find(n.Name) == null)
                    Nodes.Add(new PositionNodeViewModel(n.Name, Clamp(n.X, CanvasWidth), Clamp(n.Y, CanvasHeight)));
            foreach (var e in layout.Edges)
                if (Find(e.A) is { } a && Find(e.B) is { } b && a != b && !Edges.Any(x => x.Joins(a, b)))
                    Edges.Add(new PositionEdgeViewModel(a, b));
        }
        UpdateHint();
    }

    private void Save()
    {
        if (!_editable || _product == null || _device == null) return;
        var layout = new PositionMapLayout
        {
            Nodes = Nodes.Select(n => new PositionMapNode { Name = n.Name, X = Math.Round(n.X, 1), Y = Math.Round(n.Y, 1) }).ToList(),
            Edges = Edges.Select(e => new PositionMapEdge { A = e.A.Name, B = e.B.Name }).ToList(),
        };
        try
        {
            PositionMapStore.Save(_product, _device, layout, origin: this);
        }
        catch (Exception ex)
        {
            Hint = $"Could not save the map: {ex.Message}";
        }
    }

    private bool CanEdit()
    {
        if (_product != null && _device != null) return true;
        Hint = "No active product — select one on Home first.";
        return false;
    }

    private void UpdateHint()
    {
        Hint = _product == null ? "No active product."
            : _linkMode ? "Link mode: click the 1st node, then the 2nd."
            : Nodes.Count == 0
                ? (_editable ? "Tick “Map” on a position (or drag its name here) to add a node." : "No map for this device yet — build it on Machine › Motion.")
            : _editable ? "Click a node to Move / Update · drag to arrange · right-click a node or link to remove it."
            : "Click a node, then Move.";
    }

    private PositionNodeViewModel? Find(string name)
        => Nodes.FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>First grid spot not crowded by an existing node (falls back to the origin).</summary>
    private (double X, double Y) FreeSpot()
    {
        for (int i = 0; i < MaxSpots; i++)
        {
            double x = SpotOriginX + (i % SpotColumns) * SpotStepX;
            double y = SpotOriginY + (i / SpotColumns) * SpotStepY;
            if (!Nodes.Any(n => Math.Abs(n.X - x) < MinNodeSpacing && Math.Abs(n.Y - y) < MinNodeSpacing))
                return (x, y);
        }
        return (SpotOriginX, SpotOriginY);
    }

    /// <summary>Keeps a node centre far enough inside the canvas that its circle and label stay visible.</summary>
    private static double Clamp(double v, double max)
    {
        if (!double.IsFinite(v)) return PositionNodeViewModel.BoxWidth;
        return Math.Clamp(v, PositionNodeViewModel.BoxWidth / 2, max - PositionNodeViewModel.BoxWidth / 2);
    }
}


/// <summary>A position on the map — <see cref="X"/>/<see cref="Y"/> is the circle's centre in canvas pixels.</summary>
public sealed class PositionNodeViewModel : ObservableObject
{
    /// <summary>Node template box: the circle sits centred at the top, the name below it.</summary>
    public const double BoxWidth = 120;
    public const double Diameter = 36;

    private double _x;
    private double _y;
    private bool _isAtPosition;
    private bool _isSelected;
    private bool _isLinkSource;
    private bool _isTaught = true;

    public PositionNodeViewModel(string name, double x, double y)
    {
        Name = name;
        _x = x;
        _y = y;
    }

    public string Name { get; }

    public double X
    {
        get => _x;
        set { if (SetField(ref _x, value)) OnPropertyChanged(nameof(Left)); }
    }

    public double Y
    {
        get => _y;
        set { if (SetField(ref _y, value)) OnPropertyChanged(nameof(Top)); }
    }

    /// <summary>Canvas.Left / Canvas.Top of the node box, so the circle centre lands on (X, Y).</summary>
    public double Left => _x - BoxWidth / 2;
    public double Top => _y - Diameter / 2;

    /// <summary>The device is standing on this position (within <see cref="PositionMatch.Tolerance"/>).</summary>
    public bool IsAtPosition { get => _isAtPosition; set => SetField(ref _isAtPosition, value); }

    public bool IsSelected { get => _isSelected; set => SetField(ref _isSelected, value); }

    /// <summary>Picked as the 1st end of a link in progress.</summary>
    public bool IsLinkSource { get => _isLinkSource; set => SetField(ref _isLinkSource, value); }

    /// <summary>False when the position no longer exists for this device — the node is drawn dashed and never lights.</summary>
    public bool IsTaught { get => _isTaught; set => SetField(ref _isTaught, value); }
}

/// <summary>An undirected link between two nodes; the line follows both ends as they are dragged.</summary>
public sealed class PositionEdgeViewModel
{
    public PositionEdgeViewModel(PositionNodeViewModel a, PositionNodeViewModel b)
    {
        A = a;
        B = b;
    }

    public PositionNodeViewModel A { get; }
    public PositionNodeViewModel B { get; }

    public string Label => $"{A.Name} — {B.Name}";

    public bool Touches(PositionNodeViewModel node) => A == node || B == node;

    public bool Joins(PositionNodeViewModel p, PositionNodeViewModel q) => (A == p && B == q) || (A == q && B == p);
}

/// <summary>
/// The move arrow from <see cref="From"/> to <see cref="To"/>: a shaft between the two circle
/// edges plus an arrowhead at the target, recomputed as either node is dragged.
/// <see cref="IsActive"/> = the move is running (solid) rather than a preview (dashed).
/// </summary>
public sealed class PositionTrajectoryViewModel : ObservableObject
{
    private const double HeadLength = 14;
    private const double HeadHalfWidth = 7;
    private const double Gap = PositionNodeViewModel.Diameter / 2 + 3;

    private bool _isActive;

    public PositionTrajectoryViewModel(PositionNodeViewModel from, PositionNodeViewModel to)
    {
        From = from;
        To = to;
        from.PropertyChanged += OnNodeChanged;
        to.PropertyChanged += OnNodeChanged;
        Recompute();
    }

    public PositionNodeViewModel From { get; }
    public PositionNodeViewModel To { get; }

    public bool IsActive { get => _isActive; set => SetField(ref _isActive, value); }

    public double X1 { get; private set; }
    public double Y1 { get; private set; }
    public double X2 { get; private set; }
    public double Y2 { get; private set; }
    public PointCollection Head { get; private set; } = new();

    public string Label => $"{From.Name} → {To.Name}";

    /// <summary>Stops following the nodes (the arrow was replaced or cleared).</summary>
    public void Detach()
    {
        From.PropertyChanged -= OnNodeChanged;
        To.PropertyChanged -= OnNodeChanged;
    }

    private void OnNodeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PositionNodeViewModel.X) or nameof(PositionNodeViewModel.Y))
            Recompute();
    }

    private void Recompute()
    {
        double dx = To.X - From.X, dy = To.Y - From.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len <= 2 * Gap + HeadLength)
        {
            // Nodes (nearly) overlap — nothing sensible to draw between them.
            X1 = X2 = From.X;
            Y1 = Y2 = From.Y;
            Head = new PointCollection();
        }
        else
        {
            double ux = dx / len, uy = dy / len;
            double tipX = To.X - ux * Gap, tipY = To.Y - uy * Gap;
            double baseX = tipX - ux * HeadLength, baseY = tipY - uy * HeadLength;
            X1 = From.X + ux * Gap;
            Y1 = From.Y + uy * Gap;
            X2 = baseX;
            Y2 = baseY;
            Head = new PointCollection
            {
                new Point(tipX, tipY),
                new Point(baseX - uy * HeadHalfWidth, baseY + ux * HeadHalfWidth),
                new Point(baseX + uy * HeadHalfWidth, baseY - ux * HeadHalfWidth),
            };
        }
        OnPropertyChanged(nameof(X1));
        OnPropertyChanged(nameof(Y1));
        OnPropertyChanged(nameof(X2));
        OnPropertyChanged(nameof(Y2));
        OnPropertyChanged(nameof(Head));
    }
}
