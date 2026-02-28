using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Horizon.App;

public partial class SplashWindow : Window
{
    private readonly Horizon.Core.AppContext _context;
    private readonly ObservableCollection<DeviceItem> _devices = new();

    private static readonly Brush GrayBrush = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
    private static readonly Brush AmberBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xB9, 0x00));
    private static readonly Brush GreenBrush = new SolidColorBrush(Color.FromRgb(0x00, 0xC8, 0x53));
    private static readonly Brush RedBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x44, 0x44));
    private static readonly Brush DimBrush = new SolidColorBrush(Color.FromRgb(0x65, 0x65, 0x65));

    static SplashWindow()
    {
        GrayBrush.Freeze();
        AmberBrush.Freeze();
        GreenBrush.Freeze();
        RedBrush.Freeze();
        DimBrush.Freeze();
    }

    public SplashWindow(Horizon.Core.AppContext context)
    {
        _context = context;
        InitializeComponent();
        DeviceList.ItemsSource = _devices;
    }

    public async Task RunConnectionsAsync()
    {
        // Step 1: Initialize (load configs, discover cameras, create managers)
        SubtitleText.Text = "Loading configuration...";
        await Task.Run(() => _context.Initialize());

        // Step 2: Build device list from all managers
        BuildDeviceList();

        SubtitleText.Text = "Connecting hardware...";

        // Step 3: Connect each device one-by-one
        int success = 0;
        int failed = 0;

        for (int i = 0; i < _devices.Count; i++)
        {
            var device = _devices[i];
            device.SetConnecting();

            BottomStatus.Text = $"Connecting {i + 1} of {_devices.Count}...";

            // Yield to UI thread so the status update renders
            await Dispatcher.Yield(DispatcherPriority.Render);

            bool result = await Task.Run(() => ConnectDevice(device));

            if (result)
            {
                device.SetDone();
                success++;
            }
            else
            {
                device.SetFailed();
                failed++;
            }
        }

        // Step 4: Done
        SubtitleText.Text = "Ready";
        BottomStatus.Text = $"{success} connected, {failed} failed";

        // Brief pause so user can see final state
        await Task.Delay(800);
    }

    private void BuildDeviceList()
    {
        _devices.Clear();

        // IO devices
        foreach (var name in _context.IOManager.GetAllDeviceNames())
            _devices.Add(new DeviceItem($"IO: {name}", DeviceKind.IO, name));

        // Motion devices
        foreach (var name in _context.MotionManager.GetAllDeviceNames())
            _devices.Add(new DeviceItem($"Motion: {name}", DeviceKind.Motion, name));

        // Cameras
        foreach (var id in _context.CameraManager.GetCameraIds())
        {
            var status = _context.CameraManager.GetCameraStatus(id);
            var label = $"Camera: {status.ModelName} ({id})";
            _devices.Add(new DeviceItem(label, DeviceKind.Camera, id));
        }
    }

    private bool ConnectDevice(DeviceItem device)
    {
        try
        {
            return device.Kind switch
            {
                DeviceKind.IO => _context.IOManager.ConnectDevice(device.DeviceId),
                DeviceKind.Motion => _context.MotionManager.ConnectDevice(device.DeviceId),
                DeviceKind.Camera => _context.CameraManager.ConnectCamera(device.DeviceId),
                _ => false
            };
        }
        catch
        {
            return false;
        }
    }

    internal enum DeviceKind { IO, Motion, Camera }

    internal sealed class DeviceItem : INotifyPropertyChanged
    {
        public string Name { get; }
        public DeviceKind Kind { get; }
        public string DeviceId { get; }

        private Brush _dotBrush;
        private string _statusText;
        private Brush _statusBrush;

        public Brush DotBrush
        {
            get => _dotBrush;
            private set { _dotBrush = value; OnPropertyChanged(nameof(DotBrush)); }
        }

        public string StatusText
        {
            get => _statusText;
            private set { _statusText = value; OnPropertyChanged(nameof(StatusText)); }
        }

        public Brush StatusBrush
        {
            get => _statusBrush;
            private set { _statusBrush = value; OnPropertyChanged(nameof(StatusBrush)); }
        }

        public DeviceItem(string name, DeviceKind kind, string deviceId)
        {
            Name = name;
            Kind = kind;
            DeviceId = deviceId;
            _dotBrush = GrayBrush;
            _statusText = "Pending";
            _statusBrush = DimBrush;
        }

        public void SetConnecting()
        {
            DotBrush = AmberBrush;
            StatusText = "Connecting...";
            StatusBrush = AmberBrush;
        }

        public void SetDone()
        {
            DotBrush = GreenBrush;
            StatusText = "Connected";
            StatusBrush = GreenBrush;
        }

        public void SetFailed()
        {
            DotBrush = RedBrush;
            StatusText = "Failed";
            StatusBrush = RedBrush;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
