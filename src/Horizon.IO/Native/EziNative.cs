using System.Net;
using System.Runtime.InteropServices;

namespace Horizon.IO.Native;

internal static class EziNative
{
    private const string DllName = "EziMOTIONPlusE.dll";

    // Return codes
    public const int FMM_OK = 0;
    public const int FMM_NOT_OPEN = 1;
    public const int FMC_DISCONNECTED = 5;
    public const int FMC_TIMEOUT_ERROR = 6;
    public const int FMM_UNKNOWN_ERROR = 0xFF;

    // Connection
    [DllImport(DllName)] internal static extern int FAS_Connect(byte sb1, byte sb2, byte sb3, byte sb4, int iBdID);
    [DllImport(DllName)] internal static extern void FAS_Close(int iBdID);
    [DllImport(DllName)] internal static extern int FAS_IsSlaveExist(int iBdID);

    // Standard IO (motor driver IO pins)
    [DllImport(DllName)] internal static extern int FAS_GetIOInput(int iBdID, ref uint dwIOInput);
    [DllImport(DllName)] internal static extern int FAS_GetIOOutput(int iBdID, ref uint dwIOOutput);
    [DllImport(DllName)] internal static extern int FAS_SetIOOutput(int iBdID, uint dwIOSETMask, uint dwIOCLRMask);
    [DllImport(DllName)] internal static extern int FAS_GetIOAssignMap(int iBdID, byte iIOPinNo, ref uint dwIOLogicMask, ref byte bLevel);

    // IO Module functions (Ezi-IO devices)
    [DllImport(DllName)] internal static extern int FAS_GetInput(int iBdID, ref uint uInput, ref uint uLatch);
    [DllImport(DllName)] internal static extern int FAS_GetOutput(int iBdID, ref uint uOutput, ref uint uStatus);
    [DllImport(DllName)] internal static extern int FAS_SetOutput(int iBdID, uint uSetMask, uint uClearMask);
    [DllImport(DllName)] internal static extern int FAS_ClearLatch(int iBdID, uint uLatchMask);

    // Info
    [DllImport(DllName)] internal static extern int FAS_GetSlaveInfoEx(int iBdID, byte[] lpDriveInfo);

    // Helpers
    public static bool Connect(IPAddress ip, int boardId)
    {
        var bytes = ip.GetAddressBytes();
        return FAS_Connect(bytes[0], bytes[1], bytes[2], bytes[3], boardId) != 0;
    }

    public static bool IsSlaveExist(int boardId) => FAS_IsSlaveExist(boardId) != 0;
}
