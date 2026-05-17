using System.Globalization;
using System.Net.Sockets;
using System.Text;
using Horizon.Config;
using Horizon.Smu.Models;
using Serilog;

namespace Horizon.Smu;

/// <summary>
/// Keithley 2602B dual-channel SourceMeter over a raw TCP socket (LAN, port
/// 5025). Unlike the 2400 (SCPI over VISA), the Series 2600 speaks TSP — a
/// Lua-flavoured command set addressing channel A as <c>smua</c> and channel B
/// as <c>smub</c>.
///
/// The <see cref="ISmuDevice"/> surface is single-channel, so this adapter
/// drives one channel — the first entry of <see cref="SmuDeviceConfig.EnabledChannels"/>
/// (1 → smua, 2 → smub), defaulting to smua.
/// </summary>
public sealed class Keithley2602BAdapter : ISmuDevice
{
    private readonly object _lock = new();
    private readonly SmuDeviceConfig _config;
    private readonly string _host;
    private readonly int _port;
    /// <summary>"smua" or "smub" — the channel this adapter drives.</summary>
    private readonly string _smu;

    private TcpClient? _client;
    private NetworkStream? _stream;
    private bool _connected;
    private SmuSourceMode _mode = SmuSourceMode.Voltage;
    private bool _outputOn;
    private string _lastError = string.Empty;

    public Keithley2602BAdapter(SmuDeviceConfig config)
    {
        _config = config;
        (_host, _port) = ParseResource(config.VisaResource);
        Channels = config.EnabledChannels.Count > 0
            ? config.EnabledChannels.ToList()
            : Enumerable.Range(1, Math.Max(1, config.ChannelCount)).ToList();
        _smu = TspName(Channels.Count > 0 ? Channels[0] : 1);
    }

    public string DeviceName => _config.Name;
    public string ControllerType => _config.Type;
    public IReadOnlyList<int> Channels { get; }
    public bool IsConnected { get { lock (_lock) return _connected; } }

    /// <summary>TSP channel name — channel 2 is <c>smub</c>, everything else <c>smua</c>.</summary>
    private static string TspName(int channel) => channel == 2 ? "smub" : "smua";

    public bool Connect()
    {
        lock (_lock)
        {
            if (_connected) return true;
            try
            {
                _client = new TcpClient { ReceiveTimeout = 5000, SendTimeout = 5000 };
                _client.Connect(_host, _port);
                _stream = _client.GetStream();
                _connected = true;

                // Reset, then configure the channel: voltage-source mode, NPLC,
                // measurement auto/fixed range, current compliance, level 0.
                Write("reset()");
                Write($"{_smu}.reset()");
                Write($"{_smu}.source.func = {_smu}.OUTPUT_DCVOLTS");
                Write($"{_smu}.source.levelv = 0");
                Write($"{_smu}.measure.nplc = {Fmt(_config.MeasurementNplc)}");
                Write(_config.AutoRange
                    ? $"{_smu}.measure.autorangei = {_smu}.AUTORANGE_ON"
                    : $"{_smu}.measure.rangei = {Fmt(_config.FixedRange)}");
                if (_config.DefaultCompliance > 0)
                    Write($"{_smu}.source.limiti = {Fmt(_config.DefaultCompliance)}");
                _mode = SmuSourceMode.Voltage;
                _outputOn = false;

                _lastError = string.Empty;
                Log.Information("[SMU] {Device}: connected ({Host}:{Port}, {Channel})",
                    _config.Name, _host, _port, _smu);
                return true;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                Log.Error(ex, "[SMU] {Device}: connect failed", _config.Name);
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
            Log.Information("[SMU] {Device}: disconnected", _config.Name);
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

    public SmuError SetSourceVoltage(double volts)
        => Command(() =>
        {
            if (_mode != SmuSourceMode.Voltage)
            {
                Write($"{_smu}.source.func = {_smu}.OUTPUT_DCVOLTS");
                _mode = SmuSourceMode.Voltage;
            }
            Write($"{_smu}.source.levelv = {Fmt(volts)}");
        });

    public SmuError SetSourceCurrent(double amps)
        => Command(() =>
        {
            if (_mode != SmuSourceMode.Current)
            {
                Write($"{_smu}.source.func = {_smu}.OUTPUT_DCAMPS");
                _mode = SmuSourceMode.Current;
            }
            Write($"{_smu}.source.leveli = {Fmt(amps)}");
        });

    public SmuError SetCompliance(double value)
        => Command(() => Write(_mode == SmuSourceMode.Voltage
            ? $"{_smu}.source.limiti = {Fmt(value)}"
            : $"{_smu}.source.limitv = {Fmt(value)}"));

    public SmuError OutputOn()
        => Command(() => { Write($"{_smu}.source.output = {_smu}.OUTPUT_ON"); _outputOn = true; });

    public SmuError OutputOff()
        => Command(() => { Write($"{_smu}.source.output = {_smu}.OUTPUT_OFF"); _outputOn = false; });

    public SmuStatus GetStatus()
    {
        lock (_lock)
        {
            if (!_connected)
                return new SmuStatus { Connected = false, LastError = _lastError };
            try
            {
                _outputOn = ParseDouble(Query($"print({_smu}.source.output)")) != 0.0;
                double voltage = 0, current = 0;
                if (_outputOn)
                {
                    // measure.iv() returns current, voltage — print() tab-separates them.
                    var parts = Query($"print({_smu}.measure.iv())")
                        .Split(new[] { '\t', ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        current = Clean(ParseDouble(parts[0]));
                        voltage = Clean(ParseDouble(parts[1]));
                    }
                }
                return new SmuStatus
                {
                    Connected = true,
                    OutputOn = _outputOn,
                    SourceMode = _mode,
                    Voltage = voltage,
                    Current = current,
                };
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                return new SmuStatus
                {
                    Connected = true,
                    OutputOn = _outputOn,
                    SourceMode = _mode,
                    LastError = ex.Message,
                };
            }
        }
    }

    public SmuReading ReadMeasurement(int channel)
    {
        lock (_lock)
        {
            if (!_connected)
                return new SmuReading { Channel = channel, Timestamp = DateTime.UtcNow };
            try
            {
                // Read the requested channel directly — independent of the
                // constructor's default _smu, so either channel is readable.
                var smu = TspName(channel);
                var parts = Query($"print({smu}.measure.iv())")
                    .Split(new[] { '\t', ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                return new SmuReading
                {
                    Channel = channel,
                    Current = parts.Length > 0 ? Clean(ParseDouble(parts[0])) : 0.0,
                    Voltage = parts.Length > 1 ? Clean(ParseDouble(parts[1])) : 0.0,
                    Timestamp = DateTime.UtcNow,
                };
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                Log.Error(ex, "[SMU] {Device}: ReadMeasurement failed", _config.Name);
                return new SmuReading { Channel = channel, Timestamp = DateTime.UtcNow };
            }
        }
    }

    public SmuError SetFastMode(bool fast)
        => Command(() =>
        {
            // Standard 2600-series TSP fast-measurement settings — applied to
            // every configured channel so a later-started stream is already in
            // fast mode. Isolated here for bench tuning.
            foreach (var channel in Channels)
            {
                var smu = TspName(channel);
                if (fast)
                {
                    Write($"{smu}.measure.nplc = 0.001");
                    Write($"{smu}.measure.autozero = {smu}.AUTOZERO_OFF");
                    Write($"{smu}.measure.autorangei = {smu}.AUTORANGE_OFF");
                }
                else
                {
                    Write($"{smu}.measure.nplc = {Fmt(_config.MeasurementNplc)}");
                    Write($"{smu}.measure.autozero = {smu}.AUTOZERO_AUTO");
                    Write(_config.AutoRange
                        ? $"{smu}.measure.autorangei = {smu}.AUTORANGE_ON"
                        : $"{smu}.measure.rangei = {Fmt(_config.FixedRange)}");
                }
            }
        });

    private SmuError Command(Action action)
    {
        lock (_lock)
        {
            if (!_connected) return SmuError.Disconnected;
            try
            {
                action();
                return SmuError.Success;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                Log.Error(ex, "[SMU] {Device}: command failed", _config.Name);
                return SmuError.CommunicationError;
            }
        }
    }

    /// <summary>Sends one newline-terminated TSP command.</summary>
    private void Write(string command)
    {
        var data = Encoding.ASCII.GetBytes(command + "\n");
        _stream!.Write(data, 0, data.Length);
    }

    /// <summary>Sends a TSP <c>print(...)</c> command and reads the reply line.</summary>
    private string Query(string command)
    {
        Write(command);
        return ReadLine();
    }

    /// <summary>Reads bytes until a newline; the 2600 terminates print() output with '\n'.</summary>
    private string ReadLine()
    {
        var sb = new StringBuilder();
        var buffer = new byte[1];
        while (true)
        {
            var read = _stream!.Read(buffer, 0, 1);
            if (read == 0) break;                 // socket closed
            if (buffer[0] == (byte)'\n') break;
            if (buffer[0] != (byte)'\r') sb.Append((char)buffer[0]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Extracts host and port from a VISA-style resource such as
    /// <c>TCPIP0::10.0.0.80::5025::SOCKET</c>, a bare <c>host:port</c>, or a
    /// bare hostname (defaulting the port to 5025).
    /// </summary>
    private static (string Host, int Port) ParseResource(string resource)
    {
        const int defaultPort = 5025;
        if (string.IsNullOrWhiteSpace(resource))
            return ("127.0.0.1", defaultPort);

        if (resource.Contains("::"))
        {
            var parts = resource.Split("::", StringSplitOptions.RemoveEmptyEntries);
            // TCPIP0 :: host :: port :: SOCKET  — host is parts[1], port parts[2].
            var host = parts.Length > 1 ? parts[1] : "127.0.0.1";
            var port = parts.Length > 2 && int.TryParse(parts[2], out var p) ? p : defaultPort;
            return (host, port);
        }

        var colon = resource.LastIndexOf(':');
        if (colon > 0 && int.TryParse(resource[(colon + 1)..], out var bp))
            return (resource[..colon], bp);

        return (resource, defaultPort);
    }

    private static double ParseDouble(string text)
        => double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value : 0.0;

    /// <summary>Maps the SCPI not-a-number sentinel (~9.9E37) to NaN.</summary>
    private static double Clean(double v) => Math.Abs(v) >= 9e37 ? double.NaN : v;

    private static string Fmt(double value) => value.ToString("G6", CultureInfo.InvariantCulture);
}
