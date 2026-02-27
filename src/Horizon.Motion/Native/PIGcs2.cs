using System.Runtime.InteropServices;
using System.Text;

namespace Horizon.Motion.Native;

internal static class PIGcs2
{
    private const string DllName = "PI_GCS2_DLL_x64.dll";

    [DllImport(DllName, EntryPoint = "PI_ConnectTCPIP")]
    internal static extern int ConnectTCPIP(
        [MarshalAs(UnmanagedType.LPStr)] string szHostname, int port);

    [DllImport(DllName, EntryPoint = "PI_CloseConnection")]
    internal static extern void CloseConnection(int iId);

    [DllImport(DllName, EntryPoint = "PI_IsConnected")]
    internal static extern int IsConnected(int iId);

    [DllImport(DllName, EntryPoint = "PI_MOV")]
    internal static extern int MOV(int iId,
        [MarshalAs(UnmanagedType.LPStr)] string szAxes, double[] pdValarray);

    [DllImport(DllName, EntryPoint = "PI_MVR")]
    internal static extern int MVR(int iId,
        [MarshalAs(UnmanagedType.LPStr)] string szAxes, double[] pdValarray);

    [DllImport(DllName, EntryPoint = "PI_qPOS")]
    internal static extern int qPOS(int iId,
        [MarshalAs(UnmanagedType.LPStr)] string szAxes, double[] pdValarray);

    [DllImport(DllName, EntryPoint = "PI_IsMoving")]
    internal static extern int IsMoving(int iId,
        [MarshalAs(UnmanagedType.LPStr)] string szAxes, [MarshalAs(UnmanagedType.LPArray)] int[] pbValarray);

    [DllImport(DllName, EntryPoint = "PI_STP")]
    internal static extern int STP(int iId);

    [DllImport(DllName, EntryPoint = "PI_HLT")]
    internal static extern int HLT(int iId,
        [MarshalAs(UnmanagedType.LPStr)] string szAxes);

    [DllImport(DllName, EntryPoint = "PI_FRF")]
    internal static extern int FRF(int iId,
        [MarshalAs(UnmanagedType.LPStr)] string szAxes);

    [DllImport(DllName, EntryPoint = "PI_qFRF")]
    internal static extern int qFRF(int iId,
        [MarshalAs(UnmanagedType.LPStr)] string szAxes, [MarshalAs(UnmanagedType.LPArray)] int[] pbValarray);

    [DllImport(DllName, EntryPoint = "PI_SVO")]
    internal static extern int SVO(int iId,
        [MarshalAs(UnmanagedType.LPStr)] string szAxes, [MarshalAs(UnmanagedType.LPArray)] int[] pbValarray);

    [DllImport(DllName, EntryPoint = "PI_qSVO")]
    internal static extern int qSVO(int iId,
        [MarshalAs(UnmanagedType.LPStr)] string szAxes, [MarshalAs(UnmanagedType.LPArray)] int[] pbValarray);

    [DllImport(DllName, EntryPoint = "PI_GetError")]
    internal static extern int GetError(int iId);

    [DllImport(DllName, EntryPoint = "PI_TranslateError")]
    internal static extern int TranslateError(int iError, StringBuilder szBuffer, int iBufferSize);
}
