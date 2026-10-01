using Horizon.Common;
using Horizon.Engine;

namespace Horizon.Workflow;

/// <summary>Inputs to a verified power-supply channel power-up. Channel is 1-based.</summary>
public sealed record PsuPowerUpRequest(
    string Device,
    int Channel,
    double Volts,
    double Amps,
    int SettleMs = PsuPowerUp.DefaultSettleMs,
    double ToleranceV = PsuPowerUp.DefaultToleranceV,
    bool OffOnFail = true,
    int VerifyMs = PsuPowerUp.DefaultVerifyMs,
    bool AcceptCurrentLimit = false);

/// <summary>The verified read-back of a successful power-up.</summary>
public sealed record PsuPowerUpReading(double Voltage, double Current);

/// <summary>
/// One-shot, verified channel power-up — shared by the <c>psu.powerUp</c> program
/// block and the <c>machine.psu.powerUp</c> script function so the two can't drift:
/// verify the link (reconnect once if it dropped) → current limit → voltage →
/// output ON → settle → fresh read-back, which must show the output ON and the
/// voltage within tolerance of the setpoint.
/// Fail-safe: once the output has been commanded ON, any failure or cancel
/// switches it back OFF (unless <see cref="PsuPowerUpRequest.OffOnFail"/> is false).
/// </summary>
public static class PsuPowerUp
{
    public const int DefaultSettleMs = 150;
    public const double DefaultToleranceV = 0.1;
    public const int MaxSettleMs = 10_000;

    /// <summary>How long after the settle the read-back keeps being polled for the setpoint.</summary>
    public const int DefaultVerifyMs = 2_000;
    public const int MaxVerifyMs = 10_000;
    private const int VerifyPollMs = 100;

    /// <summary>Read current at or above this fraction of the limit counts as "at the limit" (CC mode).</summary>
    private const double CurrentLimitFraction = 0.98;

    public static async Task<Result<PsuPowerUpReading>> RunAsync(
        IPowerSupplyFacet psu, PsuPowerUpRequest req, Action<LogLevel, string> log, CancellationToken ct)
    {
        var dev = req.Device;
        if (string.IsNullOrWhiteSpace(dev) || psu.GetDevice(dev) == null)
            return Fail($"unknown power supply '{dev}'.", ErrorCode.InvalidParameter);

        var channel = req.Channel;
        var settleMs = Math.Clamp(req.SettleMs, 0, MaxSettleMs);
        var tolV = Math.Abs(req.ToleranceV);
        var tag = $"{dev} CH{channel}";

        // 1. Verify the connection — one reconnect attempt if the link dropped.
        if (!psu.IsConnected(dev))
        {
            log(LogLevel.Warning, $"psu.powerUp: {dev} not connected — reconnecting…");
            var c = await psu.ConnectAsync(dev, ct).ConfigureAwait(false);
            if (!c.IsSuccess)
                return Fail($"{dev} not connected — {c.Message}");
        }

        // 2. Current limit BEFORE voltage, so the load is protected the instant the output turns on.
        var r = await psu.SetCurrentLimitAsync(dev, channel, req.Amps).ConfigureAwait(false);
        if (!r.IsSuccess) return Fail(r.Message, r.Error);
        r = await psu.SetVoltageAsync(dev, channel, req.Volts).ConfigureAwait(false);
        if (!r.IsSuccess) return Fail(r.Message, r.Error);

        // 3. Output ON — from here on, every exit path is fail-safe.
        r = await psu.OutputOnAsync(dev, channel).ConfigureAwait(false);
        if (!r.IsSuccess) return await FailSafeAsync(psu, req, log, r.Message).ConfigureAwait(false);

        // 4. Settle, then poll the read-back until it verifies or the verify window closes.
        // One read at the settle time is not enough: a supply's output ramps (soft start,
        // load capacitance) and its measurement updates slower than its command path, so an
        // early read can show 0 V on an output that is coming up fine.
        var verifyMs = Math.Clamp(req.VerifyMs, 0, MaxVerifyMs);
        var maxPolls = verifyMs / VerifyPollMs + 1;   // bounded loop (Power of 10)
        var sw = System.Diagnostics.Stopwatch.StartNew();
        PowerSupplyChannelState? pch = null;
        try
        {
            await Task.Delay(settleMs, ct).ConfigureAwait(false);
            sw.Restart();
            for (var poll = 0; poll < maxPolls; poll++)
            {
                if (poll > 0) await Task.Delay(VerifyPollMs, ct).ConfigureAwait(false);
                await psu.RefreshAsync().ConfigureAwait(false);
                pch = psu.GetState(dev).Channels.FirstOrDefault(s => s.Channel == channel);
                if (pch != null && pch.OutputOn && Math.Abs(pch.Voltage - req.Volts) <= tolV) break;
                if (sw.ElapsedMilliseconds >= verifyMs) break;
            }
        }
        catch (OperationCanceledException)
        {
            if (req.OffOnFail) await psu.OutputOffAsync(dev, channel).ConfigureAwait(false);
            throw;
        }
        var waited = settleMs + sw.ElapsedMilliseconds;

        if (pch == null)
            return await FailSafeAsync(psu, req, log, $"{tag}: no read-back after {waited} ms.").ConfigureAwait(false);

        var reading = $"{pch.Voltage:F3} V / {pch.Current:F4} A";

        if (!pch.OutputOn)
            return await FailSafeAsync(psu, req, log,
                $"{tag}: output reads OFF after ON command ({reading}).").ConfigureAwait(false);

        if (Math.Abs(pch.Voltage - req.Volts) > tolV)
        {
            // Voltage below setpoint with the current at its limit = constant-current mode:
            // the supply is holding the load at the limit. A caller that drives a CC load on
            // purpose (the Run-page panel) accepts that; otherwise it is an overload / short.
            var atLimit = req.Amps > 0 && pch.Current >= req.Amps * CurrentLimitFraction;
            if (atLimit && req.AcceptCurrentLimit && pch.Voltage > 0)
            {
                log(LogLevel.Warning,
                    $"psu.powerUp: {tag} ON in CC mode — {reading} (current at the {req.Amps:G4} A limit, " +
                    $"voltage below {req.Volts:F3} V setpoint) after {waited} ms");
                return Result<PsuPowerUpReading>.Ok(new PsuPowerUpReading(pch.Voltage, pch.Current));
            }

            var ccHint = atLimit ? " — current at limit (CC mode: overload or short?)" : "";
            return await FailSafeAsync(psu, req, log,
                $"{tag}: read {reading} after {waited} ms, expected {req.Volts:F3} V ±{tolV:G3}{ccHint}.").ConfigureAwait(false);
        }

        log(LogLevel.Info, $"psu.powerUp: {tag} ON, verified {reading} after {waited} ms");
        return Result<PsuPowerUpReading>.Ok(new PsuPowerUpReading(pch.Voltage, pch.Current));
    }

    /// <summary>Switches the channel back OFF (if requested) and returns the failure.</summary>
    private static async Task<Result<PsuPowerUpReading>> FailSafeAsync(
        IPowerSupplyFacet psu, PsuPowerUpRequest req, Action<LogLevel, string> log, string message)
    {
        if (req.OffOnFail)
        {
            var off = await psu.OutputOffAsync(req.Device, req.Channel).ConfigureAwait(false);
            message += off.IsSuccess ? " Output switched OFF." : $" Output OFF FAILED: {off.Message}";
        }
        log(LogLevel.Error, $"psu.powerUp: {message}");
        return Fail(message);
    }

    private static Result<PsuPowerUpReading> Fail(string message, ErrorCode code = ErrorCode.OperationFailed)
        => Result<PsuPowerUpReading>.Fail(code, $"psu.powerUp: {message}");
}
