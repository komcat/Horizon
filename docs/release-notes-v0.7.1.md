# FAAM.UI v0.7.1

Release of **FAAM.UI**, the FAU-attach operator application on the Horizon
3-layer engine stack (Layer 1 `Horizon.Engine` → Layer 2 `Horizon.Workflow` → Layer 3
`FAAM.UI`). Snapshot of `master` at the tag, published self-contained for `win-x64`.
Internal engineering pre-release for rig deployment.

This page lists what changed since
[v0.7](https://github.com/komcat/Horizon/releases/tag/v0.7); see the
[v0.6](https://github.com/komcat/Horizon/releases/tag/v0.6) notes for the assets and the
self-updating install.

## New

### Position variants
One product, several sets of positions — for a unit type that uses the same programs but
slightly different locations (for example `SIPHOG` and `SIPHOG_Plus` with a different FAU
position on gantry-main and a different FAU pick-up on hex-right).

- The product's existing positions are the **base**. A **variant** holds only the positions
  that differ.
- **Header › Positions** selects what the machine uses: `base` or a variant. The choice is
  remembered with the product and cannot be changed while anything runs.
- **Read:** with a variant selected, every position lookup uses the variant's value when it
  has one, otherwise the base value. This covers the Run page, programs, `.moon` / `.hrcp`
  scripts, the alignment panels, the Motion page and the Move map.
- **Write:** every teach or edit made while a variant is selected is saved to the variant.
  Base is never changed.
- **⋯** next to the selector: **Save as new variant…** and **Delete selected variant…**
  (Engineer). Selecting a variant is Operator level.
- **Machine › Motion:** a **From** column shows whether each position comes from base or the
  variant, and **Revert to base** removes the variant's value for the selected position.
- **Awareness:** while a variant is in use the whole window has a coloured frame and the
  header reads **Positions (variant):**. Each variant has its own colour (ten colours, by
  list order, then repeating).
- **Traceability:** each DUT run records the variant it ran with. The header **Results**
  window and the **DUT Data** page show it in a new **Positions** column (`base` for runs
  without a variant, including runs from earlier versions).
- **Save As…** (product clone) copies the variants; the new product starts on base.

Not covered by variants, because they are not stored as product positions: the Run-page
**Save1 / Save2** hex-right slots, Detect Object block positions, dispensing routines, the
gripped FAU teach, tool registration and device links.

### Jog rail and Motion page work as one
- The **device** picked on the jog rail is selected on Machine › Motion, and the reverse.
- The **velocity** is shared per device: a speed applied on the jog rail appears in the
  Motion page's Velocity box, and a speed applied there shows in the rail's dropdown.

## Fixed

### Jog-rail velocity undone by a Motion-page move
- **Symptom:** a speed chosen on the jog rail (e.g. 40 mm/s) went back to 10 mm/s on the next
  Move from Machine › Motion.
- **Cause:** the Motion page moves at the value in its own Velocity box, which did not follow
  the jog rail.
- **Fix:** the two controls now share one value per device (above).
- Also fixed: choosing the speed the jog-rail dropdown already shows did nothing; it is now
  applied.

## Changed

- **DUT Data › UV runs › Export CSV:** a `Positions` column is added after `Operator`. Every
  later column moves one place to the right — adjust anything that reads the file by column
  number.

## Upgrading a station from v0.7

- **Home › Version › Update…** (Engineer). No migrations: existing products keep working on
  base until a variant is created.

## Known limitations

- A program or script that moves to a taught position with its own velocity changes the
  controller speed without updating the jog rail or the Motion page; pick a speed again to
  be sure of it.
- A position taught under a new name while a variant is selected exists only in that
  variant.
- Otherwise unchanged from [v0.7](https://github.com/komcat/Horizon/releases/tag/v0.7).
