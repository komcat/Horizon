using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using FAAM.UI.Mvvm;
using Horizon.Engine;
using Horizon.Workflow;

namespace FAAM.UI.ViewModels.Timeline;

/// <summary>
/// The Engineer page — a horizontal timeline (Gantt) sequence editor. Device lanes
/// run down the left; ordered <b>stages</b> run across the top. Blocks dropped into
/// the same stage on different lanes run <b>concurrently</b>; the stage is a barrier
/// (the next stage starts only when all of this one's blocks finish). Compiles to a
/// Layer 2 <see cref="WorkflowProgram"/> via the <c>flow.parallel</c> op and runs it
/// through <see cref="IProcessService"/>. See <c>docs/timeline-sequence-editor-design.md</c>.
/// </summary>
public sealed class TimelineEditorViewModel : ObservableObject
{
    private const int MaxLogLines = 500;

    private readonly EngineHost _host;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, LaneViewModel> _laneById;

    private string _programName = "New Program";
    private string? _selectedProgram;
    private ProcessBlockViewModel? _selectedBlock;
    private OpHelpViewModel? _selectedHelp;
    private CellViewModel? _selectedCell;

    // Stage selection: a set (for copy / range), plus the anchor a shift-click extends
    // from. Each member's StageViewModel.IsSelected drives its header highlight.
    private readonly List<StageViewModel> _selectedStages = new();
    private StageViewModel? _anchorStage;

    // Stage clipboard — per stage, the blocks on each lane captured as id-less steps
    // (a fresh block id is minted on paste so copies stay independently trackable).
    private List<List<LaneClip>>? _stageClipboard;

    private bool _isRunning;
    private bool _showAllLanes;
    private double _laneWidth = DefaultLaneWidth;
    private string _status = "—";

    // Lane-column zoom: width in px, clamped to [MinLaneWidth, MaxLaneWidth], stepped by ZoomStep.
    private const double DefaultLaneWidth = 110;
    private const double MinLaneWidth = 60;
    private const double MaxLaneWidth = 220;
    private const double ZoomStep = 15;
    private bool _isDirty;
    private bool _suppressDirty;   // true while loading / resetting, so it doesn't count as an edit
    private bool _subscribed;
    private CancellationTokenSource? _runCts;

    // Run-highlight routing, rebuilt on each run by BuildProgram().
    private readonly List<int> _topLevelStage = new();                 // top-level step index → stage index
    private readonly Dictionary<int, ProcessBlockViewModel> _inlineBlock = new(); // top-level step index → block (inline stages)
    private int _runningStage = -1;

    public TimelineEditorViewModel(EngineHost host)
    {
        _host = host;
        _dispatcher = Dispatcher.CurrentDispatcher;

        Lanes = new ObservableCollection<LaneViewModel>(LaneBuilder.Build(host));
        _laneById = Lanes.ToDictionary(l => l.Id, StringComparer.OrdinalIgnoreCase);

        BuildPalette();

        NewProgramCommand = new RelayCommand(NewProgram);
        SaveProgramCommand = new RelayCommand(SaveProgram);
        AddStageCommand = new RelayCommand(() => { AddStage(); MarkDirty(); RecomputeConditionRegions(); });
        RemoveBlockCommand = new RelayCommand(RemoveSelectedBlock, () => _selectedBlock != null);
        CopyStagesCommand = new RelayCommand(CopySelectedStages, () => _selectedStages.Count > 0);
        PasteStagesCommand = new RelayCommand(PasteStages, () => _stageClipboard is { Count: > 0 });
        RunCommand = new AsyncRelayCommand(() => RunAsync(null), () => !_isRunning);
        RunStageCommand = new AsyncRelayCommand(RunSelectedStageAsync, () => !_isRunning && _selectedStages.Count == 1);
        StopCommand = new RelayCommand(() => _runCts?.Cancel(), () => _isRunning);
        ZoomInCommand = new RelayCommand(() => LaneWidth += ZoomStep, () => _laneWidth < MaxLaneWidth);
        ZoomOutCommand = new RelayCommand(() => LaneWidth -= ZoomStep, () => _laneWidth > MinLaneWidth);

        AddStage();   // start with one empty stage to author into
        UpdateLaneVisibility();
    }

    // --- bound state ---

    public ObservableCollection<PaletteCategoryViewModel> Palette { get; } = new();
    public ObservableCollection<LaneViewModel> Lanes { get; }
    public ObservableCollection<StageViewModel> Stages { get; } = new();
    public ObservableCollection<string> Programs { get; } = new();
    public ObservableCollection<string> Log { get; } = new();

    /// <summary>
    /// When false (default), only lanes that carry a block are shown — a loaded program
    /// shows just the devices it uses. Flip on to reveal every device lane so an op can
    /// be added to a device the program doesn't touch yet.
    /// </summary>
    public bool ShowAllLanes
    {
        get => _showAllLanes;
        set { if (SetField(ref _showAllLanes, value)) UpdateLaneVisibility(); }
    }

    /// <summary>Lane column width in px — driven by the zoom buttons; bound by header + cells.</summary>
    public double LaneWidth
    {
        get => _laneWidth;
        private set
        {
            var clamped = Math.Clamp(value, MinLaneWidth, MaxLaneWidth);
            if (!SetField(ref _laneWidth, clamped)) return;
            OnPropertyChanged(nameof(LaneFontSize));
            OnPropertyChanged(nameof(BlockNameFontSize));
            OnPropertyChanged(nameof(BlockValueFontSize));
            ZoomInCommand.RaiseCanExecuteChanged();
            ZoomOutCommand.RaiseCanExecuteChanged();
        }
    }

    // Gentle font scale tracking the zoom — clamped tight so wide columns don't clip the
    // fixed-height rows, but text still grows/shrinks "a bit" with the column width.
    private double FontScale => Math.Clamp(_laneWidth / DefaultLaneWidth, 0.8, 1.2);

    /// <summary>Lane-header device name font size, scaled with the column zoom.</summary>
    public double LaneFontSize => 13 * FontScale;

    /// <summary>In-cell block op-name font size, scaled with the column zoom.</summary>
    public double BlockNameFontSize => 9 * FontScale;

    /// <summary>In-cell block param-summary font size, scaled with the column zoom.</summary>
    public double BlockValueFontSize => 11 * FontScale;

    /// <summary>
    /// Shows a lane when "Show all lanes" is on, or it carries at least one block.
    /// A program with no blocks at all (e.g. a fresh New Program) shows every lane,
    /// so there's always somewhere to drop the first op.
    /// </summary>
    private void UpdateLaneVisibility()
    {
        var anyBlocks = Lanes.Any(l => l.Cells.Any(c => c.Blocks.Count > 0));
        foreach (var lane in Lanes)
            lane.IsVisible = _showAllLanes || !anyBlocks || lane.Cells.Any(c => c.Blocks.Count > 0);
    }

    public string ProgramName { get => _programName; set { if (SetField(ref _programName, value)) MarkDirty(); } }

    /// <summary>True when the editor has unsaved changes vs the last save / load.</summary>
    public bool IsDirty { get => _isDirty; private set => SetField(ref _isDirty, value); }

    private void MarkDirty() { if (!_suppressDirty) IsDirty = true; }

    private static readonly HashSet<string> TransientProps = new()
    {
        nameof(ProcessBlockViewModel.RunStatus), nameof(ProcessBlockViewModel.IsSelected),
        nameof(ProcessBlockViewModel.PositionPreview), nameof(ProcessBlockViewModel.HasPositionPreview),
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
        set { if (SetField(ref _selectedProgram, value) && value != null) LoadProgram(value); }
    }

    /// <summary>The block whose parameters the right panel edits.</summary>
    public ProcessBlockViewModel? SelectedBlock
    {
        get => _selectedBlock;
        set
        {
            var old = _selectedBlock;
            if (!SetField(ref _selectedBlock, value)) return;
            if (old != null) old.IsSelected = false;
            if (value != null) { value.IsSelected = true; SelectedCell = FindCell(value); SelectedHelp = null; }
            OnPropertyChanged(nameof(HasSelectedBlock));
            OnPropertyChanged(nameof(HasNoSelectedBlock));
            OnPropertyChanged(nameof(ShowPaletteHelp));
            OnPropertyChanged(nameof(ShowEmptyHint));
            RemoveBlockCommand.RaiseCanExecuteChanged();
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

    /// <summary>Shows the given command's help in the right panel (clears any block selection).</summary>
    public void ShowHelp(OpSpec spec)
    {
        SelectedBlock = null;
        SelectedHelp = new OpHelpViewModel(spec);
    }

    /// <summary>Selects a cell (for palette-click placement) and clears the block selection.</summary>
    public void SelectCell(CellViewModel cell)
    {
        SelectedBlock = null;
        SelectedCell = cell;
    }

    /// <summary>Removes the given stage column.</summary>
    public void RemoveStage(StageViewModel stage)
    {
        var idx = Stages.IndexOf(stage);
        if (idx >= 0) RemoveStageAt(idx);
    }

    private CellViewModel? FindCell(ProcessBlockViewModel block)
    {
        foreach (var lane in Lanes)
            foreach (var cell in lane.Cells)
                if (cell.Blocks.Contains(block)) return cell;
        return null;
    }

    public bool HasSelectedBlock => _selectedBlock != null;
    public bool HasNoSelectedBlock => _selectedBlock == null;

    /// <summary>The cell a palette click adds to (set when a block or empty cell is clicked).</summary>
    public CellViewModel? SelectedCell { get => _selectedCell; set => SetField(ref _selectedCell, value); }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!SetField(ref _isRunning, value)) return;
            RunCommand.RaiseCanExecuteChanged();
            RunStageCommand.RaiseCanExecuteChanged();
            StopCommand.RaiseCanExecuteChanged();
        }
    }

    public string Status { get => _status; private set => SetField(ref _status, value); }

    public RelayCommand NewProgramCommand { get; }
    public RelayCommand SaveProgramCommand { get; }
    public RelayCommand AddStageCommand { get; }
    public RelayCommand RemoveBlockCommand { get; }
    public RelayCommand CopyStagesCommand { get; }
    public RelayCommand PasteStagesCommand { get; }
    public AsyncRelayCommand RunCommand { get; }
    public AsyncRelayCommand RunStageCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand ZoomInCommand { get; }
    public RelayCommand ZoomOutCommand { get; }

    /// <summary>True when at least one stage is selected (header highlight).</summary>
    public bool HasSelectedStage => _selectedStages.Count > 0;

    private void SetStageSelected(StageViewModel stage, bool on)
    {
        stage.IsSelected = on;
        if (on) { if (!_selectedStages.Contains(stage)) _selectedStages.Add(stage); }
        else _selectedStages.Remove(stage);
    }

    private void ClearStageSelection()
    {
        foreach (var s in _selectedStages) s.IsSelected = false;
        _selectedStages.Clear();
    }

    private void StageSelectionChanged()
    {
        RunStageCommand.RaiseCanExecuteChanged();
        CopyStagesCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(HasSelectedStage));
    }

    /// <summary>
    /// Plain stage-header click. Selects only this stage (and sets it as the range
    /// anchor); clicking the sole-selected stage again clears the selection. "Run
    /// Stage" runs whenever exactly one stage is selected.
    /// </summary>
    public void SelectStage(StageViewModel stage)
    {
        var sole = _selectedStages.Count == 1 && ReferenceEquals(_selectedStages[0], stage);
        ClearStageSelection();
        if (sole) _anchorStage = null;                                  // toggled off
        else { SetStageSelected(stage, true); _anchorStage = stage; }
        StageSelectionChanged();
    }

    /// <summary>
    /// Shift-click on a stage header — selects the contiguous run of stages from the
    /// anchor (the last plain-clicked stage) to <paramref name="stage"/>. With no
    /// anchor it behaves like a plain click, seeding one.
    /// </summary>
    public void SelectStageRange(StageViewModel stage)
    {
        if (_anchorStage == null || !Stages.Contains(_anchorStage)) { SelectStage(stage); return; }
        var a = Stages.IndexOf(_anchorStage);
        var b = Stages.IndexOf(stage);
        if (a < 0 || b < 0) return;
        ClearStageSelection();
        for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++)
            SetStageSelected(Stages[i], true);                          // anchor kept for further shift-clicks
        StageSelectionChanged();
    }

    // --- page lifecycle ---

    public void Activate()
    {
        RefreshPrograms();
        if (_subscribed) return;
        _host.Process.StepEvent += OnStep;
        _host.Process.LogEmitted += OnLog;
        _subscribed = true;
    }

    public void Deactivate()
    {
        if (!_subscribed) return;
        _host.Process.StepEvent -= OnStep;
        _host.Process.LogEmitted -= OnLog;
        _subscribed = false;
    }

    // --- palette ---

    private void BuildPalette()
    {
        foreach (var category in OpCatalog.Categories)
        {
            var cat = new PaletteCategoryViewModel(category);
            foreach (var spec in OpCatalog.All.Where(s => s.Category == category))
                cat.Items.Add(new PaletteItemViewModel(spec, AddToSelectedCell));
            Palette.Add(cat);
        }
    }

    /// <summary>Palette click — add the op to the selected cell (drag-drop targets a specific cell instead).</summary>
    private void AddToSelectedCell(OpSpec spec)
    {
        if (_selectedCell == null)
        {
            Status = "Pick a lane cell first (click it), then choose a command — or drag the command onto a cell.";
            return;
        }
        DropOp(spec, _selectedCell.Lane, Stages.IndexOf(StageOf(_selectedCell)));
    }

    // --- stages / cells ---

    /// <summary>Appends a stage column (and a cell on every lane). Returns its index.
    /// <paramref name="uid"/> seeds the stage's stable id when reloading a saved stage.</summary>
    public int AddStage(string? uid = null)
    {
        var idx = Stages.Count;
        var stage = new StageViewModel { Title = $"Stage {idx + 1}" };
        if (!string.IsNullOrEmpty(uid)) stage.Uid = uid;
        Stages.Add(stage);
        foreach (var lane in Lanes)
        {
            var cell = new CellViewModel(lane);
            lane.Cells.Add(cell);
            stage.Cells.Add(cell);   // same object, exposed stage-major for the vertical view
        }
        return idx;
    }

    /// <summary>Removes a stage column and its cells, keeping at least one stage.</summary>
    public void RemoveStageAt(int index)
    {
        if (index < 0 || index >= Stages.Count || Stages.Count <= 1) return;
        Stages.RemoveAt(index);
        foreach (var lane in Lanes)
            if (index < lane.Cells.Count) lane.Cells.RemoveAt(index);
        RenumberStages();
        MarkDirty();
        RecomputeConditionRegions();
        UpdateLaneVisibility();
        if (_selectedBlock != null && !BlockIsPlaced(_selectedBlock))
            SelectedBlock = null;
        if (_selectedStages.RemoveAll(s => !Stages.Contains(s)) > 0)
            StageSelectionChanged();
        if (_anchorStage != null && !Stages.Contains(_anchorStage))
            _anchorStage = null;
    }

    /// <summary>Inserts a new empty stage column at <paramref name="index"/> (and an empty
    /// cell at that position on every lane). <paramref name="uid"/> seeds the stage's stable id.</summary>
    public void InsertStage(int index, string? uid = null)
    {
        index = Math.Clamp(index, 0, Stages.Count);
        var stage = new StageViewModel();
        if (!string.IsNullOrEmpty(uid)) stage.Uid = uid;
        Stages.Insert(index, stage);
        foreach (var lane in Lanes)
        {
            var cell = new CellViewModel(lane);
            lane.Cells.Insert(index, cell);
            stage.Cells.Add(cell);   // new stage's own cells (same objects as the lanes')
        }
        RenumberStages();
        RecomputeConditionRegions();
    }

    /// <summary>Inserts an empty stage immediately to the left or right of <paramref name="stage"/>.</summary>
    public void InsertStageRelative(StageViewModel stage, bool toRight)
    {
        var idx = Stages.IndexOf(stage);
        if (idx < 0) return;
        InsertStage(toRight ? idx + 1 : idx);
        MarkDirty();
    }

    // --- stage copy / paste ---

    /// <summary>One lane's blocks within a copied stage, captured as id-less steps.</summary>
    private sealed record LaneClip(string LaneId, List<WorkflowStep> Steps);

    /// <summary>Ensures <paramref name="stage"/> is part of the selection, then copies — used
    /// by the context menu so a right-click outside the current selection copies that stage.</summary>
    public void CopyStagesFor(StageViewModel stage)
    {
        if (!_selectedStages.Contains(stage)) SelectStage(stage);
        CopySelectedStages();
    }

    /// <summary>Copies the selected stages (their blocks, in stage order) to the stage clipboard.</summary>
    public void CopySelectedStages()
    {
        if (_selectedStages.Count == 0) return;

        var clip = new List<List<LaneClip>>();
        foreach (var stage in Stages.Where(_selectedStages.Contains))   // capture in left-to-right order
        {
            var idx = Stages.IndexOf(stage);
            var lanes = new List<LaneClip>();
            foreach (var lane in Lanes)
            {
                if (idx >= lane.Cells.Count) continue;
                var blocks = lane.Cells[idx].Blocks;
                if (blocks.Count == 0) continue;
                // Drop the id so paste mints a fresh one per block (independently trackable).
                lanes.Add(new LaneClip(lane.Id, blocks.Select(b => b.ToStep() with { Id = string.Empty }).ToList()));
            }
            clip.Add(lanes);
        }

        _stageClipboard = clip;
        PasteStagesCommand.RaiseCanExecuteChanged();
        Status = $"Copied {clip.Count} stage(s).";
    }

    /// <summary>
    /// Pastes the clipboard stages after the last selected stage (or at the end when
    /// nothing is selected), then selects the newly pasted stages. Each block is rebuilt
    /// with a fresh id; a block whose lane no longer exists is skipped.
    /// </summary>
    public void PasteStages()
    {
        if (_stageClipboard is not { Count: > 0 } clipboard) return;

        var insertAt = _selectedStages.Count > 0
            ? _selectedStages.Max(Stages.IndexOf) + 1
            : Stages.Count;

        ClearStageSelection();
        var pasted = new List<StageViewModel>();
        foreach (var stageClip in clipboard)
        {
            InsertStage(insertAt);
            var stage = Stages[insertAt];
            foreach (var laneClip in stageClip)
            {
                if (!_laneById.TryGetValue(laneClip.LaneId, out var lane)) continue;   // lane gone — skip
                foreach (var step in laneClip.Steps)
                {
                    var spec = OpCatalog.Find(step.Op) ?? OpCatalog.ForUnknown(step.Op);
                    var block = ProcessBlockViewModel.FromStep(_host, spec, step);   // empty id → fresh id
                    Track(block);
                    lane.Cells[insertAt].Blocks.Add(block);
                }
            }
            pasted.Add(stage);
            insertAt++;
        }

        foreach (var s in pasted) SetStageSelected(s, true);
        _anchorStage = pasted.FirstOrDefault();
        StageSelectionChanged();
        MarkDirty();
        RecomputeConditionRegions();
        UpdateLaneVisibility();
        Status = $"Pasted {pasted.Count} stage(s).";
    }

    private void RenumberStages()
    {
        for (int i = 0; i < Stages.Count; i++)
            Stages[i].Title = $"Stage {i + 1}";
    }

    private void ClearAllStages()
    {
        ClearStageSelection();
        _anchorStage = null;
        StageSelectionChanged();
        Stages.Clear();
        foreach (var lane in Lanes)
            lane.Cells.Clear();
    }

    /// <summary>Drops a palette op onto a lane's cell in the given stage.</summary>
    public void DropOp(OpSpec spec, LaneViewModel lane, int stageIndex)
    {
        if (!lane.Accepts(spec))
        {
            Status = $"'{spec.Display}' can't run on the {lane.DomainLabel} lane.";
            return;
        }
        if (stageIndex < 0 || stageIndex >= Stages.Count) return;

        var block = new ProcessBlockViewModel(_host, spec, ProcessBlockViewModel.NewId());
        if (spec.DeviceKind != DeviceKind.None && lane.Cells.Count > stageIndex)
            block.SelectedDevice = lane.Id;   // pin to this lane's device (device ops only)

        lane.Cells[stageIndex].Blocks.Add(block);
        Track(block);
        MarkDirty();
        RecomputeConditionRegions();
        UpdateLaneVisibility();
        SelectedCell = lane.Cells[stageIndex];
        SelectedBlock = block;
        Status = $"Added '{spec.Display}' to {lane.Display}, stage {stageIndex + 1}.";
    }

    /// <summary>
    /// Moves an already-placed block to another lane's cell in the given stage (drag
    /// within the grid). Rejected if the target lane can't run the block's op. A move
    /// onto a different device lane re-pins the block to that lane's device, mirroring
    /// <see cref="DropOp"/>. A drop back onto the block's own cell is a no-op.
    /// </summary>
    public void MoveBlock(ProcessBlockViewModel block, LaneViewModel lane, int stageIndex)
    {
        if (!lane.Accepts(block.Spec))
        {
            Status = $"'{block.Display}' can't run on the {lane.DomainLabel} lane.";
            return;
        }
        if (stageIndex < 0 || stageIndex >= Stages.Count) return;

        var target = lane.Cells[stageIndex];
        var source = FindCell(block);
        if (source == null || ReferenceEquals(source, target)) return;   // not placed, or same cell

        source.Blocks.Remove(block);
        target.Blocks.Add(block);
        if (block.Spec.DeviceKind != DeviceKind.None)
            block.SelectedDevice = lane.Id;   // pin to the destination lane's device (device ops only)

        MarkDirty();
        RecomputeConditionRegions();
        UpdateLaneVisibility();
        SelectedCell = target;
        SelectedBlock = block;
        Status = $"Moved '{block.Display}' to {lane.Display}, stage {stageIndex + 1}.";
    }

    private void RemoveSelectedBlock()
    {
        if (_selectedBlock == null) return;
        foreach (var lane in Lanes)
            foreach (var cell in lane.Cells)
                if (cell.Blocks.Remove(_selectedBlock)) { SelectedBlock = null; MarkDirty(); RecomputeConditionRegions(); UpdateLaneVisibility(); return; }
    }

    private bool BlockIsPlaced(ProcessBlockViewModel block)
        => Lanes.Any(l => l.Cells.Any(c => c.Blocks.Contains(block)));

    private StageViewModel StageOf(CellViewModel cell)
    {
        var idx = cell.Lane.Cells.IndexOf(cell);
        return idx >= 0 && idx < Stages.Count ? Stages[idx] : Stages[^1];
    }

    // --- programs ---

    private void RefreshPrograms()
    {
        var names = _host.Process.ListPrograms();
        if (Programs.SequenceEqual(names)) return;
        Programs.Clear();
        foreach (var name in names)
            Programs.Add(name);
    }

    private void NewProgram()
    {
        _suppressDirty = true;
        _selectedProgram = null;
        OnPropertyChanged(nameof(SelectedProgram));
        ProgramName = "New Program";
        ClearAllStages();
        AddStage();
        SelectedBlock = null;
        SelectedCell = null;
        UpdateLaneVisibility();
        _suppressDirty = false;
        IsDirty = false;
        Status = "New program.";
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
        Status = $"Loaded '{name}' — {Stages.Count} stage(s).";
    }

    /// <summary>Rebuilds the timeline from a program: one stage per top-level step (a
    /// <c>flow.parallel</c> step fans into per-lane branches).</summary>
    private void LoadFrom(WorkflowProgram program)
    {
        ClearAllStages();
        SelectedBlock = null;
        SelectedCell = null;

        foreach (var step in program.Steps)
        {
            var isParallel = step.Op.Equals("flow.parallel", StringComparison.OrdinalIgnoreCase) && step.Branches.Count > 0;
            var s = AddStage(isParallel ? step.Id : null);   // keep the parallel group's stable id
            if (isParallel)
            {
                foreach (var branch in step.Branches)
                {
                    var lane = FindLane(branch.Lane);
                    foreach (var bstep in branch.Steps)
                        lane.Cells[s].Blocks.Add(MakeBlock(bstep));
                }
            }
            else
            {
                var lane = FindLaneForStep(step);
                lane.Cells[s].Blocks.Add(MakeBlock(step));
            }
        }
        if (Stages.Count == 0) AddStage();
        RecomputeConditionRegions();
        UpdateLaneVisibility();
    }

    private ProcessBlockViewModel MakeBlock(WorkflowStep step)
    {
        var spec = OpCatalog.Find(step.Op) ?? OpCatalog.ForUnknown(step.Op);
        var block = ProcessBlockViewModel.FromStep(_host, spec, step);
        Track(block);   // edits after load mark the program dirty (load itself is suppressed)
        return block;
    }

    /// <summary>
    /// Re-tints every stage by walking the Control lane's if/else/endif markers and
    /// tracking branch nesting: a content stage between If and Else is "Then" (pink),
    /// between Else and End If is "Else" (yellow); marker stages and stages outside any
    /// conditional are untinted. Called after any structural edit. Cheap, so it just
    /// rewrites every cell's region. Nesting uses a stack; only the innermost branch tints.
    /// </summary>
    private void RecomputeConditionRegions()
    {
        if (!_laneById.TryGetValue(LaneBuilder.ControlLaneId, out var control)) return;

        static bool IsOpener(string op)
            => op is "flow.if" or "flow.ifsignal" or "flow.ifposition";

        var stack = new Stack<ConditionRegion>();
        for (int s = 0; s < Stages.Count; s++)
        {
            var ops = s < control.Cells.Count
                ? control.Cells[s].Blocks.Select(b => b.OpKey.ToLowerInvariant()).ToList()
                : new List<string>();

            bool isBoundary = ops.Any(o => IsOpener(o) || o == "flow.else" || o == "flow.endif");
            var region = isBoundary || stack.Count == 0 ? ConditionRegion.None : stack.Peek();

            foreach (var lane in Lanes)
                if (s < lane.Cells.Count)
                    lane.Cells[s].Region = region;

            // Apply this stage's marker transitions in order, for the stages that follow.
            foreach (var o in ops)
            {
                if (IsOpener(o)) stack.Push(ConditionRegion.Then);
                else if (o == "flow.else" && stack.Count > 0) { stack.Pop(); stack.Push(ConditionRegion.Else); }
                else if (o == "flow.endif" && stack.Count > 0) stack.Pop();
            }
        }
    }

    private LaneViewModel FindLane(string id)
        => _laneById.TryGetValue(id, out var lane) ? lane : _laneById[LaneBuilder.ControlLaneId];

    private LaneViewModel FindLaneForStep(WorkflowStep step)
    {
        if (!string.IsNullOrEmpty(step.Device) && _laneById.TryGetValue(step.Device, out var lane))
            return lane;
        // Device-less: the read-out op routes to the Signal lane (by category), every
        // other device-less op to the Control lane.
        var spec = OpCatalog.Find(step.Op);
        return spec != null && LaneViewModel.IsSignalOp(spec)
            ? _laneById[LaneBuilder.SignalLaneId]
            : _laneById[LaneBuilder.ControlLaneId];
    }

    /// <summary>
    /// Pre-flight validation — returns the first block that would no-op or error
    /// (and a reason), or null. Currently flags a Move Relative with a zero / empty
    /// distance; an expression value (<c>{..}</c>) is allowed since it resolves at
    /// run time. The caller selects the offending block so the operator can fix it.
    /// </summary>
    /// <summary>
    /// Clamps every motion block's velocity field to its device's controller range
    /// (writing the capped value back so the operator sees it). 0 / blank / expression
    /// values pass through. Called before save and run.
    /// </summary>
    private void ClampVelocities()
    {
        foreach (var lane in Lanes)
            foreach (var cell in lane.Cells)
                foreach (var block in cell.Blocks)
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
        foreach (var lane in Lanes)
            foreach (var cell in lane.Cells)
                foreach (var block in cell.Blocks)
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

    private ProcessBlockViewModel? FirstInvalidBlock(out string reason)
    {
        foreach (var lane in Lanes)
            foreach (var cell in lane.Cells)
                foreach (var b in cell.Blocks)
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

    private void SaveProgram()
    {
        if (string.IsNullOrWhiteSpace(ProgramName)) { Status = "Program name is required."; return; }

        ClampVelocities();
        var bumped = ClampWaitPopups();
        var invalid = FirstInvalidBlock(out var why);
        if (invalid != null) { SelectedBlock = invalid; Status = $"Can't save — {why}"; return; }

        var program = BuildProgram();
        var result = _host.Process.SaveProgram(program);
        Status = result.IsSuccess
            ? $"Saved '{program.Name}'." + WaitPopupBumpNote(bumped)
            : result.Message;
        if (!result.IsSuccess) return;

        RefreshPrograms();
        _selectedProgram = program.Name;
        OnPropertyChanged(nameof(SelectedProgram));
        IsDirty = false;
    }

    /// <summary>
    /// Compiles the timeline to a flat <see cref="WorkflowProgram"/>: each stage with
    /// one active lane emits its blocks inline (sequential); a stage with two+ active
    /// lanes emits one <c>flow.parallel</c> step, one branch per lane. Also rebuilds
    /// the run-highlight routing tables.
    /// </summary>
    private WorkflowProgram BuildProgram(int? onlyStage = null)
    {
        _topLevelStage.Clear();
        _inlineBlock.Clear();

        var steps = new List<WorkflowStep>();
        for (int s = 0; s < Stages.Count; s++)
        {
            if (onlyStage.HasValue && s != onlyStage.Value) continue;   // run-only-this-stage filter

            var active = Lanes.Where(l => s < l.Cells.Count && l.Cells[s].Blocks.Count > 0).ToList();
            if (active.Count == 0) continue;

            if (active.Count == 1)
            {
                foreach (var block in active[0].Cells[s].Blocks)
                {
                    _inlineBlock[steps.Count] = block;
                    _topLevelStage.Add(s);
                    steps.Add(block.ToStep());
                }
            }
            else
            {
                var branches = active.Select(l => new WorkflowBranch
                {
                    Lane = l.Id,
                    Steps = l.Cells[s].Blocks.Select(b => b.ToStep()).ToList(),
                }).ToList();
                _topLevelStage.Add(s);
                steps.Add(new WorkflowStep
                {
                    Id = Stages[s].Uid,   // stable parallel-group id (trackable across saves)
                    Name = $"Stage {s + 1} (parallel)",
                    Op = "flow.parallel",
                    Branches = branches,
                });
            }
        }
        return new WorkflowProgram { Name = ProgramName.Trim(), Steps = steps };
    }

    // --- run ---

    /// <summary>Runs only the highlighted stage, or no-ops unless exactly one is selected.</summary>
    private async Task RunSelectedStageAsync()
    {
        if (_selectedStages.Count != 1) return;
        await RunAsync(Stages.IndexOf(_selectedStages[0]));
    }

    /// <summary>Runs the program, or only stage <paramref name="onlyStage"/> when set.</summary>
    private async Task RunAsync(int? onlyStage)
    {
        if (IsRunning) return;

        ClampVelocities();
        var bumped = ClampWaitPopups();
        var invalid = FirstInvalidBlock(out var why);
        if (invalid != null) { SelectedBlock = invalid; Status = $"Can't run — {why}"; return; }

        ResetRunStatus();
        Log.Clear();
        _runningStage = -1;

        // Note any sub-second Wait Popups we bumped (after the clear, so it survives).
        if (bumped > 0)
            Log.Add($"{DateTimeOffset.Now:HH:mm:ss}  {bumped} Wait Popup(s) under {MinWaitPopupMs} ms bumped to {MinWaitPopupMs} ms so the countdown is visible.");

        var label = onlyStage.HasValue ? $"stage {onlyStage.Value + 1}" : $"'{ProgramName.Trim()}'";
        var program = BuildProgram(onlyStage);
        if (program.Steps.Count == 0)
        {
            Status = onlyStage.HasValue ? "Selected stage has no blocks to run." : "Nothing to run — add a block first.";
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
        foreach (var lane in Lanes)
            foreach (var cell in lane.Cells)
                foreach (var block in cell.Blocks)
                    block.RunStatus = "—";
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
                if (ev.Index >= 0 && ev.Index < _topLevelStage.Count)
                    _runningStage = _topLevelStage[ev.Index];

                if (_inlineBlock.TryGetValue(ev.Index, out var block))
                    block.RunStatus = status;
                else
                    MarkStage(_runningStage, status);   // a parallel stage's overall marker
            }
            else
            {
                // A branch step inside the current parallel stage.
                if (_runningStage < 0) return;
                if (!_laneById.TryGetValue(ev.Lane, out var lane)) return;
                if (_runningStage >= lane.Cells.Count) return;
                var blocks = lane.Cells[_runningStage].Blocks;
                if (ev.Index >= 0 && ev.Index < blocks.Count)
                    blocks[ev.Index].RunStatus = status;
            }
        }
    }

    /// <summary>
    /// Marks a parallel stage's blocks. On start (<paramref name="status"/> ==
    /// "Running") every block lights up; on the stage's terminal marker only blocks
    /// still "Running" take the verdict, so per-branch Passed/Failed set by the
    /// branch events is preserved.
    /// </summary>
    private void MarkStage(int stage, string status)
    {
        if (stage < 0) return;
        var terminal = status != "Running";
        foreach (var lane in Lanes)
            if (stage < lane.Cells.Count)
                foreach (var block in lane.Cells[stage].Blocks)
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
