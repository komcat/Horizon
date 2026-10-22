using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using FAAM.UI.Mvvm;

namespace FAAM.UI.ViewModels.Sections;

/// <summary>
/// The Machine › Motion position map — the canvas beside the position list. Each
/// position the operator puts on the map is a circle node they can drag anywhere;
/// <b>Link</b> mode joins two nodes (click the 1st, then the 2nd) with an undirected
/// edge. A node turns green while the device stands on its position. Selecting a node
/// offers <b>Move</b> (recall it) and <b>Update</b> (re-teach it from the current pose);
/// when the device stands on another node, an arrow shows the trajectory from there to
/// the selected node — dashed as a preview, solid while the move runs. One map per
/// product + device, persisted by <see cref="PositionMapStore"/> on every edit.
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

    private readonly Func<string, Task<string>> _moveTo;
    private readonly Func<string, Task<string>> _updateTo;
    private string? _product;
    private string? _device;
    private bool _linkMode;
    private bool _canAct;
    private PositionNodeViewModel? _selected;
    private PositionNodeViewModel? _linkSource;
    private PositionNodeViewModel? _currentNode;
    private PositionNodeViewModel? _moveFrom;
    private PositionNodeViewModel? _moveTarget;
    private PositionTrajectoryViewModel? _trajectory;
    private string _hint = string.Empty;

    /// <param name="moveTo">Moves the device to the named position; returns the result line for <see cref="Hint"/>.</param>
    /// <param name="updateTo">Re-teaches the named position from the device's current pose (confirming first); returns the result line.</param>
    public PositionMapViewModel(Func<string, Task<string>> moveTo, Func<string, Task<string>> updateTo)
    {
        _moveTo = moveTo;
        _updateTo = updateTo;
        RemoveSelectedCommand = new RelayCommand(() => { if (_selected != null) RemoveNode(_selected); }, () => _selected != null);
        MoveSelectedCommand = new AsyncRelayCommand(MoveSelectedAsync, CanActOnSelected);
        UpdateSelectedCommand = new AsyncRelayCommand(UpdateSelectedAsync, CanActOnSelected);
        UpdateHint();
    }

    public ObservableCollection<PositionNodeViewModel> Nodes { get; } = new();
    public ObservableCollection<PositionEdgeViewModel> Edges { get; } = new();

    public double Width => CanvasWidth;
    public double Height => CanvasHeight;

    public RelayCommand RemoveSelectedCommand { get; }

    /// <summary>Moves the device to the selected node's position (section velocity).</summary>
    public AsyncRelayCommand MoveSelectedCommand { get; }

    /// <summary>Saves the device's current axis position into the selected node's position.</summary>
    public AsyncRelayCommand UpdateSelectedCommand { get; }

    /// <summary>Raised with a position name when it is added to or removed from the map.</summary>
    public event Action<string>? MembershipChanged;

    /// <summary>Link mode: clicks pick the two ends of a new edge instead of selecting / dragging.</summary>
    public bool LinkMode
    {
        get => _linkMode;
        set
        {
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

    /// <summary>The move arrow (current node → selected / target node); null when there is none to show.</summary>
    public PositionTrajectoryViewModel? Trajectory
    {
        get => _trajectory;
        private set
        {
            var old = _trajectory;
            if (!SetField(ref _trajectory, value)) return;
            old?.Detach();
            OnPropertyChanged(nameof(HasTrajectory));
        }
    }

    public bool HasTrajectory => _trajectory != null;

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
    /// Shows the map for <paramref name="product"/> / <paramref name="device"/> — reloaded from
    /// disk only when either changes — and flags nodes whose position no longer exists.
    /// </summary>
    public void Show(string? product, string? device, IReadOnlyCollection<string> positionNames)
    {
        if (!string.Equals(product, _product, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(device, _device, StringComparison.OrdinalIgnoreCase))
        {
            _product = product;
            _device = device;
            Load();
        }

        var names = new HashSet<string>(positionNames, StringComparer.OrdinalIgnoreCase);
        foreach (var node in Nodes)
        {
            node.IsTaught = names.Contains(node.Name);
            if (!node.IsTaught) node.IsAtPosition = false;
        }
        RaiseActCommands();
        UpdateTrajectory();
    }

    /// <summary>Whether Move / Update may run (device connected, no program running) — called each tick, raises only on change.</summary>
    public void SetCanAct(bool canAct)
    {
        if (_canAct == canAct) return;
        _canAct = canAct;
        RaiseActCommands();
    }

    public bool Contains(string name) => Find(name) != null;

    /// <summary>Puts <paramref name="name"/> on the map at (<paramref name="x"/>, <paramref name="y"/>), or the next free spot; an existing node is moved there instead.</summary>
    public void AddNode(string name, double? x = null, double? y = null)
    {
        if (!CanEdit()) return;
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
        if (!Nodes.Contains(node)) return;
        foreach (var edge in Edges.Where(e => e.Touches(node)).ToList())
            Edges.Remove(edge);
        Nodes.Remove(node);
        if (Selected == node) Selected = null;
        if (LinkSource == node) LinkSource = null;
        if (_currentNode == node) _currentNode = null;
        if (_moveFrom == node) _moveFrom = null;
        UpdateTrajectory();
        Save();
        UpdateHint();
        MembershipChanged?.Invoke(node.Name);
    }

    public void RemoveEdge(PositionEdgeViewModel edge)
    {
        if (!Edges.Remove(edge)) return;
        Save();
        Hint = $"Removed link {edge.A.Name} — {edge.B.Name}.";
    }

    /// <summary>Drag in progress — moves the node (clamped to the canvas); persisted by <see cref="CommitMove"/>.</summary>
    public void MoveNode(PositionNodeViewModel node, double x, double y)
    {
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
            Hint = node.IsTaught
                ? $"{node.Name} — Move goes there · Update saves the current axis position into it."
                : $"{node.Name} — this position no longer exists for the device.";
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

    /// <summary>
    /// Moves to the selected node. The trajectory is pinned to (start node → target) for the
    /// whole move, so it stays drawn after the device leaves the start node; it is only drawn
    /// when the move starts on a node.
    /// </summary>
    private async Task MoveSelectedAsync()
    {
        var target = _selected;
        if (target == null) return;

        _moveFrom = _currentNode;
        _moveTarget = target;
        RaiseActCommands();
        UpdateTrajectory();
        Hint = $"Moving to {target.Name}…";
        try
        {
            Hint = await _moveTo(target.Name).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Hint = $"Move to {target.Name} failed: {ex.Message}";
        }
        finally
        {
            _moveFrom = null;
            _moveTarget = null;
            RaiseActCommands();
            UpdateTrajectory();
        }
    }

    private async Task UpdateSelectedAsync()
    {
        var target = _selected;
        if (target == null) return;
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
    /// The arrow to draw: during a map move, start node → target; otherwise a preview from the
    /// node the device stands on to the selected node. None when either end is missing / the same.
    /// </summary>
    private void UpdateTrajectory()
    {
        var (from, to, active) = _moveTarget != null
            ? (_moveFrom, _moveTarget, true)
            : (_currentNode, _selected is { IsTaught: true } s ? s : null, false);

        if (from == null || to == null || from == to || !Nodes.Contains(from) || !Nodes.Contains(to))
        {
            Trajectory = null;
            return;
        }
        if (_trajectory != null && _trajectory.From == from && _trajectory.To == to)
        {
            _trajectory.IsActive = active;
            return;
        }
        Trajectory = new PositionTrajectoryViewModel(from, to) { IsActive = active };
    }

    private void Load()
    {
        Edges.Clear();
        Nodes.Clear();
        Selected = null;
        LinkSource = null;
        _currentNode = null;
        _moveFrom = null;
        Trajectory = null;
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
        if (_product == null || _device == null) return;
        var layout = new PositionMapLayout
        {
            Nodes = Nodes.Select(n => new PositionMapNode { Name = n.Name, X = Math.Round(n.X, 1), Y = Math.Round(n.Y, 1) }).ToList(),
            Edges = Edges.Select(e => new PositionMapEdge { A = e.A.Name, B = e.B.Name }).ToList(),
        };
        try
        {
            PositionMapStore.Save(_product, _device, layout);
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
            : Nodes.Count == 0 ? "Tick “Map” on a position (or drag its name here) to add a node."
            : "Click a node to Move / Update · drag to arrange · right-click a node or link to remove it.";
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
