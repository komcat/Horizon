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
    bool OffOnFail = true);

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

        try
        {
            await Task.Delay(settleMs, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (req.OffOnFail) await psu.OutputOffAsync(dev, channel).ConfigureAwait(false);
            throw;
        }

        // 4. Fresh read-back and verify.
        await psu.RefreshAsync().ConfigureAwait(false);
        var pch = psu.GetState(dev).Channels.FirstOrDefault(s => s.Channel == channel);
        if (pch == null)
            return await FailSafeAsync(psu, req, log, $"{tag}: no read-back after {settleMs} ms.").ConfigureAwait(false);

        var reading = $"{pch.Voltage:F3} V / {pch.Current:F4} A";

        if (!pch.OutputOn)
            return await FailSafeAsync(psu, req, log,
                $"{tag}: output reads OFF after ON command ({reading}).").ConfigureAwait(false);

        if (Math.Abs(pch.Voltage - req.Volts) > tolV)
        {
            // Voltage sag with the current at its limit = constant-current mode (overload / short).
            var ccHint = req.Amps > 0 && pch.Current >= req.Amps * 0.98
                ? " — current at limit (CC mode: overload or short?)" : "";
            return await FailSafeAsync(psu, req, log,
                $"{tag}: read {reading}, expected {req.Volts:F3} V ±{tolV:G3}{ccHint}.").ConfigureAwait(false);
        }

        log(LogLevel.Info, $"psu.powerUp: {tag} ON, verified {reading} after {settleMs} ms");
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
