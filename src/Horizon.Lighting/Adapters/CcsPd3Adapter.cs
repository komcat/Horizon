using System.Net.Sockets;
using System.Text;
using Horizon.Config;
using Horizon.Lighting.Models;
using Serilog;

namespace Horizon.Lighting;

/// <summary>
/// CCS PD3 (PD3-10024-8-EI) LED lighting controller over TCP, per the CCS
/// instruction guide KZ03995 §8.
/// <para>
/// Frame: <c>@</c> + channel (2) + command + data + checksum (2 hex) + CR LF.
/// The checksum is the low byte of the ASCII sum from <c>@</c> through the data.
/// Replies are <c>@ccO[data]ss</c> (accepted) or <c>@ccNee</c> (rejected:
/// 01 command, 02 checksum, 03 range). Commands used: <c>F000..F255</c>
/// intensity, <c>L0/L1</c> output off/on, <c>M</c> read F/S/L, <c>C</c> unit
/// error check.
/// </para>
/// <para>
/// Channels are configured by front-panel number (L1..L8 = 1..8); the wire code
/// is 0-based (L1 = "00"). The PD3 accepts a single TCP client, so the
/// connection is held open and closed gracefully — an abortive close can leave
/// the controller's only slot busy until it is power-cycled.
/// </para>
/// </summary>
public sealed class CcsPd3Adapter : ILightingDevice
{
    private const int FullScale = 255;
    private const int PanelChannelCount = 8;
    private const int IoTimeoutMs = 2000;          // PD3 drops a frame not completed within 4 s
    private const int DrainTimeoutMs = 500;
    private const int MaxReplyLength = 64;
    private const int MaxDrainReads = 16;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);

    private readonly object _lock = new();
    private readonly LightingControllerConfig _config;
    private readonly List<LightingChannelConfig> _channels;
    private readonly Dictionary<int, ChannelCache> _cache = new();
    private TcpClient? _client;
    private NetworkStream? _stream;
    // Written under _lock; volatile so IsConnected can be read lock-free (UI threads
    // poll it and must not wait behind an in-flight command's I/O timeout).
    private volatile bool _connected;
    private string _lastError = string.Empty;

    private sealed class ChannelCache
    {
        public int Intensity;
        public bool On;
        public string Mode = string.Empty;
    }

    /// <summary>The controller refused a command (NG reply) — the link itself is healthy.</summary>
    /// <remarks>Public only to satisfy S3871; never escapes the adapter (mapped to <see cref="LightingError"/>).</remarks>
    public sealed class Pd3RejectedException(string message) : Exception(message);

    /// <summary>A reply that is missing, malformed or out of sequence — the link can't be trusted.</summary>
    /// <remarks>Public only to satisfy S3871; never escapes the adapter (mapped to <see cref="LightingError"/>).</remarks>
    public sealed class Pd3ProtocolException(string message) : IOException(message);

    public CcsPd3Adapter(LightingControllerConfig config)
    {
        _config = config;
        _channels = new List<LightingChannelConfig>();
        foreach (var c in config.Channels.Where(c => c.Enabled))
        {
            if (c.Channel < 1 || c.Channel > PanelChannelCount || _cache.ContainsKey(c.Channel))
            {
                Log.Warning("[Lighting] {Device}: channel {Channel} ('{Name}') ignored — must be a unique L1..L{Max} number",
                    config.DeviceId, c.Channel, c.Name, PanelChannelCount);
                continue;
            }
            _channels.Add(c);
            _cache[c.Channel] = new ChannelCache { Intensity = Math.Clamp(c.DefaultIntensity, 0, FullScale) };
        }
    }

    public string DeviceId => _config.DeviceId;
    public string DisplayName => _config.DeviceName;
    public string ControllerType => _config.ControllerType;
    public IReadOnlyList<LightingChannelConfig> Channels => _channels;
    public int MaxIntensity => FullScale;
    public bool IsConnected => _connected;

    public bool Connect()
    {
        lock (_lock)
        {
            if (_connected) return true;
            try
            {
                _client = new TcpClient { NoDelay = true, ReceiveTimeout = IoTimeoutMs, SendTimeout = IoTimeoutMs };
                if (!_client.ConnectAsync(_config.IpAddress, _config.Port).Wait(ConnectTimeout))
                    throw new TimeoutException($"connect timed out after {ConnectTimeout.TotalSeconds:F0} s " +
                                               "(the PD3 accepts only one TCP client)");
                _stream = _client.GetStream();
                _connected = true;
                _lastError = string.Empty;
                Log.Information("[Lighting] {Device}: connected to {Ip}:{Port}",
                    _config.DeviceId, _config.IpAddress, _config.Port);
            }
            catch (Exception ex)
            {
                _lastError = (ex as AggregateException)?.InnerException?.Message ?? ex.Message;
                Log.Error("[Lighting] {Device}: connect to {Ip}:{Port} failed — {Error}",
                    _config.DeviceId, _config.IpAddress, _config.Port, _lastError);
                Close(graceful: false);
                return false;
            }

            // Read the real channel state (ON/OFF is not retained across a PD3
            // power cycle). A failure here closes the link again.
            PollLocked();
            return _connected;
        }
    }

    public bool Disconnect()
    {
        lock (_lock)
        {
            if (!_connected) return true;
            Close(graceful: true);
            Log.Information("[Lighting] {Device}: disconnected", _config.DeviceId);
            return true;
        }
    }

    public LightingError SetIntensity(int channel, int intensity)
    {
        lock (_lock)
        {
            var check = CheckChannel(channel);
            if (check != LightingError.Success) return check;

            intensity = Math.Clamp(intensity, 0, FullScale);
            var cache = _cache[channel];
            return Execute($"L{channel} intensity {intensity}", () =>
            {
                // F000 still glows (~0.4 %) on 24 V light units, so 0 means "off":
                // drop the output first; otherwise set the level, then switch on.
                if (intensity == 0)
                {
                    Command(channel, "L0");
                    cache.On = false;
                    Command(channel, "F000");
                }
                else
                {
                    Command(channel, $"F{intensity:D3}");
                    Command(channel, "L1");
                    cache.On = true;
                }
                cache.Intensity = intensity;
            });
        }
    }

    public LightingError SetOutput(int channel, bool isOn)
    {
        lock (_lock)
        {
            var check = CheckChannel(channel);
            if (check != LightingError.Success) return check;

            var cache = _cache[channel];
            return Execute($"L{channel} output {(isOn ? "on" : "off")}", () =>
            {
                Command(channel, isOn ? "L1" : "L0");
                cache.On = isOn;
            });
        }
    }

    public LightingError Poll()
    {
        lock (_lock)
        {
            return _connected ? PollLocked() : LightingError.Disconnected;
        }
    }

    public LightingStatus GetStatus()
    {
        lock (_lock)
        {
            var channels = _channels.Select(c =>
            {
                var s = _cache[c.Channel];
                return new LightingChannelStatus
                {
                    Channel = c.Channel, Name = c.Name, Intensity = s.Intensity, On = s.On, Mode = s.Mode,
                };
            }).ToList();
            return new LightingStatus { Connected = _connected, Channels = channels, LastError = _lastError };
        }
    }

    // ------------------------------------------------------------------ internals (caller holds _lock)

    private LightingError CheckChannel(int channel)
    {
        if (!_connected) return LightingError.Disconnected;
        return _cache.ContainsKey(channel) ? LightingError.Success : LightingError.InvalidChannel;
    }

    /// <summary>Unit error check, then an <c>M</c> read-back of every configured channel.</summary>
    private LightingError PollLocked()
    {
        var warnings = new List<string>();
        var result = Execute("poll", () =>
        {
            // C: "00" normal, "11" overcurrent or fan stop (output stopped).
            if (Transact("00", "C") == "11")
                warnings.Add("controller reports overcurrent / fan-stop error");

            foreach (var c in _channels)
            {
                var data = Command(c.Channel, "M");       // e.g. "F075.S04.L0"
                if (data.Length != 11 || data[0] != 'F' || data[4] != '.' || data[5] != 'S'
                    || data[8] != '.' || data[9] != 'L'
                    || !int.TryParse(data.AsSpan(1, 3), out var f) || !(data[10] is '0' or '1'))
                    throw new Pd3ProtocolException($"unexpected status reply '{data}' for L{c.Channel}");

                var cache = _cache[c.Channel];
                cache.Intensity = f;
                cache.Mode = data.Substring(6, 2);
                cache.On = data[10] == '1';
                if (cache.Mode != "00")
                    warnings.Add($"L{c.Channel} ({c.Name}) is in strobe mode S{cache.Mode} — dark without a trigger");
            }
        });
        if (result == LightingError.Success)
            _lastError = string.Join("; ", warnings);
        return result;
    }

    /// <summary>
    /// Runs a command sequence, mapping failures: a rejected command keeps the
    /// link; any I/O, timeout or protocol failure closes it (it can no longer
    /// be trusted to be in step).
    /// </summary>
    private LightingError Execute(string what, Action action)
    {
        try
        {
            action();
            return LightingError.Success;
        }
        catch (Pd3RejectedException ex)
        {
            _lastError = $"{what}: {ex.Message}";
            Log.Warning("[Lighting] {Device}: {Error}", _config.DeviceId, _lastError);
            return LightingError.OperationFailed;
        }
        catch (Exception ex) when (ex is IOException or SocketException or TimeoutException
                                       or ObjectDisposedException or InvalidOperationException)
        {
            _lastError = $"{what}: {ex.Message}";
            Log.Error("[Lighting] {Device}: {Error} — closing connection", _config.DeviceId, _lastError);
            Close(graceful: false);
            return LightingError.CommunicationError;
        }
    }

    /// <summary>Sends a command to a front-panel channel (L1 = 1) and returns the reply data.</summary>
    private string Command(int channel, string command)
        => Transact((channel - 1).ToString("D2"), command);

    /// <summary>Sends one frame and returns the data field of the accepted reply.</summary>
    private string Transact(string wireChannel, string command)
    {
        var body = $"@{wireChannel}{command}";
        var frame = Encoding.ASCII.GetBytes(body + Checksum(body) + "\r\n");
        _stream!.Write(frame, 0, frame.Length);

        var reply = ReadLine();
        if (reply.Length < 6 || reply[0] != '@')
            throw new Pd3ProtocolException($"'{body}': unexpected reply '{reply}'");
        if (Checksum(reply[..^2]) != reply[^2..])
            throw new Pd3ProtocolException($"'{body}': reply checksum mismatch in '{reply}'");
        if (reply.Substring(1, 2) != wireChannel)
            throw new Pd3ProtocolException($"'{body}': reply for another channel '{reply}'");

        var data = reply[4..^2];
        return reply[3] switch
        {
            'O' => data,
            'N' => throw new Pd3RejectedException(data switch
            {
                "01" => $"'{body}' rejected: command error (N01)",
                "02" => $"'{body}' rejected: checksum error (N02)",
                "03" => $"'{body}' rejected: value out of range (N03)",
                _ => $"'{body}' rejected: N{data}",
            }),
            _ => throw new Pd3ProtocolException($"'{body}': unexpected reply '{reply}'"),
        };
    }

    /// <summary>Low byte of the ASCII sum of <paramref name="text"/>, as 2 uppercase hex digits.</summary>
    private static string Checksum(string text)
    {
        int sum = 0;
        foreach (var c in text)
            sum += (byte)c;
        return (sum & 0xFF).ToString("X2");
    }

    /// <summary>
    /// Reads one reply through its LF terminator (CR dropped). Bounded by
    /// <see cref="MaxReplyLength"/>; the socket's ReceiveTimeout bounds the wait
    /// (a timeout surfaces as an <see cref="IOException"/>).
    /// </summary>
    private string ReadLine()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < MaxReplyLength; i++)
        {
            var b = _stream!.ReadByte();
            if (b < 0) throw new IOException("connection closed by the controller");
            if (b == '\n') return sb.ToString();
            if (b != '\r') sb.Append((char)b);
        }
        throw new Pd3ProtocolException($"reply exceeds {MaxReplyLength} bytes without a terminator");
    }

    /// <summary>
    /// Closes the link. Graceful: half-close, then drain (bounded) until the PD3
    /// closes its side, so its single client slot is released immediately.
    /// </summary>
    private void Close(bool graceful)
    {
        if (graceful && _client is { Connected: true } && _stream != null)
        {
            try
            {
                _client.Client.Shutdown(SocketShutdown.Send);
                _client.ReceiveTimeout = DrainTimeoutMs;
                var buffer = new byte[64];
                for (var i = 0; i < MaxDrainReads; i++)
                    if (_stream.Read(buffer, 0, buffer.Length) == 0) break;
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                // Peer didn't close within the drain window — dispose anyway.
            }
        }

        _stream?.Dispose();
        _client?.Dispose();
        _stream = null;
        _client = null;
        _connected = false;
    }
}
