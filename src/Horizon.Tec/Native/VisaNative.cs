using System.Runtime.InteropServices;
using System.Text;

namespace Horizon.Tec;

/// <summary>
/// Minimal P/Invoke bindings for the NI-VISA runtime (<c>visa64.dll</c>) — the
/// shared VISA pattern used across Horizon's instrument adapters.
/// </summary>
internal static class VisaNative
{
    private const string DllName = "visa64.dll";

    public const int VI_SUCCESS = 0;
    public const uint VI_ATTR_TMO_VALUE = 0x3FFF001A;

    [DllImport(DllName, EntryPoint = "viOpenDefaultRM")]
    public static extern int OpenDefaultRM(out int sesn);

    [DllImport(DllName, EntryPoint = "viOpen")]
    public static extern int Open(int sesn,
        [MarshalAs(UnmanagedType.LPStr)] string rsrcName,
        int accessMode, int openTimeout, out int vi);

    [DllImport(DllName, EntryPoint = "viClose")]
    public static extern int Close(int vi);

    [DllImport(DllName, EntryPoint = "viSetAttribute")]
    public static extern int SetAttribute(int vi, uint attrName, nint attrValue);

    [DllImport(DllName, EntryPoint = "viWrite")]
    public static extern int Write(int vi, byte[] buf, int cnt, out int retCnt);

    [DllImport(DllName, EntryPoint = "viRead")]
    public static extern int Read(int vi, byte[] buf, int cnt, out int retCnt);

    [DllImport(DllName, EntryPoint = "viClear")]
    public static extern int Clear(int vi);

    /// <summary>Writes a SCPI command string (appends a newline).</summary>
    public static int WriteString(int vi, string command)
    {
        var data = Encoding.ASCII.GetBytes(command + "\n");
        return Write(vi, data, data.Length, out _);
    }

    /// <summary>Reads a response string from the instrument.</summary>
    public static int ReadString(int vi, out string response, int bufSize = 4096)
    {
        var buf = new byte[bufSize];
        var status = Read(vi, buf, buf.Length, out int retCnt);
        response = retCnt > 0 ? Encoding.ASCII.GetString(buf, 0, retCnt).TrimEnd('\r', '\n') : string.Empty;
        return status;
    }
}
