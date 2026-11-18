using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using Horizon.Engine;
using Horizon.Workflow;

namespace Horizon.Station.UI.ViewModels.Timeline;

/// <summary>
/// The Engineer page — a free-canvas sequence editor. Every program has a <b>Start</b> node;
/// command blocks are dragged anywhere on the canvas and joined by arrows, and the chain of
/// arrows from Start is the execution order (strictly sequential: one arrow in, one arrow
/// out). A <b>parallel envelope</b> is one node holding several branches that run
/// concurrently. Compiles to the same flat Layer 2 <see cref="WorkflowProgram"/> as before
/// (an envelope with two or more branches → one <c>flow.parallel</c> step), so the program
/// file format is unchanged; node positions live in <see cref="ProgramCanvasLayoutStore"/>.
/// The numbered stage rows are a layout grid only. See
/// <c>docs/timeline-sequence-editor-design.md</c>.
/// </summary>
public sealed class TimelineEditorViewModel : ObservableObject
{
    private const int MaxLogLines = 500;

    // Canvas grid: a left gutter for the row numbers, rows at least MinRowHeight tall
    // (a row grows to fit its tallest node), a few spare rows below the last used one.
    public const double RowGutter = 48;
    private const double DefaultX = 70;
    private const double MinRowHeight = 84;
    private const double RowPad = 12;
    private const int MinRows = 10;
    private const int SpareRows = 4;
    private const int MaxRow = 5000;

    private const double ColumnGap = 110;   // free space between two columns of the sequence
    private const int MaxColumnBreaks = 2;  // three columns at most: Start, E1, E2

    private const double MinZoom = 0.3;
    private const double MaxZoom = 1.5;
    private const double ZoomStep = 0.1;

    private readonly EngineHost _host;
    private readonly Dispatcher _dispatcher;

    private string _programName = "New Program";
    private string _commitNote = string.Empty;
    private string _currentVersionText = "—";
    private string? _selectedProgram;
    private ProcessBlockViewModel? _selectedBlock;
    private OpHelpViewModel? _selectedHelp;
    private LinkViewModel? _selectedLink;

    // Node selection: a set (for copy / range / delete), plus the anchor a shift-click extends from.
    private readonly List<CanvasNodeViewModel> _selectedNodes = new();
    private CanvasNodeViewModel? _anchorNode;

    // Clipboard — the copied nodes as id-less steps (a fresh id is minted on paste so
    // copies stay independently trackable).
    private List<WorkflowStep>? _clipboard;

    private bool _isRunning;
    private bool _isPaused;
    private bool _isConnecting;
    private double _zoom = 1.0;
    private double _canvasWidth = 900;
    private double _canvasHeight = 600;
    private string _status = "—";
    private bool _isDirty;
    private bool _suppressDirty;   // true while loading / resetting, so it doesn't count as an edit
    private bool _subscribed;
    private CancellationTokenSource? _runCts;

    // Run-highlight routing, rebuilt on each run by BuildProgram().
    private readonly List<CanvasNodeViewModel?> _topLevelNode = new();             // top-level step index → envelope (null = inline block)
    private readonly Dictionary<int, ProcessBlockViewModel> _inlineBlock = new();  // top-level step index → block
    private CanvasNodeViewModel? _runningNode;
    private readonly Dictionary<int, CanvasNodeViewModel> _stepNode = new();        // top-level step index → the node it came from

    public TimelineEditorViewModel(EngineHost host)
    {
        _host = host;
        _dispatcher = Dispatcher.CurrentDispatcher;

        StartNode = CanvasNodeViewModel.Start();

        BuildPalette();

        NewProgramCommand = new RelayCommand(NewProgram);
        SaveProgramCommand = new RelayCommand(SaveProgram);
        CommitProgramCommand = new RelayCommand(CommitProgram);
        AddParallelCommand = new RelayCommand(AddParallel);
        TidyCommand = new RelayCommand(Tidy);
        LinkModeCommand = new RelayCommand(ToggleLinkMode);
        RemoveBlockCommand = new RelayCommand(DeleteSelection, () => _selectedBlock != null);
        RunCommand = new AsyncRelayCommand(() => RunAsync(null, null, null), () => !_isRunning);
        RunSelectedCommand = new AsyncRelayCommand(RunSelectedAsync, () => !_isRunning && HasRunnableSelection());
        RunFromCommand = new AsyncRelayCommand(RunFromSelectedAsync, () => !_isRunning && RunFromNode() != null);
        PauseCommand = new RelayCommand(TogglePause, () => _isRunning);
        StopCommand = new RelayCommand(() => _runCts?.Cancel(), () => _isRunning);
        ZoomInCommand = new RelayCommand(() => Zoom += ZoomStep, () => _zoom < MaxZoom - 1e-6);
        ZoomOutCommand = new RelayCommand(() => Zoom -= ZoomStep, () => _zoom > MinZoom + 1e-6);

        ResetCanvas();
    }

    // --- bound state ---

    public ObservableCollection<PaletteCategoryViewModel> Palette { get; } = new();
    public ObservableCollection<CanvasNodeViewModel> Nodes { get; } = new();
    public ObservableCollection<LinkViewModel> Links { get; } = new();
    public ObservableCollection<GridRowViewModel> Rows { get; } = new();
    public ObservableCollection<string> Programs { get; } = new();
    public ObservableCollection<string> Log { get; } = new();

    /// <summary>The program's Start node — always present, never saved, never deleted.</summary>
    public CanvasNodeViewModel StartNode { get; }

    // --- left-hand program list (search + hide/show, per-product hidden set) ---

    /// <summary>The program list as shown: <see cref="ProgramFilter"/> applied, hidden programs
    /// omitted unless <see cref="ShowHiddenPrograms"/>.</summary>
    public ObservableCollection<ProgramListItem> ProgramList { get; } = new();

    private string _programFilter = string.Empty;
    public string ProgramFilter
    {
        get => _programFilter;
        set { if (SetField(ref _programFilter, value ?? string.Empty)) RebuildProgramList(); }
    }

    private bool _showHiddenPrograms;
    /// <summary>On: every program, hidden ones dimmed. Off (default): only visible programs.</summary>
    public bool ShowHiddenPrograms
    {
        get => _showHiddenPrograms;
        set { if (SetField(ref _showHiddenPrograms, value)) RebuildProgramList(); }
    }

    private string _programListSummary = string.Empty;
    public string ProgramListSummary { get => _programListSummary; private set => SetField(ref _programListSummary, value); }

    private string _commandFilter = string.Empty;
    /// <summary>Filters the command palette by name, op key or category.</summary>
    public string CommandFilter
    {
        get => _commandFilter;
        set { if (SetField(ref _commandFilter, value ?? string.Empty)) ApplyCommandFilter(); }
    }

    private readonly List<PaletteCategoryViewModel> _paletteAll = new();   // unfiltered palette

    /// <summary>Canvas zoom factor — driven by the zoom buttons and Ctrl + wheel.</summary>
    public double Zoom
    {
        get => _zoom;
        set
        {
            var clamped = Math.Clamp(Math.Round(value, 2), MinZoom, MaxZoom);
            if (!SetField(ref _zoom, clamped)) return;
            ZoomInCommand.RaiseCanExecuteChanged();
            ZoomOutCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Canvas extent (unzoomed px) — grows with the nodes and the row grid.</summary>
    public double CanvasWidth { get => _canvasWidth; private set => SetField(ref _canvasWidth, value); }
    public double CanvasHeight { get => _canvasHeight; private set => SetField(ref _canvasHeight, value); }

    /// <summary>True while an arrow is being dragged out of a port — lights every in port.</summary>
    public bool IsConnecting { get => _isConnecting; set => SetField(ref _isConnecting, value); }

    /// <summary>The node under the mouse drag; the layout pass leaves its Y alone.</summary>
    public CanvasNodeViewModel? DraggingNode { get; set; }

    public string ProgramName { get => _programName; set { if (SetField(ref _programName, value)) MarkDirty(); } }

    /// <summary>
    /// Optional commit note recorded with the next save (the version repository shows
    /// it on the revision). Cleared after each successful save. Blank = no note.
    /// </summary>
    public string CommitNote { get => _commitNote; set => SetField(ref _commitNote, value); }

    /// <summary>
    /// The loaded program's current committed version, e.g. <c>v10</c> — or
    /// <c>uncommitted</c> when it has no committed revision yet. Shown next to the name.
    /// </summary>
    public string CurrentVersionText { get => _currentVersionText; private set => SetField(ref _currentVersionText, value); }

    /// <summary>True when the editor has unsaved changes vs the last save / load.</summary>
    public bool IsDirty { get => _isDirty; private set => SetField(ref _isDirty, value); }

    private void MarkDirty() { if (!_suppressDirty) IsDirty = true; }

    private static readonly HashSet<string> TransientProps = new()
    {
        nameof(ProcessBlockViewModel.RunStatus), nameof(ProcessBlockViewModel.IsSelected),
        nameof(ProcessBlockViewModel.PositionPreview), nameof(ProcessBlockViewModel.HasPositionPreview),
        // Derived from the Detect Object block's hidden params — those edits mark dirty through ParamSummary.
        nameof(ProcessBlockViewModel.DetectPositionText), nameof(ProcessBlockViewModel.DetectRecipeText),
        nameof(ProcessBlockViewModel.OutlineFiltersText),
    };

    /// <summary>Subscribes a block so any non-transient edit marks the program dirty.</summary>
    private void Track(ProcessBlockViewModel block)
        => block.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == null || !TransientProps.Contains(e.PropertyName))
                MarkDirty();
        };

    public string? SelectedProgram
    {
        get => _selectedProgram;
        set { if (SetField(ref _selectedProgram, value) && value != null) { MarkCurrentProgram(); LoadProgram(value); } }
    }

    /// <summary>The block whose parameters the right panel edits.</summary>
    public ProcessBlockViewModel? SelectedBlock
    {
        get => _selectedBlock;
        private set
        {
            var old = _selectedBlock;
            if (!SetField(ref _selectedBlock, value)) return;
            if (old != null) old.IsSelected = false;
            if (value != null) { value.IsSelected = true; SelectedHelp = null; }
            OnPropertyChanged(nameof(HasSelectedBlock));
            OnPropertyChanged(nameof(HasNoSelectedBlock));
            OnPropertyChanged(nameof(ShowPaletteHelp));
            OnPropertyChanged(nameof(ShowEmptyHint));
            RemoveBlockCommand.RaiseCanExecuteChanged();
            RunSelectedCommand.RaiseCanExecuteChanged();
            RunFromCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// Command help shown in the right panel when a palette command is clicked (and no
    /// block is selected). Set via <see cref="ShowHelp"/>; cleared when a block is
    /// selected so the parameter editor takes over.
    /// </summary>
    public OpHelpViewModel? SelectedHelp
    {
        get => _selectedHelp;
        private set
        {
            if (!SetField(ref _selectedHelp, value)) return;
            OnPropertyChanged(nameof(ShowPaletteHelp));
            OnPropertyChanged(nameof(ShowEmptyHint));
        }
    }

    /// <summary>True when the right panel should show command help (a palette item is selected, no block).</summary>
    public bool ShowPaletteHelp => _selectedBlock == null && _selectedHelp != null;

    /// <summary>True when the right panel should show the "nothing selected" hint.</summary>
    public bool ShowEmptyHint => _selectedBlock == null && _selectedHelp == null;

    /// <summary>
    /// Shows the given command's help in the right panel. The node selection is kept — it is
    /// the anchor a following double-click inserts after.
    /// </summary>
    public void ShowHelp(OpSpec spec)
    {
        SelectedBlock = null;
        SelectedHelp = new OpHelpViewModel(spec);
    }

    public bool HasSelectedBlock => _selectedBlock != null;
    public bool HasNoSelectedBlock => _selectedBlock == null;

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!SetField(ref _isRunning, value)) return;
            RunCommand.RaiseCanExecuteChanged();
            RunSelectedCommand.RaiseCanExecuteChanged();
            RunFromCommand.RaiseCanExecuteChanged();
            PauseCommand.RaiseCanExecuteChanged();
            StopCommand.RaiseCanExecuteChanged();
            if (!value) IsPaused = false;
        }
    }

    /// <summary>
    /// True while the run is held: the block in progress finishes, then the runner waits
    /// before the next one until Resume. Mirrors Layer 2's <c>PausedChanged</c>.
    /// </summary>
    public bool IsPaused
    {
        get => _isPaused;
        private set { if (SetField(ref _isPaused, value)) OnPropertyChanged(nameof(PauseLabel)); }
    }

    /// <summary>Caption of the Pause / Resume toggle.</summary>
    public string PauseLabel => _isPaused ? "Resume" : "Pause";

    private void TogglePause()
    {
        if (!_isRunning) return;
        if (_host.Process.IsPaused)
        {
            _host.Process.Resume();
        }
        else
        {
            _host.Process.Pause();
            Status = "Pausing — the current block finishes, then the run waits for Resume.";
        }
    }

    private void OnPausedChanged(bool paused)
    {
        if (_dispatcher.CheckAccess()) Apply();
        else _dispatcher.BeginInvoke(() => Apply());

        void Apply()
        {
            if (!_isRunning) return;
            IsPaused = paused;
            Status = paused ? "Paused — press Resume to continue with the next block." : "Running…";
        }
    }

    public string Status { get => _status; private set => SetField(ref _status, value); }

    public RelayCommand NewProgramCommand { get; }
    public RelayCommand SaveProgramCommand { get; }
    public RelayCommand CommitProgramCommand { get; }
    public RelayCommand AddParallelCommand { get; }
    public RelayCommand TidyCommand { get; }
    public RelayCommand LinkModeCommand { get; }
    public RelayCommand RemoveBlockCommand { get; }
    public AsyncRelayCommand RunCommand { get; }
    public AsyncRelayCommand RunSelectedCommand { get; }
    public AsyncRelayCommand RunFromCommand { get; }
    public RelayCommand PauseCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand ZoomInCommand { get; }
    public RelayCommand ZoomOutCommand { get; }

    // --- page lifecycle ---

    public void Activate()
    {
        RefreshPrograms();
        RefreshVersionText();
        if (_subscribed) return;
        _host.Process.StepEvent += OnStep;
        _host.Process.LogEmitted += OnLog;
        _host.Process.PausedChanged += OnPausedChanged;
        _subscribed = true;
    }

    public void Deactivate()
    {
        if (!_subscribed) return;
        _host.Process.StepEvent -= OnStep;
        _host.Process.LogEmitted -= OnLog;
        _host.Process.PausedChanged -= OnPausedChanged;
        _subscribed = false;
    }

    // --- palette ---

    private void BuildPalette()
    {
        foreach (var category in OpCatalog.Categories)
        {
            var cat = new PaletteCategoryViewModel(category);
            foreach (var spec in OpCatalog.All.Where(s => s.Category == category))
                cat.Items.Add(new PaletteItemViewModel(spec, AddOp));
            _paletteAll.Add(cat);
        }
        ApplyCommandFilter();
    }

    /// <summary>
    /// Rebuilds <see cref="Palette"/> from the full catalog, keeping only commands whose name,
    /// op key or category contains every word of <see cref="CommandFilter"/>; empty categories drop out.
    /// </summary>
    private void ApplyCommandFilter()
    {
        var words = _commandFilter.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Palette.Clear();
        foreach (var cat in _paletteAll)
        {
            if (words.Length == 0) { Palette.Add(cat); continue; }
            var hits = cat.Items.Where(i => words.All(w =>
                i.Display.Contains(w, StringComparison.OrdinalIgnoreCase)
                || i.Spec.OpKey.Contains(w, StringComparison.OrdinalIgnoreCase)
                || cat.Category.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
            if (hits.Count == 0) continue;
            var filtered = new PaletteCategoryViewModel(cat.Category);
            foreach (var h in hits) filtered.Items.Add(h);
            Palette.Add(filtered);
        }
    }

    // --- the chain (execution order) ---

    /// <summary>Points <paramref name="from"/>'s arrow at <paramref name="to"/> (null = no arrow),
    /// dropping whichever arrows that displaces — a node has one arrow in and one out.</summary>
    private static void Link(CanvasNodeViewModel from, CanvasNodeViewModel? to)
    {
        if (from.Next != null) from.Next.Prev = null;
        if (to?.Prev != null) to.Prev.Next = null;
        from.Next = to;
        if (to != null) to.Prev = from;
    }

    /// <summary>Splices <paramref name="node"/> into the chain right after <paramref name="anchor"/>.</summary>
    private static void InsertAfter(CanvasNodeViewModel anchor, CanvasNodeViewModel node)
    {
        var next = anchor.Next;
        Link(anchor, node);
        if (next != null) Link(node, next);
    }

    /// <summary>Takes a node out of the chain and joins its neighbours, so the order of the rest is kept.</summary>
    private static void Detach(CanvasNodeViewModel node)
    {
        var prev = node.Prev;
        var next = node.Next;
        if (prev != null) prev.Next = null;
        if (next != null) next.Prev = null;
        node.Prev = null;
        node.Next = null;
        if (prev != null && next != null) Link(prev, next);
    }

    /// <summary>The nodes reachable from Start, in execution order (Start itself excluded).</summary>
    private List<CanvasNodeViewModel> Chain()
    {
        var list = new List<CanvasNodeViewModel>();
        var seen = new HashSet<CanvasNodeViewModel> { StartNode };
        for (var n = StartNode.Next; n != null && seen.Add(n); n = n.Next)
            list.Add(n);
        return list;
    }

    private CanvasNodeViewModel Tail()
    {
        var chain = Chain();
        return chain.Count > 0 ? chain[^1] : StartNode;
    }

    /// <summary>True when following arrows from <paramref name="from"/> reaches <paramref name="target"/>.</summary>
    private static bool Reaches(CanvasNodeViewModel from, CanvasNodeViewModel target)
    {
        var seen = new HashSet<CanvasNodeViewModel>();
        for (var n = from; n != null && seen.Add(n); n = n.Next)
            if (ReferenceEquals(n, target)) return true;
        return false;
    }

    /// <summary>Draws an arrow from <paramref name="from"/> to <paramref name="to"/> (drag between ports).</summary>
    public void Connect(CanvasNodeViewModel from, CanvasNodeViewModel to)
    {
        from = EndOf(from);   // a folded block's arrow leaves its hidden closer
        if (ReferenceEquals(from, to)) return;
        if (to.Kind == CanvasNodeKind.Start) { Status = "Nothing runs before Start."; return; }
        if (Reaches(to, from)) { Status = "That arrow would make a loop — the sequence runs one way only."; return; }

        Link(from, to);
        MarkDirty();
        RefreshChain();
        Status = "Linked.";
    }

    // --- arrow mode: the toolbar's Arrow button, then click the two blocks ---

    private bool _isLinkMode;
    private CanvasNodeViewModel? _linkSource;

    /// <summary>True while the Arrow button is armed: the next block clicks draw an arrow instead of selecting.</summary>
    public bool IsLinkMode { get => _isLinkMode; private set => SetField(ref _isLinkMode, value); }

    /// <summary>Arms (or disarms) arrow mode. A single selected block becomes the arrow's start.</summary>
    public void ToggleLinkMode()
    {
        if (_isLinkMode) { CancelLinkMode(); Status = "Arrow cancelled."; return; }
        _linkSource = _selectedNodes.Count == 1 ? _selectedNodes[0] : null;
        SetSelectedLink(null);
        IsLinkMode = true;
        IsConnecting = true;
        Status = _linkSource == null
            ? "Arrow: click the block the arrow starts from (Esc cancels)."
            : "Arrow: now click the block it goes to (Esc cancels).";
    }

    public void CancelLinkMode()
    {
        if (!_isLinkMode) return;
        _linkSource = null;
        IsLinkMode = false;
        IsConnecting = false;
    }

    /// <summary>A block click while arrow mode is armed: first click = from, second = to. False when not armed.</summary>
    public bool LinkClick(CanvasNodeViewModel node)
    {
        if (!_isLinkMode) return false;
        if (_linkSource == null)
        {
            _linkSource = node;
            SetNodeSelection(new[] { node });
            Status = "Arrow: now click the block it goes to (Esc cancels).";
            return true;
        }

        var from = _linkSource;
        CancelLinkMode();
        if (ReferenceEquals(from, node)) Status = "Arrow cancelled — that is the same block.";
        else Connect(from, node);
        return true;
    }

    /// <summary>As <see cref="LinkClick(CanvasNodeViewModel)"/>, for a click on a block inside a parallel group (the group is the node).</summary>
    public bool LinkClick(ProcessBlockViewModel inner)
    {
        if (!_isLinkMode) return false;
        return OwnerOf(inner) is not { } owner || LinkClick(owner);
    }

    /// <summary>Removes an arrow (its right-click menu).</summary>
    public void RemoveLink(LinkViewModel link)
    {
        SetSelectedLink(null);
        Disconnect(link.From);
    }

    /// <summary>Removes a node's outgoing arrow.</summary>
    public void Disconnect(CanvasNodeViewModel node)
    {
        node = EndOf(node);   // a folded block's arrow leaves its hidden closer
        if (node.Next == null) return;
        Link(node, null);
        MarkDirty();
        RefreshChain();
        Status = "Arrow removed — the blocks after it no longer run until they are linked again.";
    }

    /// <summary>
    /// Re-derives everything that follows from the arrows: step numbers, which nodes are
    /// unconnected, the If / Else tint, the link list and the row layout. Called after any
    /// structural edit.
    /// </summary>
    private void RefreshChain()
    {
        var chain = Chain();
        var inChain = new HashSet<CanvasNodeViewModel>(chain) { StartNode };

        for (int i = 0; i < chain.Count; i++)
            chain[i].Ordinal = (i + 1).ToString(CultureInfo.InvariantCulture);
        foreach (var node in Nodes)
        {
            node.IsOrphan = !inChain.Contains(node);
            if (node.IsOrphan || node.Kind == CanvasNodeKind.Start) node.Ordinal = string.Empty;
        }

        RecomputeConditionRegions(chain);
        RecomputeCollapse(chain, inChain);

        // A column break lives on the arrow into its node — it goes when that arrow goes
        // (node unlinked, deleted, or no longer reachable from Start).
        foreach (var node in Nodes)
            if (node.IsColumnBreak && (!inChain.Contains(node) || node.Prev == null || node.Kind == CanvasNodeKind.Start))
                node.IsColumnBreak = false;
        var breakLabel = new Dictionary<CanvasNodeViewModel, string>();
        int flagged = 0;
        foreach (var node in chain)
        {
            if (!node.IsColumnBreak) continue;
            if (++flagged > MaxColumnBreaks) { node.IsColumnBreak = false; continue; }
            if (!node.IsHidden) breakLabel[node] = $"E{breakLabel.Count + 1}";
        }

        _selectedLink = null;
        Links.Clear();
        foreach (var node in Nodes)
        {
            if (node.IsHidden) continue;
            // A folded opener's arrow is the one leaving its hidden closer.
            var folded = _collapseEnd.TryGetValue(node, out var closer);
            var target = folded ? closer!.Next : node.Next;
            if (target == null || !Nodes.Contains(target)) continue;

            var link = new LinkViewModel(node, target) { IsVirtual = folded };
            if (breakLabel.TryGetValue(target, out var label))
            {
                link.IsBreak = true;
                link.Label = label;
            }
            Links.Add(link);
        }

        Relayout();
        RunSelectedCommand.RaiseCanExecuteChanged();
        RunFromCommand.RaiseCanExecuteChanged();
    }

    // --- folding: If … End If, Loop Start … Loop End, Group Start … Group End ---

    // Rebuilt by RecomputeCollapse() on every structural change.
    private readonly Dictionary<CanvasNodeViewModel, CanvasNodeViewModel> _collapseEnd = new();   // folded opener → its closer
    private readonly Dictionary<CanvasNodeViewModel, CanvasNodeViewModel> _hiddenOwner = new();   // hidden node → the folded opener standing for it

    private static string OpOf(CanvasNodeViewModel node) => node.Block?.OpKey.ToLowerInvariant() ?? string.Empty;

    /// <summary>The closer op that ends the range an opener op starts, or null when <paramref name="op"/> opens nothing.</summary>
    private static string? CloserFor(string op) => op switch
    {
        "flow.if" or "flow.ifsignal" or "flow.ifposition" => "flow.endif",
        "flow.loopstart" => "flow.loopend",
        "flow.groupstart" => "flow.groupend",
        _ => null,
    };

    /// <summary>Index of the closer matching the opener at <paramref name="i"/> (nesting-aware), or -1.</summary>
    private static int MatchingCloser(List<CanvasNodeViewModel> chain, int i)
    {
        var closer = CloserFor(OpOf(chain[i]));
        if (closer == null) return -1;
        int depth = 0;
        for (int j = i + 1; j < chain.Count; j++)
        {
            var op = OpOf(chain[j]);
            if (CloserFor(op) == closer) depth++;
            else if (op == closer)
            {
                if (depth == 0) return j;
                depth--;
            }
        }
        return -1;
    }

    /// <summary>
    /// Works out which openers can fold and, for the folded ones, which nodes they hide.
    /// A fold nested inside another folded range keeps its own state but is simply hidden
    /// with the rest; an opener that lost its closer (or left the chain) unfolds.
    /// </summary>
    private void RecomputeCollapse(List<CanvasNodeViewModel> chain, HashSet<CanvasNodeViewModel> inChain)
    {
        _collapseEnd.Clear();
        _hiddenOwner.Clear();
        foreach (var node in Nodes)
        {
            node.IsHidden = false;
            node.CanCollapse = false;
            node.CollapsedText = string.Empty;
            if (!inChain.Contains(node)) node.IsCollapsed = false;
        }

        for (int i = 0; i < chain.Count; i++)
        {
            var node = chain[i];
            int j = MatchingCloser(chain, i);
            node.CanCollapse = j > i;
            if (j < 0) { node.IsCollapsed = false; continue; }
            if (!node.IsCollapsed || node.IsHidden) continue;

            _collapseEnd[node] = chain[j];
            for (int k = i + 1; k <= j; k++)
            {
                chain[k].IsHidden = true;
                _hiddenOwner[chain[k]] = node;
            }
            node.CollapsedText = $"{j - i} step(s) folded · to {chain[j].Block?.Display ?? "end"}";
            node.Ordinal = $"{i + 1}–{j + 1}";
        }
    }

    /// <summary>The last node a (possibly folded) node stands for — its hidden closer, else itself.</summary>
    private CanvasNodeViewModel EndOf(CanvasNodeViewModel node)
        => _collapseEnd.TryGetValue(node, out var closer) ? closer : node;

    /// <summary>A node plus, when it is folded, every node it hides (in chain order).</summary>
    private List<CanvasNodeViewModel> WithFolded(CanvasNodeViewModel node)
    {
        var list = new List<CanvasNodeViewModel> { node };
        if (!_collapseEnd.TryGetValue(node, out var closer)) return list;
        var seen = new HashSet<CanvasNodeViewModel> { node };
        for (var n = node.Next; n != null && seen.Add(n); n = n.Next)
        {
            list.Add(n);
            if (ReferenceEquals(n, closer)) break;
        }
        return list;
    }

    /// <summary>
    /// Folds / unfolds an If, Loop Start or Group Start block: the blocks through its
    /// matching closer hide behind it. Layout only — the sequence and the program file
    /// are unchanged, and the hidden blocks still run.
    /// </summary>
    public void ToggleCollapse(CanvasNodeViewModel node)
    {
        if (!node.IsCollapsed && !node.CanCollapse)
        {
            Status = CloserFor(OpOf(node)) == null
                ? "Only If, Loop Start and Group Start blocks can be folded."
                : "Can't fold — no matching closing block further down the sequence.";
            return;
        }

        node.IsCollapsed = !node.IsCollapsed;
        RefreshChain();
        if (node.IsCollapsed)
        {
            SelectNode(node, range: false);   // anything selected inside is now hidden
            Status = $"Folded — {node.CollapsedText}. The hidden steps still run.";
        }
        else
        {
            Status = "Unfolded.";
        }
        PersistLayout(onlyIfSaved: true);
    }

    /// <summary>Unfolds whatever hides <paramref name="node"/>, so it can be shown and selected.</summary>
    private void Reveal(CanvasNodeViewModel node)
    {
        for (int guard = 0; guard < 64 && _hiddenOwner.TryGetValue(node, out var opener); guard++)
        {
            opener.IsCollapsed = false;
            RefreshChain();
        }
    }

    /// <summary>
    /// Re-tints the chain by walking the if/else/endif markers and tracking branch nesting:
    /// a block between If and Else is "Then" (pink), between Else and End If is "Else"
    /// (yellow); the markers themselves and blocks outside any conditional are untinted.
    /// Nesting uses a stack; only the innermost branch tints.
    /// </summary>
    private void RecomputeConditionRegions(List<CanvasNodeViewModel> chain)
    {
        static bool IsOpener(string op)
            => op is "flow.if" or "flow.ifsignal" or "flow.ifposition";

        foreach (var node in Nodes) node.Region = ConditionRegion.None;

        var stack = new Stack<ConditionRegion>();
        foreach (var node in chain)
        {
            var op = node.Block?.OpKey.ToLowerInvariant() ?? string.Empty;
            bool isBoundary = IsOpener(op) || op == "flow.else" || op == "flow.endif";
            node.Region = isBoundary || stack.Count == 0 ? ConditionRegion.None : stack.Peek();

            if (IsOpener(op)) stack.Push(ConditionRegion.Then);
            else if (op == "flow.else" && stack.Count > 0) { stack.Pop(); stack.Push(ConditionRegion.Else); }
            else if (op == "flow.endif" && stack.Count > 0) stack.Pop();
        }
    }

    // --- layout: stage rows + arrows ---

    /// <summary>
    /// Lays the canvas out from each node's <see cref="CanvasNodeViewModel.Row"/>: a row is as
    /// tall as its tallest node (so a parallel envelope stretches its row), nodes sit at the
    /// top of their row, and the arrows are re-routed.
    /// </summary>
    public void Relayout()
    {
        int maxRow = 0;
        foreach (var node in Nodes) maxRow = Math.Max(maxRow, node.Row);
        int count = Math.Max(MinRows, maxRow + 1 + SpareRows);

        // A row holding only folded-away nodes closes up to nothing.
        var heights = new double[count];
        var used = new bool[count];
        var shown = new bool[count];
        Array.Fill(heights, MinRowHeight);
        foreach (var node in Nodes)
        {
            used[node.Row] = true;
            if (node.IsHidden) continue;
            shown[node.Row] = true;
            heights[node.Row] = Math.Max(heights[node.Row], node.Height + 2 * RowPad);
        }
        for (int i = 0; i < count; i++)
            if (used[i] && !shown[i]) heights[i] = 0;

        while (Rows.Count < count) Rows.Add(new GridRowViewModel());
        while (Rows.Count > count) Rows.RemoveAt(Rows.Count - 1);

        double top = 0;
        for (int i = 0; i < count; i++)
        {
            Rows[i].Label = i == 0 ? string.Empty : i.ToString(CultureInfo.InvariantCulture);
            Rows[i].Top = top;
            Rows[i].Height = heights[i];
            top += heights[i];
        }

        double right = 0;
        foreach (var node in Nodes)
        {
            if (!ReferenceEquals(node, DraggingNode))
                node.Y = Rows[node.Row].Top + RowPad;
            if (!node.IsHidden) right = Math.Max(right, node.X + node.Width);
        }

        CanvasHeight = top + 20;
        CanvasWidth = Math.Max(900, right + 320);
        UpdateLinks();
    }

    /// <summary>Re-routes every arrow from the nodes' current positions (called while dragging).</summary>
    public void UpdateLinks()
    {
        foreach (var link in Links)
        {
            if (!link.IsBreak)
            {
                (link.Data, link.Head) = LinkRouter.Route(link.From, link.To);
                continue;
            }

            var (data, head, exit, entry) = LinkRouter.RouteBreak(link.From, link.To);
            link.Data = data;
            link.Head = head;
            link.ExitLeft = exit.X - LinkRouter.MarkerSize / 2;
            link.ExitTop = exit.Y - LinkRouter.MarkerSize / 2;
            link.EntryLeft = entry.X - LinkRouter.MarkerSize / 2;
            link.EntryTop = entry.Y - LinkRouter.MarkerSize / 2;
        }
    }

    /// <summary>The stage row at canvas height <paramref name="y"/> (the last row when below the grid).</summary>
    private int RowAt(double y)
    {
        for (int i = 0; i < Rows.Count; i++)
            if (y < Rows[i].Top + Rows[i].Height) return i;
        return Math.Max(0, Rows.Count - 1);
    }

    /// <summary>The topmost node under a canvas point, ignoring <paramref name="except"/>.</summary>
    public CanvasNodeViewModel? NodeAt(Point p, CanvasNodeViewModel? except = null)
    {
        for (int i = Nodes.Count - 1; i >= 0; i--)
            if (!Nodes[i].IsHidden && !ReferenceEquals(Nodes[i], except) && Nodes[i].Bounds.Contains(p))
                return Nodes[i];
        return null;
    }

    /// <summary>The parallel envelope under a canvas point, ignoring <paramref name="except"/>.</summary>
    public CanvasNodeViewModel? ParallelAt(Point p, CanvasNodeViewModel? except = null)
    {
        for (int i = Nodes.Count - 1; i >= 0; i--)
            if (Nodes[i].Kind == CanvasNodeKind.Parallel && !Nodes[i].IsHidden && !ReferenceEquals(Nodes[i], except) && Nodes[i].Bounds.Contains(p))
                return Nodes[i];
        return null;
    }

    private CanvasNodeViewModel? _dropTarget;

    /// <summary>Lights the envelope a drag would drop into (null clears).</summary>
    public void SetDropTarget(CanvasNodeViewModel? node)
    {
        if (ReferenceEquals(_dropTarget, node)) return;
        if (_dropTarget != null) _dropTarget.IsDropTarget = false;
        _dropTarget = node;
        if (node != null) node.IsDropTarget = true;
    }

    /// <summary>
    /// A node drag ended: a command block released over a parallel envelope moves into it;
    /// otherwise the node snaps to the stage row under its centre and keeps its X.
    /// </summary>
    public void EndDrag(CanvasNodeViewModel node)
    {
        DraggingNode = null;
        SetDropTarget(null);
        var centre = new Point(node.X + node.Width / 2, node.Y + node.Height / 2);

        if (node.Kind == CanvasNodeKind.Block && node.Block != null && ParallelAt(centre, node) is { } envelope)
        {
            var block = node.Block;
            Detach(node);
            Nodes.Remove(node);
            _selectedNodes.Remove(node);
            AddBlockToParallel(envelope, block);
            SelectInnerBlock(block);
            MarkDirty();
            RefreshChain();
            Status = $"Moved '{block.Display}' into the parallel group.";
            return;
        }

        node.X = Math.Max(RowGutter, node.X);
        node.Row = RowAt(centre.Y);
        Relayout();
        PersistLayout(onlyIfSaved: true);
    }

    /// <summary>
    /// Re-arranges every node: the chain top to bottom, one per row, starting a new column
    /// (back at row 1) at each column break; unconnected nodes go in a last column.
    /// </summary>
    private void Tidy()
    {
        ArrangeColumns();
        Status = "Canvas tidied.";
    }

    private void ArrangeColumns()
    {
        double x = DefaultX;
        double widest = StartNode.Width;
        int row = 1;
        StartNode.X = x;
        StartNode.Row = 0;
        foreach (var node in Chain())
        {
            if (node.IsColumnBreak && !node.IsHidden)
            {
                x += widest + ColumnGap;
                widest = 0;
                row = 1;   // row 0 of the new column holds the entry marker
            }
            node.X = x;
            node.Row = row++;
            widest = Math.Max(widest, node.Width);
        }

        x += widest + ColumnGap;
        row = 1;
        foreach (var node in Nodes.Where(n => n.IsOrphan))
        {
            node.X = x;
            node.Row = row++;
        }
        Relayout();
        PersistLayout(onlyIfSaved: true);
    }

    /// <summary>
    /// Starts a new column at <paramref name="node"/> (or undoes it): the rest of the
    /// sequence moves to the top of the next column, and the arrow into the node becomes an
    /// E marker pair. Three columns at most — E1 and E2. Layout only; execution is unchanged.
    /// </summary>
    public void ToggleColumnBreak(CanvasNodeViewModel node)
    {
        if (node.IsColumnBreak)
        {
            node.IsColumnBreak = false;
            RefreshChain();
            ArrangeColumns();
            Status = "Column break removed.";
            return;
        }

        if (node.Kind == CanvasNodeKind.Start || node.IsOrphan || node.Prev == null || ReferenceEquals(node.Prev, StartNode))
        {
            Status = "A new column can start at any connected block after the first one.";
            return;
        }
        if (Nodes.Count(n => n.IsColumnBreak) >= MaxColumnBreaks)
        {
            Status = "Three columns at most (E1 and E2) — remove a column break first.";
            return;
        }

        node.IsColumnBreak = true;
        RefreshChain();
        ArrangeColumns();
        Status = "The sequence continues in a new column from here.";
    }

    /// <summary>Gives <paramref name="node"/> the row under <paramref name="anchor"/>, pushing later rows down if it is taken.</summary>
    private void PlaceAfter(CanvasNodeViewModel anchor, CanvasNodeViewModel node)
    {
        int row = Math.Min(anchor.Row + 1, MaxRow);
        // Only nodes in the anchor's column move down — other columns keep their rows.
        bool SameColumn(CanvasNodeViewModel n)
            => !ReferenceEquals(n, node) && n.X < anchor.X + anchor.Width && n.X + n.Width > anchor.X;
        if (Nodes.Any(n => SameColumn(n) && n.Row == row))
            foreach (var n in Nodes)
                if (SameColumn(n) && n.Row >= row && n.Row < MaxRow) n.Row++;
        node.X = anchor.X;
        node.Row = row;
    }

    private void PersistLayout(bool onlyIfSaved)
    {
        if (onlyIfSaved && _selectedProgram == null) return;   // never-saved program — nothing to key on yet
        var product = _host.Process.ActiveProduct;
        var name = ProgramName.Trim();
        if (string.IsNullOrWhiteSpace(product) || name.Length == 0) return;

        var spots = new Dictionary<string, CanvasSpot>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in Nodes)
            spots[node.Uid] = new CanvasSpot
            {
                X = Math.Round(node.X, 1),
                Row = node.Row,
                ColumnBreak = node.IsColumnBreak,
                Collapsed = node.IsCollapsed,
            };
        try
        {
            ProgramCanvasLayoutStore.Save(product, name, spots);
        }
        catch (Exception ex)
        {
            // Layout is a view preference — a failed write must not block editing or saving.
            Status = $"Could not save the canvas layout: {ex.Message}";
        }
    }

    // --- selection ---

    private void SetNodeSelection(IEnumerable<CanvasNodeViewModel> nodes)
    {
        foreach (var n in _selectedNodes) n.IsSelected = false;
        _selectedNodes.Clear();
        foreach (var n in nodes)
        {
            n.IsSelected = true;
            _selectedNodes.Add(n);
        }
        RunSelectedCommand.RaiseCanExecuteChanged();
        RunFromCommand.RaiseCanExecuteChanged();
    }

    private void SetSelectedLink(LinkViewModel? link)
    {
        if (_selectedLink != null) _selectedLink.IsSelected = false;
        _selectedLink = link;
        if (link != null) link.IsSelected = true;
    }

    /// <summary>
    /// Node click. Plain: selects just this node. <paramref name="range"/> (Shift): selects
    /// every node along the chain from the last plain-clicked node to this one.
    /// </summary>
    public void SelectNode(CanvasNodeViewModel node, bool range)
    {
        SetSelectedLink(null);
        if (range && _anchorNode != null && !ReferenceEquals(_anchorNode, node))
        {
            var order = new List<CanvasNodeViewModel> { StartNode };
            order.AddRange(Chain());
            int a = order.IndexOf(_anchorNode), b = order.IndexOf(node);
            if (a >= 0 && b >= 0)
            {
                SetNodeSelection(order.Skip(Math.Min(a, b)).Take(Math.Abs(a - b) + 1));
                SelectedBlock = null;
                return;
            }
        }

        _anchorNode = node;
        SetNodeSelection(new[] { node });
        SelectedBlock = node.Block;
    }

    /// <summary>Selects a block inside a parallel envelope (its parameters show on the right).</summary>
    public void SelectInnerBlock(ProcessBlockViewModel block)
    {
        SetSelectedLink(null);
        SetNodeSelection(Array.Empty<CanvasNodeViewModel>());
        _anchorNode = OwnerOf(block);
        SelectedBlock = block;
    }

    /// <summary>Selects an arrow (Delete then removes it).</summary>
    public void SelectLink(LinkViewModel link)
    {
        SetNodeSelection(Array.Empty<CanvasNodeViewModel>());
        SelectedBlock = null;
        SetSelectedLink(link);
        Status = "Arrow selected — press Delete to remove it.";
    }

    public void ClearSelection()
    {
        SetSelectedLink(null);
        SetNodeSelection(Array.Empty<CanvasNodeViewModel>());
        _anchorNode = null;
        SelectedBlock = null;
    }

    /// <summary>Selects a block wherever it lives — its own node, or inside an envelope.</summary>
    private void SelectBlock(ProcessBlockViewModel block)
    {
        var owner = OwnerOf(block);
        if (owner == null) return;
        Reveal(owner);
        if (owner.Kind == CanvasNodeKind.Block) SelectNode(owner, range: false);
        else SelectInnerBlock(block);
    }

    private CanvasNodeViewModel? OwnerOf(ProcessBlockViewModel block)
        => Nodes.FirstOrDefault(n => n.AllBlocks().Contains(block));

    /// <summary>The envelope the current selection points at: a selected envelope, or the one holding the selected block.</summary>
    private CanvasNodeViewModel? SelectedParallel()
    {
        if (_selectedNodes.Count == 1 && _selectedNodes[0].Kind == CanvasNodeKind.Parallel) return _selectedNodes[0];
        if (_selectedBlock != null && OwnerOf(_selectedBlock) is { Kind: CanvasNodeKind.Parallel } owner) return owner;
        return null;
    }

    /// <summary>The node new blocks go after: the last selected node in chain order, else the end of the chain.</summary>
    private CanvasNodeViewModel InsertAnchor()
    {
        var order = new List<CanvasNodeViewModel> { StartNode };
        order.AddRange(Chain());
        var anchor = order.LastOrDefault(_selectedNodes.Contains);
        if (anchor == null && _anchorNode != null && order.Contains(_anchorNode)) anchor = _anchorNode;
        return EndOf(anchor ?? order[^1]);   // after a folded block = after its hidden closer
    }

    // --- adding / removing ---

    private ProcessBlockViewModel NewBlock(OpSpec spec)
    {
        var block = new ProcessBlockViewModel(_host, spec, ProcessBlockViewModel.NewId());
        Track(block);
        return block;
    }

    /// <summary>
    /// Palette double-click. With a parallel envelope selected the command joins it; otherwise
    /// it becomes a new block spliced in right after the selected node (or at the end).
    /// </summary>
    private void AddOp(OpSpec spec)
    {
        if (SelectedParallel() is { } envelope)
        {
            AddToParallel(envelope, spec);
            return;
        }

        var anchor = InsertAnchor();
        var node = CanvasNodeViewModel.ForBlock(NewBlock(spec));
        Nodes.Add(node);
        PlaceAfter(anchor, node);
        InsertAfter(anchor, node);
        MarkDirty();
        RefreshChain();
        SelectNode(node, range: false);
        Status = $"Added '{spec.Display}' as step {node.Ordinal}.";
    }

    /// <summary>Palette drag released on the open canvas: a new block there, linked to the end of the chain.</summary>
    public void DropOpAt(OpSpec spec, double x, double y)
    {
        var tail = Tail();
        var node = CanvasNodeViewModel.ForBlock(NewBlock(spec));
        node.X = Math.Max(RowGutter, x);
        node.Row = RowAt(y);
        Nodes.Add(node);
        Link(tail, node);
        MarkDirty();
        RefreshChain();
        SelectNode(node, range: false);
        Status = $"Added '{spec.Display}' as step {node.Ordinal}.";
    }

    /// <summary>Adds a new command to a parallel envelope, on the branch for its device.</summary>
    public void AddToParallel(CanvasNodeViewModel envelope, OpSpec spec)
    {
        var block = NewBlock(spec);
        AddBlockToParallel(envelope, block);
        MarkDirty();
        RefreshChain();
        SelectInnerBlock(block);
        Status = $"Added '{spec.Display}' to the parallel group.";
    }

    /// <summary>
    /// Puts a block on the envelope's branch for its lane (device, Signal or Control), making
    /// the branch if needed — commands for one device queue up in one branch instead of
    /// fighting over the device.
    /// </summary>
    private static void AddBlockToParallel(CanvasNodeViewModel envelope, ProcessBlockViewModel block)
    {
        var laneId = LaneIds.For(block);
        var branch = envelope.Branches.FirstOrDefault(b => b.LaneId.Equals(laneId, StringComparison.OrdinalIgnoreCase));
        if (branch == null)
        {
            branch = new ParallelBranchViewModel(laneId);
            envelope.Branches.Add(branch);
        }
        branch.Blocks.Add(block);
    }

    private static void RemoveFromParallel(CanvasNodeViewModel envelope, ProcessBlockViewModel block)
    {
        foreach (var branch in envelope.Branches.ToList())
            if (branch.Blocks.Remove(block) && branch.Blocks.Count == 0)
                envelope.Branches.Remove(branch);
    }

    /// <summary>Adds an empty parallel envelope after the selected node (or at the end).</summary>
    private void AddParallel()
    {
        var anchor = InsertAnchor();
        var node = CanvasNodeViewModel.ForParallel();
        Nodes.Add(node);
        PlaceAfter(anchor, node);
        InsertAfter(anchor, node);
        MarkDirty();
        RefreshChain();
        SelectNode(node, range: false);
        Status = "Parallel group added — drag commands or blocks into it; each device gets its own branch.";
    }

    /// <summary>
    /// Takes a block out of its parallel envelope and makes it a free node at
    /// (<paramref name="x"/>, <paramref name="y"/>) — unconnected until an arrow is drawn to it.
    /// </summary>
    public CanvasNodeViewModel? PopOut(ProcessBlockViewModel block, double x, double y)
    {
        if (OwnerOf(block) is not { Kind: CanvasNodeKind.Parallel } envelope) return null;
        RemoveFromParallel(envelope, block);

        var node = CanvasNodeViewModel.ForBlock(block);
        node.X = Math.Max(RowGutter, x);
        node.Y = Math.Max(0, y);
        node.Row = RowAt(y);
        Nodes.Add(node);
        MarkDirty();
        RefreshChain();
        SelectNode(node, range: false);
        Status = $"'{block.Display}' taken out of the parallel group — link it into the sequence.";
        return node;
    }

    /// <summary>Context-menu form of <see cref="PopOut"/>: the block lands just right of its envelope.</summary>
    public void MoveOut(ProcessBlockViewModel block)
    {
        if (OwnerOf(block) is not { } envelope) return;
        PopOut(block, envelope.X + envelope.Width + 40, envelope.Y);
    }

    /// <summary>Deletes one node (not Start); the nodes around it are joined so the order is kept.</summary>
    public void RemoveNode(CanvasNodeViewModel node)
    {
        if (node.Kind == CanvasNodeKind.Start) return;
        foreach (var n in WithFolded(node))   // a folded block goes with everything it hides
        {
            Detach(n);
            Nodes.Remove(n);
        }
        _selectedNodes.Remove(node);
        node.IsSelected = false;
        if (ReferenceEquals(_anchorNode, node)) _anchorNode = null;
        if (_selectedBlock != null && OwnerOf(_selectedBlock) == null) SelectedBlock = null;
        MarkDirty();
        RefreshChain();
    }

    /// <summary>Delete key / "Delete block": the selected arrow, else the selected block inside an envelope, else the selected nodes.</summary>
    public void DeleteSelection()
    {
        if (_selectedLink is { } link)
        {
            if (link.IsVirtual) { Status = "Unfold the block first — this arrow leaves a step that is folded away."; return; }
            Disconnect(link.From);
            return;
        }

        if (_selectedBlock != null && OwnerOf(_selectedBlock) is { Kind: CanvasNodeKind.Parallel } envelope)
        {
            RemoveFromParallel(envelope, _selectedBlock);
            SelectedBlock = null;
            MarkDirty();
            RefreshChain();
            return;
        }

        var doomed = _selectedNodes.Where(n => n.Kind != CanvasNodeKind.Start).SelectMany(WithFolded).Distinct().ToList();
        if (doomed.Count == 0) return;
        foreach (var node in doomed)
        {
            Detach(node);
            Nodes.Remove(node);
        }
        ClearSelection();
        MarkDirty();
        RefreshChain();
        Status = $"Deleted {doomed.Count} node(s).";
    }

    // --- copy / paste ---

    private static WorkflowStep StripIds(WorkflowStep step) => step with
    {
        Id = string.Empty,
        Branches = step.Branches
            .Select(b => b with { Steps = b.Steps.Select(s => s with { Id = string.Empty }).ToList() })
            .ToList(),
    };

    private static WorkflowStep NodeToStep(CanvasNodeViewModel node)
        => node.Block != null
            ? node.Block.ToStep()
            : new WorkflowStep
            {
                Id = node.Uid,
                Op = "flow.parallel",
                Branches = node.Branches
                    .Where(b => b.Blocks.Count > 0)
                    .Select(b => new WorkflowBranch { Lane = b.LaneId, Steps = b.Blocks.Select(x => x.ToStep()).ToList() })
                    .ToList(),
            };

    /// <summary>Copies the selection (nodes in chain order, or the one selected block inside an envelope).</summary>
    public void CopySelection()
    {
        var clip = new List<WorkflowStep>();
        if (_selectedNodes.Count == 0 && _selectedBlock != null)
        {
            clip.Add(StripIds(_selectedBlock.ToStep()));
        }
        else
        {
            var chain = Chain();
            var picked = _selectedNodes.Where(n => n.Kind != CanvasNodeKind.Start)
                .OrderBy(n => { int i = chain.IndexOf(n); return i < 0 ? int.MaxValue : i; })
                .ThenBy(n => n.Row)
                .SelectMany(WithFolded)   // a folded block copies with everything it hides
                .Distinct();
            foreach (var node in picked)
            {
                var step = NodeToStep(node);
                if (node.Kind == CanvasNodeKind.Parallel && step.Branches.Count == 0) continue;   // empty envelope
                clip.Add(StripIds(step));
            }
        }
        if (clip.Count == 0) return;

        _clipboard = clip;
        Status = $"Copied {clip.Count} node(s).";
    }

    /// <summary>
    /// Pastes the clipboard after the selected node (or at the end of the chain), in order,
    /// then selects the pasted nodes. Every block is rebuilt with a fresh id.
    /// </summary>
    public void Paste()
    {
        if (_clipboard is not { Count: > 0 } clipboard) return;

        var anchor = InsertAnchor();
        var pasted = new List<CanvasNodeViewModel>();
        foreach (var step in clipboard)
        {
            var node = NodeFromStep(step);
            Nodes.Add(node);
            PlaceAfter(anchor, node);
            InsertAfter(anchor, node);
            pasted.Add(node);
            anchor = node;
        }

        MarkDirty();
        RefreshChain();
        SetSelectedLink(null);
        SetNodeSelection(pasted);
        _anchorNode = pasted[0];
        SelectedBlock = pasted.Count == 1 ? pasted[0].Block : null;
        Status = $"Pasted {pasted.Count} node(s).";
    }

    // --- programs ---

    private void RefreshPrograms()
    {
        var names = _host.Process.ListPrograms();
        if (!Programs.SequenceEqual(names))
        {
            Programs.Clear();
            foreach (var name in names)
                Programs.Add(name);
        }
        RebuildProgramList();
    }

    /// <summary>
    /// Re-fills <see cref="ProgramList"/> from <see cref="Programs"/>: the active product's
    /// hidden set (<see cref="HiddenProgramStore"/>), the search text (every word must match),
    /// and the Show-hidden switch. The open program is marked current.
    /// </summary>
    private void RebuildProgramList()
    {
        var hidden = HiddenProgramStore.Load(_host.Process.ActiveProduct);
        var words = _programFilter.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        ProgramList.Clear();
        int hiddenCount = 0;
        foreach (var name in Programs)
        {
            bool isHidden = hidden.Contains(name);
            if (isHidden) hiddenCount++;
            if (isHidden && !_showHiddenPrograms) continue;
            if (!words.All(w => name.Contains(w, StringComparison.OrdinalIgnoreCase))) continue;
            ProgramList.Add(new ProgramListItem(name, isHidden, ToggleProgramHidden)
            {
                IsCurrent = string.Equals(name, _selectedProgram, StringComparison.OrdinalIgnoreCase),
            });
        }
        ProgramListSummary = $"{ProgramList.Count} of {Programs.Count}" + (hiddenCount > 0 ? $" · {hiddenCount} hidden" : "");
    }

    /// <summary>Hides a visible program / un-hides a hidden one, saved per product.</summary>
    private void ToggleProgramHidden(ProgramListItem item)
    {
        var product = _host.Process.ActiveProduct;
        if (string.IsNullOrWhiteSpace(product)) { Status = "No active product."; return; }
        var hidden = HiddenProgramStore.Load(product);
        if (!hidden.Remove(item.Name)) hidden.Add(item.Name);
        try
        {
            HiddenProgramStore.Save(product, hidden);
        }
        catch (Exception ex)
        {
            Status = $"Could not save hidden programs: {ex.Message}";
            return;
        }
        Status = hidden.Contains(item.Name) ? $"Hidden '{item.Name}' from the list." : $"'{item.Name}' is visible again.";
        RebuildProgramList();
    }

    /// <summary>Marks the row of the program open in the editor (none for an unsaved new program).</summary>
    private void MarkCurrentProgram()
    {
        foreach (var item in ProgramList)
            item.IsCurrent = string.Equals(item.Name, _selectedProgram, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Empties the canvas down to the Start node.</summary>
    private void ResetCanvas()
    {
        ClearSelection();
        SetDropTarget(null);
        DraggingNode = null;
        Nodes.Clear();
        StartNode.Next = null;
        StartNode.Prev = null;
        StartNode.X = DefaultX;
        StartNode.Row = 0;
        Nodes.Add(StartNode);
        RefreshChain();
    }

    private void NewProgram()
    {
        _suppressDirty = true;
        _selectedProgram = null;
        OnPropertyChanged(nameof(SelectedProgram));
        MarkCurrentProgram();
        ProgramName = "New Program";
        ResetCanvas();
        _suppressDirty = false;
        IsDirty = false;
        RefreshVersionText();
        Status = "New program — drag a command onto the canvas, or double-click one to add it after Start.";
    }

    /// <summary>
    /// Loads a program by name into the editor (always reloads, even if it's already the
    /// selected one — so the Load window can be used to discard edits). Used by the
    /// custom Load picker.
    /// </summary>
    public void LoadProgramByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        _selectedProgram = name;
        OnPropertyChanged(nameof(SelectedProgram));
        MarkCurrentProgram();
        LoadProgram(name);
    }

    /// <summary>Refreshes <see cref="CurrentVersionText"/> from the named program's latest committed revision.</summary>
    private void RefreshVersionText()
    {
        var latest = _host.Process.GetProgramHistory(ProgramName)?.Latest;
        CurrentVersionText = latest != null ? $"v{latest.Version}" : "uncommitted";
    }

    private void LoadProgram(string name)
    {
        var result = _host.Process.LoadProgram(name);
        if (!result.IsSuccess) { Status = result.Message; return; }

        _suppressDirty = true;
        ProgramName = result.Value.Name;
        LoadFrom(result.Value);
        _suppressDirty = false;
        IsDirty = false;
        RefreshVersionText();
        Status = $"Loaded '{name}' — {Chain().Count} step(s).";
    }

    /// <summary>
    /// Rebuilds the canvas from a program: one node per top-level step, chained in file order
    /// from Start (a <c>flow.parallel</c> step becomes an envelope with its branches). Nodes
    /// take their saved canvas spot; one with none sits in the row under the node before it.
    /// </summary>
    private void LoadFrom(WorkflowProgram program)
    {
        ResetCanvas();

        var saved = ProgramCanvasLayoutStore.Load(_host.Process.ActiveProduct, program.Name);
        if (saved.TryGetValue(StartNode.Uid, out var startSpot)) ApplySpot(StartNode, startSpot);

        var prev = StartNode;
        foreach (var step in program.Steps)
        {
            var node = NodeFromStep(step);
            if (saved.TryGetValue(node.Uid, out var spot))
            {
                ApplySpot(node, spot);
                node.IsColumnBreak = spot.ColumnBreak;   // RefreshChain keeps the first two
                node.IsCollapsed = spot.Collapsed;       // …and unfolds anything that isn't a matched opener
            }
            else
            {
                node.X = prev.X;
                node.Row = Math.Min(prev.Row + 1, MaxRow);
            }
            Nodes.Add(node);
            Link(prev, node);
            prev = node;
        }
        RefreshChain();
    }

    private static void ApplySpot(CanvasNodeViewModel node, CanvasSpot spot)
    {
        node.X = double.IsFinite(spot.X) ? Math.Clamp(spot.X, RowGutter, 20000) : DefaultX;
        node.Row = Math.Clamp(spot.Row, 0, MaxRow);
    }

    /// <summary>A canvas node for a step: an envelope for a <c>flow.parallel</c> with branches, else a block.</summary>
    private CanvasNodeViewModel NodeFromStep(WorkflowStep step)
    {
        var isParallel = step.Op.Equals("flow.parallel", StringComparison.OrdinalIgnoreCase) && step.Branches.Count > 0;
        if (!isParallel) return CanvasNodeViewModel.ForBlock(MakeBlock(step));

        var envelope = CanvasNodeViewModel.ForParallel(step.Id);   // keep the parallel group's stable id
        foreach (var branch in step.Branches)
        {
            if (branch.Steps.Count == 0) continue;
            var lane = new ParallelBranchViewModel(string.IsNullOrEmpty(branch.Lane) ? LaneIds.Control : branch.Lane);
            foreach (var bstep in branch.Steps)
                lane.Blocks.Add(MakeBlock(bstep));
            envelope.Branches.Add(lane);
        }
        return envelope;
    }

    private ProcessBlockViewModel MakeBlock(WorkflowStep step)
    {
        var spec = OpCatalog.Find(step.Op) ?? OpCatalog.ForUnknown(step.Op);
        var block = ProcessBlockViewModel.FromStep(_host, spec, step);
        Track(block);   // edits after load mark the program dirty (load itself is suppressed)
        return block;
    }

    private IEnumerable<ProcessBlockViewModel> AllBlocks() => Nodes.SelectMany(n => n.AllBlocks());

    /// <summary>
    /// Clamps every motion block's velocity field to its device's controller range
    /// (writing the capped value back so the operator sees it). 0 / blank / expression
    /// values pass through. Called before save and run.
    /// </summary>
    private void ClampVelocities()
    {
        foreach (var block in AllBlocks())
        {
            var vp = block.Params.FirstOrDefault(p => string.Equals(p.Key, "velocity", StringComparison.OrdinalIgnoreCase));
            if (vp == null || string.IsNullOrWhiteSpace(vp.StringValue) || vp.StringValue.Contains('{')) continue;
            if (!double.TryParse(vp.StringValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v <= 0) continue;

            var type = _host.Engine.Motion.GetDevice(block.SelectedDevice ?? string.Empty)?.ControllerType;
            var clamped = MotionVelocityLimits.Clamp(v, type);
            if (Math.Abs(clamped - v) > 1e-9)
                vp.StringValue = clamped.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// A Wait Popup shorter than this renders for less than a frame or two — the
    /// window is shown and closed before WPF paints it, so the operator sees nothing.
    /// Below this floor we bump the duration up so the countdown is actually visible.
    /// </summary>
    private const int MinWaitPopupMs = 1000;

    /// <summary>
    /// Bumps every Wait Popup whose duration is a positive sub-second value up to
    /// <see cref="MinWaitPopupMs"/> (writing the value back so the operator sees it),
    /// so a too-short popup can't flash past invisibly. 0 / blank / expression values
    /// pass through (0 is a no-op popup; an expression resolves at run time). Returns
    /// the number of blocks bumped. Called before save and run, alongside
    /// <see cref="ClampVelocities"/>.
    /// </summary>
    private int ClampWaitPopups()
    {
        int bumped = 0;
        foreach (var block in AllBlocks())
        {
            if (!block.OpKey.Equals("flow.waitPopup", StringComparison.OrdinalIgnoreCase)) continue;
            var mp = block.Params.FirstOrDefault(p => string.Equals(p.Key, "ms", StringComparison.OrdinalIgnoreCase));
            if (mp == null || string.IsNullOrWhiteSpace(mp.StringValue) || mp.StringValue.Contains('{')) continue;
            if (!double.TryParse(mp.StringValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms)) continue;
            if (ms > 0 && ms < MinWaitPopupMs)
            {
                mp.StringValue = MinWaitPopupMs.ToString(CultureInfo.InvariantCulture);
                bumped++;
            }
        }
        return bumped;
    }

    /// <summary>A short status suffix noting how many Wait Popups were bumped, or empty.</summary>
    private static string WaitPopupBumpNote(int bumped)
        => bumped > 0 ? $" ({bumped} short Wait Popup(s) bumped to {MinWaitPopupMs} ms)" : string.Empty;

    /// <summary>
    /// Pre-flight validation — returns the first block that would no-op or error
    /// (and a reason), or null. Currently flags a Move Relative with a zero / empty
    /// distance; an expression value (<c>{..}</c>) is allowed since it resolves at
    /// run time. The caller selects the offending block so the operator can fix it.
    /// </summary>
    private ProcessBlockViewModel? FirstInvalidBlock(out string reason)
    {
        foreach (var b in AllBlocks())
        {
            if (!b.OpKey.Equals("motion.moveRelative", StringComparison.OrdinalIgnoreCase))
                continue;
            var d = b.Params.FirstOrDefault(p => p.Key == "distance")?.StringValue ?? string.Empty;
            if (d.Contains('{')) continue;   // expression — resolved at run time
            if (string.IsNullOrWhiteSpace(d) ||
                (double.TryParse(d, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v == 0))
            {
                reason = "Move Relative needs a non-zero distance.";
                return b;
            }
        }
        reason = string.Empty;
        return null;
    }

    /// <summary>
    /// Save writes the working <c>.hwf</c> only — it does not create a version. Commit
    /// (below) is what records a numbered revision in the version repository.
    /// </summary>
    private void SaveProgram()
    {
        var program = BuildValidatedProgram(out var bumped);
        if (program == null) return;

        var result = _host.Process.SaveProgram(program);
        if (!result.IsSuccess) { Status = result.Message; return; }

        Status = $"Saved '{program.Name}'." + WaitPopupBumpNote(bumped);
        AfterPersisted(program.Name);
    }

    /// <summary>
    /// Commit saves the working file <i>and</i> records a new numbered version (with the
    /// optional <see cref="CommitNote"/>) in the version repository. The note is cleared
    /// afterwards — it belongs to the revision just committed.
    /// </summary>
    private void CommitProgram()
    {
        var program = BuildValidatedProgram(out var bumped);
        if (program == null) return;

        var result = _host.Process.CommitProgram(program, CommitNote);
        if (!result.IsSuccess) { Status = result.Message; return; }

        Status = $"Committed '{program.Name}'." + WaitPopupBumpNote(bumped);
        CommitNote = string.Empty;
        AfterPersisted(program.Name);
    }

    /// <summary>Validates + compiles the canvas to a program, or null (with <see cref="Status"/> set) on failure.</summary>
    private WorkflowProgram? BuildValidatedProgram(out int bumped)
    {
        bumped = 0;
        if (string.IsNullOrWhiteSpace(ProgramName)) { Status = "Program name is required."; return null; }

        // Only the chain from Start is written to the file — an unconnected node would be
        // lost on the next load, so saving is refused until it is linked in or deleted.
        var orphans = Nodes.Where(n => n.IsOrphan).ToList();
        if (orphans.Count > 0)
        {
            SelectNode(orphans[0], range: false);
            Status = $"Can't save — {orphans.Count} node(s) are not connected to Start (shown faded). Link them into the sequence or delete them.";
            return null;
        }

        ClampVelocities();
        bumped = ClampWaitPopups();
        var invalid = FirstInvalidBlock(out var why);
        if (invalid != null) { SelectBlock(invalid); Status = $"Can't save — {why}"; return null; }

        return BuildProgram();
    }

    /// <summary>Refreshes the program list + selection and clears the dirty flag after a save/commit.</summary>
    private void AfterPersisted(string programName)
    {
        RefreshPrograms();
        _selectedProgram = programName;
        OnPropertyChanged(nameof(SelectedProgram));
        MarkCurrentProgram();
        IsDirty = false;
        RefreshVersionText();   // a commit bumps the version; a plain save leaves it
        PersistLayout(onlyIfSaved: false);
    }

    /// <summary>
    /// Compiles the canvas to a flat <see cref="WorkflowProgram"/> by walking the arrows
    /// from Start: a block emits its step; an envelope with one branch emits that branch's
    /// blocks inline (sequential); an envelope with two+ branches emits one
    /// <c>flow.parallel</c> step, one branch per lane. With <paramref name="onlyBlock"/> or
    /// <paramref name="onlyNode"/> just that block / node is compiled ("Run Selected");
    /// with <paramref name="fromNode"/> the chain from that node to the end ("Run From").
    /// Also rebuilds the run-highlight routing tables.
    /// </summary>
    private WorkflowProgram BuildProgram(CanvasNodeViewModel? onlyNode = null, ProcessBlockViewModel? onlyBlock = null,
        CanvasNodeViewModel? fromNode = null)
    {
        _topLevelNode.Clear();
        _inlineBlock.Clear();

        _stepNode.Clear();

        var steps = new List<WorkflowStep>();
        void Inline(ProcessBlockViewModel block, CanvasNodeViewModel? owner)
        {
            _inlineBlock[steps.Count] = block;
            if (owner != null) _stepNode[steps.Count] = owner;
            _topLevelNode.Add(null);
            steps.Add(block.ToStep());
        }

        if (onlyBlock != null)
        {
            Inline(onlyBlock, OwnerOf(onlyBlock));
            return new WorkflowProgram { Name = ProgramName.Trim(), Steps = steps };
        }

        var chain = Chain();
        var nodes = onlyNode != null ? WithFolded(onlyNode)   // a folded block runs its whole range
                  : fromNode != null ? chain.Skip(Math.Max(0, chain.IndexOf(fromNode))).ToList()
                  : chain;
        foreach (var node in nodes)
        {
            if (node.Block != null) { Inline(node.Block, node); continue; }
            if (node.Kind != CanvasNodeKind.Parallel) continue;

            var active = node.Branches.Where(b => b.Blocks.Count > 0).ToList();
            if (active.Count == 0) continue;
            if (active.Count == 1)
            {
                foreach (var block in active[0].Blocks) Inline(block, node);
                continue;
            }

            int ordinal = chain.IndexOf(node) + 1;
            _stepNode[steps.Count] = node;
            _topLevelNode.Add(node);
            steps.Add(new WorkflowStep
            {
                Id = node.Uid,   // stable parallel-group id (trackable across saves)
                Name = $"Stage {ordinal} (parallel)",
                Op = "flow.parallel",
                Branches = active.Select(b => new WorkflowBranch
                {
                    Lane = b.LaneId,
                    Steps = b.Blocks.Select(x => x.ToStep()).ToList(),
                }).ToList(),
            });
        }
        return new WorkflowProgram { Name = ProgramName.Trim(), Steps = steps };
    }

    // --- run ---

    private bool HasRunnableSelection()
        => _selectedBlock != null || (_selectedNodes.Count == 1 && _selectedNodes[0].Kind != CanvasNodeKind.Start);

    /// <summary>Runs only the selected block, or the one selected node (a parallel envelope runs all its branches).</summary>
    private async Task RunSelectedAsync()
    {
        if (_selectedNodes.Count == 1 && _selectedNodes[0].Kind != CanvasNodeKind.Start)
            await RunAsync(_selectedNodes[0], null, null);
        else if (_selectedBlock != null)
            await RunAsync(null, _selectedBlock, null);
    }

    /// <summary>
    /// The node "Run From" would start at: the one selected node, or the envelope holding
    /// the selected block — provided it is on the chain from Start. Null otherwise.
    /// </summary>
    private CanvasNodeViewModel? RunFromNode()
    {
        var node = _selectedNodes.Count == 1 ? _selectedNodes[0]
                 : _selectedNodes.Count == 0 && _selectedBlock != null ? OwnerOf(_selectedBlock)
                 : null;
        return node == null || node.Kind == CanvasNodeKind.Start || node.IsOrphan ? null : node;
    }

    /// <summary>Runs the sequence from the selected step to the end, skipping everything before it.</summary>
    private async Task RunFromSelectedAsync()
    {
        if (RunFromNode() is { } node)
            await RunAsync(null, null, node);
    }

    /// <summary>
    /// Runs the program — or only <paramref name="onlyNode"/> / <paramref name="onlyBlock"/>,
    /// or the chain from <paramref name="fromNode"/> to the end, when set.
    /// </summary>
    private async Task RunAsync(CanvasNodeViewModel? onlyNode, ProcessBlockViewModel? onlyBlock, CanvasNodeViewModel? fromNode)
    {
        if (IsRunning) return;
        bool partial = onlyNode != null || onlyBlock != null;

        ClampVelocities();
        var bumped = ClampWaitPopups();
        var invalid = FirstInvalidBlock(out var why);
        if (invalid != null) { SelectBlock(invalid); Status = $"Can't run — {why}"; return; }

        ResetRunStatus();
        Log.Clear();
        _runningNode = null;

        // Note any sub-second Wait Popups we bumped (after the clear, so it survives).
        if (bumped > 0)
            Log.Add($"{DateTimeOffset.Now:HH:mm:ss}  {bumped} Wait Popup(s) under {MinWaitPopupMs} ms bumped to {MinWaitPopupMs} ms so the countdown is visible.");

        int orphans = Nodes.Count(n => n.IsOrphan);
        if (!partial && orphans > 0)
            Log.Add($"{DateTimeOffset.Now:HH:mm:ss}  {orphans} node(s) are not connected to Start and will not run.");

        var label = partial ? "the selection"
                  : fromNode != null ? $"'{ProgramName.Trim()}' from step {fromNode.Ordinal}"
                  : $"'{ProgramName.Trim()}'";
        if (fromNode != null)
        {
            Log.Add($"{DateTimeOffset.Now:HH:mm:ss}  Run from step {fromNode.Ordinal}: steps 1–{Math.Max(1, Chain().IndexOf(fromNode))} are skipped — variables, positions and outputs they would have set are NOT set.");
            if (fromNode.Region != ConditionRegion.None)
                Log.Add($"{DateTimeOffset.Now:HH:mm:ss}  Step {fromNode.Ordinal} is inside an If / Else branch whose If is skipped — the Else / End If after it may not behave as in a full run.");
        }
        var program = BuildProgram(onlyNode, onlyBlock, fromNode);
        if (program.Steps.Count == 0)
        {
            Status = partial ? "The selection has no blocks to run." : "Nothing to run — add a block after Start first.";
            return;
        }

        _runCts = new CancellationTokenSource();
        IsRunning = true;
        Status = $"Running {label}…";
        try
        {
            var result = await _host.Process.RunAsync(program, ct: _runCts.Token).ConfigureAwait(true);
            Status = _runCts.IsCancellationRequested
                ? $"Stopped: {label}"
                : result.IsSuccess ? $"Completed: {label}" : $"Failed — {result.Message}";
        }
        catch (Exception ex)
        {
            Status = $"Error: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    private void ResetRunStatus()
    {
        foreach (var node in Nodes) node.RunStatus = "—";
        foreach (var block in AllBlocks()) block.RunStatus = "—";
    }

    // --- engine events (background threads → marshal to UI) ---

    private void OnStep(WorkflowStepEvent e)
    {
        if (_dispatcher.CheckAccess()) Apply(e);
        else _dispatcher.BeginInvoke(() => Apply(e));

        void Apply(WorkflowStepEvent ev)
        {
            var status = !ev.Completed ? "Running" : ev.Success ? "Passed" : "Failed";

            if (ev.Lane == null)
            {
                // Top-level step: either an inline block, or a flow.parallel marker.
                _stepNode.TryGetValue(ev.Index, out var source);
                MarkFolded(source, status);
                if (_inlineBlock.TryGetValue(ev.Index, out var block))
                {
                    block.RunStatus = status;
                    return;
                }
                if (ev.Index >= 0 && ev.Index < _topLevelNode.Count)
                    _runningNode = _topLevelNode[ev.Index];
                MarkParallel(_runningNode, status);   // a parallel envelope's overall marker
            }
            else
            {
                // A branch step inside the current parallel envelope.
                var branch = _runningNode?.Branches
                    .FirstOrDefault(b => b.LaneId.Equals(ev.Lane, StringComparison.OrdinalIgnoreCase));
                if (branch == null) return;
                if (ev.Index >= 0 && ev.Index < branch.Blocks.Count)
                    branch.Blocks[ev.Index].RunStatus = status;
            }
        }
    }

    /// <summary>
    /// A step folded away behind an opener reports on that opener: it shows Running while
    /// any hidden step runs, Failed as soon as one fails, and Passed once the closer is done.
    /// </summary>
    private void MarkFolded(CanvasNodeViewModel? source, string status)
    {
        if (source == null || !_hiddenOwner.TryGetValue(source, out var opener)) return;
        if (status == "Passed" && !ReferenceEquals(EndOf(opener), source)) return;
        opener.RunStatus = status;
    }

    /// <summary>
    /// Marks a parallel envelope and its blocks. On start (<paramref name="status"/> ==
    /// "Running") every block lights up; on the envelope's terminal marker only blocks
    /// still "Running" take the verdict, so per-branch Passed/Failed set by the
    /// branch events is preserved.
    /// </summary>
    private static void MarkParallel(CanvasNodeViewModel? node, string status)
    {
        if (node == null) return;
        node.RunStatus = status;
        var terminal = status != "Running";
        foreach (var block in node.AllBlocks())
            if (!terminal || block.RunStatus == "Running")
                block.RunStatus = status;
    }

    private void OnLog(LogEntry entry)
    {
        if (_dispatcher.CheckAccess()) Append(entry);
        else _dispatcher.BeginInvoke(() => Append(entry));

        void Append(LogEntry e)
        {
            Log.Add($"{e.Timestamp:HH:mm:ss}  {e.Message}");
            while (Log.Count > MaxLogLines)
                Log.RemoveAt(0);
        }
    }
}
