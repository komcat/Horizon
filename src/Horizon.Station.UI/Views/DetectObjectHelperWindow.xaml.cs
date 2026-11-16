using System.Windows;
using Horizon.Common;
using Horizon.Engine;
using Horizon.Workflow;

namespace Horizon.Station.UI.Views;

/// <summary>
/// The Detect Object block's <b>Vision helper</b>: the Vision Profile page embedded as an editor
/// for the block's own recipe (live view + overlays, camera / exposure / lighting, the filter
/// chain, the filtered image, template + method parameters, the matched crop and the result
/// list), plus the block's final result — X / Y in the camera device's axis values and P
/// (0° = up on the image, clockwise +) — a guarded test move, and <b>Save to block</b>.
/// Nothing is written to the product's Vision Profiles. See <c>docs/detect-object-design.md</c>.
/// </summary>
public partial class DetectObjectHelperWindow : Window
{
    private readonly EngineHost _host;
    private readonly Action<string, bool, bool, bool> _saveRecipe;
    private readonly VisionProfileView _view;
    private readonly string _initialRecipe;
    private DetectedObject? _raw;          // the detection as found, before the X / Y / P selection
    private MotionPosition? _rawPose;      // the camera pose it was found at
    private DetectedObject? _last;         // what the block would return (selection applied)

    /// <param name="recipeJson">The block's current recipe (blank = start from the page defaults).</param>
    /// <param name="use">The block's X / Y / P selection (which parts of the result it takes from the detection).</param>
    /// <param name="saveRecipe">Receives the recipe JSON + the X / Y / P selection when the engineer presses Save to block.</param>
    public DetectObjectHelperWindow(EngineHost host, string blockName, string recipeJson,
        (bool X, bool Y, bool P) use, Action<string, bool, bool, bool> saveRecipe)
    {
        _host = host;
        _saveRecipe = saveRecipe;
        _initialRecipe = recipeJson;
        InitializeComponent();
        Title = $"Vision helper — {blockName}";

        string device = CameraDevice;
        UseXCheck.Content = $"X ({device}, mm)";
        UseYCheck.Content = $"Y ({device}, mm)";
        UseXCheck.IsChecked = use.X;
        UseYCheck.IsChecked = use.Y;
        UsePCheck.IsChecked = use.P;

        _view = new VisionProfileView(host) { Embedded = true };
        _view.Detected += OnDetected;
        ViewHost.Content = _view;

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private string CameraDevice
    {
        get
        {
            var d = _host.Settings.Current.MoveClickDevice;
            return string.IsNullOrWhiteSpace(d) ? "gantry-main" : d;
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _view.Activate();
            if (DetectObjectRecipe.Parse(_initialRecipe) is { } recipe)
            {
                await _view.LoadRecipe(recipe);
                StatusText.Text = $"Loaded the block's recipe ({DetectObjectRecipe.Describe(_initialRecipe)}). Press Detect to test it.";
            }
        }
        catch (Exception ex) { StatusText.Text = $"Could not load the block's recipe: {ex.Message}"; }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _view.Detected -= OnDetected;
        _view.Deactivate();   // releases the camera lease this page holds
    }

    /// <summary>A Detect finished on the page — turn its pixels into the block's X / Y / P at the camera's pose.</summary>
    private async void OnDetected(VisionProfile recipe, CameraFrame frame, VisionDetectResult? result)
    {
        try
        {
            _raw = null;
            _rawPose = null;
            _last = null;
            if (result != null)
            {
                var pose = await _host.Engine.Motion.GetPositionAsync(CameraDevice);
                if (!pose.IsSuccess)
                {
                    ShowResult(null);
                    StatusText.Text = $"Detected, but {CameraDevice}'s position could not be read — {pose.Message}";
                    return;
                }
                var obj = _host.Process.ToDetectedObject(recipe, result, pose.Value, frame.Width, frame.Height);
                if (obj.Found) { _raw = obj; _rawPose = pose.Value; }
            }

            ApplySelection();
            StatusText.Text = _last != null
                ? "Detected. This is what the block returns to the program."
                : "Nothing detected — at run time the block reports found = 0 (or asks the operator, when that is on).";
        }
        catch (Exception ex) { StatusText.Text = $"Result error: {ex.Message}"; }
    }

    private bool UseX => UseXCheck.IsChecked == true;
    private bool UseY => UseYCheck.IsChecked == true;
    private bool UseP => UsePCheck.IsChecked == true;

    /// <summary>Re-derives what the block would return from the last detection and the X / Y / P tick boxes.</summary>
    private void ApplySelection()
    {
        _last = _raw != null && _rawPose != null
            ? DetectObjectMath.Select(_raw, _rawPose, UseX, UseY, UseP)
            : null;
        ShowResult(_last);
    }

    private void OnUseChanged(object sender, RoutedEventArgs e)
    {
        ApplySelection();
        StatusText.Text = "X / Y / P selection changed — press Save to block to keep it.";
    }

    private void ShowResult(DetectedObject? obj)
    {
        // An unticked value is shown dimmed: X / Y = the camera's own position, P = 0.
        XText.Opacity = UseX ? 1.0 : 0.4;
        YText.Opacity = UseY ? 1.0 : 0.4;
        PText.Opacity = UseP ? 1.0 : 0.4;
        XText.Text = obj != null ? obj.X.ToString("F4") : "—";
        YText.Text = obj != null ? obj.Y.ToString("F4") : "—";
        PText.Text = obj != null ? obj.P.ToString("+0.00;-0.00;0.00") : "—";
        ScoreText.Text = obj != null ? obj.Score.ToString("F2") : "—";
        TestMoveButton.IsEnabled = obj != null;
    }

    /// <summary>Centres the camera on the last result with the block's 5 mm guard (refused while anything runs).</summary>
    private async void OnTestMove(object sender, RoutedEventArgs e)
    {
        if (_last is not { } obj) return;
        string? busy = _host.Process.IsRunning ? "a program / deck is running"
            : _host.Activity.ProcessBusy ? "a Run-page process step is running"
            : _host.Activity.AlignmentBusy ? "an alignment or another move is running"
            : null;
        if (busy != null) { StatusText.Text = $"Not now — {busy}."; return; }

        TestMoveButton.IsEnabled = false;
        try
        {
            using var gate = _host.Activity.Begin(ActivityKind.Alignment, "Vision helper: move to result");
            var r = await _host.Process.MoveCameraToDetectedObjectAsync(obj, DetectObjectRequest.HardMaxMoveMm);
            StatusText.Text = r.IsSuccess
                ? "Camera centred on the result. Press Detect again to check it lands on the crosshair."
                : r.Message;
            _host.Log.Add(new LogEntry(r.IsSuccess ? LogLevel.Info : LogLevel.Warning, DateTimeOffset.Now, "Vision",
                r.IsSuccess ? $"Vision helper: camera moved to X {obj.X:F4}  Y {obj.Y:F4}." : $"Vision helper: {r.Message}"));
        }
        catch (Exception ex) { StatusText.Text = $"Move error: {ex.Message}"; }
        finally { TestMoveButton.IsEnabled = _last != null; }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        try
        {
            // The block owns this recipe: it has no profile identity and no named position.
            var recipe = _view.CurrentRecipe(snapshotExposure: true) with
            {
                Uid = string.Empty, Name = string.Empty, PositionName = string.Empty, Device = CameraDevice,
            };
            if (string.IsNullOrEmpty(recipe.CameraId)) { StatusText.Text = "Pick a camera first."; return; }

            _saveRecipe(DetectObjectRecipe.Serialize(recipe), UseX, UseY, UseP);
            bool noTemplate = recipe.DetectionMethod == "ncc" && recipe.Template == null;
            StatusText.Text = noTemplate
                ? "Saved to the block — but NCC has no template yet, so the block cannot detect. Create one and save again."
                : "Saved to the block. Save the program to keep it.";
        }
        catch (Exception ex) { StatusText.Text = $"Save error: {ex.Message}"; }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
