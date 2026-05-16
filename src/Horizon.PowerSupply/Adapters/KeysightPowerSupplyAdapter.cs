using System.Globalization;
using System.Net.Sockets;
using System.Text;
using Horizon.Config;
using Horizon.PowerSupply.Models;
using Serilog;

namespace Horizon.PowerSupply;

/// <summary>
/// Keysight E36xxx programmable supply over LXI — SCPI on a raw TCP socket
/// (typically port 5025). Multi-channel supplies select a channel with
/// <c>INST:NSEL</c> before each command. All I/O is serialized by a lock.
/// </summary>
public sealed class KeysightPowerSupplyAdapter : IPowerSupplyDevice
{
    private readonly object _lock = new();
    private readonly PowerSupplyConfig _config;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private bool _connected;
    private string _lastError = string.Empty;

    public KeysightPowerSupplyAdapter(PowerSupplyConfig config) => _config = config;

    public string DeviceName => _config.Name;
    public string ControllerType => _config.Type;
    public int ChannelCount => _config.Channels < 1 ? 1 : _config.Channels;
    public bool IsConnected { get { lock (_lock) return _connected; } }

    public bool Connect()
    {
        lock (_lock)
        {
            if (_connected) return true;
            try
            {
                _client = new TcpClient { ReceiveTimeout = 2000, SendTimeout = 2000 };
                _client.Connect(_config.Ip, _config.Port);
                _stream = _client.GetStream();
                _connected = true;
                Write("*CLS");
                _lastError = string.Empty;
                Log.Information("[PSU] {Device}: connected to {Ip}:{Port}",
                    _config.Name, _config.Ip, _config.Port);
                return true;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                Log.Error(ex, "[PSU] {Device}: connect failed", _config.Name);
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
            Log.Information("[PSU] {Device}: disconnected", _config.Name);
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

    public PowerSupplyError SetVoltage(int channel, double volts)
        => Command(channel, $"VOLT {volts.ToString("G6", CultureInfo.InvariantCulture)}");

    public PowerSupplyError SetCurrentLimit(int channel, double amps)
        => Command(channel, $"CURR {amps.ToString("G6", CultureInfo.InvariantCulture)}");

    public PowerSupplyError OutputOn(int channel) => Command(channel, "OUTP ON");

    public PowerSupplyError OutputOff(int channel) => Command(channel, "OUTP OFF");

    private PowerSupplyError Command(int channel, string scpi)
    {
        lock (_lock)
        {
            if (!_connected) return PowerSupplyError.Disconnected;
            if (channel < 1 || channel > ChannelCount) return PowerSupplyError.InvalidChannel;
            try
            {
                SelectChannel(channel);
                Write(scpi);
                return PowerSupplyError.Success;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                Log.Error(ex, "[PSU] {Device}: command '{Scpi}' failed", _config.Name, scpi);
                return PowerSupplyError.CommunicationError;
            }
        }
    }

    public PowerSupplyStatus GetStatus()
    {
        lock (_lock)
        {
            if (!_connected)
                return new PowerSupplyStatus { Connected = false, LastError = _lastError };

            var channels = new List<PowerSupplyChannelStatus>();
            try
            {
                for (int ch = 1; ch <= ChannelCount; ch++)
                {
                    SelectChannel(ch);
                    channels.Add(new PowerSupplyChannelStatus
                    {
                        Channel = ch,
                        Voltage = QueryDouble("MEAS:VOLT?"),
                        Current = QueryDouble("MEAS:CURR?"),
                        OutputOn = QueryDouble("OUTP?") != 0.0,
                    });
                }
                return new PowerSupplyStatus { Connected = true, Channels = channels };
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                return new PowerSupplyStatus { Connected = true, Channels = channels, LastError = ex.Message };
            }
        }
    }

    private void SelectChannel(int channel)
    {
        if (ChannelCount > 1)
            Write($"INST:NSEL {channel}");
    }

    private void Write(string command)
    {
        var data = Encoding.ASCII.GetBytes(command + "\n");
        _stream!.Write(data, 0, data.Length);
    }

    private double QueryDouble(string query)
    {
        Write(query);
        return double.TryParse(ReadLine(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value : 0.0;
    }

    private string ReadLine()
    {
        var sb = new StringBuilder();
        var buffer = new byte[1];
        while (_stream!.Read(buffer, 0, 1) == 1)
        {
            if (buffer[0] == (byte)'\n') break;
            sb.Append((char)buffer[0]);
        }
        return sb.ToString().Trim('\r', '\n', ' ');
    }
}
