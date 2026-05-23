using Horizon.Engine;
using Horizon.Workflow.Scanning.Feedback;

namespace Horizon.Workflow.Scanning;

/// <summary>
/// Layer 2 orchestrator for a hexapod beam-alignment scan — port of the legacy
/// <c>AdaptiveScanController</c>. Drives the phases: build feedback → record
/// baseline → run the <see cref="CoarseToFineStrategy"/> → return to the peak.
/// Pure code: no UI, no <c>IProcessService</c> coupling. Front-ends construct
/// it directly with an <see cref="IHorizonEngine"/>.
/// </summary>
public sealed class HexapodScanEngine
{
    private const int FirstSampleTimeoutSeconds = 10;

    private readonly IHorizonEngine _engine;
    private readonly Action<LogLevel, string> _log;

    public HexapodScanEngine(IHorizonEngine engine, Action<LogLevel, string>? log = null)
    {
        _engine = engine;
        _log = log ?? ((_, _) => { });
    }

    /// <summary>
    /// Runs a coarse-to-fine scan on <paramref name="hexapodDevice"/>, optimising
    /// the signal from <paramref name="feedback"/>. Cancelling <paramref name="ct"/>
    /// halts the scan; the hexapod still returns to the best position found
    /// (legacy <c>HaltScan</c> contract).
    /// </summary>
    public async Task<ScanResult> RunAsync(
        string hexapodDevice,
        FeedbackDescriptor feedback,
        ScanParameters parameters,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        var startTime = DateTime.Now;

        var validation = parameters.Validate();
        if (!validation.IsSuccess)
            return Failed(hexapodDevice, startTime, ScanState.Error, validation.Message);

        if (_engine.Motion.GetDevice(hexapodDevice) == null)
            return Failed(hexapodDevice, startTime, ScanState.Error,
                $"Motion device '{hexapodDevice}' not found.");
        if (!_engine.Motion.IsConnected(hexapodDevice))
            return Failed(hexapodDevice, startTime, ScanState.Error,
                $"Motion device '{hexapodDevice}' is not connected.");

        var feedbackSource = ScanFeedbackFactory.Create(_engine, feedback);
        try
        {
            var started = await feedbackSource.StartAsync(ct).ConfigureAwait(false);
            if (!started.IsSuccess)
                return Failed(hexapodDevice, startTime, ScanState.Error,
                    $"Feedback source failed to start: {started.Message}");

            _log(LogLevel.Info, $"Scan feedback: {feedbackSource.Description}.");

            // Apply the scan velocity (mm/s), best-effort. SetDeviceVelocityAsync
            // is controller-aware: PI hexapod → system velocity, ACS → every axis.
            if (parameters.ScanVelocityMmPerSec > 0)
            {
                var velocity = await _engine.Motion
                    .SetDeviceVelocityAsync(hexapodDevice, parameters.ScanVelocityMmPerSec)
                    .ConfigureAwait(false);
                _log(velocity.IsSuccess ? LogLevel.Info : LogLevel.Warning,
                    velocity.IsSuccess
                        ? $"Scan velocity set to {parameters.ScanVelocityMmPerSec:0.###} mm/s."
                        : $"Set scan velocity failed: {velocity.Message}");
            }

            var context = new ScanContext(_engine, feedbackSource, hexapodDevice, parameters, progress, _log);
            bool halted = false;

            // Baseline → scan. A cancel here surfaces as OperationCanceledException;
            // it is caught so the scan can still settle on the best position found
            // (legacy ExecuteScan: a halt still runs ReturnToBestPosition).
            try
            {
                // --- Baseline (legacy RecordBaseline) ---
                context.State = ScanState.RecordingBaseline;
                context.ReportPhase(0, "Recording baseline…");
                await WaitForFirstSampleAsync(feedbackSource, ct).ConfigureAwait(false);

                var baselinePosition = await context.GetCurrentPositionAsync(ct).ConfigureAwait(false);
                double baselineValue = await feedbackSource
                    .ReadAveragedAsync(parameters.AveragingCount, ct).ConfigureAwait(false);
                context.Baseline = new BaselineInfo(baselineValue, baselinePosition, parameters.AveragingCount, true);
                _log(LogLevel.Info, $"Baseline = {baselineValue:G6} {feedbackSource.Unit}.");

                // --- Scan (legacy ExecuteScanStrategy) ---
                context.State = ScanState.Scanning;
                var scanResult = await new CoarseToFineStrategy()
                    .ExecuteAsync(context, ct).ConfigureAwait(false);
                if (!scanResult.IsSuccess)
                    _log(LogLevel.Warning, $"Scan strategy reported: {scanResult.Message}");
            }
            catch (OperationCanceledException)
            {
                halted = true;
                _log(LogLevel.Warning, "Scan halted — returning to best position found.");
            }

            // --- Return to best position (legacy ReturnToBestPosition) ---
            // On a halt this still runs, on a fresh token, so the hexapod ends
            // at the peak — matching the legacy HaltScan contract.
            var returnCt = halted ? CancellationToken.None : ct;
            context.State = ScanState.ReturningToPeak;
            double verifiedPeak = context.GlobalPeak.Value;
            if (context.GlobalPeak.Valid)
            {
                context.ReportPhase(0.95, "Returning to peak…");
                if (await context.MoveToPositionAsync(context.GlobalPeak.Position, returnCt).ConfigureAwait(false))
                {
                    await context.SettleAsync(returnCt).ConfigureAwait(false);
                    verifiedPeak = await feedbackSource.ReadAsync(returnCt).ConfigureAwait(false);
                    _log(LogLevel.Info, $"Peak verified = {verifiedPeak:G6} {feedbackSource.Unit}.");
                }
            }
            else
            {
                _log(LogLevel.Warning, "No peak found — staying at current position.");
            }

            context.State = halted ? ScanState.Halted : ScanState.Completed;
            context.ReportPhase(1.0, halted ? "Scan halted." : "Scan completed successfully.");

            // Noise verdict — a peak counts only if it rose clear of the σ floor.
            double noiseSigma = context.NoiseSigma();
            double peakSnr = context.PeakSnr();
            bool significant = context.PeakIsSignificant();
            string snrText = double.IsPositiveInfinity(peakSnr) ? "∞" : peakSnr.ToString("F1");

            string message;
            if (context.NoSignalAbort)
                message = $"No signal — {context.AbortReason}.";
            else if (significant)
                message = $"{(halted ? "Halted" : "Completed")} — real peak, SNR {snrText} σ " +
                          $"(peak {verifiedPeak:G6} {feedbackSource.Unit} vs baseline " +
                          $"{context.Baseline.Value:G6} {feedbackSource.Unit}).";
            else
                message = $"{(halted ? "Halted" : "Completed")} — noise, peak SNR {snrText} σ " +
                          $"< {parameters.MinPeakSigma:0.#} σ (no significant peak found).";

            _log(significant ? LogLevel.Info : LogLevel.Warning, message);

            return new ScanResult
            {
                Device = hexapodDevice,
                StartTime = startTime,
                EndTime = DateTime.Now,
                FinalState = context.State,
                Baseline = context.Baseline,
                Peak = context.GlobalPeak,
                Measurements = context.DataPoints.ToList(),
                AxisScans = context.AxisScans.ToList(),
                Success = !halted,
                Message = message,
                NoiseSigma = noiseSigma,
                PeakSnr = peakSnr,
                PeakIsSignificant = significant,
            };
        }
        catch (Exception ex)
        {
            _log(LogLevel.Error, $"Scan error: {ex.Message}");
            return Failed(hexapodDevice, startTime, ScanState.Error, ex.Message);
        }
        finally
        {
            // Always stop the stream, even on cancel/fault.
            await feedbackSource.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Waits until the feedback source has produced its first sample, or times out.</summary>
    private static async Task WaitForFirstSampleAsync(IScanFeedbackSource source, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(FirstSampleTimeoutSeconds);
        while (!source.HasValidSample && DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(20, ct).ConfigureAwait(false);
        }
        if (!source.HasValidSample)
            throw new TimeoutException($"No feedback sample from {source.Description} within {FirstSampleTimeoutSeconds} s.");
    }

    private static ScanResult Failed(string device, DateTime startTime, ScanState state, string message)
        => new()
        {
            Device = device,
            StartTime = startTime,
            EndTime = DateTime.Now,
            FinalState = state,
            Success = false,
            Message = message,
        };
}
