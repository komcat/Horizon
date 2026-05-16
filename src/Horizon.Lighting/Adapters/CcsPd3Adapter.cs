using System.Net.Sockets;
using System.Text;
using Horizon.Config;
using Horizon.Lighting.Models;
using Serilog;

namespace Horizon.Lighting;

/// <summary>
/// CCS PD3 LED lighting controller over a TCP socket. Each intensity command is
/// a checksummed frame the controller acknowledges; <see cref="GetStatus"/> still
/// reflects the last commanded values (no periodic status read-back).
/// Note: the PD3 accepts only one TCP connection at a time.
/// </summary>
public sealed class CcsPd3Adapter : ILightingDevice
{
    private readonly object _lock = new();
    private readonly LightingControllerConfig _config;
    private readonly HashSet<int> _validChannels;
    private readonly Dictionary<int, int> _intensities = new();
    private TcpClient? _client;
    private NetworkStream? _stream;
    private bool _connected;
    private string _lastError = string.Empty;

    public CcsPd3Adapter(LightingControllerConfig config)
    {
        _config = config;
        _validChannels = config.Channels.Select(c => c.Channel).ToHashSet();
        foreach (var c in config.Channels)
            _intensities[c.Channel] = c.DefaultIntensity;
    }

    public string DeviceId => _config.DeviceId;
    public string DisplayName => _config.DeviceName;
    public string ControllerType => _config.ControllerType;
    public IReadOnlyList<LightingChannelConfig> Channels => _config.Channels;
    public bool IsConnected { get { lock (_lock) return _connected; } }

    public bool Connect()
    {
        lock (_lock)
        {
            if (_connected) return true;
            try
            {
                _client = new TcpClient { ReceiveTimeout = 2000, SendTimeout = 2000 };
                _client.Connect(_config.IpAddress, _config.Port);
                _stream = _client.GetStream();
                _connected = true;
                _lastError = string.Empty;
                Log.Information("[Lighting] {Device}: connected to {Ip}:{Port}",
                    _config.DeviceId, _config.IpAddress, _config.Port);
                return true;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                Log.Error(ex, "[Lighting] {Device}: connect failed", _config.DeviceId);
                Cleanup();
                return false;
            }
        }
    }

    public bool Disconnect()
    {
        lock (_lock)
        {
            if (!_connected) return true;
            Cleanup();
            Log.Information("[Lighting] {Device}: disconnected", _config.DeviceId);
            return true;
        }
    }

    private void Cleanup()
    {
        try { _stream?.Dispose(); } catch { }
        try { _client?.Dispose(); } catch { }
        _stream = null;
        _client = null;
        _connected = false;
    }

    public LightingError SetIntensity(int channel, int intensity)
    {
        lock (_lock)
        {
            if (!_connected) return LightingError.Disconnected;
            if (!_validChannels.Contains(channel)) return LightingError.InvalidChannel;
            try
            {
                SendIntensity(channel, intensity);
                _intensities[channel] = intensity;
                return LightingError.Success;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                Log.Error(ex, "[Lighting] {Device}: SetIntensity ch{Channel} failed", _config.DeviceId, channel);
                return LightingError.CommunicationError;
            }
        }
    }

    public LightingStatus GetStatus()
    {
        lock (_lock)
        {
            var channels = _config.Channels.Select(c => new LightingChannelStatus
            {
                Channel = c.Channel,
                Name = c.Name,
                Intensity = _intensities.TryGetValue(c.Channel, out var i) ? i : 0,
            }).ToList();
            return new LightingStatus { Connected = _connected, Channels = channels, LastError = _lastError };
        }
    }

    /// <summary>
    /// Sends a channel-intensity command and checks the controller's reply.
    /// Frame: <c>@</c> + channel (2 digits) + <c>F</c> + intensity (3 digits) +
    /// a 2-hex-digit checksum + CRLF. The checksum is the low byte of the ASCII
    /// sum of the body. Matches the bobcat-engine <c>CCSPD3Adapter</c> reference.
    /// </summary>
    private void SendIntensity(int channel, int intensity)
    {
        intensity = Math.Clamp(intensity, 0, 255);
        var body = $"@{channel:D2}F{intensity:D3}";
        var frame = body + Checksum(body) + "\r\n";

        var data = Encoding.ASCII.GetBytes(frame);
        _stream!.Write(data, 0, data.Length);

        // The PD3 is request/response — confirm the command was accepted.
        var response = ReadResponse();
        if (response.Length == 0)
            throw new InvalidOperationException("PD3 no response");
        if (response.Contains("N01"))
            throw new InvalidOperationException("PD3 command error (N01)");
        if (response.Contains("N02"))
            throw new InvalidOperationException("PD3 checksum error (N02)");
        if (response.Contains("N03"))
            throw new InvalidOperationException("PD3 value out of range (N03)");
    }

    /// <summary>Low byte of the ASCII sum of <paramref name="body"/>, as 2 uppercase hex digits.</summary>
    private static string Checksum(string body)
    {
        int sum = 0;
        foreach (var c in body)
            sum += (byte)c;
        return (sum & 0xFF).ToString("X2");
    }

    /// <summary>
    /// Reads the controller's reply up to a CR/LF terminator. The TcpClient's
    /// <c>ReceiveTimeout</c> bounds the wait; a timeout returns what was read.
    /// </summary>
    private string ReadResponse()
    {
        var sb = new StringBuilder();
        var buffer = new byte[1];
        try
        {
            while (true)
            {
                var read = _stream!.Read(buffer, 0, 1);
                if (read == 0) break;                          // socket closed
                if (buffer[0] == (byte)'\r' || buffer[0] == (byte)'\n')
                {
                    if (sb.Length > 0) break;                  // end of frame
                    continue;                                  // skip leading terminators
                }
                sb.Append((char)buffer[0]);
            }
        }
        catch (IOException) { /* receive timeout — return what we have */ }
        return sb.ToString();
    }
}
