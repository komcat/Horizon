using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace FAAM.UI.Siphog;

/// <summary>One decoded data frame from the SiPhOG server (six fixed fields).</summary>
public readonly record struct SiphogFrame(
    double SledCurrentMa,
    double PhotoCurrentUa,
    double SledTempC,
    double TargetSagPwrV,
    double SagPwrV,
    double TecCurrentMa);

/// <summary>
/// TCP client for the SiPhOG server (see <c>C:\SOFTWARE\SiphogSW\PROTOCOL.md</c>):
/// LF-framed ASCII on 127.0.0.1:65432. The server streams six-float CSV data
/// frames at ~200 Hz; the client sends uppercase commands (currently
/// <c>SET_CURRENT:&lt;mA&gt;</c>) and gets back a single <c>ACK:</c>/<c>ERR:</c> line
/// per command (FIFO-correlated, so commands are serialized).
///
/// The receive loop runs on a background task and only stores the latest frame +
/// a frame counter; the UI samples those on a throttled timer rather than reacting
/// to every frame (the stream is far faster than any sensible refresh rate).
/// </summary>
public sealed class SiphogClient : IDisposable
{
    public const string DefaultHost = "127.0.0.1";
    public const int DefaultPort = 65432;
    private const int ReplyTimeoutMs = 2000;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _commandLock = new(1, 1);

    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private TaskCompletionSource<string>? _pendingReply;

    private SiphogFrame _latest;
    private bool _hasFrame;
    private long _frameCount;
    private string _rxBuffer = string.Empty;

    /// <summary>True between a successful connect and a disconnect / drop.</summary>
    public bool IsConnected { get; private set; }

    /// <summary>Raised when the connection opens (true) or closes/drops (false). May fire on a background thread.</summary>
    public event Action<bool>? ConnectionChanged;

    /// <summary>
    /// Raised for every decoded data frame, on the receive thread (≈200 Hz). For a
    /// chart/ring that buffers on a background thread — do NOT touch the UI directly
    /// from here (the table/rate use <see cref="Latest"/> + <see cref="FrameCount"/>
    /// sampled on a throttled timer instead).
    /// </summary>
    public event Action<SiphogFrame>? FrameDecoded;

    /// <summary>The most recent decoded frame, or null if none received yet.</summary>
    public SiphogFrame? Latest
    {
        get { lock (_gate) return _hasFrame ? _latest : null; }
    }

    /// <summary>Total frames decoded since the current connection opened — drives the rate display.</summary>
    public long FrameCount => Interlocked.Read(ref _frameCount);

    public async Task ConnectAsync(string host = DefaultHost, int port = DefaultPort, CancellationToken ct = default)
    {
        Disconnect();

        var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port, ct).ConfigureAwait(false);

        _tcp = tcp;
        _stream = tcp.GetStream();
        _cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _frameCount, 0);
        lock (_gate) { _hasFrame = false; _rxBuffer = string.Empty; }

        IsConnected = true;
        // The receive loop lives for the whole connection, governed by _cts.Token —
        // intentionally NOT the connect-scoped 'ct', so pass None to Task.Run.
        _ = Task.Run(() => ReceiveLoopAsync(_stream, _cts.Token), CancellationToken.None);
        ConnectionChanged?.Invoke(true);
    }

    public void Disconnect()
    {
        var wasConnected = IsConnected;
        IsConnected = false;

        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { _cts?.Dispose(); } catch { /* ignore */ }   // dispose before reassign on reconnect (was leaking)
        try { _stream?.Dispose(); } catch { /* ignore */ }
        try { _tcp?.Close(); } catch { /* ignore */ }
        _stream = null;
        _tcp = null;
        _cts = null;
        _pendingReply?.TrySetCanceled();

        if (wasConnected)
            ConnectionChanged?.Invoke(false);
    }

    /// <summary>
    /// Sends a command and waits for its <c>ACK:</c>/<c>ERR:</c> reply. Commands are
    /// serialized (one in flight at a time) per the protocol's FIFO rule. Returns the
    /// reply line, or an <c>ERR:</c>-style string on timeout / not-connected.
    /// </summary>
    public async Task<string> SendCommandAsync(string command, CancellationToken ct = default)
    {
        var stream = _stream;
        if (!IsConnected || stream == null)
            return "ERR:not connected";

        await _commandLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingReply = tcs;

            var bytes = Encoding.ASCII.GetBytes(command + "\n");
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);

            using var timeout = new CancellationTokenSource(ReplyTimeoutMs);
            using (ct.Register(() => tcs.TrySetCanceled()))
            using (timeout.Token.Register(() => tcs.TrySetResult("ERR:reply timeout")))
                return await tcs.Task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return $"ERR:{ex.Message}";
        }
        finally
        {
            _pendingReply = null;
            _commandLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (n <= 0) break;   // server closed the connection
                Feed(Encoding.ASCII.GetString(buffer, 0, n));
            }
        }
        catch (OperationCanceledException) { /* disconnect requested */ }
        catch { /* socket error — fall through to mark disconnected */ }
        finally
        {
            if (IsConnected)   // server-initiated drop (not our Disconnect)
            {
                IsConnected = false;
                ConnectionChanged?.Invoke(false);
            }
        }
    }

    /// <summary>Appends a received chunk and dispatches every complete LF-terminated line.</summary>
    private void Feed(string chunk)
    {
        _rxBuffer += chunk;
        int nl;
        while ((nl = _rxBuffer.IndexOf('\n')) >= 0)
        {
            var line = _rxBuffer[..nl].Trim();
            _rxBuffer = _rxBuffer[(nl + 1)..];
            if (line.Length > 0)
                HandleLine(line);
        }
    }

    private void HandleLine(string line)
    {
        if (line.StartsWith("ACK:", StringComparison.Ordinal) ||
            line.StartsWith("ERR:", StringComparison.Ordinal))
        {
            _pendingReply?.TrySetResult(line);
            return;
        }

        var c = line[0];
        if (c is (>= '0' and <= '9') or '-' or '+' && TryParseFrame(line, out var frame))
        {
            lock (_gate) { _latest = frame; _hasFrame = true; }
            Interlocked.Increment(ref _frameCount);
            FrameDecoded?.Invoke(frame);
        }
        // else: unknown line — ignore (forward-compat per the protocol).
    }

    private static bool TryParseFrame(string line, out SiphogFrame frame)
    {
        frame = default;
        var parts = line.Split(',');
        if (parts.Length != 6) return false;

        Span<double> v = stackalloc double[6];
        for (int i = 0; i < 6; i++)
            if (!double.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]))
                return false;

        frame = new SiphogFrame(v[0], v[1], v[2], v[3], v[4], v[5]);
        return true;
    }

    public void Dispose() => Disconnect();
}
