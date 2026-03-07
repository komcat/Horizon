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

    // Current wizard step (1/2/3)
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

    // Alignment state (Step 3)
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
        // Only one trainer type for now — already selected
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
        }

        _activeStep = step;
        WizardStep1.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        WizardStep2.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        WizardStep3.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
        HighlightStepButton(step);

        // Reset sub-step views
        if (step == 1) ShowStep1Start();
        if (step == 2) ShowStep2Start();
    }

    private bool IsCapturingInProgress()
    {
        if (_activeStep == 1 &&
            (Step1_Capture.Visibility == Visibility.Visible || Step1_Review.Visibility == Visibility.Visible))
            return true;
        if (_activeStep == 2 &&
            (Step2_Capture.Visibility == Visibility.Visible || Step2_Review.Visibility == Visibility.Visible))
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

        StepBtn1.Foreground = step == 1 ? Brushes.White : (Brush)FindResource("Theme.ForegroundBrush");
        StepBtn2.Foreground = step == 2 ? Brushes.White : (Brush)FindResource("Theme.ForegroundBrush");
        StepBtn3.Foreground = step == 3 ? Brushes.White : (Brush)FindResource("Theme.ForegroundBrush");

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
            // All corners captured — move to center, capture reference image, then review
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

        // Start wizard for this bond position (overwrites it)
        _pendingBondName = bp.Name;
        _cornerTarget = bp.Corners.Count >= 3 ? 3 : 2;
        _cornerCurrent = 1;
        _capturedCorners.Clear();
        _cornerDisplayItems.Clear();

        // Remove old, will re-add on complete
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

        // Start corner wizard for this ref (overwrites it)
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
    // STEP 3: RETRACE BONDING (RUN)
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

        // Show reference thumbnail
        ShowRefThumbnail(rp);

        // Move to taught position
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

            _correctedPositions.Add(new CorrectedPosition
            {
                Name = bp.Name,
                CorrectedX = cx,
                CorrectedY = cy,
                CorrectedZ = cz,
                DeltaX = cx - bp.GlobalX,
                DeltaY = cy - bp.GlobalY,
                Scale = scale
            });

            AppendLog($"{bp.Name}: corrected=({cx:F4},{cy:F4}) delta=({cx - bp.GlobalX:F4},{cy - bp.GlobalY:F4}) scale={scale:F6}");
        }

        AlignStatusText.Text = $"Alignment complete. Scale={scale:F6}, {_correctedPositions.Count} positions corrected.";
    }

    private void GoToCorrected_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not CorrectedPosition cp) return;
        var device = DeviceCombo.SelectedItem as string;
        if (string.IsNullOrEmpty(device) || !_context.IsInitialized) return;

        var target = new PositionStruct { X = cp.CorrectedX, Y = cp.CorrectedY, Z = cp.CorrectedZ };
        _context.MotionManager.MoveToAbs6(device, target, blocking: false);
        AppendLog($"Moving to corrected '{cp.Name}' ({cp.CorrectedX:F4},{cp.CorrectedY:F4},{cp.CorrectedZ:F4})");
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
