using System.Globalization;
using Horizon.Common;
using Horizon.Engine;

namespace Horizon.Workflow.Signals;

/// <summary>
/// Built-in <see cref="ISignalSource"/> over the generic engine measurement facets
/// (SMU / PSU / Measurement / TEC / Laser). It mirrors the per-domain read steps
/// already in <c>StepDispatcher</c> (<c>smu.readCurrent</c>, <c>psu.readVoltage</c>, …)
/// but surfaces them as one unified, dynamically-built catalog so a single
/// <c>signal.read</c> block can pull any of them. Coordinate-only and product-free —
/// no SiPhOG knowledge lives here.
///
/// Key grammar (all device names verbatim, channels 1-based):
/// <list type="bullet">
///   <item><c>smu.{device}.ch{n}.current</c> | <c>.voltage</c></item>
///   <item><c>psu.{device}.ch{n}.voltage</c> | <c>.current</c></item>
///   <item><c>meas.{device}.ch{n}</c></item>
///   <item><c>tec.{device}.temperature</c></item>
///   <item><c>laser.{device}.temperature</c> | <c>laser.{device}.current</c></item>
/// </list>
/// </summary>
internal sealed class EngineSignalSource : ISignalSource
{
    private static readonly string[] Prefixes = { "smu.", "psu.", "meas.", "tec.", "laser." };

    private readonly IHorizonEngine _engine;

    public EngineSignalSource(IHorizonEngine engine) => _engine = engine;

    public bool Owns(string key)
        => Prefixes.Any(p => key.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<SignalDescriptor> Describe()
    {
        var list = new List<SignalDescriptor>();

        // --- SMU: source-measure, per configured channel ---
        foreach (var dev in _engine.Smu.DeviceNames)
        {
            if (!_engine.Smu.IsConnected(dev)) continue;
            var channels = _engine.Smu.GetDevice(dev)?.Channels;
            var chans = channels is { Count: > 0 } ? channels : new[] { 1 };
            bool multi = chans.Count > 1;
            foreach (var ch in chans)
            {
                var sfx = multi ? $" CH{ch}" : string.Empty;
                list.Add(new SignalDescriptor($"smu.{dev}.ch{ch}.current",  $"SMU {dev}{sfx} — current", "A", "SMU"));
                list.Add(new SignalDescriptor($"smu.{dev}.ch{ch}.voltage",  $"SMU {dev}{sfx} — voltage", "V", "SMU"));
            }
        }

        // --- PSU: programmable supply outputs, per channel ---
        foreach (var dev in _engine.PowerSupply.DeviceNames)
        {
            if (!_engine.PowerSupply.IsConnected(dev)) continue;
            int count = Math.Max(1, _engine.PowerSupply.GetDevice(dev)?.ChannelCount ?? 1);
            bool multi = count > 1;
            for (int ch = 1; ch <= count; ch++)
            {
                var sfx = multi ? $" CH{ch}" : string.Empty;
                list.Add(new SignalDescriptor($"psu.{dev}.ch{ch}.voltage", $"PSU {dev}{sfx} — voltage", "V", "PSU"));
                list.Add(new SignalDescriptor($"psu.{dev}.ch{ch}.current", $"PSU {dev}{sfx} — current", "A", "PSU"));
            }
        }

        // --- Measurement: picoammeter channels (unit from the device's last poll) ---
        foreach (var dev in _engine.Measurement.DeviceNames)
        {
            if (!_engine.Measurement.IsConnected(dev)) continue;
            int count = Math.Max(1,
                _engine.Measurement.GetDevice(dev)?.ChannelCount ?? _engine.Measurement.GetState(dev).Channels.Count);
            var unit = _engine.Measurement.GetState(dev).Unit;
            if (string.IsNullOrWhiteSpace(unit)) unit = "A";
            bool multi = count > 1;
            for (int ch = 1; ch <= count; ch++)
            {
                var sfx = multi ? $" CH{ch}" : string.Empty;
                list.Add(new SignalDescriptor($"meas.{dev}.ch{ch}", $"Measurement {dev}{sfx}", unit, "Measurement"));
            }
        }

        // --- TEC: standalone cooler temperature ---
        foreach (var dev in _engine.Tec.DeviceNames)
        {
            if (!_engine.Tec.IsConnected(dev)) continue;
            list.Add(new SignalDescriptor($"tec.{dev}.temperature", $"TEC {dev} — temperature", "°C", "TEC"));
        }

        // --- Laser controller (CLD101x): TEC temperature + diode current ---
        foreach (var dev in _engine.Laser.DeviceNames)
        {
            if (!_engine.Laser.IsConnected(dev)) continue;
            list.Add(new SignalDescriptor($"laser.{dev}.temperature", $"Laser {dev} — temperature", "°C", "Laser"));
            list.Add(new SignalDescriptor($"laser.{dev}.current",     $"Laser {dev} — current",     "mA", "Laser"));
        }

        return list;
    }

    public async Task<Result<double>> ReadAsync(string key, CancellationToken ct = default)
    {
        var parts = key.Split('.');
        try
        {
            switch (parts[0].ToLowerInvariant())
            {
                // smu.{device}.ch{n}.{current|voltage}
                case "smu" when parts.Length == 4:
                {
                    var dev = parts[1];
                    int ch = ParseChannel(parts[2]);
                    var r = await _engine.Smu.ReadMeasurementAsync(dev, ch).ConfigureAwait(false);
                    if (!r.IsSuccess) return Result<double>.Fail(r.Error, r.Message);
                    return Result<double>.Ok(IsCurrent(parts[3]) ? r.Value.Current : r.Value.Voltage);
                }

                // psu.{device}.ch{n}.{voltage|current}
                case "psu" when parts.Length == 4:
                {
                    var dev = parts[1];
                    int ch = ParseChannel(parts[2]);
                    await _engine.PowerSupply.RefreshAsync().ConfigureAwait(false);
                    var state = _engine.PowerSupply.GetState(dev);
                    if (!state.Valid)
                        return Result<double>.Fail(ErrorCode.OperationFailed, $"Power supply '{dev}' has no readings yet.");
                    var pch = state.Channels.FirstOrDefault(c => c.Channel == ch);
                    if (pch == null)
                        return Result<double>.Fail(ErrorCode.InvalidParameter, $"Channel {ch} not found for '{dev}'.");
                    return Result<double>.Ok(IsCurrent(parts[3]) ? pch.Current : pch.Voltage);
                }

                // meas.{device}.ch{n}
                case "meas" when parts.Length == 3:
                {
                    var dev = parts[1];
                    int ch = ParseChannel(parts[2]);
                    var state = _engine.Measurement.GetState(dev);
                    int idx = ch - 1;
                    if (!state.Valid || idx < 0 || idx >= state.Channels.Count)
                        return Result<double>.Fail(ErrorCode.OperationFailed, $"Measurement '{dev}' CH{ch}: no reading available.");
                    return Result<double>.Ok(state.Channels[idx]);
                }

                // tec.{device}.temperature
                case "tec" when parts.Length == 3:
                {
                    var dev = parts[1];
                    await _engine.Tec.RefreshAsync().ConfigureAwait(false);
                    var t = _engine.Tec.GetState(dev).Temperature;
                    if (double.IsNaN(t))
                        return Result<double>.Fail(ErrorCode.OperationFailed, $"TEC '{dev}' has no temperature reading.");
                    return Result<double>.Ok(t);
                }

                // laser.{device}.{temperature|current}
                case "laser" when parts.Length == 3:
                {
                    var dev = parts[1];
                    var state = _engine.Laser.GetState(dev);
                    if (!state.Connected)
                        return Result<double>.Fail(ErrorCode.OperationFailed, $"Laser '{dev}' is not connected.");
                    return Result<double>.Ok(IsCurrent(parts[2]) ? state.LaserCurrent : state.Temperature);
                }
            }
        }
        catch (Exception ex)
        {
            return Result<double>.Fail(ErrorCode.OperationFailed, $"signal '{key}': {ex.Message}");
        }

        return Result<double>.Fail(ErrorCode.InvalidParameter, $"Unrecognised engine signal key '{key}'.");
    }

    private static bool IsCurrent(string field)
        => field.Equals("current", StringComparison.OrdinalIgnoreCase);

    /// <summary>Parses a <c>"ch{n}"</c> (or bare <c>"{n}"</c>) channel token; defaults to 1.</summary>
    private static int ParseChannel(string token)
    {
        var t = token.StartsWith("ch", StringComparison.OrdinalIgnoreCase) ? token[2..] : token;
        return int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : 1;
    }
}
