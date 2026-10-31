"""Port FireDog recipes (a program slot) into Horizon DLAM programs (.hwf) + a deck (.hwd).

Reads FireDog's machine root read-only (D:\\ProgramData\\FireDog) and writes, for review, into
tools/dlam/programs/<deck>/ : one .hwf per recipe and the deck (+ CaptureView.moon). With
--install it also installs into %APPDATA%\\Horizon\\products\\<product>\\ :
  processes\\*.hwf, decks\\<deck>.hwd (the first deck made active), scripts\\CaptureView.moon + UV_Advanced.moon,
  positions.json        - FireDog product positions (hidden ones skipped), by name
  config\\faam_position_map.json - FireDog's per-device "ProcessFlow" maps, for motion.movePath
An existing positions.json / map entry is backed up (*.bak-<stamp>) before it is replaced.

Usage: firedog_port.py [--install] [--product DLAM]
Mapping notes are in docs/dlam-ui.md section 9.
"""
import datetime
import json
import os
import shutil
import sys

FD_ROOT = r"D:\ProgramData\FireDog"
FD_PRODUCT = os.path.join(FD_ROOT, "products", "siphog888")
FD_RECIPES = os.path.join(FD_PRODUCT, "758e0810.recipes.json")
FD_POSITIONS = os.path.join(FD_PRODUCT, "1682f762.positions.json")
FD_MAPS = os.path.join(FD_ROOT, "config", "maps.local.json")
FD_MAP_NAME = "ProcessFlow"

# The decks being ported: (deck name, deck uid, entry uid prefix, FireDog source, recipes in slot
# order with the program names). The first deck is made the product's active deck on --install.
DECKS = [
    ("PROD Tall AUTO V3", "fd-prod-tall-v3", "fd-v3", "slot 'PROD Tall AUTO V3'", [
        ("48877384", "Initial Machine"),
        ("519c5828", "Probe SLED and PIC"),
        ("eb47eb90", "PP Left - Array"),
        ("16933405", "PP Right - Array"),
        ("795859c2", "UV advance1"),
    ]),
    ("Manual Troubleshooting", "fd-manual-troubleshooting", "fd-mt",
     "slot 'PROD Tall AUTO V3' folder 'Manual Troubleshooting'", [
        ("b8bf7cce", "Reject Left"),
        ("24810455", "Reject Right"),
        ("3d17b61c", "LEFT HOME"),
        ("c98418ed", "RIGHT HOME"),
        ("19cdd889", "Place Left lens"),
        ("19d198c3", "Place Right lens"),
        ("2ef86ac1", "Put down left lens2"),
        ("88d8b233", "Put down right lens2"),
    ]),
]

# Horizon device names on this rig (config imported from FireDog's hardware.json).
SMU = "Keithley_2602B"
PSU = "PSU_Laser"
CAMERA = "main_camera"
LIGHT_DEVICE = "LIGHT1"
TOPICS = {"keithley/ch1": f"smu.{SMU}.ch1.current", "keithley/ch2": f"smu.{SMU}.ch2.current"}

HERE = os.path.dirname(os.path.abspath(__file__))
OUT_ROOT = os.path.join(HERE, "programs")
UV_SCRIPT = os.path.join(HERE, "scripts", "UV_Advanced.moon")

CAPTURE_VIEW_MOON = """-- CaptureView - FireDog's capture_view: snap a camera and show the frame in the Run page's
-- snapshot box (e.g. the serial-number label, just before the serial prompt).
-- args (optional): camera (default "main_camera"), label (caption).
local a = args or {}
local path = machine.camera.snap(a.camera or "main_camera")
machine.ui.showImage(path, a.label or "")
"""


def num(v):
    """A parameter value as Horizon writes it (invariant, no trailing .0)."""
    if isinstance(v, bool):
        return "true" if v else "false"
    if isinstance(v, float) and v.is_integer():
        v = int(v)
    return str(v)


class Positions:
    def __init__(self):
        doc = json.load(open(FD_POSITIONS, encoding="utf-8"))["positions"]
        self.by_uid = {}
        self.records = []
        for dev, items in doc.items():
            for p in items:
                self.by_uid[p["uid"]] = (dev, p["name"])
                if not p.get("hidden"):
                    self.records.append((dev, p))

    def name(self, uid, device):
        dev, name = self.by_uid.get(uid, (None, None))
        if name is None:
            raise SystemExit(f"position uid {uid} not found")
        if dev != device:
            raise SystemExit(f"position {uid} ({name}) belongs to {dev}, not {device}")
        return name


class Program:
    """Converts one FireDog recipe into a Horizon WorkflowProgram (PascalCase JSON)."""

    def __init__(self, positions, prefix, notes):
        self.pos = positions
        self.prefix = prefix
        self.n = 0
        self.notes = notes
        self.last_device = ""   # the last motion device, for a FireDog step that names none (xcan)

    def step(self, op, name="", device="", params=None, enabled=True, before=0, after=0):
        self.n += 1
        s = {"Id": f"{self.prefix}-{self.n:02d}", "Name": name, "Op": op}
        if device:
            s["Device"] = device
        s["Parameters"] = {k: num(v) for k, v in (params or {}).items() if v is not None and v != ""}
        if not enabled:
            s["Enabled"] = False
        if before:
            s["DelayBeforeMs"] = int(before)
        if after:
            s["DelayAfterMs"] = int(after)
        return s

    def comment(self, text, enabled=True):
        return self.step("flow.comment", "FireDog: " + text[:60], params={"text": text}, enabled=enabled)

    # --- moves -------------------------------------------------------------------------------

    def move_params(self, p):
        exposure = p.get("exposure_us") or 0
        return {
            "velocity": p.get("velocity_mm_s") or 0,
            "approachDistance": p.get("approach_mm") or 0,
            "approachVelocity": p.get("approach_velocity_mm_s") or 0,
            "camera": CAMERA if exposure > 0 else "",
            "exposure": exposure,
        }

    def convert(self, s, enabled=True):
        """FireDog step → list of Horizon steps."""
        en = enabled and s.get("enabled", True)
        t, p, nick = s["type"], s.get("params", {}), s.get("nickname", "")
        dev = p.get("device", "")
        if dev.startswith(("hex-", "gantry-")):
            self.last_device = dev
        delays = {"before": p.get("pre_delay_ms", 0), "after": p.get("post_delay_ms", 0)}

        if t == "group":
            if any(c["type"] == "run_script" and "hexapod_align" in c["params"].get("file", "") for c in s["children"]):
                return self.xcan_group(s, en)
            out = [self.step("flow.groupStart", nick, params={"name": nick or "group"}, enabled=en)]
            for c in s["children"]:
                out += self.convert(c, en)
            return out + [self.step("flow.groupEnd", nick, enabled=en)]

        if t == "parallel":
            lanes = {}
            for c in s["children"]:
                lanes.setdefault(c["params"]["device"], []).extend(self.convert(c, en))
            par = self.step("flow.parallel", nick or "parallel: " + ", ".join(lanes), enabled=en)
            par["Branches"] = [{"Lane": d, "Steps": steps} for d, steps in lanes.items()]
            return [par]

        if t == "if":
            cond = f'{p["key"]} {p["op"]} {num(p["value"])}'
            out = [self.step("flow.if", nick, params={"condition": cond}, enabled=en)]
            for c in s["children"]:
                out += self.convert(c, en)
            return out + [self.step("flow.endif", nick, enabled=en)]

        if t in ("move_to_position", "move_to_position_approach"):
            return [self.step("motion.moveToPosition", nick, dev,
                              {"position": self.pos.name(p["position"], dev), **self.move_params(p)}, en, **delays)]

        if t == "move_lift_off":
            return [self.step("motion.liftOff", nick, dev,
                              {"position": self.pos.name(p["position"], dev), "liftMm": p.get("lift_mm", 2),
                               "liftAxis": p.get("lift_axis", ""), **self.move_params(p)}, en, **delays)]

        if t == "move_path":
            return [self.step("motion.movePath", nick, dev,
                              {"to": self.pos.name(p["to"], dev),
                               "from": self.pos.name(p["from"], dev) if p.get("from") else "",
                               "startTolMm": p.get("start_tol_mm"), **self.move_params(p)}, en, **delays)]

        if t == "move_axis_to":
            # No absolute single-axis move block: read the pose, then move each axis by (target - now).
            prefix = "fd_axis"
            out = [self.step("motion.readPosition", nick or f"read {dev} pose", dev, {"prefix": prefix}, en)]
            for a in p.get("axes", ""):
                target = p.get(f"{a.lower()}_mm", 0)
                out.append(self.step("motion.moveRelative", f"{dev} {a} to {num(target)} mm", dev,
                                     {"axis": a, "distance": f"expr:{num(target)} - {prefix}_{a.lower()}",
                                      "velocity": p.get("velocity_mm_s") or 0}, en, **delays))
            return out

        if t == "move_relative":
            return [self.step("motion.moveRelative", nick, dev,
                              {"axis": p["axis"], "distance": p["delta_mm"], "velocity": p.get("velocity_mm_s") or 0},
                              en, **delays)]

        if t == "set_velocity":
            return [self.step("motion.setVelocity", nick, dev, {"speed": p["velocity_mm_s"]}, en)]

        if t in ("retract_slide", "extend_slide"):
            op = "pneumatic.retract" if t == "retract_slide" else "pneumatic.extend"
            return [self.step(op, nick, p["slide"], enabled=en)]

        if t == "set_output":
            return [self.step("io.setOutputByName", nick, dev, {"pin": p["pin"], "state": bool(p["on"])}, en, **delays)]

        if t == "delay":
            return [self.step("flow.wait", nick, params={"ms": p["ms"]}, enabled=en)]

        if t == "camera_exposure":
            return [self.step("camera.setExposure", nick, CAMERA, {"exposureUs": p["us"]}, en)]

        if t == "light_color":
            return [self.step("lighting.setLightColor", nick, LIGHT_DEVICE,
                              {"light": p["light"], "color": p["color"], "percent": p["percent"]}, en)]

        if t == "prompt":
            return [self.step("flow.askUser", nick, params={"title": nick or "Confirm", "message": p["message"]}, enabled=en)]

        if t == "prompt_input" and p.get("ctx") == "serial":
            # prompt_input(serial) + dut_open(new) → one serial prompt that opens the DUT. newUnit:
            # never reuse the previous unit's (sticky) serial; a Run-page Start DUT is still adopted.
            return [self.step("dut.promptSerial", nick or "DUT serial",
                              params={"message": p.get("message", ""), "var": "serial", "newUnit": True}, enabled=en)]

        if t == "dut_open":
            return []   # folded into the serial prompt above

        if t == "capture_view":
            return [self.step("script.run", nick or "capture view", params={
                "script": "CaptureView", "args": f'camera = "{CAMERA}", label = "{p.get("label", "")}"'}, enabled=en)]

        if t == "log":
            msg = p["message"]
            if "{" in msg:
                # FireDog interpolates {ctx}; flow.log does not - the read steps log their values.
                return [self.comment("log " + msg, en)]
            return [self.step("flow.log", nick, params={"message": msg}, enabled=en)]

        if t == "read_topic":
            return [self.step("signal.read", nick, params={"signal": TOPICS[p["topic"]], "storeAs": p["ctx"]}, enabled=en)]

        if t == "keithley_read":
            return [self.step("smu.readCurrent", nick, SMU, {"channel": p.get("channel", 1), "captureAs": p["ctx"]}, en)]

        if t == "psu_startup":
            return [self.step("psu.powerUp", nick, p.get("device") or PSU, {
                "channel": p.get("channel", 1), "volts": p["volts"], "amps": p["amps"],
                "settleMs": p.get("settle_ms"), "toleranceV": p.get("tol_v"),
                "offOnFail": bool(p.get("off_on_fail", True)),
                "acceptCurrentLimit": p.get("mode", "") == "CC",
                "captureAs": p.get("ctx_prefix", "")}, en)]

        if t == "psu_output":
            op = "psu.outputOn" if p.get("on") else "psu.outputOff"
            return [self.step(op, nick, p.get("device") or PSU, {"channel": p.get("channel", 1)}, en)]

        if t == "dut_record":
            return [self.step("dut.recordFromContext", nick, params={"key": p["key"], "from": p["ctx"]}, enabled=en)]

        if t == "dut_close":
            return [self.step("dut.close", nick, params={"verdict": "Pass"}, enabled=en)]

        if t == "xcan":
            # FireDog's inline XCAN step (no device: the recipe's hexapod) → scan.xcan.
            device = dev or self.last_device
            return [self.step("scan.xcan", nick or f"XCAN {device} ({p.get('steps_um', '')} um)", device, {
                "signal": TOPICS[p.get("topic", "keithley/ch1")], "unit": "uA",
                "axes": str(p.get("axes", "XYZ")).replace(",", ""), "stepsUm": p.get("steps_um"),
                "settleMinMs": p.get("settle_min_ms") or None, "velocity": p.get("velocity_mm_s") or None,
                "adaptive": bool(p.get("adaptive", False)), "exitTarget": p.get("exit_target_ua", 0),
                "zoneLow": p.get("zone_low_ua"), "zoneHigh": p.get("zone_high_ua"),
                "captureAs": "xcan_" + device.split("-")[-1]}, en)]

        if t == "run_script" and "uv_advanced" in p.get("file", ""):
            return [self.step("script.run", nick or "UV advanced", params={"script": "UV_Advanced"}, enabled=en)]

        if t.startswith("cld_"):
            # The CLD101x laser controller is not configured on this rig (disabled in FireDog too).
            self.notes.append(f"{self.prefix}: {t} kept as a comment ({'disabled' if not en else 'ENABLED - check'})")
            return [self.comment(f"{t} {json.dumps(p)}", en)]

        raise SystemExit(f"{self.prefix}: no mapping for FireDog step '{t}' {json.dumps(p)}")

    def xcan_group(self, s, en):
        """ctx_set align_* + run_script hexapod_align.lua (+ log / dut_record of align_best) → scan.xcan."""
        nick = s.get("nickname", "")
        ctx = {c["params"]["key"]: c["params"]["value"] for c in s["children"] if c["type"] == "ctx_set"}
        device = ctx["align_device"]
        capture = "xcan_" + device.split("-")[-1]   # xcan_left / xcan_right
        out = [self.step("flow.groupStart", nick, params={"name": nick}, enabled=en)]
        params = {
            "signal": TOPICS[ctx.get("align_topic", "keithley/ch1")],
            "unit": "uA",
            "axes": str(ctx.get("align_axes", "X,Y,Z")).replace(",", ""),
            "stepsUm": ctx.get("align_steps_um", "10,5,2,1"),
            "settleMs": ctx.get("align_settle_ms"),
            "exitTarget": ctx.get("align_exit_threshold", 0),
            "captureAs": capture,
        }
        out.append(self.step("scan.xcan", f"XCAN {device} ({params['stepsUm']} um)", device, params, en))
        for c in s["children"]:
            if c["type"] == "dut_record" and c["params"]["ctx"] == "align_best":
                out.append(self.step("dut.recordFromContext", c.get("nickname", ""),
                                     params={"key": c["params"]["key"], "from": f"{capture}_best"}, enabled=en))
        return out + [self.step("flow.groupEnd", nick, enabled=en)]


def convert_all():
    """→ [(deck, programs)], positions, notes. Step ids p<n>- number the programs across all decks."""
    recipes = {r["uid"]: r for r in json.load(open(FD_RECIPES, encoding="utf-8"))["recipes"]}
    positions = Positions()
    notes = []
    decks = []
    n = 0
    for deck_name, deck_uid, entry_prefix, source, deck_recipes in DECKS:
        programs = []
        for uid, name in deck_recipes:
            n += 1
            r = recipes[uid]
            prog = Program(positions, f"p{n}", notes)
            steps = [prog.step("flow.comment", "Ported from FireDog", params={
                "text": f"FireDog recipe '{r['name']}' ({uid}), product siphog888, {source}. {r.get('description', '')}"})]
            for s in r["steps"]:
                steps += prog.convert(s)
            programs.append({"Name": name, "Steps": steps})
        deck = {"Uid": deck_uid, "Name": deck_name, "Entries": [
            {"Uid": f"{entry_prefix}-{i}", "Program": name, "Enabled": True, "Condition": "", "Auto": False,
             "DelaySeconds": 0, "ShowInRun": True}
            for i, (_, name) in enumerate(deck_recipes, 1)]}
        decks.append((deck, programs))
    return decks, positions, notes


def write_json(path, obj):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(obj, f, indent=2, ensure_ascii=False)
        f.write("\n")


def backup(path, stamp):
    if os.path.exists(path):
        shutil.copy2(path, f"{path}.bak-{stamp}")
        print(f"  backed up {path}")


def install(product, decks, positions):
    appdata = os.environ["APPDATA"]
    pdir = os.path.join(appdata, "Horizon", "products", product)
    if not os.path.isfile(os.path.join(pdir, "product.json")):
        raise SystemExit(f"product '{product}' not found at {pdir} - start DLAM.UI once to create it")
    stamp = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")

    for deck, programs in decks:
        for p in programs:
            write_json(os.path.join(pdir, "processes", p["Name"] + ".hwf"), p)
        write_json(os.path.join(pdir, "decks", deck["Name"] + ".hwd"), deck)
    write_json(os.path.join(pdir, "decks", "active.json"), {"ActiveDeck": decks[0][0]["Name"]})
    os.makedirs(os.path.join(pdir, "scripts"), exist_ok=True)
    with open(os.path.join(pdir, "scripts", "CaptureView.moon"), "w", encoding="utf-8", newline="\n") as f:
        f.write(CAPTURE_VIEW_MOON)
    shutil.copy2(UV_SCRIPT, os.path.join(pdir, "scripts", "UV_Advanced.moon"))

    # positions.json - FireDog's names; an existing file is merged (FireDog wins on a clash).
    ppath = os.path.join(pdir, "positions.json")
    backup(ppath, stamp)
    doc = json.load(open(ppath, encoding="utf-8")) if os.path.exists(ppath) else {"productName": product, "points": []}
    ported = {(d, p["name"]) for d, p in positions.records}
    points = [x for x in doc.get("points", []) if (x.get("deviceName"), x.get("positionName")) not in ported]
    for dev, p in positions.records:
        a = p["axes"]
        points.append({"deviceName": dev, "positionName": p["name"],
                       "x": a.get("X", 0.0), "y": a.get("Y", 0.0), "z": a.get("Z", 0.0),
                       "u": a.get("U", 0.0), "v": a.get("V", 0.0), "w": a.get("W", 0.0),
                       "velocity": 0, "camera": "", "exposureUs": None})
    write_json(ppath, {"productName": doc.get("productName") or product, "points": points})
    print(f"  positions.json: {len(positions.records)} FireDog positions ({len(points)} total)")

    # faam_position_map.json - the FireDog map per device, nodes by position name.
    mpath = os.path.join(appdata, "Horizon", "config", "faam_position_map.json")
    backup(mpath, stamp)
    mdoc = json.load(open(mpath, encoding="utf-8")) if os.path.exists(mpath) else {}
    products = mdoc.setdefault("products", {})
    devices = products.setdefault(product, {})
    for dev, maps in json.load(open(FD_MAPS, encoding="utf-8"))["maps"].items():
        m = next((m for m in maps if m["name"] == FD_MAP_NAME), None)
        if m is None:
            continue
        names = {}
        for n in m["nodes"]:
            d, name = positions.by_uid.get(n["pos_uid"], (None, None))
            if d == dev and (dev, name) in ported:
                names[n["pos_uid"]] = (name, n["x"], n["y"])
            else:
                print(f"  map {dev}: node {n['pos_uid']} is not a ported {dev} position - skipped")
        edges = [{"a": names[e["a"]][0], "b": names[e["b"]][0]} for e in m["edges"] if e["a"] in names and e["b"] in names]
        devices[dev] = {"nodes": [{"name": nm, "x": x, "y": y} for nm, x, y in names.values()], "edges": edges}
        print(f"  map {dev}: {len(names)} nodes, {len(edges)} of {len(m['edges'])} links")
    write_json(mpath, mdoc)
    print(f"installed into {pdir}")


def main():
    decks, positions, notes = convert_all()
    for deck, programs in decks:
        out_dir = os.path.join(OUT_ROOT, deck["Name"].replace(" ", "_"))
        for p in programs:
            write_json(os.path.join(out_dir, p["Name"] + ".hwf"), p)
        write_json(os.path.join(out_dir, deck["Name"] + ".hwd"), deck)
        print(f"wrote {len(programs)} programs + deck to {out_dir}")
    first_dir = os.path.join(OUT_ROOT, decks[0][0]["Name"].replace(" ", "_"))
    with open(os.path.join(first_dir, "CaptureView.moon"), "w", encoding="utf-8", newline="\n") as f:
        f.write(CAPTURE_VIEW_MOON)
    for n in notes:
        print("  note:", n)
    if "--install" in sys.argv:
        product = sys.argv[sys.argv.index("--product") + 1] if "--product" in sys.argv else "DLAM"
        install(product, decks, positions)


if __name__ == "__main__":
    main()
