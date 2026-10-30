using System.Globalization;
using System.Net.Sockets;
using System.Text;
using Horizon.Config;
using Horizon.Smu.Models;
using Serilog;

namespace Horizon.Smu;

/// <summary>
/// Keithley 2602B dual-channel SourceMeter. The Series 2600 speaks TSP — a
/// Lua-flavoured command set addressing channel A as <c>smua</c> and channel B
/// as <c>smub</c>. Transport by resource: a VISA <c>…::INSTR</c> resource (VXI-11,
/// e.g. <c>TCPIP0::192.168.0.111::INSTR</c>) opens through NI-VISA, as FireDog does
/// on the DLAM rig; <c>host</c>, <c>host:port</c> or <c>…::port::SOCKET</c> open a raw
/// TCP socket (default port 5025). Connect is confirmed by an <c>*IDN?</c> reply —
/// an instrument that accepts the link but never answers is NOT connected.
///
/// The <see cref="ISmuDevice"/> source surface is single-channel: the source level and
/// mode go to the PRIMARY channel — the first entry of <see cref="SmuDeviceConfig.EnabledChannels"/>
/// (1 → smua, 2 → smub), defaulting to smua. But an SMU measures only with its output ON
/// (an output that is off reads ~0 A, exactly like a dark photodiode), so connect configures
/// EVERY enabled channel (voltage source at 0 V) and output ON / OFF and compliance apply to
/// every enabled channel — the primary at its set level, the others at 0 V (FireDog's rule for
/// this instrument). Every channel is readable through <see cref="ReadMeasurement"/>.
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
    /// <summary>True for a VISA (VXI-11 INSTR) session, false for a raw socket.</summary>
    private readonly bool _useVisa;
    private int _visaRm;
    private int _visaInstr;
    private bool _connected;

    /// <summary>A reply line that is a TSP prompt token, not data — skipped by <see cref="Query"/>.</summary>
    private static readonly string[] PromptTokens = { "TSP>", "TSP?", ">>>>" };

    /// <summary>Lines read past prompt tokens before a query gives up on finding its reply.</summary>
    private const int MaxReplyLines = 4;
    private SmuSourceMode _mode = SmuSourceMode.Voltage;
    private bool _outputOn;
    private string _lastError = string.Empty;

    public Keithley2602BAdapter(SmuDeviceConfig config)
    {
        _config = config;
        _useVisa = config.VisaResource.Trim().EndsWith("::INSTR", StringComparison.OrdinalIgnoreCase);
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
                OpenTransport();

                // Silence the TSP prompts and error echo BEFORE the first query — with them
                // on, "TSP>" tokens / error text interleave with print() replies and every
                // read lands one reply out of step. Then prove the instrument answers.
                Write("localnode.prompts = 0");
                Write("localnode.showerrors = 0");
                var identity = Query("*IDN?");
                if (identity.Length == 0)
                    throw new IOException("no reply to *IDN? — the link opened but the instrument does not answer");
                _connected = true;

                // Configure every enabled channel: smuX.reset() (DC volts, 0 V, output OFF),
                // voltage-source mode, NPLC, measurement auto/fixed range, compliance, level 0.
                Write("errorqueue.clear()");
                Write("format.data = format.ASCII");
                foreach (var smu in ChannelNames())
                {
                    Write($"{smu}.reset()");
                    Write($"{smu}.source.func = {smu}.OUTPUT_DCVOLTS");
                    Write($"{smu}.source.levelv = 0");
                    Write($"{smu}.measure.nplc = {Fmt(_config.MeasurementNplc)}");
                    Write(_config.AutoRange
                        ? $"{smu}.measure.autorangei = {smu}.AUTORANGE_ON"
                        : $"{smu}.measure.rangei = {Fmt(_config.FixedRange)}");
                    if (_config.DefaultCompliance > 0)
                        Write($"{smu}.source.limiti = {Fmt(_config.DefaultCompliance)}");
                }
                _mode = SmuSourceMode.Voltage;
                _outputOn = false;

                _lastError = string.Empty;
                Log.Information("[SMU] {Device}: connected — {Identity} ({Link}, primary {Channel})",
                    _config.Name, identity, _useVisa ? _config.VisaResource : $"{_host}:{_port}", _smu);
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

    /// <summary>Opens the VISA session or the raw socket (see the class remarks).</summary>
    private void OpenTransport()
    {
        if (!_useVisa)
        {
            _client = new TcpClient { ReceiveTimeout = 5000, SendTimeout = 5000 };
            _client.Connect(_host, _port);
            _stream = _client.GetStream();
            return;
        }
        if (VisaNative.OpenDefaultRM(out _visaRm) < VisaNative.VI_SUCCESS)
            throw new IOException("NI-VISA resource manager unavailable (visa64.dll)");
        if (VisaNative.Open(_visaRm, _config.VisaResource, 0, 5000, out _visaInstr) < VisaNative.VI_SUCCESS)
            throw new IOException($"VISA open failed for {_config.VisaResource}");
        if (VisaNative.SetAttribute(_visaInstr, VisaNative.VI_ATTR_TMO_VALUE, 5000) < VisaNative.VI_SUCCESS)
            Log.Warning("[SMU] {Device}: could not set the VISA timeout", _config.Name);
        // Drop whatever a previous session left in the buffers.
        if (VisaNative.Clear(_visaInstr) < VisaNative.VI_SUCCESS)
            Log.Warning("[SMU] {Device}: VISA clear failed", _config.Name);
    }

    private void Cleanup()
    {
        try { _stream?.Dispose(); } catch { }
        try { _client?.Dispose(); } catch { }
        // Non-zero handles mean visa64.dll is loaded; a failed close leaves nothing to undo.
        if (_visaInstr != 0) _ = VisaNative.Close(_visaInstr);
        if (_visaRm != 0) _ = VisaNative.Close(_visaRm);
        _stream = null;
        _client = null;
        _visaInstr = 0;
        _visaRm = 0;
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

    /// <summary>Compliance on every enabled channel (the others source 0 V, so a current limit protects them too).</summary>
    public SmuError SetCompliance(double value)
        => Command(() =>
        {
            foreach (var smu in ChannelNames())
                Write(smu != _smu || _mode == SmuSourceMode.Voltage
                    ? $"{smu}.source.limiti = {Fmt(value)}"
                    : $"{smu}.source.limitv = {Fmt(value)}");
        });

    /// <summary>Output ON on every enabled channel — a channel whose output is off cannot measure.</summary>
    public SmuError OutputOn()
        => Command(() =>
        {
            foreach (var smu in ChannelNames()) Write($"{smu}.source.output = {smu}.OUTPUT_ON");
            _outputOn = true;
        });

    /// <summary>Output OFF on every enabled channel.</summary>
    public SmuError OutputOff()
        => Command(() =>
        {
            foreach (var smu in ChannelNames()) Write($"{smu}.source.output = {smu}.OUTPUT_OFF");
            _outputOn = false;
        });

    /// <summary>The TSP names of the enabled channels, primary first, each once.</summary>
    private IEnumerable<string> ChannelNames()
        => new[] { _smu }.Concat(Channels.Select(TspName)).Distinct();

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
        if (_useVisa)
        {
            if (VisaNative.WriteString(_visaInstr, command) < VisaNative.VI_SUCCESS)
                throw new IOException($"VISA write failed: {command}");
            return;
        }
        var data = Encoding.ASCII.GetBytes(command + "\n");
        _stream!.Write(data, 0, data.Length);
    }

    /// <summary>Sends a TSP query and returns its reply line, skipping any stray prompt tokens.</summary>
    private string Query(string command)
    {
        Write(command);
        for (int i = 0; i < MaxReplyLines; i++)
        {
            var line = ReadReply().Trim();
            if (!PromptTokens.Contains(line)) return line;
        }
        return string.Empty;
    }

    /// <summary>One reply: a VISA read (ended by the END bit) or a raw-socket line.</summary>
    private string ReadReply()
    {
        if (!_useVisa) return ReadLine();
        if (VisaNative.ReadString(_visaInstr, out var response) < VisaNative.VI_SUCCESS)
            throw new IOException("VISA read failed (timeout?)");
        return response;
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
