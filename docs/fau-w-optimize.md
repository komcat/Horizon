# FAU_WOptimize — W optimisation with XY re-centring

`FAU_WOptimize.moon` (product scripts folder, e.g.
`%APPDATA%\Horizon\products\FAAM\scripts\`) optimises the FAU's W angle (hex-right local)
for SAG PWR. A W step walks the spot sideways, so every W candidate is judged only after X / Y
are re-centred. Run it after FAU_PowerClimb, from a Run Script block, a script preset, or the
Script page.

## What it does

1. **T0 baseline** — refuses to start below `minStartV`. A "rise" must beat the best by
   `minGainPct` % or `noiseK` × the T0 noise, whichever is larger.
2. **Reference** — with `centreStart`, XY-centre at the starting W.
3. **Probe both directions** — W + `wStep` → XY centre; back to the reference; W − `wStep` →
   XY centre. The better probe wins if it rises over the reference; otherwise W stays put.
4. **Continue** stepping W in the winning direction, XY-centring after each step, while SAG
   rises (`wStall` misses, `maxWSteps` or `wLimitDeg` end it).
5. **Park** on the best centred pose and re-read.

**XY centring** is the FAU_GridScan XY climb: for each step in `xySets` (default 1 µm, then
0.5 µm), passes of Y then X — + first, − if the first + step does not rise, walk while rising —
until a pass moves neither axis. Each centring ends back on its best point with a fresh read.

**Safety** — Z never moves. X / Y stay within `xyLimitUm` of the start, W within `wLimitDeg`;
a move past a limit is refused, never clamped. STOP or an error → the abort handler returns
the FAU to the best pose measured.

## Arguments (all optional)

| Key | Default | Meaning |
|---|---|---|
| `wStep` | `0.15` | W step (deg) |
| `wLimitDeg` | `1.5` | Max W change from the start |
| `maxWSteps` | `10` | Max W steps in the winning direction (probe included) |
| `wStall` | `1` | Non-rising W steps before stopping |
| `xySets` | `{1, 0.5}` | XY centring steps (µm), coarse → fine |
| `centrePasses` | `5` | Max Y-then-X passes per centring step |
| `centreMaxSteps` | `30` | Max steps in one XY direction |
| `centreStart` | `true` | XY-centre at the starting W before probing |
| `xyLimitUm` | `30` | Max X / Y offset from the start |
| `minGainPct` | `0.5` | Rise threshold, % of the best… |
| `noiseK` | `3` | …or noiseK × T0 std, whichever is larger |
| `samples` | `5` | Frames averaged per measurement |
| `frameMs` | `40` | SiPhOG frame period |
| `baseSamples` | `20` | Frames for the T0 baseline |
| `minStartV` | `0.001` | Refuse to start below this SAG (V) |

## Examples

```lua
{ }                                     -- defaults: 0.15 deg steps, centre at 1 then 0.5 um
{ wStep = 0.1 }                         -- finer W
{ xySets = {2, 1, 0.5} }                -- wider XY re-centre after each W step
{ wLimitDeg = 0.6, maxWSteps = 4 }      -- small W window
{ wStall = 2 }                          -- tolerate one non-rising W step
{ minGainPct = 1.0, samples = 10 }      -- noisy signal
```

In a program: add a Run Script block after the fine FAU_PowerClimb stage, script
`FAU_WOptimize`, Arguments as above (blank = defaults).
