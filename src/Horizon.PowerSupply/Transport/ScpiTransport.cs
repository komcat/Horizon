using System.Net.Sockets;
using System.Text;

namespace Horizon.PowerSupply;

/// <summary>
/// A line-oriented SCPI link to one instrument. Commands and queries are
/// newline-terminated; responses are returned with the terminator trimmed.
/// Implementations are NOT thread-safe — the owning adapter serializes access.
/// Every failure throws (<see cref="InvalidOperationException"/> / <see cref="IOException"/>),
/// so the adapter maps it to a <c>PowerSupplyError</c> in one place.
/// </summary>
internal interface IScpiTransport : IDisposable
{
    /// <summary>Human-readable address for logs, e.g. <c>192.168.0.88:5025</c>.</summary>
    string Address { get; }

    void Open();
    void Write(string command);
    string Query(string query);
}

/// <summary>SCPI over NI-VISA — USBTMC (<c>USB0::…::INSTR</c>) or VXI-11 (<c>TCPIP0::host::INSTR</c>).</summary>
internal sealed class VisaScpiTransport : IScpiTransport
{
    private const int OpenTimeoutMs = 2000;
    private const int IoTimeoutMs = 5000;

    private int _rmSession;
    private int _instrSession;

    public VisaScpiTransport(string resource) => Address = resource;

    public string Address { get; }

    public void Open()
    {
        var status = VisaNative.OpenDefaultRM(out _rmSession);
        if (status < VisaNative.VI_SUCCESS)
            throw new InvalidOperationException($"OpenDefaultRM failed: 0x{status:X8}");

        status = VisaNative.Open(_rmSession, Address, 0, OpenTimeoutMs, out _instrSession);
        if (status < VisaNative.VI_SUCCESS)
        {
            Dispose();
            throw new InvalidOperationException($"Open('{Address}') failed: 0x{status:X8}");
        }

        VisaNative.SetAttribute(_instrSession, VisaNative.VI_ATTR_TMO_VALUE, IoTimeoutMs);
        VisaNative.SetAttribute(_instrSession, VisaNative.VI_ATTR_TERMCHAR_EN, 1);
        VisaNative.SetAttribute(_instrSession, VisaNative.VI_ATTR_TERMCHAR, '\n');
        VisaNative.Clear(_instrSession);
        // USBTMC devices need a moment to settle after the session opens.
        Thread.Sleep(200);
    }

    public void Write(string command)
    {
        var status = VisaNative.WriteString(_instrSession, command);
        if (status < VisaNative.VI_SUCCESS)
            throw new InvalidOperationException($"VISA write failed: 0x{status:X8}");
    }

    public string Query(string query)
    {
        Write(query);
        var status = VisaNative.ReadString(_instrSession, out var response);
        if (status < VisaNative.VI_SUCCESS)
            throw new InvalidOperationException($"VISA read failed: 0x{status:X8}");
        return response.Trim();
    }

    public void Dispose()
    {
        try { if (_instrSession != 0) VisaNative.Close(_instrSession); } catch { }
        try { if (_rmSession != 0) VisaNative.Close(_rmSession); } catch { }
        _instrSession = 0;
        _rmSession = 0;
    }
}

/// <summary>
/// SCPI on a raw TCP socket (LAN, no VISA runtime needed). Connect, send and
/// receive are all time-bounded; a response longer than <see cref="MaxResponseBytes"/>
/// without a newline is treated as a protocol error.
/// </summary>
internal sealed class TcpScpiTransport : IScpiTransport
{
    private const int ConnectTimeoutMs = 3000;
    private const int IoTimeoutMs = 3000;
    private const int MaxResponseBytes = 4096;

    private readonly string _host;
    private readonly int _port;
    private TcpClient? _client;
    private NetworkStream? _stream;

    public TcpScpiTransport(string host, int port)
    {
        _host = host;
        _port = port;
    }

    public string Address => $"{_host}:{_port}";

    public void Open()
    {
        _client = new TcpClient { ReceiveTimeout = IoTimeoutMs, SendTimeout = IoTimeoutMs, NoDelay = true };
        // TcpClient.Connect has no timeout of its own — bound it explicitly.
        bool completed;
        try
        {
            completed = _client.ConnectAsync(_host, _port).Wait(ConnectTimeoutMs);
        }
        catch (AggregateException ex) when (ex.InnerException != null)
        {
            Dispose();
            throw new IOException($"Connect to {Address} failed: {ex.InnerException.Message}", ex.InnerException);
        }
        if (!completed)
        {
            Dispose();
            throw new TimeoutException($"Connect to {Address} timed out after {ConnectTimeoutMs} ms");
        }
        _stream = _client.GetStream();
    }

    public void Write(string command)
    {
        var data = Encoding.ASCII.GetBytes(command + "\n");
        Stream().Write(data, 0, data.Length);
    }

    public string Query(string query)
    {
        Write(query);
        return ReadLine();
    }

    private string ReadLine()
    {
        var stream = Stream();
        var sb = new StringBuilder();
        for (var i = 0; i < MaxResponseBytes; i++)
        {
            var b = stream.ReadByte();   // honours ReceiveTimeout (throws IOException)
            if (b < 0) throw new IOException($"{Address} closed the connection");
            if (b == '\n') return sb.ToString().Trim();
            sb.Append((char)b);
        }
        throw new InvalidOperationException($"{Address}: response exceeded {MaxResponseBytes} bytes without a terminator");
    }

    private NetworkStream Stream()
        => _stream ?? throw new InvalidOperationException($"{Address}: not connected");

    public void Dispose()
    {
        try { _stream?.Dispose(); } catch { }
        try { _client?.Dispose(); } catch { }
        _stream = null;
        _client = null;
    }
}
