# Needle calibration and dispensing (FireDog port)

Ported from FireDog's `docs/needle-dispense-design.md`, `config/scripts/needle_calibration.lua`,
`config/scripts/dispense_procedure.lua`, the Needle page and the Dispense helper window. The
procedures were Lua scripts there; here they are Layer 2 routines driven through
`IOperatorPrompt` (`ConfirmAsync` for yes/no, `JogPopupAsync` for "jog, then Continue").

## The lever arm

Camera and needle ride the same gantry about (−104, +13) mm apart. The calibration measures:

```
dx, dy   add to a CAMERA pose to get the pose that puts the NEEDLE there
dz       add to a FOCUSED camera Z to reach the height the needle touches at
```

Stored machine-global in `%APPDATA%\Horizon\config\camera_needle_calibration.json`
(`CameraNeedleCalibrationData`). The file keeps its earlier field names: `VcamneedleX/Y` = dx/dy,
`FocusToNeedleZmm` = −dz. Next to the offset it records the hardware (gantry, slide, valve IO + pin),
the protocol parameters, `Valid` (measured, not a seed), `Verified` / `ZChecked` / residuals, and
the camera, needle and imprint poses the offset came from.

- **Machine-level.** Re-teach when the needle changes, not when the product changes.
- **Axis space.** Everything is a raw axis coordinate of the gantry. Nothing goes through a jog transform.
- **Z direction from the measurement.** "Down" is `sign(dz)` (`DownSign`). It is never hardcoded.
- **Seed.** Machine › Settings › *Camera → Needle seed* takes a tape-measure X/Y. It stores
  `Valid = false`: enough to aim the first imprint, never enough to dispense through. Changing it
  un-calibrates the needle. Saving Settings without changing the seed changes nothing.

## Calibration — `CameraNeedleCalibrationRoutine` (Home › Calibration › Camera → Needle)

Pass 1 **measures**. The operator jogs the needle onto the coupon. Pass 2+ **verifies**. The
machine drives to `cam Z + dz`, stops `VerifyGapMm` short, and the operator closes the gap.

0. Once: **clear first**. Each `device = position` (this product's saved positions, by name) is
   moved to in order. A missing name refuses the run before anything moves.
1. Coupon prompt. Passes 2+ ask for a *clean* spot.
2. Jog: centre the camera on the coupon and focus it with Z. There is no auto-focus in Horizon, so
   the operator focuses. dZ is measured down from this focal plane.
3. Read the camera pose. Traverse the needle over the spot at the camera's Z (`VTraverse`).
4. Confirm, then extend the slide and wait `SettleMs`.
5. Down:
   - **Verify pass:** `VApproach` to the safety height, then `VFinal` to the verify gap. A 10 µm Z
     jog closes the gap.
   - **Pass 1:** park `FirstClearMm` above the *previous* touch height, then the operator jogs Z.
6. Read the tip pose. Confirm, then fire a `ShotMs` valve pulse. The valve closes even on a STOP.
7. Lift `LiftMm` toward the camera's Z (`VLiftoff`), **then** raise the slide.
8. Camera back over the spot. Jog the crosshair onto the imprint's centre and read the dot pose.
9. Compute `dx = tip − dot` and `dz = tip Z − cam Z`. On verify passes, compute the XY residual
   and the Z residual (signed along down).

Every pass is saved. **Verified** means a verify pass came in under `TolMm` in XY **and** under
`ZTolMm` in Z. With passes ≥ 2 and no agreement, the offset is still saved, but the run fails.
`Passes = 1` succeeds with an UNVERIFIED warning.

Failure handling:

- **Failure while the needle is down:** lift, then retract the slide, then rethrow.
- **STOP:** close the valve and retract the slide. The gantry does not move.
- **Velocity:** read before the first move and written back on every exit (`NeedleGantry`).

The page shows the needle-motion drawing, labelled with the parameters, and the parameters in
four columns under the stages they drive. Below that are the hardware, *Clear first* and
*Save parameters*. Beside the feed are the offset, the state (VERIFIED / UNVERIFIED /
VERIFY FAILED / NOT CALIBRATED) and the three poses.

## Dispensing — `DispenseRoutineRunner` (Engineer › Dispensing, `dispense.runRoutine`)

A routine is a Dot / Grid / Line **pattern** plus FireDog's `dispense_run` parameters
(`DispenseShot`). The hardware and the touch height come from the calibration, not the routine.
When the needle changes, re-calibrating keeps every routine valid. The program puts the camera
over the target first.

```
touch  = cam Z + dz + dir·ZTrimMm     trim corrects the calibration
stop   = touch − dir·GapMm            gap is a deliberate standoff for the bead
safety = stop − dir·SafetyMm          fast → slow handover
```

1. Refuse if the needle is not calibrated. Warn if the calibration is unverified.
2. `Adjust`: an XY jog onto this part's point. It applies to this run only, and Z is locked to the
   taught Z.
3. Move the needle over the first dot at the camera's Z (`VTravel`, 0 = the device's own speed).
   `Confirm` asks here, then the slide extends.
4. `VApproach` to safety, then `VFinal` to stop. `Confirm` asks before the first shot. Then
   `BeforeMs`, the `ShotMs` pulse, and `AfterMs`.
5. Lift `LiftMm` at `VLiftoff`. Grid and line patterns hop between dots at max(lift, safety) with
   the slide down.
6. After the last dot, raise the slide. Z climbs all the way to the camera's Z, then XY moves.
   `LiftMm = 0` raises the slide first.
7. `VerifyBack`: move the camera over the pattern anchor. With `Confirm` on, ask
   "does it look right?". With it off and `VerifyMs` > 0, show a countdown that a Cancel rejects.

On a failure with the needle down, the runner closes the valve, lifts, retracts the slide and
climbs to the camera's Z. A declined shot confirm also takes this path. On STOP it closes the
valve and retracts the slide only. The velocity is restored on every exit.

The tab draws the same needle-motion figure with the climb-first retreat, plus the gap/trim
close-up (`GapTrimDiagram`).

## Not ported

- `auto_focus` / `disp_autofocus`: Horizon has no auto-focus. The operator focuses in calibration
  step 2, and a dispense uses the taught Z.
- `pick_point` click-to-mark: replaced by "centre the crosshair on the imprint", which yields the
  same world position (MoveClick makes it one click).
- `disp_capture` (image to the DUT record).
- A routine-embedded taught point (`disp_point`): the program's move puts the camera there.
- The `needle_cal_run` program block: calibration runs from the Calibration page only.
- A shipped `.local.json` seed / `needle_cal.json` pair: one file holds both, and the seed lives in
  Machine › Settings.
