using Horizon.Motion;
using Horizon.Motion.Models;
using Horizon.Pneumatic;
using Serilog;

namespace Horizon.Core.Dispensing;

public sealed class DispenseExecutor
{
    private readonly UnifiedMotionManager _motion;
    private readonly PneumaticManager _pneumatic;
    private readonly NeedleCameraCalibrationStore _calStore;
    private readonly DispenseProfileStore _profileStore;
    private readonly ProductManager _productManager;

    /// <summary>
    /// UI prompt delegate: (title, message) → user response ("ok"=continue, null=cancelled).
    /// Injected by AppContext to show WPF dialogs from the UI thread.
    /// </summary>
    public Func<string, string, Task<string?>>? PromptUser { get; set; }

    public DispenseExecutor(
        UnifiedMotionManager motion,
        PneumaticManager pneumatic,
        NeedleCameraCalibrationStore calStore,
        DispenseProfileStore profileStore,
        ProductManager productManager)
    {
        _motion = motion;
        _pneumatic = pneumatic;
        _calStore = calStore;
        _profileStore = profileStore;
        _productManager = productManager;
    }

    public async Task<bool> ExecuteAsync(
        string profileName,
        Dictionary<string, object> args,
        CancellationToken ct)
    {
        var productName = _productManager.ActiveProductName;
        if (string.IsNullOrEmpty(productName))
        {
            Log.Error("[Dispense] No active product");
            return false;
        }

        // Step 1: Load or create profile
        var profile = _profileStore.Load(productName, profileName)
                      ?? new DispenseProfile { Name = profileName };

        // Apply runtime overrides from args
        profile = ApplyOverrides(profile, args);

        var device = profile.MotionDevice;
        if (string.IsNullOrEmpty(device))
        {
            Log.Error("[Dispense] No motion device specified");
            return false;
        }

        // Step 2: Load needle-camera calibration (optional — if not calibrated, user positions needle directly)
        var cal = _calStore.Load(productName);
        var hasOffset = cal != null && cal.Valid;
        var offsetX = hasOffset ? cal!.OffsetX : 0.0;
        var offsetY = hasOffset ? cal!.OffsetY : 0.0;
        if (hasOffset)
            Log.Information("[Dispense] Needle-camera offset: X={OffsetX:F4} Y={OffsetY:F4}", offsetX, offsetY);
        else
            Log.Warning("[Dispense] No needle-camera calibration — user will position needle directly");

        // Save/restore velocity
        var origVelX = _motion.GetVelocity(device, "X");
        var origVelY = _motion.GetVelocity(device, "Y");
        var origVelZ = _motion.GetVelocity(device, "Z");

        try
        {
            // Set normal velocity
            SetAllVelocity(device, profile.Velocity);

            // ── Step 3: Navigate camera to CamDispPos ──
            ct.ThrowIfCancellationRequested();
            if (!profile.Taught)
            {
                // First time — ask user to move camera
                var result = await Prompt("Dispense Setup",
                    $"[{profileName}] Move camera to the dispense target position, then click OK.");
                if (result == null) return false;

                var pos = _motion.GetCurrentPosition(device);
                if (pos == null) return false;
                profile = profile with
                {
                    CameraDispensePosition = new DispensePosition { X = pos.X, Y = pos.Y, Z = pos.Z }
                };
                Log.Information("[Dispense] CamDispPos taught: X={X:F3} Y={Y:F3} Z={Z:F3}", pos.X, pos.Y, pos.Z);
            }
            else
            {
                var camPos = profile.CameraDispensePosition!;
                _motion.MoveToAbs(device, "Z", camPos.Z, true);
                ct.ThrowIfCancellationRequested();
                _motion.MoveToAbs(device, "X", camPos.X, true);
                _motion.MoveToAbs(device, "Y", camPos.Y, true);
                ct.ThrowIfCancellationRequested();

                if (!profile.SkipCameraVerification && !profile.AutomaticMode)
                {
                    var result = await Prompt("Verify Position",
                        $"[{profileName}] Camera is at dispense position. Adjust if needed, then click OK.");
                    if (result == null) return false;

                    // Read possibly adjusted position
                    var pos = _motion.GetCurrentPosition(device);
                    if (pos != null)
                    {
                        profile = profile with
                        {
                            CameraDispensePosition = new DispensePosition { X = pos.X, Y = pos.Y, Z = pos.Z }
                        };
                    }
                }
            }

            // ── Step 4: Navigate needle to DispPos (XY only) ──
            ct.ThrowIfCancellationRequested();
            double dispX, dispY;

            if (hasOffset)
            {
                // Use camera position + calibrated offset
                var camDispPos = profile.CameraDispensePosition!;
                dispX = camDispPos.X + offsetX;
                dispY = camDispPos.Y + offsetY;
            }
            else if (!profile.Taught)
            {
                // No calibration, first time — ask user to position needle directly
                var result = await Prompt("Position Needle",
                    $"[{profileName}] Move needle to the dispense XY position, then click OK.");
                if (result == null) return false;

                var pos = _motion.GetCurrentPosition(device);
                if (pos == null) return false;
                dispX = pos.X;
                dispY = pos.Y;
            }
            else
            {
                // No calibration, but have saved dispense position
                dispX = profile.DispensePosition?.X ?? 0;
                dispY = profile.DispensePosition?.Y ?? 0;
            }

            profile = profile with
            {
                DispensePosition = new DispensePosition { X = dispX, Y = dispY }
            };

            _motion.MoveToAbs(device, "X", dispX, true);
            _motion.MoveToAbs(device, "Y", dispY, true);
            ct.ThrowIfCancellationRequested();

            Log.Information("[Dispense] Needle at DispPos: X={X:F3} Y={Y:F3}", dispX, dispY);

            // Step 4.1: Extend dispenser pneumatic
            if (!string.IsNullOrEmpty(profile.PneumaticSlide))
            {
                _pneumatic.ExtendAndWait(profile.PneumaticSlide);
                Log.Information("[Dispense] Dispenser extended: {Slide}", profile.PneumaticSlide);
            }

            // ── Step 5: Z approach ──
            ct.ThrowIfCancellationRequested();
            if (!profile.Taught || profile.ReteachZ)
            {
                // Move to safe Z first if we have a previous value
                if (profile.DispenseZHeight > 0)
                {
                    var safeZ = profile.DispenseZHeight - profile.SafetyHeight;
                    _motion.MoveToAbs(device, "Z", safeZ, true);
                }

                var result = await Prompt("Teach Z Height",
                    $"[{profileName}] Jog Z to the dispense height, then click OK.");
                if (result == null) return false;

                var pos = _motion.GetCurrentPosition(device);
                if (pos != null)
                {
                    profile = profile with { DispenseZHeight = pos.Z };
                    Log.Information("[Dispense] Dispense Z taught: {Z:F3}", pos.Z);
                }
            }
            else
            {
                // Two-phase approach: fast to safety height, then slow to target
                var safeZ = profile.DispenseZHeight - profile.SafetyHeight;
                _motion.MoveToAbs(device, "Z", safeZ, true);
                ct.ThrowIfCancellationRequested();

                // Switch to safety speed for final approach
                _motion.SetVelocity(device, "Z", profile.SafetySpeed);
                _motion.MoveToAbs(device, "Z", profile.DispenseZHeight, true);
                ct.ThrowIfCancellationRequested();

                Log.Information("[Dispense] Z at dispense height: {Z:F3} (safety approach)", profile.DispenseZHeight);
            }

            // ── Step 6: Dispense ──
            ct.ThrowIfCancellationRequested();
            if (profile.DelayBeforeMs > 0)
                await Task.Delay(profile.DelayBeforeMs, ct);

            Log.Information("[Dispense] Dispensing for {Duration}ms", profile.DispenseDurationMs);
            // Dispense action: the pneumatic is already extended (syringe down),
            // the dispense duration is the dwell time at position
            await Task.Delay(profile.DispenseDurationMs, ct);

            if (profile.DelayAfterMs > 0)
                await Task.Delay(profile.DelayAfterMs, ct);

            // ── Step 7: Lift off ──
            ct.ThrowIfCancellationRequested();
            _motion.SetVelocity(device, "Z", profile.LiftOffSpeed);
            _motion.MoveRelative(device, "Z", -profile.LiftOffHeight, true);
            Log.Information("[Dispense] Lift off: {Height:F3}mm at {Speed:F3}mm/s",
                profile.LiftOffHeight, profile.LiftOffSpeed);

            // Restore normal velocity
            SetAllVelocity(device, profile.Velocity);

            // ── Step 8: Retract pneumatic ──
            if (!string.IsNullOrEmpty(profile.PneumaticSlide))
            {
                _pneumatic.RetractAndWait(profile.PneumaticSlide);
                Log.Information("[Dispense] Dispenser retracted: {Slide}", profile.PneumaticSlide);
            }

            // ── Step 9: Move Z back to safe height ──
            ct.ThrowIfCancellationRequested();
            var returnZ = profile.CameraDispensePosition?.Z ?? (profile.DispenseZHeight - profile.SafetyHeight - 5);
            _motion.MoveToAbs(device, "Z", returnZ, true);

            // ── Step 10: Navigate to CamDispPos for verification ──
            ct.ThrowIfCancellationRequested();
            if (hasOffset && profile.CameraDispensePosition != null)
            {
                _motion.MoveToAbs(device, "X", profile.CameraDispensePosition.X, true);
                _motion.MoveToAbs(device, "Y", profile.CameraDispensePosition.Y, true);
            }

            if (!profile.SkipCameraVerification && !profile.AutomaticMode)
            {
                await Prompt("Verify Dispense",
                    $"[{profileName}] Verify dispense quality. Click OK to continue.");
            }

            // ── Step 11: Save profile ──
            profile = profile with
            {
                Taught = true,
                ReteachZ = false,
                TaughtAt = DateTimeOffset.Now.ToString("o")
            };
            _profileStore.Save(productName, profile);
            Log.Information("[Dispense] Profile '{Name}' completed and saved", profileName);

            return true;
        }
        catch (OperationCanceledException)
        {
            Log.Warning("[Dispense] Cancelled: {Name}", profileName);
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Dispense] Error in '{Name}'", profileName);
            return false;
        }
        finally
        {
            // Always restore original velocities
            if (origVelX > 0) _motion.SetVelocity(device, "X", origVelX);
            if (origVelY > 0) _motion.SetVelocity(device, "Y", origVelY);
            if (origVelZ > 0) _motion.SetVelocity(device, "Z", origVelZ);

            // Ensure pneumatic retracted on error/cancel
            if (!string.IsNullOrEmpty(profile.PneumaticSlide))
            {
                try { _pneumatic.Retract(profile.PneumaticSlide); } catch { }
            }
        }
    }

    private void SetAllVelocity(string device, double speed)
    {
        _motion.SetVelocity(device, "X", speed);
        _motion.SetVelocity(device, "Y", speed);
        _motion.SetVelocity(device, "Z", speed);
    }

    private async Task<string?> Prompt(string title, string message)
    {
        if (PromptUser == null)
        {
            Log.Warning("[Dispense] No prompt handler — assuming OK");
            return "ok";
        }
        return await PromptUser(title, message);
    }

    private static DispenseProfile ApplyOverrides(DispenseProfile profile, Dictionary<string, object> args)
    {
        // Allow runtime overrides from recipe script args
        if (args.TryGetValue("velocity", out var v))
            profile = profile with { Velocity = Convert.ToDouble(v) };
        if (args.TryGetValue("dispenseDuration", out var dd))
            profile = profile with { DispenseDurationMs = Convert.ToInt32(dd) };
        if (args.TryGetValue("delayBefore", out var db))
            profile = profile with { DelayBeforeMs = Convert.ToInt32(db) };
        if (args.TryGetValue("delayAfter", out var da))
            profile = profile with { DelayAfterMs = Convert.ToInt32(da) };
        if (args.TryGetValue("safetyHeight", out var sh))
            profile = profile with { SafetyHeight = Convert.ToDouble(sh) };
        if (args.TryGetValue("safetySpeed", out var ss))
            profile = profile with { SafetySpeed = Convert.ToDouble(ss) };
        if (args.TryGetValue("liftOffHeight", out var lh))
            profile = profile with { LiftOffHeight = Convert.ToDouble(lh) };
        if (args.TryGetValue("liftOffSpeed", out var ls))
            profile = profile with { LiftOffSpeed = Convert.ToDouble(ls) };
        if (args.TryGetValue("skipCamera", out var sc))
            profile = profile with { SkipCameraVerification = Convert.ToBoolean(sc) };
        if (args.TryGetValue("automatic", out var am))
            profile = profile with { AutomaticMode = Convert.ToBoolean(am) };
        if (args.TryGetValue("reteachZ", out var rz))
            profile = profile with { ReteachZ = Convert.ToBoolean(rz) };
        if (args.TryGetValue("motionDevice", out var md))
            profile = profile with { MotionDevice = md.ToString()! };
        if (args.TryGetValue("pneumatic", out var pn))
            profile = profile with { PneumaticSlide = pn.ToString()! };
        return profile;
    }
}
