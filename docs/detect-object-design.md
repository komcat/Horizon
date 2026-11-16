# Detect Object block (`vision.detectObject`)

A self-contained "find it with the camera" block for the Engineer timeline. Unlike
`vision.runProfile` (which runs a shared, named Vision Profile and returns raw pixels), a Detect
Object block owns its camera position and its vision recipe, returns the object's **X / Y in
gantry-main mm and P in degrees**, can move the camera onto the result, and can fall back to the
operator marking the spot by hand.

## What the block stores

`WorkflowStep.Parameters` is `string → string`, and the editor only keeps keys declared in the
`OpSpec`. Everything is therefore a declared parameter; the block-owned ones are
`ParamKind.Hidden` (never drawn as a field, edited by the block's own buttons).

| Param | Kind | Default | Meaning |
|---|---|---|---|
| `posX` `posY` `posZ` | Hidden | blank | The block's camera position on the camera device (gantry-main). All three or none. Never written to `positions.json`. |
| `recipe` | Hidden | blank | The vision recipe: a `VisionProfile` as compact JSON (`DetectObjectRecipe`). |
| `captureAs` | Text | `obj` | Prefix of the result variables. |
| `useX` `useY` `useP` | Bool | `true` | Which parts of the result come from the detection. Unticked X / Y = the camera's own axis value (no correction on that axis; the move to the result leaves it alone). Unticked P = 0. Also the tick boxes above X / Y / P in the Vision helper. Example: a horizontal edge locates Y and P but not X — untick X. |
| `velocity` | Number | `0` | mm/s for the block's moves; 0 = leave the device as it is. Restored on every exit. |
| `settleMs` | Number | `300` | Wait after arriving, before the frame is taken. |
| `requireMatch` | Bool | `true` | Nothing found = step error. Unticked: `found = 0`, the program continues. |
| `askOperator` | Bool | `false` | On a failed detection, open the mark window. |
| `moveToResult` | Bool | `false` | Centre the camera on the result (X / Y). |
| `maxMoveMm` | Number | `5` | Guard for that move; values above 5 count as 5. Over the guard = refused. |
| `offsetX` `offsetY` `offsetZ` | Number | `0` | After the move to the result, a further move on the camera device's own axes. Not covered by the guard. |

The camera device is `AppSettings.MoveClickDevice` (gantry-main).

**NCC templates** stay files — HALCON loads the `.ncm` model from disk — in the product's existing
`vision\templates\{tpl-uid}\`, referenced by `TemplateRef.Uid` inside `recipe`. So:

- a program commit versions the recipe with the block (about 1–2 KB of JSON, no image data);
- copying a block, or the product (`CloneProduct` copies `vision\`), keeps working;
- the helper never deletes a template folder — an older committed version may still point at it.

Not handled: importing a deck from **another** product does not bring the template files; the block
then fails with "no template" until one is taught again.

## Results

Written to the run's `WorkflowContext`, so they live until the program (or deck) ends:

| Variable | Value |
|---|---|
| `{p}_found` | `1` / `0` |
| `{p}_x`, `{p}_y` | The detected point in the camera device's axis values (mm) |
| `{p}_p` | Angle in degrees — see below |
| `{p}_score` | The detector's score (0 for an operator mark) |
| `{p}_px`, `{p}_py` | The point in source pixels |
| `{p}_manual` | `1` when the operator marked it |

`{p}_found` is reset to `0` when the block starts, so a failure never leaves an older result behind.
Later blocks use `var:obj_x`, `expr:obj_x + 1.5`, or an If block on `obj_found == 1`.

**X / Y** = camera pose when the frame was taken + (pixel − image centre) × `MmPerPixel`, with the
MoveClick axis signs (`DetectObjectMath.ToGantry`; the same maths Part Alignment uses). There is no
rotation term between camera and gantry. The point is the NCC anchor, otherwise the method's `x`/`y`.

**P**: 0° = straight up on the image, clockwise positive, −180…+180.

| Method | P |
|---|---|
| NCC | `Template.Rotation − matchAngle` (HALCON's match angle is counter-clockwise). The anchor offset is turned with the match, so the anchor is right at any angle. |
| Line, Edge (regression) | `90 − angle`, folded to −90…+90 (a line has no direction) |
| Corner | `90 − angle` (the bisector) |
| Circle, operator mark | `0` |

The NCC sign follows HALCON's documented convention and **must be confirmed on the bench** with a
part rotated by a known amount; if P decreases for a clockwise turn, flip it in
`DetectObjectMath.FromResult`. `vision.runProfile` is unchanged (it still reports the raw angle and
the unrotated anchor).

## Run sequence (`Vision/DetectObjectRoutine.cs`)

1. Parse the recipe; none → error "open the Vision helper".
2. A velocity > 0 is applied after the current one is read (unreadable → refused), and put back on
   every exit.
3. Position taught → move the camera device's X / Y / Z there (other axes untouched), then confirm
   it arrived — a STOP mid-move also ends a move "successfully".
4. Apply the recipe's lighting; hold a camera grab lease; wait `settleMs`; wait for fresh frames.
5. Detect with the recipe (its exposure is applied first, as for a named profile). A detector
   failure — NCC "no match", HALCON missing — counts as not found and is logged.
6. Not found and `askOperator` → `IOperatorPrompt.MarkLocationAsync`. The pose is read again after
   the mark, in case the operator jogged while the window was open.
7. The result is handed to the program **before** any move, so the variables are set even when the
   move is refused.
8. Not found → error when `requireMatch`, else success with `found = 0` and no moves.
9. `moveToResult` → distance from the current pose to the result; over `min(maxMoveMm, 5)` →
   **refused** with the distance in the message. Otherwise move X / Y and confirm, then the offset
   move when any offset is non-zero.

`StepDispatcher.Vision.cs` reads the parameters strictly: a non-blank value that is not a number
fails the step rather than becoming 0.

## Editor

On the block's parameter panel, a box below the fields:

- **Go** — moves the camera to the block's position at the block's velocity.
- **Teach here** — after a confirm (now / was), stores the camera's current X / Y / Z in the block.
- **Clear** — forgets the position; the block then detects wherever the camera is.
- **Vision helper…** — opens `DetectObjectHelperWindow`.

Go and Teach here are refused while a program, a Run-page step or an alignment is running.

**Vision helper** embeds `VisionProfileView` (`Embedded = true` hides its profile toolbar), which
gives the live view with overlays, camera / exposure / lighting, the three-stage filter chain, the
filtered image, the template and method parameters, the matched crop and the result list. The
window adds:

- the final **X · Y · P** and score, as the block will return them;
- **Test move to result** — the same guarded move (5 mm);
- **Save to block** — writes the recipe (with the camera's live exposure) into the block. Nothing is
  written to the product's Vision Profiles. Save or commit the program to keep it.

**Manual mark** (`MarkLocationWindow`, via `OperatorPrompt.MarkLocationAsync`): a live feed with the
centre crosshair. The operator clicks the feature (the standard `MarkerPlacer`: arrows nudge one
pixel, Enter / Confirm accepts). Cancel or closing the window = nothing found; a run STOP closes it.
The tower light shows "waiting for the operator" while it is open.

## Not included

- `.moon` / `.hrcp` equivalents of the block.
- Copying template files on a cross-product deck import.
- A guard on the offset move (it is an authored value, like any relative move).
