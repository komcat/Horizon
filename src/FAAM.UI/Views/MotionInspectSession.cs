using System.Collections.ObjectModel;
using System.Globalization;
using FAAM.UI.Mvvm;
using Horizon.Common;
using Horizon.Engine;
using Horizon.Workflow;

namespace FAAM.UI.Views;

/// <summary>
/// What the motion inspector needs from its host (the Run page's process panel):
/// the engine, the panel log, the position editor, and a guarded way to run one
/// move so the inspector's motion is cancelled by STOP / a safety trip and blocks
/// the alignment panels exactly like a process step does.
/// </summary>
public interface IMotionInspectHost
{
    EngineHost Host { get; }

    void InspectorLog(LogLevel level, string message);

    /// <summary>Opens the non-modal per-position editor for a taught point.</summary>
    void OpenPositionEditor(string device, string position);

    /// <summary>
    /// Runs one inspector move under the panel's cancellation scope and the
    /// process-busy gate. Returns a failure (never throws) when the move is
    /// blocked, cancelled, or the hardware reports an error.
    /// </summary>
    Task<Result> RunInspectorMoveAsync(string what, Func<CancellationToken, Task<Result>> body);
}

/// <summary>
/// One row of the motion inspector: a single move the program performs, in
/// program order, with the taught values it will recall. Values are mutable so an
/// in-place re-teach refreshes the grid without rebuilding it.
/// </summary>
public sealed class MotionInspectRow : ObservableObject
{
    /// <summary>1-based position of this move in the program's move sequence.</summary>
    public required int Seq { get; init; }
    public required string Position { get; init; }
    public required string Device { get; init; }

    /// <summary>The program step this move came from (nickname or op key) — shown as a tooltip.</summary>
    public required string StepName { get; init; }

    /// <summary>
    /// Per-step velocity the program overrides the taught point's with (mm/s);
    /// 0 = the move uses the point's stored velocity.
    /// </summary>
    public required double VelocityOverride { get; init; }

    /// <summary>Opens this row's position editor (wired by the session).</summary>
    public Action? Edit { get; init; }

    private string _velocity = "—";
    public string Velocity { get => _velocity; set => SetField(ref _velocity, value); }

    private string _x = "—";
    public string X { get => _x; set => SetField(ref _x, value); }

    private string _y = "—";
    public string Y { get => _y; set => SetField(ref _y, value); }

    private string _z = "—";
    public string Z { get => _z; set => SetField(ref _z, value); }

    private string _u = "—";
    public string U { get => _u; set => SetField(ref _u, value); }

    private string _v = "—";
    public string V { get => _v; set => SetField(ref _v, value); }

    private string _w = "—";
    public string W { get => _w; set => SetField(ref _w, value); }

    private string _camera = "—";
    public string Camera { get => _camera; set => SetField(ref _camera, value); }

    private string _exposure = "—";
    public string Exposure { get => _exposure; set => SetField(ref _exposure, value); }

    private bool _isTaught;
    /// <summary>False when the product has no taught point of this name — the row cannot be stepped to.</summary>
    public bool IsTaught { get => _isTaught; set => SetField(ref _isTaught, value); }

    private bool _isActive;
    /// <summary>True for the row the step cursor sits on — carries the Update button.</summary>
    public bool IsActive { get => _isActive; set => SetField(ref _isActive, value); }

    private bool _atPosition;
    /// <summary>True when the row's device is physically standing on this taught point right now.</summary>
    public bool AtPosition { get => _atPosition; set => SetField(ref _atPosition, value); }
}

/// <summary>
/// The motion inspector for one process button: the ordered list of named-position
/// moves that button's program actually performs (top-level, inside
/// <c>flow.parallel</c> branches, and inside sub-programs), plus a step cursor the
/// operator drives forward and backward one move at a time.
///
/// Stepping is the teaching loop: step to a row, look at the part, press Update to
/// overwrite that taught point with where the device is now. The cursor is also
/// synced from the hardware — every row whose device is standing on its taught
/// point is flagged, so the operator can see where the machine is in the sequence.
/// </summary>
public sealed class MotionInspectSession : ObservableObject
{
    /// <summary>Recursion cap on nested <c>flow.subProgram</c> expansion (Power of 10 rule 2).</summary>
    private const int MaxSubProgramDepth = 8;

    /// <summary>Upper bound on the <c>device1/device2/…</c> probe of a parallel move (mirrors StepDispatcher).</summary>
    private const int MaxParallelDevices = 64;

    private readonly IMotionInspectHost _owner;

    public MotionInspectSession(IMotionInspectHost owner, string title, string subtitle)
    {
        _owner = owner;
        Title = title;
        Subtitle = subtitle;

        ForwardCommand = new AsyncRelayCommand(() => StepAsync(+1), () => ActiveIndex + 1 < Rows.Count);
        BackCommand = new AsyncRelayCommand(() => StepAsync(-1), () => ActiveIndex > 0);
        UpdateCommand = new AsyncRelayCommand(UpdateActiveAsync, () => ActiveRow != null);
        SyncCommand = new AsyncRelayCommand(SyncFromHardwareAsync);
    }

    /// <summary>The program the rows came from.</summary>
    public string Title { get; }

    /// <summary>The step button that opened the inspector.</summary>
    public string Subtitle { get; }

    public ObservableCollection<MotionInspectRow> Rows { get; } = new();

    public AsyncRelayCommand ForwardCommand { get; }
    public AsyncRelayCommand BackCommand { get; }
    public AsyncRelayCommand UpdateCommand { get; }
    public AsyncRelayCommand SyncCommand { get; }

    private int _activeIndex = -1;
    /// <summary>Cursor into <see cref="Rows"/>; -1 = before the first move (nothing stepped yet).</summary>
    public int ActiveIndex
    {
        get => _activeIndex;
        private set
        {
            if (!SetField(ref _activeIndex, value)) return;
            for (int i = 0; i < Rows.Count; i++)
                Rows[i].IsActive = i == value;
            OnPropertyChanged(nameof(ActiveRow));
            OnPropertyChanged(nameof(CursorText));
            RaiseCanExecute();
        }
    }

    public MotionInspectRow? ActiveRow
        => _activeIndex >= 0 && _activeIndex < Rows.Count ? Rows[_activeIndex] : null;

    /// <summary>"3 / 11" style cursor read-out for the header.</summary>
    public string CursorText
        => Rows.Count == 0 ? "no moves"
         : _activeIndex < 0 ? $"— / {Rows.Count}"
         : $"{_activeIndex + 1} / {Rows.Count}";

    private string _status = "Step through the program's moves one at a time.";
    public string Status { get => _status; private set => SetField(ref _status, value); }

    // --- Building the sequence -------------------------------------------------

    /// <summary>
    /// Builds a session for a step button's program: expands the program into its
    /// ordered list of named-position moves and resolves each one's taught values.
    /// Returns null when the program cannot be loaded.
    /// </summary>
    public static MotionInspectSession? Build(IMotionInspectHost owner, string programName, string stepLabel)
    {
        var loaded = owner.Host.Process.LoadProgram(programName);
        if (!loaded.IsSuccess)
        {
            owner.InspectorLog(LogLevel.Warning,
                $"Inspect Motion: program '{programName}' could not be loaded ({loaded.Message}).");
            return null;
        }

        var moves = new List<PlannedMove>();
        Collect(owner.Host.Process, loaded.Value.Steps, moves, depth: 0);

        var session = new MotionInspectSession(owner, programName, stepLabel);
        for (int i = 0; i < moves.Count; i++)
        {
            var m = moves[i];
            var row = new MotionInspectRow
            {
                Seq = i + 1,
                Position = m.Position,
                Device = m.Device,
                StepName = m.StepName,
                VelocityOverride = m.VelocityOverride,
                Edit = () => owner.OpenPositionEditor(m.Device, m.Position),
            };
            session.Rows.Add(row);
        }

        session.RefreshRows();
        if (moves.Count == 0)
            session.Status = "This program performs no named-position moves.";
        return session;
    }

    /// <summary>One named-position move a program performs, in program order.</summary>
    private sealed record PlannedMove(string Device, string Position, double VelocityOverride, string StepName);

    /// <summary>
    /// Walks a step sequence in execution order and collects every named-position
    /// move. Recurses into <c>flow.parallel</c> branches (lane order — the moves run
    /// concurrently, so any single-file order is a presentation choice) and into
    /// <c>flow.subProgram</c> children. Disabled steps are skipped: they don't run,
    /// so they aren't part of the sequence being taught.
    /// </summary>
    private static void Collect(IProcessService process, IEnumerable<WorkflowStep> steps, List<PlannedMove> into, int depth)
    {
        foreach (var s in steps)
        {
            if (!s.Enabled) continue;

            switch (s.Op.ToLowerInvariant())
            {
                case "motion.movetoposition":
                {
                    var pos = s.Parameters.GetValueOrDefault("position", string.Empty);
                    if (!string.IsNullOrWhiteSpace(pos))
                        into.Add(new PlannedMove(s.Device, pos, NumOf(s, "velocity"), Label(s)));
                    break;
                }

                case "motion.parallelmove":
                {
                    for (int i = 1; i <= MaxParallelDevices; i++)
                    {
                        if (!s.Parameters.TryGetValue($"device{i}", out var dev) || string.IsNullOrWhiteSpace(dev))
                            break;
                        var pos = s.Parameters.GetValueOrDefault($"position{i}", string.Empty);
                        if (!string.IsNullOrWhiteSpace(pos))
                            into.Add(new PlannedMove(dev, pos, 0, Label(s)));
                    }
                    break;
                }

                case "flow.parallel":
                    foreach (var branch in s.Branches)
                        Collect(process, branch.Steps, into, depth);
                    break;

                case "flow.subprogram":
                {
                    if (depth >= MaxSubProgramDepth) break;
                    var child = s.Parameters.GetValueOrDefault("program", string.Empty);
                    if (string.IsNullOrWhiteSpace(child)) break;
                    var loaded = process.LoadProgram(child);
                    if (loaded.IsSuccess)
                        Collect(process, loaded.Value.Steps, into, depth + 1);
                    break;
                }
            }
        }
    }

    private static string Label(WorkflowStep s) => string.IsNullOrWhiteSpace(s.Name) ? s.Op : s.Name;

    /// <summary>Literal numeric parameter, or 0 when absent / a variable reference we can't resolve here.</summary>
    private static double NumOf(WorkflowStep s, string key)
        => s.Parameters.TryGetValue(key, out var raw)
           && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v : 0;

    // --- Row values ------------------------------------------------------------

    /// <summary>Re-reads every row's taught values from the active product (after an edit / re-teach).</summary>
    public void RefreshRows()
    {
        foreach (var row in Rows)
            Fill(row);
        OnPropertyChanged(nameof(CursorText));
    }

    private void Fill(MotionInspectRow row)
    {
        var np = _owner.Host.Process.GetPosition(row.Device, row.Position);
        var c = np?.Coordinates;
        row.IsTaught = np != null;
        // Show the velocity the move will actually use: the step's override when it
        // sets one, otherwise the point's stored velocity.
        row.Velocity = row.VelocityOverride > 0 ? Fmt(row.VelocityOverride) : Fmt(np?.Velocity);
        row.X = Fmt(c?.X);
        row.Y = Fmt(c?.Y);
        row.Z = Fmt(c?.Z);
        row.U = Fmt(c?.U);
        row.V = Fmt(c?.V);
        row.W = Fmt(c?.W);
        row.Camera = string.IsNullOrWhiteSpace(np?.Camera) ? "—" : np!.Camera!;
        row.Exposure = Fmt(np?.ExposureUs);
    }

    private static string Fmt(double? v) => v.HasValue ? v.Value.ToString("0.###", CultureInfo.InvariantCulture) : "—";

    // --- Stepping --------------------------------------------------------------

    /// <summary>
    /// Moves the cursor by one row in <paramref name="direction"/> and drives that
    /// row's device to its taught point. The cursor only advances when the move
    /// succeeds, so a blocked or failed move leaves the inspector pointing at where
    /// the machine actually is.
    /// </summary>
    private async Task StepAsync(int direction)
    {
        int target = _activeIndex + direction;
        if (target < 0 || target >= Rows.Count)
        {
            Status = direction > 0 ? "Already at the last move." : "Already at the first move.";
            return;
        }

        var row = Rows[target];

        // An untaught point has nowhere to move to — advance the cursor anyway so the
        // operator can jog there and press Update. That is how a fresh product gets
        // taught: walk the sequence, and teach each point as you reach it.
        if (!row.IsTaught)
        {
            ActiveIndex = target;
            Status = $"'{row.Position}' is not taught on {row.Device} — jog there, then press Update.";
            _owner.InspectorLog(LogLevel.Warning,
                $"Inspect step {row.Seq}: '{row.Position}' has no taught point — cursor moved, machine did not.");
            return;
        }

        Status = $"Moving {row.Device} → {row.Position}…";
        var result = await _owner.RunInspectorMoveAsync(
            $"inspect step {row.Seq} ({row.Position})",
            ct => _owner.Host.Process.MoveToNamedPositionAsync(
                row.Device, row.Position, row.VelocityOverride, ct));

        if (!result.IsSuccess)
        {
            Status = $"Move to '{row.Position}' failed: {result.Message}";
            _owner.InspectorLog(LogLevel.Warning, $"Inspect step {row.Seq}: {result.Message}");
            await SyncFromHardwareAsync().ConfigureAwait(true);
            return;
        }

        ActiveIndex = target;
        Status = $"At step {row.Seq} — {row.Device} · {row.Position}.";
        _owner.InspectorLog(LogLevel.Info, $"Inspect step {row.Seq}: {row.Device} → {row.Position}.");
        await SyncFromHardwareAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Re-teaches the active row's point from where its device is standing right
    /// now, keeping the point's velocity / camera / exposure and the axes the device
    /// doesn't own. This is the seamless-teach path: step to a row, nudge with the
    /// jog rail, press Update.
    /// </summary>
    private async Task UpdateActiveAsync()
    {
        var row = ActiveRow;
        if (row == null) return;

        var info = _owner.Host.Engine.Motion.GetDevice(row.Device);
        if (info == null)
        {
            Status = $"Motion device '{row.Device}' is not available.";
            return;
        }

        var live = await _owner.Host.Engine.Motion.GetPositionAsync(row.Device).ConfigureAwait(true);
        if (!live.IsSuccess)
        {
            Status = $"Could not read {row.Device}: {live.Message}";
            _owner.InspectorLog(LogLevel.Warning, $"Inspect update '{row.Position}': {live.Message}");
            return;
        }

        // Keep everything about the point except the axes this device actually owns —
        // a 3-axis gantry must not zero a stored U/V/W, and the recall settings
        // (velocity, camera, exposure) are taught separately in the editor.
        var stored = _owner.Host.Process.GetPosition(row.Device, row.Position);
        var pose = Merge(stored?.Coordinates ?? MotionPosition.Zero, live.Value, info.Axes);

        var saved = _owner.Host.Process.SetPositionFull(
            row.Device, row.Position, pose, stored?.Velocity ?? 0, stored?.Camera, stored?.ExposureUs);

        if (!saved.IsSuccess)
        {
            Status = $"Save '{row.Position}' failed: {saved.Message}";
            _owner.InspectorLog(LogLevel.Warning, $"Inspect update '{row.Position}' failed: {saved.Message}");
            return;
        }

        // The same taught point may appear at several places in the sequence — refresh
        // every row so they all show the new coordinates.
        RefreshRows();
        Status = $"Updated '{row.Position}' on {row.Device} from the live position.";
        _owner.InspectorLog(LogLevel.Info,
            $"Inspect update: {row.Device}.{row.Position} re-taught at the current pose.");
        await SyncFromHardwareAsync().ConfigureAwait(true);
    }

    /// <summary>Overlays the live pose's owned axes onto a stored pose, leaving the rest untouched.</summary>
    private static MotionPosition Merge(MotionPosition stored, MotionPosition live, IReadOnlyList<string> axes)
    {
        double x = stored.X, y = stored.Y, z = stored.Z, u = stored.U, v = stored.V, w = stored.W;
        foreach (var axis in axes)
        {
            switch (axis.Trim().ToUpperInvariant())
            {
                case "X": x = live.X; break;
                case "Y": y = live.Y; break;
                case "Z": z = live.Z; break;
                case "U": u = live.U; break;
                case "V": v = live.V; break;
                case "W": w = live.W; break;
            }
        }
        return new MotionPosition(x, y, z, u, v, w);
    }

    // --- Hardware sync ---------------------------------------------------------

    /// <summary>
    /// Reads every device the sequence touches and flags each row whose device is
    /// standing on that row's taught point. When the cursor is still unset, it
    /// snaps to the first matching row — so opening the inspector mid-process
    /// already knows which step the machine is on.
    /// </summary>
    public async Task SyncFromHardwareAsync()
    {
        if (Rows.Count == 0) return;

        var poses = new Dictionary<string, MotionPosition>(StringComparer.OrdinalIgnoreCase);
        var axes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in Rows.Select(r => r.Device).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var info = _owner.Host.Engine.Motion.GetDevice(device);
            if (info == null) continue;
            var live = await _owner.Host.Engine.Motion.GetPositionAsync(device).ConfigureAwait(true);
            if (!live.IsSuccess) continue;
            poses[device] = live.Value;
            axes[device] = info.Axes;
        }

        int firstMatch = -1;
        for (int i = 0; i < Rows.Count; i++)
        {
            var row = Rows[i];
            bool at = row.IsTaught
                && poses.TryGetValue(row.Device, out var live)
                && axes.TryGetValue(row.Device, out var deviceAxes)
                && PositionMatch.Matches(_owner.Host.Process.GetPosition(row.Device, row.Position)?.Coordinates, live, deviceAxes);
            row.AtPosition = at;
            if (at && firstMatch < 0) firstMatch = i;
        }

        if (_activeIndex < 0 && firstMatch >= 0)
        {
            ActiveIndex = firstMatch;
            Status = $"Machine is at step {Rows[firstMatch].Seq} — {Rows[firstMatch].Position}.";
        }
    }

    private void RaiseCanExecute()
    {
        ForwardCommand.RaiseCanExecuteChanged();
        BackCommand.RaiseCanExecuteChanged();
        UpdateCommand.RaiseCanExecuteChanged();
    }
}
