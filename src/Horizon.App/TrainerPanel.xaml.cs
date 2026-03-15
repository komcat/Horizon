using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Horizon.Core;
using Horizon.Motion.Models;

namespace Horizon.App;

public partial class TrainerPanel : UserControl
{
    private readonly IAppContext _context;
    private readonly TrainerStore _store = new();
    private DispatcherTimer? _pollTimer;

    private TrainingSet? _currentSet;
    private readonly ObservableCollection<BondingPoint> _bondingPoints = new();
    private readonly ObservableCollection<ReferencePoint> _refPoints = new();
    private readonly ObservableCollection<CorrectedPosition> _correctedPositions = new();

    // Current wizard step (1/2/3/4)
    private int _activeStep = 1;

    // Corner capture state (Step 1 wizard)
    private int _cornerTarget; // 2 or 3
    private int _cornerCurrent; // which corner we're capturing next (1-based)
    private string _pendingBondName = "";
    private string? _centerImagePath;
    private readonly List<BondingCorner> _capturedCorners = new();
    private readonly ObservableCollection<CornerDisplayItem> _cornerDisplayItems = new();

    // Reference capture state (Step 2 wizard)
    private int _pendingRefIndex;
    private int _refCornerTarget;
    private int _refCornerCurrent;
    private string? _refCenterImagePath;
    private readonly List<ReferenceCorner> _capturedRefCorners = new();
    private readonly ObservableCollection<CornerDisplayItem> _refCornerDisplayItems = new();

    // Step 3 state (secondary devices)
    private int _step3BondIndex;
    private string _step3DeviceName = "";
    private string _step3Method = "2-point";
    private DeviceSnapshot? _step3DevicePos;
    private int _step3ObsCornerTarget;
    private int _step3ObsCornerCurrent;
    private readonly List<BondingCorner> _step3ObsCorners = new();
    private readonly ObservableCollection<CornerDisplayItem> _step3ObsCornerDisplay = new();
    private string? _step3CenterImage;
    private readonly ObservableCollection<Step3AssignmentRow> _step3Rows = new();

    // Step 3 Z state
    private int _step3ZSubStep; // 0=registerZobj, 1=enterT, 2=registerZbond, 3=review
    private double _step3ZObj;
    private double _step3ClearanceT;
    private double _step3ZBond;

    // Alignment state (Step 4)
    private int _alignRefIndex;
    private List<ReferencePoint> _actualRefs = new();

    public TrainerPanel(IAppContext context)
    {
        _context = context;
        InitializeComponent();

        BondingGrid.ItemsSource = _bondingPoints;
        RefGrid.ItemsSource = _refPoints;
        CorrectedGrid.ItemsSource = _correctedPositions;
        CapturedCornersList.ItemsSource = _cornerDisplayItems;
        RefCapturedCornersList.ItemsSource = _refCornerDisplayItems;
        Step3Grid.ItemsSource = _step3Rows;
        Step3ObsCornersList.ItemsSource = _step3ObsCornerDisplay;

        PopulateDeviceCombo();
        HighlightStepButton(1);
        StartPolling();
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
    }

    private void PollTimer_Tick(object? sender, EventArgs e)
    {
        var device = DeviceCombo.SelectedItem as string;
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

        // Update live position in capture views
        if (Step1_Capture.Visibility == Visibility.Visible)
            CaptureLivePos.Text = $"Position:  X={pos.X:F4}  Y={pos.Y:F4}  Z={pos.Z:F4}";
        if (Step2_Capture.Visibility == Visibility.Visible)
            RefCaptureLivePos.Text = $"Position:  X={pos.X:F4}  Y={pos.Y:F4}  Z={pos.Z:F4}";
        if (Step3_XYCapture.Visibility == Visibility.Visible)
            Step3LivePos.Text = $"Gantry:  X={pos.X:F4}  Y={pos.Y:F4}  Z={pos.Z:F4}";
        if (Step3_ZTeach.Visibility == Visibility.Visible)
            Step3ZLivePos.Text = $"Gantry Z={pos.Z:F4}";

        // Show secondary device position during Step 3 capture
        if (Step3_XYCapture.Visibility == Visibility.Visible && !string.IsNullOrEmpty(_step3DeviceName))
        {
            var secPos = _context.MotionManager.GetCurrentPosition(_step3DeviceName);
            if (secPos != null)
                Step3DevicePos.Text = $"{_step3DeviceName}: X={secPos.X:F4} Y={secPos.Y:F4} Z={secPos.Z:F4} U={secPos.U:F4} V={secPos.V:F4} W={secPos.W:F4}";
        }
    }

    // --- Device & Set combos ---

    private void PopulateDeviceCombo()
    {
        DeviceCombo.Items.Clear();
        if (_context.IsInitialized)
        {
            foreach (var name in _context.MotionManager.GetAllDeviceNames())
                DeviceCombo.Items.Add(name);
        }
        if (DeviceCombo.Items.Count > 0)
            DeviceCombo.SelectedIndex = 0;
    }

    private void DeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

    private void LoadDataset(string name)
    {
        _currentSet = _store.Load(name);
        if (_currentSet == null) return;

        if (!string.IsNullOrEmpty(_currentSet.DeviceName))
        {
            for (int i = 0; i < DeviceCombo.Items.Count; i++)
            {
                if (string.Equals(DeviceCombo.Items[i] as string, _currentSet.DeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    DeviceCombo.SelectedIndex = i;
                    break;
                }
            }
        }

        RefreshFromSet();
        UpdateDatasetInfo();
    }

    private void UpdateDatasetInfo()
    {
        if (_currentSet == null)
        {
            DatasetNameText.Text = "(no dataset loaded)";
            DatasetPathText.Text = "";
            return;
        }

        DatasetNameText.Text = _currentSet.Name;
        DatasetPathText.Text = _store.GetFilePath(_currentSet.Name);
    }

    private void RefreshFromSet()
    {
        _bondingPoints.Clear();
        _refPoints.Clear();
        _correctedPositions.Clear();

        if (_currentSet == null) return;

        foreach (var bp in _currentSet.BondingPoints)
            _bondingPoints.Add(bp);
        foreach (var rp in _currentSet.ReferencePoints)
            _refPoints.Add(rp);

        UpdateFrameStatus();
        UpdateStepIndicators();
    }

    private void UpdateFrameStatus()
    {
        if (_currentSet?.Valid == true)
            FrameStatusText.Text = $"Frame OK\nOrigin: ({_currentSet.OriginX:F3},{_currentSet.OriginY:F3})\nXaxis: ({_currentSet.XAxisX:F4},{_currentSet.XAxisY:F4})";
        else
            FrameStatusText.Text = "Frame: not computed";
    }

    // --- Training set CRUD ---

    private void LoadSet_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new DatasetDialog(_store, _currentSet?.Name)
        {
            Owner = Window.GetWindow(this)
        };
        if (dlg.ShowDialog() == true && dlg.SelectedName != null)
        {
            LoadDataset(dlg.SelectedName);
            AppendLog($"Loaded dataset '{dlg.SelectedName}'");
        }
    }

    private void NewSet_Click(object sender, RoutedEventArgs e)
    {
        var name = PromptForName("New Training Set", "Enter name:");
        if (string.IsNullOrWhiteSpace(name)) return;

        var device = DeviceCombo.SelectedItem as string ?? "gantry-main";
        _currentSet = new TrainingSet { Name = name, DeviceName = device };
        _store.Save(_currentSet);
        RefreshFromSet();
        UpdateDatasetInfo();
        AppendLog($"Created new dataset '{name}'");
    }

    private void SaveSet_Click(object sender, RoutedEventArgs e)
    {
        if (_currentSet == null) return;
        _store.Save(_currentSet);
        AppendLog($"Saved training set '{_currentSet.Name}'");
    }

    private void DeleteSet_Click(object sender, RoutedEventArgs e)
    {
        if (_currentSet == null) return;
        var name = _currentSet.Name;

        if (MessageBox.Show($"Delete training set '{name}'?", "Confirm",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        _store.Delete(name);
        _currentSet = null;
        RefreshFromSet();
        UpdateDatasetInfo();
        AppendLog($"Deleted dataset '{name}'");
    }

    // --- Nav & Step switching ---

    private void NavButton_Click(object sender, RoutedEventArgs e)
    {
        // Only one trainer type for now
    }

    private void StepButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;
        if (int.TryParse(tag, out int step))
            SwitchToStep(step);
    }

    private void SwitchToStep(int step)
    {
        // Guard: warn if leaving a step with incomplete capture in progress
        if (step != _activeStep && IsCapturingInProgress())
        {
            var result = MessageBox.Show(
                "Data capture is not complete. Discard current capture and switch?",
                "Incomplete Data", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
                return;

            // Discard in-progress capture
            if (_activeStep == 1)
            {
                _capturedCorners.Clear();
                _cornerDisplayItems.Clear();
            }
            else if (_activeStep == 2)
            {
                _capturedRefCorners.Clear();
                _refCornerDisplayItems.Clear();
            }
            else if (_activeStep == 3)
            {
                _step3ObsCorners.Clear();
                _step3ObsCornerDisplay.Clear();
            }
        }

        _activeStep = step;
        WizardStep1.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        WizardStep2.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        WizardStep3.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
        WizardStep4.Visibility = step == 4 ? Visibility.Visible : Visibility.Collapsed;
        HighlightStepButton(step);

        // Reset sub-step views
        if (step == 1) ShowStep1Start();
        if (step == 2) ShowStep2Start();
        if (step == 3) ShowStep3Start();
    }

    private bool IsCapturingInProgress()
    {
        if (_activeStep == 1 &&
            (Step1_Capture.Visibility == Visibility.Visible || Step1_Review.Visibility == Visibility.Visible))
            return true;
        if (_activeStep == 2 &&
            (Step2_Capture.Visibility == Visibility.Visible || Step2_Review.Visibility == Visibility.Visible))
            return true;
        if (_activeStep == 3 &&
            (Step3_XYCapture.Visibility == Visibility.Visible || Step3_ZTeach.Visibility == Visibility.Visible))
            return true;
        return false;
    }

    private void HighlightStepButton(int step)
    {
        var active = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4));
        var inactive = (Brush)FindResource("Theme.BackgroundAltBrush");

        StepBtn1.Background = step == 1 ? active : inactive;
        StepBtn2.Background = step == 2 ? active : inactive;
        StepBtn3.Background = step == 3 ? active : inactive;
        StepBtn4.Background = step == 4 ? active : inactive;

        var activeFg = Brushes.White;
        var inactiveFg = (Brush)FindResource("Theme.ForegroundBrush");
        StepBtn1.Foreground = step == 1 ? activeFg : inactiveFg;
        StepBtn2.Foreground = step == 2 ? activeFg : inactiveFg;
        StepBtn3.Foreground = step == 3 ? activeFg : inactiveFg;
        StepBtn4.Foreground = step == 4 ? activeFg : inactiveFg;

        UpdateStepIndicators();
    }

    private void UpdateStepIndicators()
    {
        var green = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
        var gray = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
        var greenStroke = new SolidColorBrush(Color.FromRgb(0x66, 0xBB, 0x6A));
        var grayStroke = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77));

        // Step 1 is good when at least 1 bonding point exists
        bool step1Ok = _bondingPoints.Count > 0 && _bondingPoints.All(bp => bp.Valid);
        StepIndicator1.Fill = step1Ok ? green : gray;
        StepIndicator1.Stroke = step1Ok ? greenStroke : grayStroke;

        // Step 2 is good when at least 2 valid reference points exist
        bool step2Ok = _refPoints.Count(rp => rp.Valid) >= 2;
        StepIndicator2.Fill = step2Ok ? green : gray;
        StepIndicator2.Stroke = step2Ok ? greenStroke : grayStroke;

        // Step 3 is good when at least 1 bonding point has secondary teaching
        bool step3Ok = _bondingPoints.Any(bp => bp.SecondaryTeaching?.HasXYTeaching == true);
        StepIndicator3.Fill = step3Ok ? green : gray;
        StepIndicator3.Stroke = step3Ok ? greenStroke : grayStroke;
    }

    // ============================================================
    // STEP 1: BONDING POSITIONS WIZARD
    // ============================================================

    private void ShowStep1Start()
    {
        Step1_Start.Visibility = Visibility.Visible;
        Step1_Capture.Visibility = Visibility.Collapsed;
        Step1_Review.Visibility = Visibility.Collapsed;
    }

    private void BeginCapture_Click(object sender, RoutedEventArgs e)
    {
        if (_currentSet == null) { ShowNoSet(); return; }

        var name = BondNameBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            name = $"Bond{_bondingPoints.Count + 1}";
            BondNameBox.Text = name;
        }

        _pendingBondName = name;
        _cornerTarget = CornerCountCombo.SelectedIndex == 0 ? 2 : 3;
        _cornerCurrent = 1;
        _centerImagePath = null;
        _capturedCorners.Clear();
        _cornerDisplayItems.Clear();

        PopulateCameraCombo(CameraCombo);
        ShowCaptureView();
    }

    private void ShowCaptureView()
    {
        Step1_Start.Visibility = Visibility.Collapsed;
        Step1_Capture.Visibility = Visibility.Visible;
        Step1_Review.Visibility = Visibility.Collapsed;

        CaptureTitle.Text = $"Capture Point {_cornerCurrent} of {_cornerTarget}";
        CaptureInstruction.Text = $"Jog to perimeter Point {_cornerCurrent}, then click Capture.";
    }

    private void CaptureCorner_Click(object sender, RoutedEventArgs e)
    {
        var pos = GetCurrentPos();
        if (pos == null) { AppendLog("Error: no position available"); return; }

        var corner = new BondingCorner
        {
            X = pos.X,
            Y = pos.Y,
            Z = pos.Z
        };

        // Try camera capture
        string? imagePath = CaptureImageToTrainerDir(CameraCombo, corner.Uid);
        if (imagePath != null)
            corner = corner with { ImagePath = imagePath };

        _capturedCorners.Add(corner);
        _cornerDisplayItems.Add(new CornerDisplayItem
        {
            Label = $"Point {_cornerCurrent}",
            Coords = $"X={pos.X:F4}  Y={pos.Y:F4}  Z={pos.Z:F4}",
            ImagePath = imagePath
        });

        AppendLog($"Captured Point {_cornerCurrent}: ({pos.X:F4},{pos.Y:F4},{pos.Z:F4})");

        _cornerCurrent++;
        if (_cornerCurrent > _cornerTarget)
        {
            MoveToCenterAndCapture();
        }
        else
        {
            ShowCaptureView();
        }
    }

    private void CancelCapture_Click(object sender, RoutedEventArgs e)
    {
        _capturedCorners.Clear();
        _cornerDisplayItems.Clear();
        ShowStep1Start();
    }

    private async void MoveToCenterAndCapture()
    {
        var (cx, cy, cz) = TrainerMath.ComputeCenter(_capturedCorners);

        // Move to computed center
        var device = DeviceCombo.SelectedItem as string;
        if (!string.IsNullOrEmpty(device) && _context.IsInitialized)
        {
            AppendLog($"Moving to center ({cx:F4},{cy:F4},{cz:F4})...");
            CaptureTitle.Text = "Moving to center...";
            CaptureInstruction.Text = $"Moving to computed center ({cx:F4},{cy:F4}) to capture reference image.";

            var target = new PositionStruct { X = cx, Y = cy, Z = cz };
            _context.MotionManager.MoveToAbs6(device, target, blocking: false);

            // Wait for motion to settle
            await Task.Delay(1500);
        }

        // Capture center reference image
        _centerImagePath = null;
        var uid = Guid.NewGuid().ToString();
        var imgPath = CaptureImageToTrainerDir(CameraCombo, uid);
        if (imgPath != null)
        {
            _centerImagePath = imgPath;
            AppendLog($"Center reference image saved: {imgPath}");
        }
        else
        {
            AppendLog("Center reference image capture skipped/failed");
        }

        ShowStep1Review();
    }

    private void ShowStep1Review()
    {
        Step1_Start.Visibility = Visibility.Collapsed;
        Step1_Capture.Visibility = Visibility.Collapsed;
        Step1_Review.Visibility = Visibility.Visible;

        var (cx, cy, cz) = TrainerMath.ComputeCenter(_capturedCorners);
        ReviewCenterText.Text = $"Computed Center:  X={cx:F4}  Y={cy:F4}  Z={cz:F4}";

        var reviewItems = new ObservableCollection<CornerDisplayItem>();
        for (int i = 0; i < _capturedCorners.Count; i++)
        {
            var c = _capturedCorners[i];
            reviewItems.Add(new CornerDisplayItem
            {
                Label = $"Point {i + 1}",
                Coords = $"X={c.X:F4}  Y={c.Y:F4}  Z={c.Z:F4}",
                ImagePath = c.ImagePath
            });
        }
        ReviewCornersList.ItemsSource = reviewItems;
    }

    private void CompleteCapture_Click(object sender, RoutedEventArgs e)
    {
        var (cx, cy, cz) = TrainerMath.ComputeCenter(_capturedCorners);

        var bond = new BondingPoint
        {
            Name = _pendingBondName,
            Corners = new List<BondingCorner>(_capturedCorners),
            GlobalX = cx,
            GlobalY = cy,
            GlobalZ = cz,
            CenterImagePath = _centerImagePath,
            Valid = true
        };

        _bondingPoints.Add(bond);
        SyncSetFromUI();
        AppendLog($"Added '{bond.Name}' center=({cx:F4},{cy:F4}) with {_capturedCorners.Count} corners");

        _capturedCorners.Clear();
        _cornerDisplayItems.Clear();
        BondNameBox.Text = "";
        ShowStep1Start();
    }

    private void DiscardCapture_Click(object sender, RoutedEventArgs e)
    {
        _capturedCorners.Clear();
        _cornerDisplayItems.Clear();
        ShowStep1Start();
    }

    private void GoToBondCenter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not BondingPoint bp) return;
        var device = DeviceCombo.SelectedItem as string;
        if (string.IsNullOrEmpty(device) || !_context.IsInitialized) return;

        var target = new PositionStruct { X = bp.GlobalX, Y = bp.GlobalY, Z = bp.GlobalZ };
        _context.MotionManager.MoveToAbs6(device, target, blocking: false);
        AppendLog($"Moving to '{bp.Name}' center ({bp.GlobalX:F4},{bp.GlobalY:F4},{bp.GlobalZ:F4})");
    }

    private void ReteachBond_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not BondingPoint bp) return;
        int idx = _bondingPoints.IndexOf(bp);
        if (idx < 0) return;

        _pendingBondName = bp.Name;
        _cornerTarget = bp.Corners.Count >= 3 ? 3 : 2;
        _cornerCurrent = 1;
        _capturedCorners.Clear();
        _cornerDisplayItems.Clear();

        _bondingPoints.RemoveAt(idx);
        SyncSetFromUI();

        PopulateCameraCombo(CameraCombo);
        ShowCaptureView();
    }

    private void DeleteBond_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not BondingPoint bp) return;
        _bondingPoints.Remove(bp);
        SyncSetFromUI();
    }

    // ============================================================
    // STEP 2: REFERENCE POINTS WIZARD
    // ============================================================

    private void ShowStep2Start()
    {
        Step2_Start.Visibility = Visibility.Visible;
        Step2_Capture.Visibility = Visibility.Collapsed;
        Step2_Review.Visibility = Visibility.Collapsed;
    }

    private void TeachRefWizard_Click(object sender, RoutedEventArgs e)
    {
        if (_currentSet == null) { ShowNoSet(); return; }

        _pendingRefIndex = _refPoints.Count + 1;
        _refCornerTarget = RefCornerCountCombo.SelectedIndex == 0 ? 2 : 3;
        _refCornerCurrent = 1;
        _refCenterImagePath = null;
        _capturedRefCorners.Clear();
        _refCornerDisplayItems.Clear();

        PopulateCameraCombo(RefCameraCombo);
        ShowRefCaptureView();
    }

    private void ShowRefCaptureView()
    {
        Step2_Start.Visibility = Visibility.Collapsed;
        Step2_Capture.Visibility = Visibility.Visible;
        Step2_Review.Visibility = Visibility.Collapsed;

        RefCaptureTitle.Text = $"Teach Reference {_pendingRefIndex} — Point {_refCornerCurrent} of {_refCornerTarget}";
        RefCaptureInstruction.Text = $"Jog to perimeter Point {_refCornerCurrent} of Reference {_pendingRefIndex}, then click Capture.";
    }

    private void CaptureRef_Click(object sender, RoutedEventArgs e)
    {
        var pos = GetCurrentPos();
        if (pos == null) { AppendLog("Error: no position available"); return; }

        var corner = new ReferenceCorner
        {
            X = pos.X,
            Y = pos.Y,
            Z = pos.Z
        };

        string? imagePath = CaptureImageToTrainerDir(RefCameraCombo, corner.Uid);
        if (imagePath != null)
            corner = corner with { ImagePath = imagePath };

        _capturedRefCorners.Add(corner);
        _refCornerDisplayItems.Add(new CornerDisplayItem
        {
            Label = $"Point {_refCornerCurrent}",
            Coords = $"X={pos.X:F4}  Y={pos.Y:F4}  Z={pos.Z:F4}",
            ImagePath = imagePath
        });

        AppendLog($"Ref{_pendingRefIndex} Point {_refCornerCurrent}: ({pos.X:F4},{pos.Y:F4},{pos.Z:F4})");

        _refCornerCurrent++;
        if (_refCornerCurrent > _refCornerTarget)
        {
            MoveToRefCenterAndCapture();
        }
        else
        {
            ShowRefCaptureView();
        }
    }

    private async void MoveToRefCenterAndCapture()
    {
        var (cx, cy, cz) = TrainerMath.ComputeRefCenter(_capturedRefCorners);

        var device = DeviceCombo.SelectedItem as string;
        if (!string.IsNullOrEmpty(device) && _context.IsInitialized)
        {
            AppendLog($"Moving to Ref{_pendingRefIndex} center ({cx:F4},{cy:F4},{cz:F4})...");
            RefCaptureTitle.Text = "Moving to center...";
            RefCaptureInstruction.Text = $"Moving to computed center ({cx:F4},{cy:F4}) to capture reference image.";

            var target = new PositionStruct { X = cx, Y = cy, Z = cz };
            _context.MotionManager.MoveToAbs6(device, target, blocking: false);

            await Task.Delay(1500);
        }

        _refCenterImagePath = null;
        var uid = Guid.NewGuid().ToString();
        var imgPath = CaptureImageToTrainerDir(RefCameraCombo, uid);
        if (imgPath != null)
        {
            _refCenterImagePath = imgPath;
            AppendLog($"Ref center image saved: {imgPath}");
        }

        ShowStep2Review();
    }

    private void ShowStep2Review()
    {
        Step2_Start.Visibility = Visibility.Collapsed;
        Step2_Capture.Visibility = Visibility.Collapsed;
        Step2_Review.Visibility = Visibility.Visible;

        var (cx, cy, cz) = TrainerMath.ComputeRefCenter(_capturedRefCorners);
        RefReviewCenterText.Text = $"Computed Center:  X={cx:F4}  Y={cy:F4}  Z={cz:F4}";

        var reviewItems = new ObservableCollection<CornerDisplayItem>();
        for (int i = 0; i < _capturedRefCorners.Count; i++)
        {
            var c = _capturedRefCorners[i];
            reviewItems.Add(new CornerDisplayItem
            {
                Label = $"Point {i + 1}",
                Coords = $"X={c.X:F4}  Y={c.Y:F4}  Z={c.Z:F4}",
                ImagePath = c.ImagePath
            });
        }
        RefReviewCornersList.ItemsSource = reviewItems;
    }

    private void CompleteRefCapture_Click(object sender, RoutedEventArgs e)
    {
        var (cx, cy, cz) = TrainerMath.ComputeRefCenter(_capturedRefCorners);

        var rp = new ReferencePoint
        {
            Name = $"Ref{_pendingRefIndex}",
            Corners = new List<ReferenceCorner>(_capturedRefCorners),
            GlobalX = cx,
            GlobalY = cy,
            GlobalZ = cz,
            CenterImagePath = _refCenterImagePath,
            Valid = true
        };

        _refPoints.Add(rp);
        SyncSetFromUI();
        AppendLog($"Added '{rp.Name}' center=({cx:F4},{cy:F4}) with {_capturedRefCorners.Count} corners");

        _capturedRefCorners.Clear();
        _refCornerDisplayItems.Clear();
        ShowStep2Start();
    }

    private void DiscardRefCapture_Click(object sender, RoutedEventArgs e)
    {
        _capturedRefCorners.Clear();
        _refCornerDisplayItems.Clear();
        ShowStep2Start();
    }

    private void CancelRefCapture_Click(object sender, RoutedEventArgs e)
    {
        _capturedRefCorners.Clear();
        _refCornerDisplayItems.Clear();
        ShowStep2Start();
    }

    private void GoToRefCenter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not ReferencePoint rp) return;
        var device = DeviceCombo.SelectedItem as string;
        if (string.IsNullOrEmpty(device) || !_context.IsInitialized) return;

        var target = new PositionStruct { X = rp.GlobalX, Y = rp.GlobalY, Z = rp.GlobalZ };
        _context.MotionManager.MoveToAbs6(device, target, blocking: false);
        AppendLog($"Moving to '{rp.Name}' center ({rp.GlobalX:F4},{rp.GlobalY:F4},{rp.GlobalZ:F4})");
    }

    private void ReteachRef_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not ReferencePoint rp) return;
        int idx = _refPoints.IndexOf(rp);
        if (idx < 0) return;

        _pendingRefIndex = int.TryParse(rp.Name.Replace("Ref", ""), out var n) ? n : _refPoints.Count;
        _refCornerTarget = rp.Corners.Count >= 3 ? 3 : 2;
        _refCornerCurrent = 1;
        _capturedRefCorners.Clear();
        _refCornerDisplayItems.Clear();

        _refPoints.RemoveAt(idx);
        SyncSetFromUI();

        PopulateCameraCombo(RefCameraCombo);
        ShowRefCaptureView();
    }

    private void DeleteRef_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not ReferencePoint rp) return;
        _refPoints.Remove(rp);
        SyncSetFromUI();
    }

    private void ComputeFrame_Click(object sender, RoutedEventArgs e)
    {
        if (_currentSet == null) { ShowNoSet(); return; }

        var validRefs = _refPoints.Where(r => r.Valid).ToList();
        if (validRefs.Count < 2)
        {
            MessageBox.Show("Need at least 2 valid reference points to compute frame.",
                "Trainer", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var ref1 = validRefs[0];
        var ref2 = validRefs[1];
        var (originX, originY, xAxisX, xAxisY, yAxisX, yAxisY) =
            TrainerMath.ComputeCoordinateAxes(ref1, ref2);

        // Convert all bonding points to local coords
        var updatedBonds = new List<BondingPoint>();
        for (int i = 0; i < _bondingPoints.Count; i++)
        {
            var bp = _bondingPoints[i];
            var (localX, localY) = TrainerMath.WorldToLocal(
                bp.GlobalX, bp.GlobalY, originX, originY, xAxisX, xAxisY, yAxisX, yAxisY);
            updatedBonds.Add(bp with { LocalX = localX, LocalY = localY });
        }

        _bondingPoints.Clear();
        foreach (var bp in updatedBonds)
            _bondingPoints.Add(bp);

        _currentSet = _currentSet with
        {
            OriginX = originX, OriginY = originY,
            XAxisX = xAxisX, XAxisY = xAxisY,
            YAxisX = yAxisX, YAxisY = yAxisY,
            ReferencePoints = _refPoints.ToList(),
            BondingPoints = _bondingPoints.ToList(),
            Valid = true
        };
        _store.Save(_currentSet);
        UpdateFrameStatus();
        AppendLog($"Frame computed. Origin=({originX:F4},{originY:F4}), Xaxis=({xAxisX:F4},{xAxisY:F4})");
    }

    private void CompleteRefs_Click(object sender, RoutedEventArgs e)
    {
        if (_currentSet == null) return;
        SyncSetFromUI();
        _store.Save(_currentSet);
        AppendLog("Reference points saved.");
    }

    // ============================================================
    // STEP 3: SECONDARY DEVICES
    // ============================================================

    private void ShowStep3Start()
    {
        Step3_Start.Visibility = Visibility.Visible;
        Step3_XYCapture.Visibility = Visibility.Collapsed;
        Step3_XYReview.Visibility = Visibility.Collapsed;
        Step3_ZTeach.Visibility = Visibility.Collapsed;

        RefreshStep3Grid();
    }

    private void RefreshStep3Grid()
    {
        _step3Rows.Clear();
        var primaryDevice = DeviceCombo.SelectedItem as string ?? "";

        // Get secondary device names
        var secondaryDevices = new List<string>();
        if (_context.IsInitialized)
        {
            foreach (var name in _context.MotionManager.GetAllDeviceNames())
            {
                if (!string.Equals(name, primaryDevice, StringComparison.OrdinalIgnoreCase))
                    secondaryDevices.Add(name);
            }
        }

        for (int i = 0; i < _bondingPoints.Count; i++)
        {
            var bp = _bondingPoints[i];
            var st = bp.SecondaryTeaching;
            _step3Rows.Add(new Step3AssignmentRow
            {
                BondIndex = i,
                BondName = bp.Name,
                DeviceName = st?.DeviceName ?? (secondaryDevices.Count > 0 ? secondaryDevices[0] : ""),
                Method = st?.Method ?? "2-point",
                XYStatus = st?.HasXYTeaching == true ? "Taught" : "Not taught",
                ZStatus = st?.HasZTeaching == true ? $"Zrel={st.ZRel:F4}" : "Not taught",
                CanTeachZ = st?.HasXYTeaching == true
            });
        }
    }

    private void Step3_Teach_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not Step3AssignmentRow row) return;
        if (_currentSet == null) { ShowNoSet(); return; }
        if (string.IsNullOrEmpty(row.DeviceName))
        {
            MessageBox.Show("No secondary device available.", "Trainer", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _step3BondIndex = row.BondIndex;
        _step3DeviceName = row.DeviceName;
        _step3Method = row.Method;
        _step3DevicePos = null;
        _step3ObsCornerTarget = _step3Method == "3-point" ? 3 : 2;
        _step3ObsCornerCurrent = 1;
        _step3ObsCorners.Clear();
        _step3ObsCornerDisplay.Clear();
        _step3CenterImage = null;

        PopulateCameraCombo(Step3CameraCombo);
        ShowStep3XYCapture_Phase1();
    }

    private void ShowStep3XYCapture_Phase1()
    {
        Step3_Start.Visibility = Visibility.Collapsed;
        Step3_XYCapture.Visibility = Visibility.Visible;
        Step3_XYReview.Visibility = Visibility.Collapsed;
        Step3_ZTeach.Visibility = Visibility.Collapsed;

        Step3CaptureTitle.Text = $"Position Secondary Device: {_step3DeviceName}";
        Step3CaptureInstruction.Text = $"Jog '{_step3DeviceName}' to the desired position for bond '{_bondingPoints[_step3BondIndex].Name}', then click 'Confirm Device Pos'.";
        Step3ConfirmDevicePosBtn.Visibility = Visibility.Visible;
        Step3CaptureObsBtn.Visibility = Visibility.Collapsed;
        Step3DevicePos.Text = "";
    }

    private void Step3_ConfirmDevicePos_Click(object sender, RoutedEventArgs e)
    {
        if (!_context.IsInitialized) return;

        var pos = _context.MotionManager.GetCurrentPosition(_step3DeviceName);
        if (pos == null)
        {
            AppendLog($"Error: cannot read position from '{_step3DeviceName}'");
            return;
        }

        _step3DevicePos = new DeviceSnapshot
        {
            DeviceName = _step3DeviceName,
            X = pos.X, Y = pos.Y, Z = pos.Z,
            U = pos.U, V = pos.V, W = pos.W
        };

        AppendLog($"Secondary device pos: X={pos.X:F4} Y={pos.Y:F4} Z={pos.Z:F4} U={pos.U:F4} V={pos.V:F4} W={pos.W:F4}");

        // Switch to Phase 2: gantry observation corners
        ShowStep3XYCapture_Phase2();
    }

    private void ShowStep3XYCapture_Phase2()
    {
        Step3CaptureTitle.Text = $"Observe '{_bondingPoints[_step3BondIndex].Name}' — Corner {_step3ObsCornerCurrent} of {_step3ObsCornerTarget}";
        Step3CaptureInstruction.Text = $"Now jog the GANTRY to observe the secondary device's feature. Capture {_step3ObsCornerTarget} observation corners.";
        Step3ConfirmDevicePosBtn.Visibility = Visibility.Collapsed;
        Step3CaptureObsBtn.Visibility = Visibility.Visible;
    }

    private void Step3_CaptureObs_Click(object sender, RoutedEventArgs e)
    {
        var pos = GetCurrentPos();
        if (pos == null) { AppendLog("Error: no gantry position available"); return; }

        var corner = new BondingCorner
        {
            X = pos.X,
            Y = pos.Y,
            Z = pos.Z
        };

        string? imagePath = CaptureImageToTrainerDir(Step3CameraCombo, corner.Uid);
        if (imagePath != null)
            corner = corner with { ImagePath = imagePath };

        _step3ObsCorners.Add(corner);
        _step3ObsCornerDisplay.Add(new CornerDisplayItem
        {
            Label = $"Obs {_step3ObsCornerCurrent}",
            Coords = $"X={pos.X:F4}  Y={pos.Y:F4}  Z={pos.Z:F4}",
            ImagePath = imagePath
        });

        AppendLog($"Obs corner {_step3ObsCornerCurrent}: ({pos.X:F4},{pos.Y:F4},{pos.Z:F4})");

        _step3ObsCornerCurrent++;
        if (_step3ObsCornerCurrent > _step3ObsCornerTarget)
        {
            Step3_MoveToObsCenterAndCapture();
        }
        else
        {
            Step3CaptureTitle.Text = $"Observe '{_bondingPoints[_step3BondIndex].Name}' — Corner {_step3ObsCornerCurrent} of {_step3ObsCornerTarget}";
        }
    }

    private async void Step3_MoveToObsCenterAndCapture()
    {
        var (cx, cy, cz) = TrainerMath.ComputeCenter(_step3ObsCorners);

        var device = DeviceCombo.SelectedItem as string;
        if (!string.IsNullOrEmpty(device) && _context.IsInitialized)
        {
            AppendLog($"Moving gantry to observation center ({cx:F4},{cy:F4},{cz:F4})...");
            Step3CaptureTitle.Text = "Moving to observation center...";

            var target = new PositionStruct { X = cx, Y = cy, Z = cz };
            _context.MotionManager.MoveToAbs6(device, target, blocking: false);
            await Task.Delay(1500);
        }

        _step3CenterImage = null;
        var uid = Guid.NewGuid().ToString();
        var imgPath = CaptureImageToTrainerDir(Step3CameraCombo, uid);
        if (imgPath != null)
        {
            _step3CenterImage = imgPath;
            AppendLog($"Observation center image saved: {imgPath}");
        }

        ShowStep3XYReview(cx, cy, cz);
    }

    private void ShowStep3XYReview(double cx, double cy, double cz)
    {
        Step3_Start.Visibility = Visibility.Collapsed;
        Step3_XYCapture.Visibility = Visibility.Collapsed;
        Step3_XYReview.Visibility = Visibility.Visible;
        Step3_ZTeach.Visibility = Visibility.Collapsed;

        Step3ReviewDeviceText.Text = $"Device: {_step3DeviceName}  Pos: X={_step3DevicePos!.X:F4} Y={_step3DevicePos.Y:F4} Z={_step3DevicePos.Z:F4}";
        Step3ReviewObsCenterText.Text = $"Observation Center:  X={cx:F4}  Y={cy:F4}  Z={cz:F4}";
    }

    private void Step3_CompleteXY_Click(object sender, RoutedEventArgs e)
    {
        if (_step3DevicePos == null) return;

        var (cx, cy, cz) = TrainerMath.ComputeCenter(_step3ObsCorners);

        var teaching = new SecondaryTeaching
        {
            DeviceName = _step3DeviceName,
            Method = _step3Method,
            DevicePosition = _step3DevicePos,
            ObservationCorners = new List<BondingCorner>(_step3ObsCorners),
            ObservationCenterX = cx,
            ObservationCenterY = cy,
            ObservationCenterZ = cz,
            CenterImagePath = _step3CenterImage
        };

        // Preserve existing Z teaching if any
        var existingSt = _bondingPoints[_step3BondIndex].SecondaryTeaching;
        if (existingSt?.HasZTeaching == true)
        {
            teaching = teaching with
            {
                ZObj = existingSt.ZObj,
                ClearanceT = existingSt.ClearanceT,
                ZBond = existingSt.ZBond,
                ZRel = existingSt.ZRel
            };
        }

        var bp = _bondingPoints[_step3BondIndex] with { SecondaryTeaching = teaching };
        _bondingPoints[_step3BondIndex] = bp;
        SyncSetFromUI();

        AppendLog($"Secondary XY teaching saved for '{bp.Name}' → {_step3DeviceName}");

        _step3ObsCorners.Clear();
        _step3ObsCornerDisplay.Clear();
        ShowStep3Start();
    }

    private void Step3_DiscardXY_Click(object sender, RoutedEventArgs e)
    {
        _step3ObsCorners.Clear();
        _step3ObsCornerDisplay.Clear();
        ShowStep3Start();
    }

    private void Step3_CancelXY_Click(object sender, RoutedEventArgs e)
    {
        _step3ObsCorners.Clear();
        _step3ObsCornerDisplay.Clear();
        ShowStep3Start();
    }

    private void Step3_Clear_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not Step3AssignmentRow row) return;
        if (row.BondIndex < 0 || row.BondIndex >= _bondingPoints.Count) return;

        var bp = _bondingPoints[row.BondIndex] with { SecondaryTeaching = null };
        _bondingPoints[row.BondIndex] = bp;
        SyncSetFromUI();
        RefreshStep3Grid();
        AppendLog($"Cleared secondary teaching for '{bp.Name}'");
    }

    // --- Step 3: Z Teaching ---

    private void Step3_ZTeach_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not Step3AssignmentRow row) return;
        if (row.BondIndex < 0 || row.BondIndex >= _bondingPoints.Count) return;
        if (_bondingPoints[row.BondIndex].SecondaryTeaching?.HasXYTeaching != true)
        {
            MessageBox.Show("Teach XY first.", "Trainer", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _step3BondIndex = row.BondIndex;
        _step3DeviceName = row.DeviceName;
        _step3ZSubStep = 0;
        _step3ZObj = 0;
        _step3ClearanceT = 0;
        _step3ZBond = 0;

        ShowStep3ZTeachView();
    }

    private void ShowStep3ZTeachView()
    {
        Step3_Start.Visibility = Visibility.Collapsed;
        Step3_XYCapture.Visibility = Visibility.Collapsed;
        Step3_XYReview.Visibility = Visibility.Collapsed;
        Step3_ZTeach.Visibility = Visibility.Visible;

        UpdateStep3ZView();
    }

    private void UpdateStep3ZView()
    {
        var bondName = _bondingPoints[_step3BondIndex].Name;

        Step3ZRegisterObjBtn.Visibility = _step3ZSubStep == 0 ? Visibility.Visible : Visibility.Collapsed;
        Step3ZEnterTBtn.Visibility = _step3ZSubStep == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step3ZRegisterBondBtn.Visibility = _step3ZSubStep == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step3ZCompleteBtn.Visibility = _step3ZSubStep == 3 ? Visibility.Visible : Visibility.Collapsed;

        switch (_step3ZSubStep)
        {
            case 0:
                Step3ZTitle.Text = $"Z Teaching: {bondName} — Register Zobj";
                Step3ZInstruction.Text = "Move gantry Z to the object surface (focus on the feature), then click 'Register Zobj'.";
                Step3ZStatus.Text = "";
                break;
            case 1:
                Step3ZTitle.Text = $"Z Teaching: {bondName} — Enter Clearance";
                Step3ZInstruction.Text = $"Zobj = {_step3ZObj:F4}. Enter the clearance distance T (positive = above object).";
                Step3ZStatus.Text = $"Zobj = {_step3ZObj:F4}";
                break;
            case 2:
                Step3ZTitle.Text = $"Z Teaching: {bondName} — Register Zbond";
                Step3ZInstruction.Text = $"Zobj = {_step3ZObj:F4}, T = {_step3ClearanceT:F4}. Move gantry Z to the bonding surface, then click 'Register Zbond'.";
                Step3ZStatus.Text = $"Zobj = {_step3ZObj:F4}  T = {_step3ClearanceT:F4}";
                break;
            case 3:
                var zRel = _step3ZBond - _step3ZObj + _step3ClearanceT;
                Step3ZTitle.Text = $"Z Teaching: {bondName} — Review";
                Step3ZInstruction.Text = "Review the Z parameters and click 'Complete Z' to save.";
                Step3ZStatus.Text = $"Zobj = {_step3ZObj:F4}  T = {_step3ClearanceT:F4}  Zbond = {_step3ZBond:F4}  Zrel = {zRel:F4}";
                break;
        }
    }

    private void Step3_RegisterZObj_Click(object sender, RoutedEventArgs e)
    {
        var pos = GetCurrentPos();
        if (pos == null) { AppendLog("Error: no gantry position"); return; }

        _step3ZObj = pos.Z;
        AppendLog($"Zobj registered: {_step3ZObj:F4}");
        _step3ZSubStep = 1;
        UpdateStep3ZView();
    }

    private void Step3_EnterT_Click(object sender, RoutedEventArgs e)
    {
        var input = PromptForName("Clearance T", "Enter clearance distance T (mm):");
        if (string.IsNullOrWhiteSpace(input)) return;
        if (!double.TryParse(input, out var t))
        {
            MessageBox.Show("Invalid number.", "Trainer", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _step3ClearanceT = t;
        AppendLog($"Clearance T = {_step3ClearanceT:F4}");
        _step3ZSubStep = 2;
        UpdateStep3ZView();
    }

    private void Step3_RegisterZBond_Click(object sender, RoutedEventArgs e)
    {
        var pos = GetCurrentPos();
        if (pos == null) { AppendLog("Error: no gantry position"); return; }

        _step3ZBond = pos.Z;
        AppendLog($"Zbond registered: {_step3ZBond:F4}");
        _step3ZSubStep = 3;
        UpdateStep3ZView();
    }

    private void Step3_CompleteZ_Click(object sender, RoutedEventArgs e)
    {
        var zRel = _step3ZBond - _step3ZObj + _step3ClearanceT;
        var bp = _bondingPoints[_step3BondIndex];
        var st = bp.SecondaryTeaching;
        if (st == null) return;

        st = st with
        {
            ZObj = _step3ZObj,
            ClearanceT = _step3ClearanceT,
            ZBond = _step3ZBond,
            ZRel = zRel
        };

        _bondingPoints[_step3BondIndex] = bp with { SecondaryTeaching = st };
        SyncSetFromUI();

        AppendLog($"Z teaching saved for '{bp.Name}': Zrel={zRel:F4}");
        ShowStep3Start();
    }

    private void Step3_CancelZ_Click(object sender, RoutedEventArgs e)
    {
        ShowStep3Start();
    }

    // ============================================================
    // STEP 4: RETRACE BONDING (RUN)
    // ============================================================

    private void StartAlignment_Click(object sender, RoutedEventArgs e)
    {
        if (_currentSet == null || !_currentSet.Valid)
        {
            MessageBox.Show("No valid training set. Complete Steps 1-2 first.",
                "Trainer", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _actualRefs = new List<ReferencePoint>();
        _alignRefIndex = 0;
        _correctedPositions.Clear();

        MoveToRef(0);
    }

    private void MoveToRef(int index)
    {
        if (_currentSet == null) return;
        var refs = _currentSet.ReferencePoints.Where(r => r.Valid).ToList();
        if (index >= refs.Count)
        {
            ComputeCorrections();
            RefThumbnailPanel.Visibility = Visibility.Collapsed;
            return;
        }

        _alignRefIndex = index;
        var rp = refs[index];
        AlignStatusText.Text = $"Jog to {rp.Name}, adjust, then Accept. Taught: ({rp.GlobalX:F4},{rp.GlobalY:F4})";
        AcceptRefBtn.IsEnabled = true;

        ShowRefThumbnail(rp);

        var device = DeviceCombo.SelectedItem as string;
        if (!string.IsNullOrEmpty(device) && _context.IsInitialized)
        {
            var target = new PositionStruct { X = rp.GlobalX, Y = rp.GlobalY, Z = rp.GlobalZ };
            _context.MotionManager.MoveToAbs6(device, target, blocking: false);
        }

        AppendLog($"Moving to {rp.Name} ({rp.GlobalX:F4},{rp.GlobalY:F4},{rp.GlobalZ:F4})");
    }

    private void ShowRefThumbnail(ReferencePoint rp)
    {
        var imgPath = rp.CenterImagePath ?? rp.ImagePath;
        if (!string.IsNullOrEmpty(imgPath) && File.Exists(imgPath))
        {
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.UriSource = new Uri(imgPath, UriKind.Absolute);
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.EndInit();
                RefThumbnailImage.Source = bi;
                RefThumbnailCoords.Text = $"({rp.GlobalX:F4}, {rp.GlobalY:F4})";
                RefThumbnailPanel.Visibility = Visibility.Visible;
                return;
            }
            catch { }
        }
        RefThumbnailPanel.Visibility = Visibility.Collapsed;
    }

    private void AcceptRef_Click(object sender, RoutedEventArgs e)
    {
        var pos = GetCurrentPos();
        if (pos == null) return;
        if (_currentSet == null) return;

        var refs = _currentSet.ReferencePoints.Where(r => r.Valid).ToList();
        if (_alignRefIndex >= refs.Count) return;

        var taught = refs[_alignRefIndex];
        var actual = new ReferencePoint
        {
            Name = taught.Name,
            GlobalX = pos.X,
            GlobalY = pos.Y,
            GlobalZ = pos.Z,
            Valid = true
        };
        _actualRefs.Add(actual);
        AppendLog($"Accepted {actual.Name} at ({pos.X:F4},{pos.Y:F4},{pos.Z:F4})");

        MoveToRef(_alignRefIndex + 1);
    }

    private void ComputeCorrections()
    {
        if (_currentSet == null || _actualRefs.Count < 2) return;

        AcceptRefBtn.IsEnabled = false;

        var (liveOriginX, liveOriginY, liveXAxisX, liveXAxisY, liveYAxisX, liveYAxisY) =
            TrainerMath.ComputeCoordinateAxes(_actualRefs[0], _actualRefs[1]);

        var taughtRefs = _currentSet.ReferencePoints.Where(r => r.Valid).ToList();
        double scale = TrainerMath.ComputeScale(taughtRefs[0], taughtRefs[1], _actualRefs[0], _actualRefs[1]);

        _correctedPositions.Clear();

        foreach (var bp in _currentSet.BondingPoints)
        {
            var (cx, cy, cz) = TrainerMath.ComputeCorrectedPosition(
                bp, liveOriginX, liveOriginY, liveXAxisX, liveXAxisY, liveYAxisX, liveYAxisY, scale);

            var corrected = new CorrectedPosition
            {
                Name = bp.Name,
                CorrectedX = cx,
                CorrectedY = cy,
                CorrectedZ = cz,
                DeltaX = cx - bp.GlobalX,
                DeltaY = cy - bp.GlobalY,
                Scale = scale
            };

            // Compute secondary correction if teaching exists
            var st = bp.SecondaryTeaching;
            if (st?.HasXYTeaching == true)
            {
                // Delta in gantry-global space: difference between where the obs center
                // would be now (corrected bonding pos) vs where it was taught
                double deltaGX = cx - bp.GlobalX;
                double deltaGY = cy - bp.GlobalY;

                // Transform to device-local axes
                var (dlX, dlY, dlZ) = _context.AxisTransformService.GlobalToLocal(st.DeviceName, deltaGX, deltaGY, 0);

                double finalX = st.DevicePosition.X + dlX;
                double finalY = st.DevicePosition.Y + dlY;
                double finalZ = st.DevicePosition.Z + dlZ;

                // Add Z correction if Z teaching exists
                bool zEnabled = st.HasZTeaching;
                if (zEnabled && st.ZRel.HasValue)
                {
                    var (zlX, zlY, zlZ) = _context.AxisTransformService.GlobalToLocal(st.DeviceName, 0, 0, st.ZRel.Value);
                    finalX += zlX;
                    finalY += zlY;
                    finalZ += zlZ;
                }

                corrected.Secondary = new SecondaryCorrection
                {
                    DeviceName = st.DeviceName,
                    FinalX = finalX,
                    FinalY = finalY,
                    FinalZ = finalZ,
                    U = st.DevicePosition.U,
                    V = st.DevicePosition.V,
                    W = st.DevicePosition.W,
                    DeltaXGlobal = deltaGX,
                    DeltaYGlobal = deltaGY,
                    ZEnabled = zEnabled
                };
            }

            _correctedPositions.Add(corrected);

            var secInfo = corrected.Secondary != null
                ? $" sec={corrected.Secondary.DeviceName}({corrected.Secondary.FinalX:F4},{corrected.Secondary.FinalY:F4},{corrected.Secondary.FinalZ:F4})"
                : "";
            AppendLog($"{bp.Name}: corrected=({cx:F4},{cy:F4}) delta=({cx - bp.GlobalX:F4},{cy - bp.GlobalY:F4}) scale={scale:F6}{secInfo}");
        }

        AlignStatusText.Text = $"Alignment complete. Scale={scale:F6}, {_correctedPositions.Count} positions corrected.";
    }

    private void GoToCorrected_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not CorrectedPosition cp) return;
        var device = DeviceCombo.SelectedItem as string;
        if (string.IsNullOrEmpty(device) || !_context.IsInitialized) return;

        // Move primary device (gantry) to corrected bonding position
        var target = new PositionStruct { X = cp.CorrectedX, Y = cp.CorrectedY, Z = cp.CorrectedZ };
        _context.MotionManager.MoveToAbs6(device, target, blocking: false);
        AppendLog($"Moving to corrected '{cp.Name}' ({cp.CorrectedX:F4},{cp.CorrectedY:F4},{cp.CorrectedZ:F4})");

        // Move secondary device if correction exists
        if (cp.Secondary != null)
        {
            var sec = cp.Secondary;
            var secTarget = new PositionStruct
            {
                X = sec.FinalX, Y = sec.FinalY, Z = sec.FinalZ,
                U = sec.U, V = sec.V, W = sec.W
            };
            _context.MotionManager.MoveToAbs6(sec.DeviceName, secTarget, blocking: false);
            AppendLog($"  Secondary '{sec.DeviceName}' → ({sec.FinalX:F4},{sec.FinalY:F4},{sec.FinalZ:F4},{sec.U:F4},{sec.V:F4},{sec.W:F4})");
        }
    }

    // ============================================================
    // Camera helpers
    // ============================================================

    private void PopulateCameraCombo(ComboBox combo)
    {
        combo.Items.Clear();
        if (!_context.IsInitialized) return;

        var cameras = _context.CameraManager.GetCamerasWithNames();
        foreach (var (id, displayName) in cameras)
            combo.Items.Add(new CameraItem { Id = id, DisplayName = displayName });

        if (combo.Items.Count > 0)
            combo.SelectedIndex = 0;
    }

    private string? CaptureImageToTrainerDir(ComboBox cameraCombo, string uid)
    {
        if (cameraCombo.Items.Count == 0)
        {
            AppendLog("Camera capture skipped: no cameras available");
            return null;
        }
        if (cameraCombo.SelectedItem is not CameraItem cam)
        {
            AppendLog("Camera capture skipped: no camera selected");
            return null;
        }
        return CaptureImageForCamera(cam.Id, uid);
    }

    private string? CaptureImageForCamera(string cameraId, string uid)
    {
        if (!_context.IsInitialized)
        {
            AppendLog("Camera capture skipped: not initialized");
            return null;
        }

        var imageDir = _store.GetImageDirectory();
        var prevDir = _context.CameraManager.GetImageOutputDirectory();
        try
        {
            _context.CameraManager.SetImageOutputDirectory(imageDir);
            var filename = $"{uid}.bmp";
            AppendLog($"Capturing image: camera={cameraId}, file={filename}, dir={imageDir}");
            if (_context.CameraManager.CaptureImage(cameraId, filename))
            {
                var fullPath = Path.Combine(imageDir, filename);
                if (File.Exists(fullPath))
                {
                    AppendLog($"Image saved: {fullPath}");
                    return fullPath;
                }
                AppendLog($"CaptureImage returned true but file not found: {fullPath}");
            }
            else
            {
                AppendLog($"CaptureImage returned false for camera '{cameraId}'");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Camera capture failed: {ex.Message}");
        }
        finally
        {
            _context.CameraManager.SetImageOutputDirectory(prevDir);
        }
        return null;
    }

    // ============================================================
    // Helpers
    // ============================================================

    private PositionStruct? GetCurrentPos()
    {
        var device = DeviceCombo.SelectedItem as string;
        if (string.IsNullOrEmpty(device) || !_context.IsInitialized) return null;
        return _context.MotionManager.GetCurrentPosition(device);
    }

    private void SyncSetFromUI()
    {
        if (_currentSet == null) return;
        var device = DeviceCombo.SelectedItem as string ?? _currentSet.DeviceName;
        _currentSet = _currentSet with
        {
            DeviceName = device,
            BondingPoints = _bondingPoints.ToList(),
            ReferencePoints = _refPoints.ToList()
        };
        UpdateStepIndicators();
    }

    private void ShowNoSet()
    {
        MessageBox.Show("Create or select a training set first.", "Trainer",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void AppendLog(string msg)
    {
        Dispatcher.Invoke(() =>
        {
            var ts = DateTime.Now.ToString("HH:mm:ss");
            LogText.Text += $"[{ts}] {msg}\n";
            LogScroller.ScrollToEnd();
        });
    }

    private static string? PromptForName(string title, string prompt)
    {
        var dlg = new Window
        {
            Title = title,
            Width = 320, Height = 140,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Background = Brushes.White
        };

        var sp = new StackPanel { Margin = new Thickness(12) };
        sp.Children.Add(new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 6) });
        var tb = new TextBox { FontFamily = new FontFamily("Consolas") };
        sp.Children.Add(tb);
        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var okBtn = new Button { Content = "OK", Width = 70, Margin = new Thickness(0, 0, 4, 0), IsDefault = true };
        var cancelBtn = new Button { Content = "Cancel", Width = 70, IsCancel = true };
        okBtn.Click += (_, _) => { dlg.DialogResult = true; dlg.Close(); };
        btnPanel.Children.Add(okBtn);
        btnPanel.Children.Add(cancelBtn);
        sp.Children.Add(btnPanel);
        dlg.Content = sp;
        tb.Focus();

        return dlg.ShowDialog() == true ? tb.Text.Trim() : null;
    }
}

// --- Supporting types ---

public class CorrectedPosition
{
    public string Name { get; set; } = "";
    public double CorrectedX { get; set; }
    public double CorrectedY { get; set; }
    public double CorrectedZ { get; set; }
    public double DeltaX { get; set; }
    public double DeltaY { get; set; }
    public double Scale { get; set; }
    public SecondaryCorrection? Secondary { get; set; }
    public string SecondaryInfo => Secondary != null ? Secondary.DeviceName : "";
}

public class SecondaryCorrection
{
    public string DeviceName { get; set; } = "";
    public double FinalX { get; set; }
    public double FinalY { get; set; }
    public double FinalZ { get; set; }
    public double U { get; set; }
    public double V { get; set; }
    public double W { get; set; }
    public double DeltaXGlobal { get; set; }
    public double DeltaYGlobal { get; set; }
    public bool ZEnabled { get; set; }
}

public class Step3AssignmentRow
{
    public int BondIndex { get; set; }
    public string BondName { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string Method { get; set; } = "2-point";
    public string XYStatus { get; set; } = "Not taught";
    public string ZStatus { get; set; } = "Not taught";
    public bool CanTeachZ { get; set; }
}

public class CameraItem
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public override string ToString() => DisplayName;
}

public class CornerDisplayItem
{
    public string Label { get; set; } = "";
    public string Coords { get; set; } = "";
    public string? ImagePath { get; set; }
}
