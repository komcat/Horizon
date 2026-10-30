-- UV_Advanced - two UV cure loops with fixed-step drift correction on a hexapod
-- (DLAM; converted from FireDog's uv_advanced.lua, itself a port of Horizon's UV_SAA5)
--
-- Install: copy into the product's scripts folder (%APPDATA%\Horizon\products\<name>\scripts\)
-- and run it from a Run Script block or a script preset.
--
-- Every pulse: output OFF offMs -> ON onMs -> OFF afterMs, then the loop's cure wait.
--
-- Loop 1 - l1Pin (UV_PLC1), l1N pulses. Each pulse reads T0 before the trigger and T1 after the
-- wait. When T1 is l1DropPct (0.5 %) or more below T0, an X/Y ladder of fixed stepUm steps,
-- l1SettleMs each, from the start pose:
--   1) X+step: better than T1 -> stay, done
--   2) else X-step: better than T1 -> stay, done
--   3) else back to the start, then the same on Y (Y+, then Y-)
--   4) nothing better -> back at the start pose
-- Each pulse is logged against the previous one and the loop start, with how much of the drop
-- the correction won back, and a summary table after the loop.
--
-- Loop 2 - l2Pin (UV_PLC2), l2N pulses. Records T2.0 before the first pulse and T2.i after each.
-- For pulses 1..l2CorrectN, when T2.i is l2DropPct (1 %) or more below the previous reading, an
-- X-only correction from X0, each stepUm shift made as l2Substeps sub-steps (l2SubstepMs each)
-- and then an l2SettleMs settle:
--   1) X+step: better than X0 -> stay
--   2) else back to X0, then X-step: better than X0 -> stay
--   3) else back to X0
--
-- Signal: the MAGNITUDE of the feedback signal, in uA, averaged over 'samples' reads
-- (machine.signal.tryRead - a live instrument read for an SMU key). A photocurrent can read
-- negative; "better" always means more light. A measurement where no read succeeded is n/a, and
-- an n/a reading never triggers a correction.
--
-- Moves are ABSOLUTE targets off the pose read at the start of each correction (moveAbs), so
-- "back to the start" lands on the start and steps never stack up error.
--
-- Safety (machine.onAbort): on STOP or any error both UV outputs are switched OFF, and if a
-- correction was in progress the hexapod returns to that correction's start pose - the last
-- known-good position (FireDog left it wherever it stopped). Outside a correction nothing moves.
-- The outputs are NOT switched off at a normal end: every pulse already leaves its output OFF.
--
-- Results: .moon scripts cannot write program context / DUT values yet, so every reading is in
-- the log - per-pulse lines, a loop-1 summary table, the loop-2 series, baseline and final.
--
-- Arguments - the global 'args' table (script preset, the Run Script block's Arguments or the
-- Script editor's Args box). Every key is optional; an unknown key or a wrong type fails the
-- script before anything moves.
--   io           IO device                                   "IOBottom"
--   device       hexapod to correct                          "hex-right"
--   signal       feedback signal key                         "smu.Keithley_2602B.ch1.current"
--   samples      reads averaged per measurement              5
--   sampleMs     wait between reads, ms                      100
--   offMs / onMs / afterMs   pulse shape, ms                 100 / 250 / 150
--   stepUm       correction step, um (max 5)                 0.5
--   l1Pin        loop 1 output                               "UV_PLC1"
--   l1N          loop 1 pulses                               6
--   l1WaitMs     loop 1 cure wait after each pulse           16000
--   l1DropPct    correct when T1 is this % below T0          0.5
--   l1SettleMs   settle after each X/Y step, ms              500
--   l2Pin        loop 2 output                               "UV_PLC2"
--   l2N          loop 2 pulses                               10
--   l2WaitMs     loop 2 cure wait after each pulse           16000
--   l2CorrectN   pulses 1..N may correct                     10
--   l2DropPct    correct when this % below previous          1.0
--   l2Substeps   each X shift made in this many steps        5
--   l2SubstepMs  wait after each sub-step, ms                300
--   l2SettleMs   settle after each shift, ms                 1000
-- e.g.  { device = "hex-left", l1N = 4, l2N = 6 }

local STEP_CAP_UM = 5   -- hard ceiling on stepUm, whatever the arguments say

local DEFAULTS = {
  io = "IOBottom", device = "hex-right", signal = "smu.Keithley_2602B.ch1.current",
  samples = 5, sampleMs = 100, offMs = 100, onMs = 250, afterMs = 150, stepUm = 0.5,
  l1Pin = "UV_PLC1", l1N = 6, l1WaitMs = 16000, l1DropPct = 0.5, l1SettleMs = 500,
  l2Pin = "UV_PLC2", l2N = 10, l2WaitMs = 16000, l2CorrectN = 10, l2DropPct = 1.0,
  l2Substeps = 5, l2SubstepMs = 300, l2SettleMs = 1000,
}

-- ---- arguments -> cfg (nothing moves or switches until this passes) ----------------------------
local function check(ok, msg)
  if not ok then error("UV_Advanced: " .. msg) end
end

local cfg = {}
for k, v in pairs(DEFAULTS) do cfg[k] = v end
for k, v in pairs(args or {}) do
  local d = DEFAULTS[k]
  check(d ~= nil, "unknown argument '" .. tostring(k) .. "'")
  check(type(v) == type(d), string.format("argument '%s' must be a %s (got %s)", tostring(k), type(d), type(v)))
  cfg[k] = v
end
for _, k in ipairs({ "samples", "sampleMs", "offMs", "onMs", "afterMs", "l1N", "l1WaitMs", "l1SettleMs",
                     "l2N", "l2WaitMs", "l2CorrectN", "l2Substeps", "l2SubstepMs", "l2SettleMs" }) do
  check(cfg[k] >= 0, k .. " must be >= 0")
  cfg[k] = math.floor(cfg[k])
end
check(cfg.samples >= 1, "samples must be >= 1")
check(cfg.l2Substeps >= 1, "l2Substeps must be >= 1")
check(cfg.stepUm > 0 and cfg.stepUm <= STEP_CAP_UM, string.format("stepUm must be > 0 and <= %d", STEP_CAP_UM))

local STEP_MM  = cfg.stepUm / 1000.0
local L1_DROP  = -math.abs(cfg.l1DropPct)
local L2_DROP  = -math.abs(cfg.l2DropPct)
local MIN_GAIN = 0.0   -- a loop 1 direction must beat T1 by more than this (%)

local corrections = 0
local correcting = nil   -- {x, y} start pose while a correction runs, for the abort handler

-- ---- abort: UV off, and back to the correction's start if one was running -----------------------
-- No pcall in the script sandbox: a failing call ends the handler, so the UV outputs go first.
machine.onAbort(function(reason)
  machine.io.setNamed(cfg.io, cfg.l1Pin, false)
  machine.io.setNamed(cfg.io, cfg.l2Pin, false)
  machine.log("UV_Advanced " .. tostring(reason) .. ": UV outputs switched OFF")
  if correcting ~= nil then
    machine.motion.moveAbs(cfg.device, "X", correcting.x)
    machine.motion.moveAbs(cfg.device, "Y", correcting.y)
    machine.log(string.format("UV_Advanced: %s returned to the correction start X %.5f Y %.5f",
      cfg.device, correcting.x, correcting.y))
  end
end)

-- ---- formatting -------------------------------------------------------------------------------
local function fmt(v)
  if v == nil then return "n/a" end
  return string.format("%.4f", v)
end

-- Percent change of v vs ref; nil if either is missing.
local function pct(v, ref)
  if v == nil or ref == nil then return nil end
  if math.abs(ref) < 1e-12 then return 0 end
  return (v - ref) / math.abs(ref) * 100
end

local function fmtPct(p)
  if p == nil then return "n/a" end
  return string.format("%.2f %%", p)
end

local function logf(...) machine.log(string.format(...)) end

-- ---- signal -----------------------------------------------------------------------------------
-- Average magnitude, uA, of the reads that succeeded; nil when none did.
local function readUa()
  local sum, ok = 0, 0
  for i = 1, cfg.samples do
    local v = machine.signal.tryRead(cfg.signal)
    if v ~= nil then sum, ok = sum + math.abs(v) * 1e6, ok + 1 end
    if i < cfg.samples then machine.wait(cfg.sampleMs) end
  end
  if ok == 0 then return nil end
  return sum / ok
end

-- ---- output -----------------------------------------------------------------------------------
local function pulse(pin)
  machine.io.setNamed(cfg.io, pin, false)
  machine.wait(cfg.offMs)
  machine.io.setNamed(cfg.io, pin, true)
  machine.wait(cfg.onMs)
  machine.io.setNamed(cfg.io, pin, false)
  machine.wait(cfg.afterMs)
end

-- ---- motion -----------------------------------------------------------------------------------
local function pose()
  local p = machine.motion.position(cfg.device)
  check(p ~= nil and p.x ~= nil and p.y ~= nil, cfg.device .. " reports no X/Y position")
  return p.x, p.y
end

local function moveTo(axis, v)
  machine.motion.moveAbs(cfg.device, axis, v)
end

-- ---- loop 1 correction: X then Y, fixed steps -------------------------------------------------
-- Try start+step, then start-step, on one axis (reading r0 at the start). A direction that beats
-- r0 is kept: returns its reading and name. Otherwise back to the start and nil.
local function tryAxis(tag, axis, start, r0)
  moveTo(axis, start + STEP_MM)
  machine.wait(cfg.l1SettleMs)
  local rPlus = readUa()
  local gPlus = pct(rPlus, r0)
  logf("%s  %s+%.1fum: %s uA  (%s vs start)", tag, axis, cfg.stepUm, fmt(rPlus), fmtPct(gPlus))
  if gPlus ~= nil and gPlus > MIN_GAIN then
    logf("%s  %s+ improved, staying there", tag, axis)
    return rPlus, axis .. "+"
  end

  moveTo(axis, start - STEP_MM)
  machine.wait(cfg.l1SettleMs)
  local rMinus = readUa()
  local gMinus = pct(rMinus, r0)
  logf("%s  %s-%.1fum: %s uA  (%s vs start)", tag, axis, cfg.stepUm, fmt(rMinus), fmtPct(gMinus))
  if gMinus ~= nil and gMinus > MIN_GAIN then
    logf("%s  %s- improved, staying there", tag, axis)
    return rMinus, axis .. "-"
  end

  moveTo(axis, start)
  machine.wait(cfg.l1SettleMs)
  return nil
end

-- The first direction that improves ends the correction. Returns the reading where the stage ends
-- up and the direction kept ("none" = back at the start).
local function correctXY(tag, r0)
  corrections = corrections + 1
  local x0, y0 = pose()
  correcting = { x = x0, y = y0 }
  logf("%s  correcting %s from X %.5f Y %.5f, start %s uA", tag, cfg.device, x0, y0, fmt(r0))
  local r, dir = tryAxis(tag, "X", x0, r0)
  if r == nil then r, dir = tryAxis(tag, "Y", y0, r0) end
  if r == nil then
    r, dir = readUa(), "none"
    logf("%s  no direction improved, back at start: %s uA", tag, fmt(r))
  end
  correcting = nil
  return r, dir
end

-- Share of the T0 -> T1 drop the correction won back (tc = after correction).
local function recovery(t0, t1, tc)
  if t0 == nil or t1 == nil or tc == nil or t0 - t1 <= 1e-12 then return nil end
  return (tc - t1) / (t0 - t1) * 100
end

local function loop1()
  local pin, n = cfg.l1Pin, cfg.l1N
  logf("UV_Advanced: loop 1 - %s x %d, wait %d ms, correct on a drop of %.2f %%", pin, n, cfg.l1WaitMs, -L1_DROP)
  local rows = {}
  local ref, prevT0, prevT1 = nil, nil, nil
  for i = 1, n do
    local tag = string.format("%s %d/%d", pin, i, n)
    local t0 = readUa()
    if ref == nil then ref = t0 end
    pulse(pin)
    machine.wait(cfg.l1WaitMs)
    local t1 = readUa()
    local d1 = pct(t1, t0)
    logf("%s  T0=%s  T1=%s uA  (%s)", tag, fmt(t0), fmt(t1), fmtPct(d1))
    if i > 1 then
      logf("%s  vs previous: T0 %s, T1 %s  |  T1 vs loop start %s", tag,
        fmtPct(pct(t0, prevT0)), fmtPct(pct(t1, prevT1)), fmtPct(pct(t1, ref)))
    end

    local tEnd, corr = t1, "-"
    if d1 == nil then
      logf("%s  reading n/a, no correction", tag)
      corr = "n/a"
    elseif d1 <= L1_DROP then
      local tc, dir = correctXY(tag, t1)
      tEnd, corr = tc, dir
      logf("%s  after correction: %s uA  (%s vs T1, %s vs T0, recovered %s of the drop)",
        tag, fmt(tc), fmtPct(pct(tc, t1)), fmtPct(pct(tc, t0)), fmtPct(recovery(t0, t1, tc)))
    end

    rows[i] = { t0 = t0, t1 = t1, d1 = d1, corr = corr, tEnd = tEnd }
    prevT0, prevT1 = t0, t1
    machine.progress(i / (cfg.l1N + cfg.l2N) * 100, tag)
  end

  logf("%s summary (loop start T0=%s uA):", pin, fmt(ref))
  machine.log("  #  T0        T1        T1vsT0    corr  end       end vs start")
  for i, r in ipairs(rows) do
    logf("  %d  %s  %s  %s  %s  %s  %s", i, fmt(r.t0), fmt(r.t1), fmtPct(r.d1),
      r.corr, fmt(r.tEnd), fmtPct(pct(r.tEnd, ref)))
  end
end

-- ---- loop 2 correction: X only, in sub-steps ---------------------------------------------------
-- Walk X from 'from' to 'to' in l2Substeps steps, then settle; returns the reading there.
local function walkX(from, to)
  for k = 1, cfg.l2Substeps do
    moveTo("X", from + (to - from) * k / cfg.l2Substeps)
    machine.wait(cfg.l2SubstepMs)
  end
  machine.wait(cfg.l2SettleMs)
  return readUa()
end

-- X correction from X0 (reading r0 there). Returns the reading where X ends up.
local function correctX(tag, r0)
  corrections = corrections + 1
  local x0, y0 = pose()
  correcting = { x = x0, y = y0 }
  logf("%s  correcting %s X from X0 %.5f: %s uA", tag, cfg.device, x0, fmt(r0))

  local result
  local rPlus = walkX(x0, x0 + STEP_MM)
  logf("%s  X+%.1fum: %s uA  (%s vs X0)", tag, cfg.stepUm, fmt(rPlus), fmtPct(pct(rPlus, r0)))
  if rPlus ~= nil and rPlus > r0 then
    logf("%s  X+ improved, staying there", tag)
    result = rPlus
  else
    local rBack = walkX(x0 + STEP_MM, x0)
    logf("%s  back at X0: %s uA  (%s vs X0)", tag, fmt(rBack), fmtPct(pct(rBack, r0)))
    local rMinus = walkX(x0, x0 - STEP_MM)
    logf("%s  X-%.1fum: %s uA  (%s vs X0)", tag, cfg.stepUm, fmt(rMinus), fmtPct(pct(rMinus, r0)))
    if rMinus ~= nil and rMinus > r0 then
      logf("%s  X- improved, staying there", tag)
      result = rMinus
    else
      result = walkX(x0 - STEP_MM, x0)
      logf("%s  neither direction improved, back at X0: %s uA  (%s vs X0)", tag, fmt(result), fmtPct(pct(result, r0)))
    end
  end
  correcting = nil
  return result
end

local function loop2()
  local pin, n = cfg.l2Pin, cfg.l2N
  logf("UV_Advanced: loop 2 - %s x %d, wait %d ms, correct pulses 1..%d on a drop of %.2f %%",
    pin, n, cfg.l2WaitMs, math.min(cfg.l2CorrectN, n), -L2_DROP)
  local readings = {}
  local prev = readUa()
  logf("%s  T2.0=%s uA", pin, fmt(prev))
  for i = 1, n do
    local tag = string.format("%s %d/%d", pin, i, n)
    pulse(pin)
    machine.wait(cfg.l2WaitMs)
    local t = readUa()
    readings[i] = fmt(t)
    local d = pct(t, prev)
    logf("%s  T2.%d=%s uA  (%s vs previous %s)", tag, i, fmt(t), fmtPct(d), fmt(prev))

    if i <= cfg.l2CorrectN then
      if d == nil then
        logf("%s  reading n/a, no correction", tag)
      elseif d <= L2_DROP then
        local after = correctX(tag, t)
        if after ~= nil then t = after end   -- the next pulse compares against where X ended up
      end
    end
    if t ~= nil then prev = t end
    machine.progress((cfg.l1N + i) / (cfg.l1N + cfg.l2N) * 100, tag)
  end
  logf("%s  T2.1..%d: %s", pin, n, table.concat(readings, ", "))
end

-- ---- run ----------------------------------------------------------------------------------------
local base = readUa()
logf("UV_Advanced: baseline %s uA on %s, correcting %s", fmt(base), cfg.signal, cfg.device)

loop1()
loop2()

local final = readUa()
logf("UV_Advanced: UV sequence completed - final %s uA (%s vs baseline %s uA), %d correction(s)",
  fmt(final), fmtPct(pct(final, base)), fmt(base), corrections)
