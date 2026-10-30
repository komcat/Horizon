-- XCAN - coordinate-descent active alignment, coarse to fine, on one hexapod
-- (DLAM; FireDog's hexapod_align.lua on the machine API - the same algorithm as the
-- Layer 2 scan.xcan block, see docs/dlam-ui.md 5.1)
--
-- For each step size (coarse -> fine), each axis in turn (Z, Y, X by default):
--   ratchet-walk one direction while the signal rises; unless that walk went uphill (the
--   peak is then bracketed), walk the other direction too; settle on the better point.
-- After each axis, drive back to the global peak if the signal fell more than improvePct
-- below it. At the end, park on the global peak - or, if the exit target was reached, stay
-- exactly on the point that reached it.
--
-- Feedback: Keithley_2602B channel 1 by default (machine.smu.readCurrent - a fresh
-- instrument measurement per read, so the first read after a move is a post-move sample).
-- 'signal' switches to a catalog key (machine.signal.read; not available in Horizon.Console).
-- Signal levels below are in uA and the signal is MAXIMISED as read (signed).
--
-- Bias: by default the script sets 'bias' volts on the primary SMU channel and switches the
-- SMU outputs ON (every channel - a 2602B channel with its output off reads ~0 A).
-- bias = false leaves the SMU exactly as it is.
--
-- Dark guard: a baseline under minSignalUa (default 1 uA) stops the script before anything moves
-- - an XCAN in the dark only chases instrument noise. Power the light source first.
--
-- Safety (machine.onAbort): on STOP / Ctrl+C or any error the hexapod returns to the global
-- peak - the best point measured - unless the exit target had already been reached.
-- Every step is capped at 100 um and every walk at maxTravelUm (<= 1000 um) per direction.
--
-- Arguments - the global 'args' table (Run XCAN menu in Horizon.Console passes the device;
-- elsewhere a script preset, the Run Script block's Arguments or the Script editor's Args box).
-- Every key is optional; an unknown key or a wrong type fails the script before anything moves.
--   device        hexapod                                          "hex-left"
--   smu, channel  feedback SMU and channel                         "Keithley_2602B", 1
--   signal        catalog signal key instead of smu/channel        ""
--   bias          volts on the primary channel + outputs ON (false = leave)   1.0
--   axes          descent order                                    "ZYX"
--   stepsUm       ladder, coarse -> fine                           {10, 5, 2, 1}
--   exitUa        stop-and-stay target (0 = off)                   0
--   adaptive      skip rungs coarser than the signal's log zone    false
--   zoneLowUa / zoneHighUa  adaptive zone range                    1 / 400
--   settleMinMs   wait before each measurement                     50
--   samples       reads averaged per measurement                   1
--   avgCount      reads averaged for the baseline                  3
--   consecLimit   non-improving steps that end a direction         3
--   sigDropPct    relative drop that ends a direction at once      15
--   maxTravelUm   travel cap per direction                         200
--   improvePct    global-peak recovery threshold                   1
--   minSignalUa   refuse to start below this |baseline| (0 = off)  1
--   velocity      scan velocity mm/s, set once (0 = keep)          1
--   tolUm         "already there" tolerance                        1
--   skipOpposite / dirMemory / reuseSample   cycle-time trims      true
--   trace         one CSV log line per sample                      false
-- e.g.  { device = "hex-right", exitUa = 400, trace = true }

local STEP_CAP_UM, TRAVEL_CAP_UM = 100, 1000

local DEFAULTS = {
  device = "hex-left", smu = "Keithley_2602B", channel = 1, signal = "", bias = 1.0,
  axes = "ZYX", stepsUm = { 10, 5, 2, 1 },
  exitUa = 0, adaptive = false, zoneLowUa = 1, zoneHighUa = 400,
  settleMinMs = 50, samples = 1, avgCount = 3, consecLimit = 3, sigDropPct = 15,
  maxTravelUm = 200, improvePct = 1, minSignalUa = 1, velocity = 1, tolUm = 1,
  skipOpposite = true, dirMemory = true, reuseSample = true, trace = false,
}

-- ---- arguments -> cfg (nothing moves until this passes) -------------------------------------
local function check(ok, msg)
  if not ok then error("XCAN: " .. msg) end
end

local cfg = {}
for k, v in pairs(DEFAULTS) do cfg[k] = v end
for k, v in pairs(args or {}) do
  local d = DEFAULTS[k]
  check(d ~= nil, "unknown argument '" .. tostring(k) .. "'")
  local ok = type(v) == type(d) or (k == "bias" and v == false)
  check(ok, string.format("argument '%s' must be a %s (got %s)", tostring(k), type(d), type(v)))
  cfg[k] = v
end

local AXES = {}
for a in string.gmatch(string.upper(cfg.axes), "[XYZUVW]") do AXES[#AXES + 1] = a end
check(#AXES > 0, "axes names no axis (use X Y Z U V W)")
check(#cfg.stepsUm > 0, "stepsUm is empty")
local STEPS = {}
for i, s in ipairs(cfg.stepsUm) do
  check(type(s) == "number" and s > 0 and s <= STEP_CAP_UM,
    string.format("stepsUm[%d] must be a number in (0, %d]", i, STEP_CAP_UM))
  STEPS[i] = s / 1000.0
end
check(cfg.maxTravelUm > 0 and cfg.maxTravelUm <= TRAVEL_CAP_UM,
  string.format("maxTravelUm must be in (0, %d]", TRAVEL_CAP_UM))
check(cfg.samples >= 1 and cfg.avgCount >= 1 and cfg.consecLimit >= 1, "samples, avgCount and consecLimit must be >= 1")
check(cfg.settleMinMs >= 0 and cfg.sigDropPct > 0 and cfg.improvePct >= 0 and cfg.tolUm > 0,
  "settleMinMs >= 0, sigDropPct > 0, improvePct >= 0, tolUm > 0")
check(cfg.channel == 1 or cfg.channel == 2, "channel must be 1 or 2")
if cfg.adaptive then
  check(cfg.zoneLowUa > 0 and cfg.zoneHighUa > cfg.zoneLowUa, "adaptive zones need 0 < zoneLowUa < zoneHighUa")
end
cfg.samples, cfg.avgCount, cfg.consecLimit = math.floor(cfg.samples), math.floor(cfg.avgCount), math.floor(cfg.consecLimit)

local DEV = cfg.device
local MAX_TRAVEL = cfg.maxTravelUm / 1000.0
local TOL = cfg.tolUm / 1000.0
local FEED = cfg.signal ~= "" and cfg.signal or string.format("%s ch%d", cfg.smu, cfg.channel)

-- ---- state --------------------------------------------------------------------------------------
local exitHit = false
local nSamples = 0
local gpeak = { v = -math.huge, pos = nil }
local dirMem = {}
local tr = { seq = 0, v = nil, phase = "base", axis = "-", dir = 0, step = 0 }

-- ---- abort: back to the global peak (unless the exit target was reached) ----------------------
machine.onAbort(function(reason)
  if exitHit or gpeak.pos == nil then return end
  local p = gpeak.pos
  machine.motion.movePose(DEV, { x = p.x, y = p.y, z = p.z, u = p.u, v = p.v, w = p.w })
  machine.log(string.format("XCAN %s: %s returned to the global peak (%.4g uA)", tostring(reason), DEV, gpeak.v))
end)

-- ---- signal -------------------------------------------------------------------------------------
local function readUa()
  local a
  if cfg.signal ~= "" then a = machine.signal.read(cfg.signal)
  else a = machine.smu.readCurrent(cfg.smu, cfg.channel) end
  check(a == a, "feedback " .. FEED .. " reads NaN (over range?)")
  return a * 1e6
end

local function measure()
  if cfg.settleMinMs > 0 then machine.wait(cfg.settleMinMs) end
  local sum = 0
  for _ = 1, cfg.samples do sum = sum + readUa() end
  local v = sum / cfg.samples
  nSamples = nSamples + 1
  if cfg.exitUa ~= 0 and v >= cfg.exitUa and not exitHit then
    exitHit = true
    machine.log(string.format("XCAN exit target reached: %.4g >= %.4g uA", v, cfg.exitUa))
  end
  return v
end

-- ---- motion -------------------------------------------------------------------------------------
local function snapshot() return machine.motion.position(DEV) end
local function pos(axis) return snapshot()[string.lower(axis)] end
local function moveRel(axis, d) machine.motion.moveRel(DEV, axis, d) end
-- An absolute move to where the stage already stands (within tolUm) is skipped - the walk
-- often returns to a point it never left.
local function moveAbs(axis, t)
  if math.abs(pos(axis) - t) <= TOL then return end
  machine.motion.moveAbs(DEV, axis, t)
end
local function gotoPose(p)
  machine.motion.movePose(DEV, { x = p.x, y = p.y, z = p.z, u = p.u, v = p.v, w = p.w })
end

local function updatePeak(v)
  if v > gpeak.v then gpeak.v, gpeak.pos = v, snapshot() end
end

local function trace(v, note)
  if not cfg.trace then return end
  tr.seq = tr.seq + 1
  local p = snapshot()
  local cols = {}
  for _, a in ipairs(AXES) do cols[#cols + 1] = string.format("%s=%.5f", a, p[string.lower(a)]) end
  machine.log(string.format("xcan,%d,%s,%s,%+d,%g,%s,v=%.6g,dv=%+.4g,%s", tr.seq, tr.phase, tr.axis, tr.dir,
    tr.step * 1000, table.concat(cols, ","), v, tr.v and (v - tr.v) or 0, note))
  tr.v = v
end

local function phase(ph, axis, dir, step) tr.phase, tr.axis, tr.dir, tr.step = ph, axis, dir, step end

-- ---- adaptive zoning: signal -> the zone's step (equal log slices of [low, high]) -------------
local function zoneStep(v)
  if v <= cfg.zoneLowUa then return STEPS[1] end
  if v >= cfg.zoneHighUa then return STEPS[#STEPS] end
  local frac = math.log(v / cfg.zoneLowUa) / math.log(cfg.zoneHighUa / cfg.zoneLowUa)
  return STEPS[math.min(math.floor(frac * #STEPS) + 1, #STEPS)]
end

-- ---- the ratchet walk: one axis, one direction --------------------------------------------------
-- v0: a sample already taken where the stage stands (reuse). Returns maxV, maxPos, improved, curV
-- (the value measured where the stage is left).
local function scanDirection(axis, step, dir, v0)
  phase("scan", axis, dir, step)
  local maxV = v0
  if maxV == nil then
    maxV = measure()
    trace(maxV, "start")
  end
  local maxPos = pos(axis)
  updatePeak(maxV)
  local prev, consec, dist, movedFromMax, improved = maxV, 0, 0.0, false, false
  local maxSteps = math.ceil(MAX_TRAVEL / step) + 1
  local k = 0
  while k < maxSteps and not exitHit and consec < cfg.consecLimit and dist < MAX_TRAVEL do
    k = k + 1
    moveRel(axis, dir * step)
    local v = measure()
    dist = dist + step
    if v > maxV then
      maxV, maxPos = v, pos(axis)
      consec, movedFromMax, improved = 0, false, true
      updatePeak(v)
      trace(v, "better")
    else
      consec = consec + 1
      movedFromMax = true
      if prev > 0 and (prev - v) / prev > cfg.sigDropPct / 100.0 then
        trace(v, "drop-break")
        break
      end
      trace(v, string.format("worse(%d/%d)", consec, cfg.consecLimit))
    end
    prev = v
  end
  local curV = maxV
  if movedFromMax and not exitHit then   -- on an exit, STAY on the point that hit it
    moveAbs(axis, maxPos)
    local v = measure()
    trace(v, "return-to-best")
    if v > maxV then maxV = v end
    curV = v
  end
  return maxV, maxPos, improved, curV
end

-- ---- one axis at one step: one direction, the other unless bracketed, keep the better -----------
local function scanAxis(axis, step, v0)
  local p0 = pos(axis)
  local d1 = (cfg.dirMemory and dirMem[axis]) or 1
  local v1, p1, imp1, cur1 = scanDirection(axis, step, d1, cfg.reuseSample and v0 or nil)
  if exitHit then return v1, nil end
  if imp1 then
    dirMem[axis] = d1
    if cfg.skipOpposite then
      machine.log(string.format("  %s @ %g um: best=%.4g (one-sided)", axis, step * 1000, v1))
      return v1, cur1
    end
  end
  moveAbs(axis, p0)   -- a no-improvement walk already stands on p0, so cur1 is a sample there
  local v2, p2, imp2, cur2 = scanDirection(axis, step, -d1, (cfg.reuseSample and not imp1) and cur1 or nil)
  if exitHit then return v2, nil end
  if imp2 then dirMem[axis] = -d1 end
  local bestV, bestP = v1, p1
  if v2 > v1 then bestV, bestP = v2, p2 end
  local curV = nil
  if math.abs(bestP - p2) <= TOL then curV = cur2 else moveAbs(axis, bestP) end
  machine.log(string.format("  %s @ %g um: best=%.4g", axis, step * 1000, bestV))
  return bestV, curV
end

-- ---- run ----------------------------------------------------------------------------------------
local ladder = {}
for _, s in ipairs(STEPS) do ladder[#ladder + 1] = string.format("%g", s * 1000) end
machine.log(string.format("XCAN: %s on %s, axes %s, steps %s um%s", DEV, FEED, table.concat(AXES, ","),
  table.concat(ladder, ","), cfg.exitUa ~= 0 and string.format(", exit at %g uA", cfg.exitUa) or ""))

if cfg.bias ~= false and cfg.signal == "" then
  machine.smu.sourceVoltage(cfg.smu, cfg.bias)
  machine.smu.outputOn(cfg.smu)
  machine.log(string.format("XCAN: %s biased at %g V, outputs ON", cfg.smu, cfg.bias))
  machine.wait(300)
end
if cfg.velocity > 0 then machine.motion.setVelocity(DEV, cfg.velocity) end
if cfg.adaptive then
  local bounds = {}
  for i = 1, #STEPS - 1 do
    bounds[#bounds + 1] = string.format("%.3g", cfg.zoneLowUa * (cfg.zoneHighUa / cfg.zoneLowUa) ^ (i / #STEPS))
  end
  machine.log(string.format("XCAN adaptive ladder: %d log zones over [%g, %g] uA (bounds %s)",
    #STEPS, cfg.zoneLowUa, cfg.zoneHighUa, table.concat(bounds, "/")))
end
if cfg.trace then
  machine.log("xcan,header: seq,phase,axis,dir,step_um,pos(" .. table.concat(AXES, ",") .. "),value_uA,dv,note")
end

-- Averaged baseline - the only averaging in the scan.
local carry, sum = nil, 0
for i = 1, cfg.avgCount do
  local v = measure()
  trace(v, "baseline")
  sum, carry = sum + v, v
  if i < cfg.avgCount then machine.wait(50) end
end
local baseline = sum / cfg.avgCount
machine.log(string.format("XCAN baseline = %.4g uA", baseline))
check(math.abs(baseline) >= cfg.minSignalUa,
  string.format("baseline %.3g uA below minSignalUa %g - is the beam on?", baseline, cfg.minSignalUa))
updatePeak(baseline)

local passesRun, passesSkipped = 0, 0
for si, step in ipairs(STEPS) do
  if exitHit then break end
  local skip = false
  if cfg.adaptive then
    local ref = math.max(carry or 0, gpeak.v)   -- the best REACHABLE signal, not one noisy sample
    local zs = zoneStep(ref)
    if step > zs * 1.001 then
      machine.log(string.format("XCAN pass @ %g um skipped (adaptive: %.4g uA -> zone step %g um)", step * 1000, ref, zs * 1000))
      skip = true
      passesSkipped = passesSkipped + 1
    end
  end
  if not skip then
    passesRun = passesRun + 1
    machine.log(string.format("XCAN pass @ %g um", step * 1000))
    for _, axis in ipairs(AXES) do
      local _, curV = scanAxis(axis, step, carry)
      if exitHit then break end
      phase("check", axis, 0, step)
      local cur = cfg.reuseSample and curV or nil   -- the return-to-best sample IS the check
      if cur == nil then
        machine.wait(100)
        cur = measure()
        trace(cur, "axis-done")
        if exitHit then break end
      end
      carry = cur
      if cur > 0 and gpeak.pos ~= nil and (gpeak.v - cur) / cur > cfg.improvePct / 100.0 then
        machine.log(string.format("  recovering to global peak (%.4g > %.4g uA)", gpeak.v, cur))
        gotoPose(gpeak.pos)
        carry = measure()
        trace(carry, "recovered")
      end
    end
  end
  machine.progress(si / #STEPS * 100, string.format("pass %d/%d", si, #STEPS))
end

-- Finish: on the exit point when the target was reached, else on the best point seen.
phase("final", "-", 0, 0)
local finalV = measure()
trace(finalV, "final")
if not exitHit and gpeak.pos ~= nil and gpeak.v > finalV then
  gotoPose(gpeak.pos)
  finalV = measure()
  trace(finalV, "final@gpeak")
end

local gain = (baseline > 0 and finalV > 0) and string.format("%.2fx", finalV / baseline) or "n/a (signal not positive)"
local p = snapshot()
machine.log(string.format("XCAN done: %s baseline %.4g -> best %.4g uA (delta %+.4g, gain %s)%s; %d pass(es) run, %d skipped, %d samples",
  DEV, baseline, finalV, finalV - baseline, gain, exitHit and ", exit target reached" or "", passesRun, passesSkipped, nSamples))
machine.log(string.format("XCAN final pose: X %.5f  Y %.5f  Z %.5f  U %.5f  V %.5f  W %.5f", p.x, p.y, p.z, p.u, p.v, p.w))
