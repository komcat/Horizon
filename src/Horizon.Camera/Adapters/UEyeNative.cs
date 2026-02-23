using System.Runtime.InteropServices;

namespace Horizon.Camera.Adapters;

/// <summary>
/// P/Invoke declarations for the legacy IDS uEye SDK (ueye_api_64.dll).
/// Mirrors the C API used by bobcat_engine's IDSCameraAdapter.
/// </summary>
internal static class UEyeNative
{
    private const string DllName = "ueye_api_64";

    // Return codes
    public const int IS_SUCCESS = 0;
    public const int IS_NO_SUCCESS = -1;

    // Camera handle type
    // HCAM is a DWORD (uint) in the uEye SDK

    // Color modes
    public const int IS_CM_RGB8_PACKED = 1;
    public const int IS_CM_BGR8_PACKED = 11;

    // Trigger modes
    public const int IS_SET_TRIGGER_OFF = 0;
    public const int IS_SET_TRIGGER_SOFTWARE = 6;

    // Video commands
    public const int IS_DONT_WAIT = 0x0000;
    public const int IS_FORCE_VIDEO_STOP = 0x0001;

    // Gain
    public const int IS_GET_MASTER_GAIN = 0x8000;
    public const int IS_IGNORE_PARAMETER = -1;

    // AOI commands
    public const int IS_AOI_IMAGE_GET_AOI = 0x0002;

    // Auto parameter commands
    public const int IS_SET_ENABLE_AUTO_SHUTTER = 1;
    public const int IS_GET_ENABLE_AUTO_SHUTTER = 2;
    public const int IS_SET_ENABLE_AUTO_GAIN = 3;
    public const int IS_GET_ENABLE_AUTO_GAIN = 4;
    public const int IS_SET_AUTO_REFERENCE = 5;
    public const int IS_SET_AUTO_SPEED = 7;
    public const int IS_SET_AUTO_HYSTERESIS = 9;

    // Exposure commands
    public const int IS_EXPOSURE_CMD_GET_EXPOSURE = 7;
    public const int IS_EXPOSURE_CMD_SET_EXPOSURE = 12;
    public const int IS_EXPOSURE_CMD_GET_EXPOSURE_RANGE = 17;
    public const int IS_EXPOSURE_CMD_GET_EXPOSURE_RANGE_MAX = 19;
    public const int IS_EXPOSURE_CMD_GET_EXPOSURE_RANGE_INC = 21;

    [StructLayout(LayoutKind.Sequential)]
    public struct IS_RECT
    {
        public int s32X;
        public int s32Y;
        public int s32Width;
        public int s32Height;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct UEYE_CAMERA_INFO
    {
        public uint dwCameraID;
        public uint dwDeviceID;
        public uint dwSensorID;
        public uint dwInUse;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)]
        public string SerNo;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)]
        public string Model;
        public uint dwStatus;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
        public uint[] dwReserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string FullModelName;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 5)]
        public uint[] dwReserved2;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct CAMINFO
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 12)]
        public string SerNo;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 20)]
        public string ID;       // Camera model
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 10)]
        public string Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 12)]
        public string Date;
        public byte Select;
        public byte Type;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string Reserved;
    }

    // Camera enumeration
    [DllImport(DllName, EntryPoint = "is_GetNumberOfCameras")]
    public static extern int GetNumberOfCameras(ref int pnNumCams);

    // Camera list — must be called with manually allocated buffer
    [DllImport(DllName, EntryPoint = "is_GetCameraList")]
    public static extern int GetCameraList(IntPtr pucl);

    // Camera lifecycle
    [DllImport(DllName, EntryPoint = "is_InitCamera")]
    public static extern int InitCamera(ref uint phCam, IntPtr hWnd);

    [DllImport(DllName, EntryPoint = "is_ExitCamera")]
    public static extern int ExitCamera(uint hCam);

    [DllImport(DllName, EntryPoint = "is_GetCameraInfo")]
    public static extern int GetCameraInfo(uint hCam, ref CAMINFO pInfo);

    // Color mode
    [DllImport(DllName, EntryPoint = "is_SetColorMode")]
    public static extern int SetColorMode(uint hCam, int mode);

    // AOI
    [DllImport(DllName, EntryPoint = "is_AOI")]
    public static extern int AOI(uint hCam, int command, ref IS_RECT pParam, int sizeOfParam);

    // Image memory
    [DllImport(DllName, EntryPoint = "is_AllocImageMem")]
    public static extern int AllocImageMem(uint hCam, int width, int height, int bitspixel, ref IntPtr ppcImgMem, ref int pid);

    [DllImport(DllName, EntryPoint = "is_SetImageMem")]
    public static extern int SetImageMem(uint hCam, IntPtr pcMem, int id);

    [DllImport(DllName, EntryPoint = "is_FreeImageMem")]
    public static extern int FreeImageMem(uint hCam, IntPtr pcMem, int id);

    // Capture
    [DllImport(DllName, EntryPoint = "is_CaptureVideo")]
    public static extern int CaptureVideo(uint hCam, int wait);

    [DllImport(DllName, EntryPoint = "is_StopLiveVideo")]
    public static extern int StopLiveVideo(uint hCam, int wait);

    [DllImport(DllName, EntryPoint = "is_HasVideoStarted")]
    public static extern int HasVideoStarted(uint hCam, ref int pBool);

    // Exposure
    [DllImport(DllName, EntryPoint = "is_Exposure")]
    public static extern int Exposure(uint hCam, int command, ref double pParam, int sizeOfParam);

    // Hardware gain
    [DllImport(DllName, EntryPoint = "is_SetHardwareGain")]
    public static extern int SetHardwareGain(uint hCam, int nMaster, int nRed, int nGreen, int nBlue);

    // Auto parameters
    [DllImport(DllName, EntryPoint = "is_SetAutoParameter")]
    public static extern int SetAutoParameter(uint hCam, int param, ref double pval1, ref double pval2);
}
