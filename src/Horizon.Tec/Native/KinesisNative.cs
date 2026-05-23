using System.Runtime.InteropServices;

namespace Horizon.Tec;

/// <summary>
/// P/Invoke bindings for the Thorlabs Kinesis TC-Cube TEC controller
/// (<c>Thorlabs.MotionControl.TCube.TEC.dll</c>, deployed alongside
/// <c>Thorlabs.MotionControl.DeviceManager.dll</c> + FTDI driver, all from
/// the Kinesis SDK installer).
///
/// All device methods take an ASCII serial-number string (8 digits, e.g.
/// <c>"87814387"</c>). Kinesis returns <see cref="FtOk"/> on success.
///
/// Header reference: <c>Thorlabs.MotionControl.TCube.TEC.h</c>.
/// </summary>
internal static class KinesisNative
{
    private const string TecDll = "Thorlabs.MotionControl.TCube.TEC.dll";
    private const string DeviceManagerDll = "Thorlabs.MotionControl.DeviceManager.dll";

    /// <summary>Kinesis success code (<c>FT_OK</c>).</summary>
    public const short FtOk = 0;

    /// <summary>Sensor / transducer types (matches <c>TC_SensorTypes</c>).</summary>
    public enum SensorType : short
    {
        /// <summary>AD590/AD592 transducer — 1 unit = 0.01 °C, range -4500..14500.</summary>
        Transducer = 0,
        /// <summary>20 kΩ NTC thermistor — 1 unit = 10 Ω, range 0..2000.</summary>
        TH20kOhm = 1,
        /// <summary>200 kΩ NTC thermistor — 1 unit = 100 Ω, range 0..2000.</summary>
        TH200kOhm = 2,
    }

    // ---- Device discovery (DeviceManager) ----

    [DllImport(DeviceManagerDll, EntryPoint = "TLI_BuildDeviceList")]
    public static extern short TLI_BuildDeviceList();

    // ---- TC_* (TCube.TEC) ----

    [DllImport(TecDll, EntryPoint = "TC_Open", CharSet = CharSet.Ansi)]
    public static extern short TC_Open([MarshalAs(UnmanagedType.LPStr)] string serialNo);

    [DllImport(TecDll, EntryPoint = "TC_Close", CharSet = CharSet.Ansi)]
    public static extern void TC_Close([MarshalAs(UnmanagedType.LPStr)] string serialNo);

    [DllImport(TecDll, EntryPoint = "TC_LoadSettings", CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool TC_LoadSettings([MarshalAs(UnmanagedType.LPStr)] string serialNo);

    [DllImport(TecDll, EntryPoint = "TC_StartPolling", CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool TC_StartPolling([MarshalAs(UnmanagedType.LPStr)] string serialNo, int milliseconds);

    [DllImport(TecDll, EntryPoint = "TC_StopPolling", CharSet = CharSet.Ansi)]
    public static extern void TC_StopPolling([MarshalAs(UnmanagedType.LPStr)] string serialNo);

    [DllImport(TecDll, EntryPoint = "TC_RequestStatus", CharSet = CharSet.Ansi)]
    public static extern short TC_RequestStatus([MarshalAs(UnmanagedType.LPStr)] string serialNo);

    [DllImport(TecDll, EntryPoint = "TC_RequestSensorType", CharSet = CharSet.Ansi)]
    public static extern short TC_RequestSensorType([MarshalAs(UnmanagedType.LPStr)] string serialNo);

    [DllImport(TecDll, EntryPoint = "TC_GetSensorType", CharSet = CharSet.Ansi)]
    public static extern SensorType TC_GetSensorType([MarshalAs(UnmanagedType.LPStr)] string serialNo);

    [DllImport(TecDll, EntryPoint = "TC_SetSensorType", CharSet = CharSet.Ansi)]
    public static extern short TC_SetSensorType([MarshalAs(UnmanagedType.LPStr)] string serialNo, SensorType sensor);

    /// <summary>Returns the latest polled temperature in device units (see <see cref="SensorType"/>).</summary>
    [DllImport(TecDll, EntryPoint = "TC_GetTemperatureReading", CharSet = CharSet.Ansi)]
    public static extern short TC_GetTemperatureReading([MarshalAs(UnmanagedType.LPStr)] string serialNo);

    /// <summary>
    /// Returns the latest polled TEC drive current. Declared as <c>WORD</c> in
    /// the Kinesis header but the underlying value is signed 16-bit
    /// milliamps — reinterpret as <see cref="short"/> at the call site.
    /// </summary>
    [DllImport(TecDll, EntryPoint = "TC_GetCurrentReading", CharSet = CharSet.Ansi)]
    public static extern ushort TC_GetCurrentReading([MarshalAs(UnmanagedType.LPStr)] string serialNo);

    [DllImport(TecDll, EntryPoint = "TC_SetTemperature", CharSet = CharSet.Ansi)]
    public static extern short TC_SetTemperature([MarshalAs(UnmanagedType.LPStr)] string serialNo, short temperature);

    [DllImport(TecDll, EntryPoint = "TC_Enable", CharSet = CharSet.Ansi)]
    public static extern short TC_Enable([MarshalAs(UnmanagedType.LPStr)] string serialNo);

    [DllImport(TecDll, EntryPoint = "TC_Disable", CharSet = CharSet.Ansi)]
    public static extern short TC_Disable([MarshalAs(UnmanagedType.LPStr)] string serialNo);
}
