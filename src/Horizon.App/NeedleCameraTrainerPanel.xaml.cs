using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Horizon.Core;

namespace Horizon.App;

public partial class NeedleCameraTrainerPanel : UserControl
{
    private readonly IAppContext _context;
    private readonly NeedleCameraCalibrationStore _store = new();
    private DispatcherTimer? _pollTimer;

    private int _currentStep; // 0=config, 1-8=wizard panels
    private NeedleCameraCalibrationConfig _config = new();
    private NeedleCameraCalibration? _previousCal;

    // Captured during calibration
    private CalibrationPosition? _cameraPos;    // step 2
    private CalibrationPosition? _needleXYPos;  // step 6
    private double _startingZ;                   // Z before needle descent
    private CalibrationPosition? _needleZPos;   // step 9
    private CalibrationPosition? _markPos;       // step 15

    // Computed offsets
    private double _offsetX, _offsetY, _offsetZ;

    private bool _busy;
    private CancellationTokenSource? _cts;

    private static readonly SolidColorBrush ActiveStepBrush = new(Color.FromRgb(0x4E, 0xC9, 0xB0));
    private static readonly SolidColorBrush CompletedStepBrush = new(Color.FromRgb(0x32, 0xCD, 0x32));
    private static readonly SolidColorBrush InactiveStepFg = new(Color.FromRgb(0xF1, 0xF1, 0xF1));
    private static readonly SolidColorBrush InactiveStepBg = new(Color.FromRgb(0x2D, 0x2D, 0x30));

    private string ActiveProduct => _context.IsInitialized
        ? _context.ProductManager.ActiveProductName
        : string.Empty;

    public NeedleCameraTrainerPanel(IAppContext context)
    {
        _context = context;
        InitializeComponent();

        PopulateCombos();
        RefreshActiveLabel();
        LoadPreviousCal();
        ShowStep(0);
        StartPolling();

        if (_context.IsInitialized)
            _context.ProductManager.ActiveProductChanged += () =>
                Dispatcher.BeginInvoke(() =>
                {
                    RefreshActiveLabel();
                    LoadPreviousCal();
                });
    }

    // --- Polling ---

    private void StartPolling()
    {
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _pollTimer.Tick += PollTimer_Tick;
        _pollTimer.Start();
    }

    public void StopPolling()
    {
        if (_pollTimer != null)
        {
            _pollTimer.Stop();
            _pollTimer.Tick -= PollTimer_Tick;
            _pollTimer = null;
        }
        _cts?.Cancel();
    }

    private void PollTimer_Tick(object? sender, EventArgs e)
    {
        var device = _config.MotionDevice;
        if (!_context.IsInitialized || string.IsNullOrEmpty(device))
        {
            LivePositionText.Text = "-- not connected --";
            return;
        }

        var pos = _context.MotionManager.GetCurrentPosition(device);
        if (pos == null)
        {
            LivePositionText.Text = "-- not connected --";
            return;
        }

        LivePositionText.Text = $"X={pos.X:F4}  Y={pos.Y:F4}  Z={pos.Z:F4}";

        // Update live position in visible panels
        if (PanelCameraFocus.Visibility == Visibility.Visible)
            CamFocusLivePos.Text = $"X={pos.X:F4}  Y={pos.Y:F4}  Z={pos.Z:F4}";
        if (PanelNeedleXY.Visibility == Visibility.Visible)
            NeedleXYLivePos.Text = $"X={pos.X:F4}  Y={pos.Y:F4}  Z={pos.Z:F4}";
        if (PanelNeedleZ.Visibility == Visibility.Visible)
            NeedleZLivePos.Text = $"Z={pos.Z:F4}";
        if (PanelMeasure.Visibility == Visibility.Visible)
            MeasureLivePos.Text = $"X={pos.X:F4}  Y={pos.Y:F4}  Z={pos.Z:F4}";
    }

    // --- Setup ---

    private void PopulateCombos()
    {
        ConfigDeviceCombo.Items.Clear();
        ConfigPneumaticCombo.Items.Clear();

        if (_context.IsInitialized)
        {
            foreach (var name in _context.MotionManager.GetAllDeviceNames())
                ConfigDeviceCombo.Items.Add(name);
            foreach (var name in _context.PneumaticManager.GetAllSlideNames())
                ConfigPneumaticCombo.Items.Add(name);
        }

        if (ConfigDeviceCombo.Items.Count > 0)
            ConfigDeviceCombo.SelectedIndex = 0;
        if (ConfigPneumaticCombo.Items.Count > 0)
            ConfigPneumaticCombo.SelectedIndex = 0;
    }

    private void RefreshActiveLabel()
    {
        var product = ActiveProduct;
        ActiveProductText.Text = string.IsNullOrEmpty(product) ? "(none)" : product;
        NoProductWarning.Visibility = string.IsNullOrEmpty(product) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LoadPreviousCal()
    {
        var product = ActiveProduct;
        _previousCal = string.IsNullOrEmpty(product) ? null : _store.Load(product);

        if (_previousCal is { Valid: true })
        {
            PreviousCalText.Text = $"Last cal: OffsetX={_previousCal.OffsetX:F4}  OffsetY={_previousCal.OffsetY:F4}  OffsetZ={_previousCal.OffsetZ:F4}  ({_previousCal.CalibratedAt})";

            // Pre-fill config from previous
            if (!string.IsNullOrEmpty(_previousCal.Config.MotionDevice))
            {
                for (int i = 0; i < ConfigDeviceCombo.Items.Count; i++)
                {
                    if (string.Equals(ConfigDeviceCombo.Items[i] as string, _previousCal.Config.MotionDevice, StringComparison.OrdinalIgnoreCase))
                    {
                        ConfigDeviceCombo.SelectedIndex = i;
                        break;
                    }
                }
            }
            if (!string.IsNullOrEmpty(_previousCal.Config.PneumaticSlide))
            {
                for (int i = 0; i < ConfigPneumaticCombo.Items.Count; i++)
                {
                    if (string.Equals(ConfigPneumaticCombo.Items[i] as string, _previousCal.Config.PneumaticSlide, StringComparison.OrdinalIgnoreCase))
                    {
                        ConfigPneumaticCombo.SelectedIndex = i;
                        break;
                    }
                }
            }
            ConfigOffsetXBox.Text = _previousCal.Config.EstimateOffsetX.ToString("F4");
            ConfigOffsetYBox.Text = _previousCal.Config.EstimateOffsetY.ToString("F4");
        }
        else
        {
            PreviousCalText.Text = "No previous calibration data.";
        }
    }

    // --- Step Navigation ---

    private void ShowStep(int step)
    {
        _currentStep = step;

        PanelConfig.Visibility = step == 0 ? Visibility.Visible : Visibility.Collapsed;
        PanelCameraFocus.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        PanelDeploy.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        PanelNeedleXY.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
        PanelNeedleZ.Visibility = step == 4 ? Visibility.Visible : Visibility.Collapsed;
        PanelMark.Visibility = step == 5 ? Visibility.Visible : Visibility.Collapsed;
        PanelReturn.Visibility = step == 6 ? Visibility.Visible : Visibility.Collapsed;
        PanelMeasure.Visibility = step == 7 ? Visibility.Visible : Visibility.Collapsed;
        PanelSave.Visibility = step == 8 ? Visibility.Visible : Visibility.Collapsed;

        UpdateSidebar(step);
        UpdateStatusBar(step);
    }

    private void UpdateSidebar(int step)
    {
        // Map panel steps to sidebar phases
        // Phase 1 (Camera): steps 0-1, Phase 2 (Deploy): step 2, Phase 3 (Needle): steps 3-4
        // Phase 4 (Return): steps 5-6, Phase 5 (Measure): steps 7-8
        var buttons = new[] { SideStep1, SideStep2, SideStep3, SideStep4, SideStep5 };
        int activePhase = step switch
        {
            0 or 1 => 1,
            2 => 2,
            3 or 4 => 3,
            5 or 6 => 4,
            7 or 8 => 5,
            _ => 0
        };

        for (int i = 0; i < buttons.Length; i++)
        {
            int phase = i + 1;
            bool completed = phase < activePhase;
            bool active = phase == activePhase;

            if (completed)
            {
                buttons[i].Foreground = CompletedStepBrush;
                buttons[i].Background = InactiveStepBg;
            }
            else if (active)
            {
                buttons[i].Foreground = ActiveStepBrush;
                buttons[i].Background = new SolidColorBrush(Color.FromRgb(0x3E, 0x3E, 0x42));
            }
            else
            {
                buttons[i].Foreground = InactiveStepFg;
                buttons[i].Background = InactiveStepBg;
            }
        }
    }

    private void UpdateStatusBar(int step)
    {
        StatusBarText.Text = step switch
        {
            0 => "Step 0: Configure calibration settings",
            1 => "Steps 1-2: Focus camera on dispensing target and register position",
            2 => "Steps 3-4: Moving to offset position and deploying needle",
            3 => "Steps 5-6: Position needle XY over target",
            4 => "Steps 7-9: Lower needle to surface",
            5 => "Step 10: Verify needle mark on surface",
            6 => "Steps 11-13: Returning to camera position",
            7 => "Steps 14-15: Measure needle mark position",
            8 => "Step 16: Review and save calibration",
            _ => ""
        };
    }

    private void SideStep_Click(object sender, RoutedEventArgs e)
    {
        // Sidebar clicks are informational only during calibration - don't navigate
    }

    // --- Phase A: Config ---

    private void BeginCalibration_Click(object sender, RoutedEventArgs e)
    {
        var product = ActiveProduct;
        if (string.IsNullOrEmpty(product))
        {
            AppendLog("No active product selected.");
            return;
        }

        var device = ConfigDeviceCombo.SelectedItem as string;
        var pneumatic = ConfigPneumaticCombo.SelectedItem as string;

        if (string.IsNullOrEmpty(device))
        {
            AppendLog("Select a motion device.");
            return;
        }
        if (string.IsNullOrEmpty(pneumatic))
        {
            AppendLog("Select a pneumatic slide.");
            return;
        }

        if (!double.TryParse(ConfigOffsetXBox.Text, out double estX))
        {
            AppendLog("Invalid Estimate Offset X value.");
            return;
        }
        if (!double.TryParse(ConfigOffsetYBox.Text, out double estY))
        {
            AppendLog("Invalid Estimate Offset Y value.");
            return;
        }

        _config = new NeedleCameraCalibrationConfig
        {
            MotionDevice = device,
            PneumaticSlide = pneumatic,
            EstimateOffsetX = estX,
            EstimateOffsetY = estY
        };

        // Reload previous cal for this product
        _previousCal = _store.Load(product);

        // Clear captured state
        _cameraPos = null;
        _needleXYPos = null;
        _needleZPos = null;
        _markPos = null;
        _startingZ = 0;
        _offsetX = _offsetY = _offsetZ = 0;

        AppendLog($"Calibration started: device={device}, pneumatic={pneumatic}, estOffset=({estX:F4}, {estY:F4})");
        ShowStep(1);
    }

    // --- Phase A: Camera Focus (Panel 1) ---

    private void RegisterCamera_Click(object sender, RoutedEventArgs e)
    {
        var pos = GetCurrentPosition();
        if (pos == null)
        {
            AppendLog("Cannot read position - device not connected.");
            return;
        }

        _cameraPos = pos;
        CamFocusCapturedText.Text = $"Captured: X={pos.X:F4}  Y={pos.Y:F4}  Z={pos.Z:F4}";
        AppendLog($"Camera position registered: X={pos.X:F4}  Y={pos.Y:F4}  Z={pos.Z:F4}");

        ShowStep(2);
        ExecuteDeploySequence();
    }

    // --- Phase B: Deploy (Panel 2) ---

    private async void ExecuteDeploySequence()
    {
        if (_cameraPos == null) return;

        _cts = new CancellationTokenSource();
        SetBusy(true);
        DeployStopBtn.Visibility = Visibility.Visible;

        try
        {
            var device = _config.MotionDevice;
            var dx = _config.EstimateOffsetX;
            var dy = _config.EstimateOffsetY;

            // Step 3: Move XY by offset
            DeployStatus1.Text = $"  Moving XY by offset ({dx:F4}, {dy:F4})...";
            DeployStatus1.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD5, 0x4F));
            AppendLog($"Moving XY relative: dX={dx:F4}, dY={dy:F4}");

            await Task.Run(() =>
            {
                _context.MotionManager.MoveRelative(device, "X", dx, false);
                _context.MotionManager.MoveRelative(device, "Y", dy, false);
                WaitForMotionComplete(device, _cts.Token);
            }, _cts.Token);

            _cts.Token.ThrowIfCancellationRequested();
            DeployStatus1.Text = "  Moving XY by offset... Done";
            DeployStatus1.Foreground = CompletedStepBrush;

            // Step 4: Extend pneumatic
            DeployStatus2.Text = "  Extending dispenser...";
            DeployStatus2.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD5, 0x4F));
            AppendLog("Extending pneumatic dispenser...");

            await Task.Run(() =>
            {
                _context.PneumaticManager.ExtendAndWait(_config.PneumaticSlide);
            }, _cts.Token);

            _cts.Token.ThrowIfCancellationRequested();
            DeployStatus2.Text = "  Extending dispenser... Done";
            DeployStatus2.Foreground = CompletedStepBrush;
            DeployStatus3.Text = "Deploy complete. Advancing...";

            AppendLog("Deploy sequence complete.");
            DeployStopBtn.Visibility = Visibility.Collapsed;
            SetBusy(false);

            // Setup needle XY panel
            SetupNeedleXYPanel();
            ShowStep(3);
        }
        catch (OperationCanceledException)
        {
            DeployStatus3.Text = "Stopped by user.";
            DeployStatus3.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x00));
            AppendLog("Deploy sequence stopped.");
            DeployStopBtn.Visibility = Visibility.Collapsed;
            SetBusy(false);
        }
        catch (Exception ex)
        {
            DeployStatus3.Text = $"Error: {ex.Message}";
            DeployStatus3.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x00));
            AppendLog($"Deploy error: {ex.Message}");
            DeployStopBtn.Visibility = Visibility.Collapsed;
            SetBusy(false);
        }
    }

    private void DeployStop_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        try { _context.MotionManager.StopAll(_config.MotionDevice); } catch { }
    }

    // --- Phase C: Needle XY (Panel 3) ---

    private void SetupNeedleXYPanel()
    {
        if (_previousCal is { Valid: true, NeedleXYPosition: not null })
        {
            NeedleXYInstruction.Text = $"Previous needle position available: X={_previousCal.NeedleXYPosition.X:F4}  Y={_previousCal.NeedleXYPosition.Y:F4}";
            NeedleXYAutoPanel.Visibility = Visibility.Visible;
        }
        else
        {
            NeedleXYInstruction.Text = "No previous data. Manually jog the gantry to align the needle tip over the dispensing target.";
            NeedleXYAutoPanel.Visibility = Visibility.Collapsed;
        }
    }

    private async void AutoMoveNeedleXY_Click(object sender, RoutedEventArgs e)
    {
        if (_previousCal?.NeedleXYPosition == null || _busy) return;

        var target = _previousCal.NeedleXYPosition;
        SetBusy(true);
        AppendLog($"Auto-moving to previous XY: X={target.X:F4}  Y={target.Y:F4}");

        try
        {
            var device = _config.MotionDevice;
            await Task.Run(() =>
            {
                _context.MotionManager.MoveToAbs(device, "X", target.X, false);
                _context.MotionManager.MoveToAbs(device, "Y", target.Y, false);
                WaitForMotionComplete(device);
            });
            AppendLog("Auto-move XY complete.");
        }
        catch (Exception ex)
        {
            AppendLog($"Auto-move XY error: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SkipAutoNeedleXY_Click(object sender, RoutedEventArgs e)
    {
        NeedleXYAutoPanel.Visibility = Visibility.Collapsed;
        NeedleXYInstruction.Text = "Manually jog the gantry to align the needle tip over the dispensing target.";
    }

    private void ConfirmNeedleXY_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var pos = GetCurrentPosition();
        if (pos == null)
        {
            AppendLog("Cannot read position.");
            return;
        }

        _needleXYPos = pos;
        _startingZ = pos.Z;
        AppendLog($"Needle XY confirmed: X={pos.X:F4}  Y={pos.Y:F4}  (startingZ={pos.Z:F4})");

        SetupNeedleZPanel();
        ShowStep(4);
    }

    // --- Phase C: Needle Z (Panel 4) ---

    private void SetupNeedleZPanel()
    {
        if (_previousCal is { Valid: true, NeedleZPosition: not null })
        {
            NeedleZInstruction.Text = $"Previous Z position: Z={_previousCal.NeedleZPosition.Z:F4}";
            NeedleZAutoPanel.Visibility = Visibility.Visible;
        }
        else
        {
            NeedleZInstruction.Text = "Manually jog Z down until the needle touches the surface. The needle should leave a visible mark.";
            NeedleZAutoPanel.Visibility = Visibility.Collapsed;
        }
    }

    private async void AutoMoveNeedleZ_Click(object sender, RoutedEventArgs e)
    {
        if (_previousCal?.NeedleZPosition == null || _busy) return;

        var targetZ = _previousCal.NeedleZPosition.Z;
        SetBusy(true);
        AppendLog($"Auto-moving Z to previous: Z={targetZ:F4}");

        try
        {
            var device = _config.MotionDevice;
            await Task.Run(() =>
            {
                _context.MotionManager.MoveToAbs(device, "Z", targetZ, true);
            });
            AppendLog("Auto-move Z complete.");
        }
        catch (Exception ex)
        {
            AppendLog($"Auto-move Z error: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SkipAutoNeedleZ_Click(object sender, RoutedEventArgs e)
    {
        NeedleZAutoPanel.Visibility = Visibility.Collapsed;
        NeedleZInstruction.Text = "Manually jog Z down until the needle touches the surface. The needle should leave a visible mark.";
    }

    private void ConfirmNeedleZ_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var pos = GetCurrentPosition();
        if (pos == null)
        {
            AppendLog("Cannot read position.");
            return;
        }

        _needleZPos = pos;
        AppendLog($"Needle Z confirmed: Z={pos.Z:F4}");

        ShowStep(5);
    }

    // --- Phase D: Mark (Panel 5) ---

    private void MarkOk_Click(object sender, RoutedEventArgs e)
    {
        AppendLog("Mark confirmed. Starting return sequence...");
        ShowStep(6);
        ExecuteReturnSequence();
    }

    // --- Phase D: Return (Panel 6) ---

    private async void ExecuteReturnSequence()
    {
        if (_cameraPos == null) return;

        _cts = new CancellationTokenSource();
        SetBusy(true);
        ReturnStopBtn.Visibility = Visibility.Visible;

        try
        {
            var device = _config.MotionDevice;

            // Step 11: Move Z up to starting height
            ReturnStatus1.Text = $"  Moving Z up to Z={_startingZ:F4}...";
            ReturnStatus1.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD5, 0x4F));
            AppendLog($"Moving Z up to starting height: Z={_startingZ:F4}");

            await Task.Run(() =>
            {
                _context.MotionManager.MoveToAbs(device, "Z", _startingZ, true);
            }, _cts.Token);

            _cts.Token.ThrowIfCancellationRequested();
            ReturnStatus1.Text = "  Moving Z up... Done";
            ReturnStatus1.Foreground = CompletedStepBrush;

            // Step 12: Retract pneumatic
            ReturnStatus2.Text = "  Retracting dispenser...";
            ReturnStatus2.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD5, 0x4F));
            AppendLog("Retracting pneumatic dispenser...");

            await Task.Run(() =>
            {
                _context.PneumaticManager.RetractAndWait(_config.PneumaticSlide);
            }, _cts.Token);

            _cts.Token.ThrowIfCancellationRequested();
            ReturnStatus2.Text = "  Retracting dispenser... Done";
            ReturnStatus2.Foreground = CompletedStepBrush;

            // Step 13: Move XY back to camera position
            ReturnStatus3.Text = $"  Moving XY to camera pos ({_cameraPos.X:F4}, {_cameraPos.Y:F4})...";
            ReturnStatus3.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD5, 0x4F));
            AppendLog($"Moving XY back to camera position: X={_cameraPos.X:F4}  Y={_cameraPos.Y:F4}");

            await Task.Run(() =>
            {
                _context.MotionManager.MoveToAbs(device, "X", _cameraPos.X, false);
                _context.MotionManager.MoveToAbs(device, "Y", _cameraPos.Y, false);
                WaitForMotionComplete(device, _cts.Token);
            }, _cts.Token);

            _cts.Token.ThrowIfCancellationRequested();
            ReturnStatus3.Text = "  Moving XY back... Done";
            ReturnStatus3.Foreground = CompletedStepBrush;

            AppendLog("Return sequence complete.");
            ReturnStopBtn.Visibility = Visibility.Collapsed;
            SetBusy(false);

            ShowStep(7);
        }
        catch (OperationCanceledException)
        {
            AppendLog("Return sequence stopped.");
            ReturnStopBtn.Visibility = Visibility.Collapsed;
            SetBusy(false);
        }
        catch (Exception ex)
        {
            AppendLog($"Return error: {ex.Message}");
            ReturnStopBtn.Visibility = Visibility.Collapsed;
            SetBusy(false);
        }
    }

    private void ReturnStop_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        try { _context.MotionManager.StopAll(_config.MotionDevice); } catch { }
    }

    // --- Phase E: Measure (Panel 7) ---

    private void CaptureMarkPosition_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var pos = GetCurrentPosition();
        if (pos == null)
        {
            AppendLog("Cannot read position.");
            return;
        }

        _markPos = pos;
        AppendLog($"Mark position captured: X={pos.X:F4}  Y={pos.Y:F4}  Z={pos.Z:F4}");

        // Compute total offsets (estimate + residual)
        if (_cameraPos != null && _needleZPos != null)
        {
            var residualX = _markPos.X - _cameraPos.X;
            var residualY = _markPos.Y - _cameraPos.Y;
            _offsetX = _config.EstimateOffsetX + residualX;
            _offsetY = _config.EstimateOffsetY + residualY;
            _offsetZ = _cameraPos.Z - _needleZPos.Z;

            MeasureOffsetText.Text = $"Total offset: X={_offsetX:F4}  Y={_offsetY:F4}  Z={_offsetZ:F4}\n" +
                                     $"(residual: X={residualX:F4}  Y={residualY:F4})";
            AppendLog($"Total offset: X={_offsetX:F4}  Y={_offsetY:F4}  Z={_offsetZ:F4}");
        }

        CaptureMarkBtn.IsEnabled = false;
        AcceptMeasureBtn.Visibility = Visibility.Visible;
    }

    private void AcceptMeasure_Click(object sender, RoutedEventArgs e)
    {
        BuildSaveSummary();
        ShowStep(8);
    }

    // --- Phase E: Save (Panel 8) ---

    private void BuildSaveSummary()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Camera Position:");
        if (_cameraPos != null)
            sb.AppendLine($"  X={_cameraPos.X:F4}  Y={_cameraPos.Y:F4}  Z={_cameraPos.Z:F4}");
        sb.AppendLine();
        sb.AppendLine("Needle XY:");
        if (_needleXYPos != null)
            sb.AppendLine($"  X={_needleXYPos.X:F4}  Y={_needleXYPos.Y:F4}");
        sb.AppendLine();
        sb.AppendLine("Needle Z:");
        if (_needleZPos != null)
            sb.AppendLine($"  Z={_needleZPos.Z:F4}");
        sb.AppendLine();
        sb.AppendLine("Mark Position:");
        if (_markPos != null)
            sb.AppendLine($"  X={_markPos.X:F4}  Y={_markPos.Y:F4}  Z={_markPos.Z:F4}");
        sb.AppendLine();
        sb.AppendLine("─────────────────────────────────");
        sb.AppendLine($"Total Offset X:  {_offsetX:F4}");
        sb.AppendLine($"Total Offset Y:  {_offsetY:F4}");
        sb.AppendLine($"Offset Z:        {_offsetZ:F4}");

        SaveSummaryText.Text = sb.ToString();
    }

    private void SaveCalibration_Click(object sender, RoutedEventArgs e)
    {
        var product = ActiveProduct;
        if (string.IsNullOrEmpty(product))
        {
            AppendLog("No active product - cannot save.");
            return;
        }

        var cal = new NeedleCameraCalibration
        {
            Config = _config,
            CameraPosition = _cameraPos,
            NeedleXYPosition = _needleXYPos,
            NeedleZPosition = _needleZPos,
            MarkPosition = _markPos,
            OffsetX = _offsetX,
            OffsetY = _offsetY,
            OffsetZ = _offsetZ,
            StartingZ = _startingZ,
            CalibratedAt = DateTimeOffset.Now.ToString("o"),
            Valid = true
        };

        try
        {
            _store.Save(product, cal);
            _previousCal = cal;
            SaveStatusText.Text = $"Saved to {product}/needle-camera-cal.json";
            AppendLog($"Calibration saved for product '{product}'.");
        }
        catch (Exception ex)
        {
            SaveStatusText.Text = $"Save failed: {ex.Message}";
            AppendLog($"Save error: {ex.Message}");
        }
    }

    private void Restart_Click(object sender, RoutedEventArgs e)
    {
        _cameraPos = null;
        _needleXYPos = null;
        _needleZPos = null;
        _markPos = null;
        _startingZ = 0;
        _offsetX = _offsetY = _offsetZ = 0;

        // Reset panel states
        CamFocusCapturedText.Text = "";
        DeployStatus1.Text = "  Moving XY by offset...";
        DeployStatus1.Foreground = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));
        DeployStatus2.Text = "  Extending dispenser...";
        DeployStatus2.Foreground = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));
        DeployStatus3.Text = "";
        ReturnStatus1.Text = "  Moving Z up to starting height...";
        ReturnStatus1.Foreground = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));
        ReturnStatus2.Text = "  Retracting dispenser...";
        ReturnStatus2.Foreground = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));
        ReturnStatus3.Text = "  Moving XY back to camera position...";
        ReturnStatus3.Foreground = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));
        MeasureOffsetText.Text = "";
        CaptureMarkBtn.IsEnabled = true;
        AcceptMeasureBtn.Visibility = Visibility.Collapsed;
        SaveSummaryText.Text = "";
        SaveStatusText.Text = "";

        LoadPreviousCal();
        ShowStep(0);
    }

    // --- Helpers ---

    private CalibrationPosition? GetCurrentPosition()
    {
        if (!_context.IsInitialized || string.IsNullOrEmpty(_config.MotionDevice))
            return null;

        var pos = _context.MotionManager.GetCurrentPosition(_config.MotionDevice);
        if (pos == null) return null;

        return new CalibrationPosition { X = pos.X, Y = pos.Y, Z = pos.Z };
    }

    private void WaitForMotionComplete(string device, CancellationToken ct = default)
    {
        var deadline = Environment.TickCount64 + 30000;
        while (_context.MotionManager.IsMoving(device) && Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();
            Thread.Sleep(50);
        }
    }

    private void SetBusy(bool busy, string? description = null)
    {
        _busy = busy;
        BeginCalibrationBtn.IsEnabled = !busy;
    }

    private void AppendLog(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        LogList.Items.Add($"[{timestamp}] {message}");
        if (LogList.Items.Count > 0)
            LogList.ScrollIntoView(LogList.Items[^1]);
    }
}
