# FAU_PowerClimb — arguments (injected parameters)

`FAU_PowerClimb.moon` is the coarse-to-fine SAG PWR climb of the FAU (hex-right) onto the PIC.
One script, many configurations: what it does is set by its **arguments**, a Lua table the
script reads as the global `args`.

## Where arguments come from

| Where | Field | Used by |
|---|---|---|
| Engineer page, **Run Script** block | *Arguments (Lua table, optional)* | programs / decks |
| Machine › Settings › **Script presets** | *Arguments* (+ *No exit threshold*) | Run-page Scanning buttons |
| **Script** page | *Args* box | testing a script by hand |

Rules:

- Lua table literal; the outer `{ }` may be left off.
- **Every key is optional** — a key you leave out keeps its default. Blank = all defaults.
- Data only: numbers, `true`/`false`, strings, nested tables. No functions or formulas.
- An **unknown key or a wrong type fails the script before anything moves**
  (e.g. `zBudget = 30` → `unknown argument 'zBudget'`; `z = "no"` → `must be a boolean`).
- A table argument (`sets`, `targets`) **replaces** the default table, it is not merged.

## All keys

| Key | Default | Meaning |
|---|---|---|
| `sets` | `{{10,1},{5,1},{2,0.5},{1,0.5},{0.5,0.25}}` | Step sets `{zUm, xyUm}`, coarse → fine. One climb (Z, then Y, then X) per set. |
| `z` | `true` | `false` = climb X/Y only; the Z step of each set is ignored. |
| `recentreY` | `true` | Re-centre Y after every Z step and judge the Z step on the re-centred reading. |
| `fit` | `true` | End each X/Y climb at the 3-point fitted (Gaussian) peak. |
| `zBudgetUm` | `40` | Max Z advance from the start (hard cap 45). |
| `xyLimitUm` | `30` | Max X / Y offset from the start. |
| `minGainPct` | `1.0` | A step must beat the peak by this % to count as a rise… |
| `noiseK` | `3` | …or by `noiseK` × the T0 noise (std), whichever is larger. |
| `zStall` | `1` | Non-rising Z steps before going back to the best point. |
| `maxSteps` | `30` | Max steps in one climb direction. |
| `samples` | `5` | SiPhOG frames averaged per measurement. |
| `frameMs` | `40` | SiPhOG frame period (25 Hz). |
| `baseSamples` | `20` | Frames for the T0 baseline. |
| `minStartV` | `0.001` | Refuse to start below this SAG (V) — no light coupled. |
| `zSign` | `1` | `1` / `-1`: the local Z direction toward the PIC. |
| `targets` | see below | Early-exit SAG level by photo current. `{}` = no early exit. |
| `exit` | `true` | `false` = never exit early, climb every set. |

## Exit level (`targets`)

Each measurement reads SAG **and** photo current (PD). The rows are checked **top to bottom**;
the first row with `PD > aboveUa` gives the exit level `sagV`. The climb stops as soon as its
peak's SAG is above that level. PD below every row (or unreadable) = no exit, every set runs.

Default:

```lua
targets = {
  { aboveUa = 800, sagV = 2.0 },   -- PD > 800 uA   -> stop once SAG > 2.0 V
  { aboveUa = 500, sagV = 1.5 },   -- PD 500-800 uA -> stop once SAG > 1.5 V
  { aboveUa = 300, sagV = 1.1 },   -- PD 300-500 uA -> stop once SAG > 1.1 V
}
```

**Order the rows highest `aboveUa` first** — the script does not sort them, and a low row
placed first catches everything.

## Examples

### Coarse climb, then fine climb (two Run Script blocks)

Stage 2 block — name `Coarse`, script `FAU_PowerClimb`:

```lua
{ sets = {{10,1},{5,1},{2,0.5}}, exit = false }
```

Climbs every coarse set (no early exit), so the fine stage always starts near the peak.

Stage 3 block — name `Fine`, script `FAU_PowerClimb`:

```lua
{ sets = {{1,0.5},{0.5,0.25}} }
```

Fine sets with the default exit levels.

### One fixed exit level, whatever the photo current

```lua
{ sets = {{2,0.5},{1,0.5},{0.5,0.25}},
  targets = { {aboveUa = 0, sagV = 1.8} } }
```

### Your own exit levels (more steps)

```lua
{ sets = {{2,0.5},{1,0.5},{0.5,0.25}},
  targets = {
    {aboveUa = 900, sagV = 2.2},
    {aboveUa = 700, sagV = 1.9},
    {aboveUa = 500, sagV = 1.5},
    {aboveUa = 300, sagV = 1.1},
    {aboveUa = 0,   sagV = 0.8},
  } }
```

### No early exit at all

```lua
{ exit = false }
```

(Same as `targets = {}`, and what a preset's *No exit threshold* tick adds.)

### X / Y only (no Z advance)

```lua
{ z = false, sets = {{0,0.5},{0,0.25}} }
```

### Tighter travel limits

```lua
{ sets = {{2,0.5},{1,0.5},{0.5,0.25}}, zBudgetUm = 20, xyLimitUm = 10 }
```

A move past a limit is refused (that direction ends), never clamped.

### Noisy signal — average more, demand a bigger rise

```lua
{ samples = 10, baseSamples = 40, minGainPct = 2.0, noiseK = 4 }
```

### Plain grid steps, no fitted peak, no Y re-centre

```lua
{ fit = false, recentreY = false }
```

## What arguments cannot do

The exit rule is a lookup table: "SAG above X while PD is above Y". Rules such as *N % above
T0*, *SAG/PD ratio*, *stop on a plateau*, or an interpolated curve need a new key in the script
itself (`%APPDATA%\Horizon\products\FAAM\scripts\FAU_PowerClimb.moon`).
