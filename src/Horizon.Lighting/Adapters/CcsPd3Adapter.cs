using System.Net.Sockets;
using System.Text;
using Horizon.Config;
using Horizon.Lighting.Models;
using Serilog;

namespace Horizon.Lighting;

/// <summary>
/// CCS PD3 LED lighting controller over a TCP socket. The controller is
/// write-only here — channel intensities are commanded and cached; there is no
/// read-back, so <see cref="GetStatus"/> reflects the last commanded values.
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
    /// Sends the channel-intensity command. NOTE: the wire format below is a
    /// plausible placeholder — confirm it against the CCS PD3 protocol manual
    /// and adjust this one method to match the controller.
    /// </summary>
    private void SendIntensity(int channel, int intensity)
    {
        var data = Encoding.ASCII.GetBytes($"L{channel},{intensity}\r\n");
        _stream!.Write(data, 0, data.Length);
    }
}
