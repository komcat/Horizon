"""
One-off migration: overlay hand-taught coordinates from the legacy SAA config
(siphogplus_20260605) onto the Horizon SIPHOG-Plus positions.json.

Strategy (chosen by operator):
  - Update X/Y/Z (gantry) and X/Y/Z/U/V/W (hexapod) for the 20 legacy positions,
    keyed by (device, name): legacy gantry -> 'gantry-main', hexapod -> 'hex-right'.
  - PRESERVE Velocity / camera / exposureUs from the current Horizon entries
    (so PIC & FAU keep main_camera + exposure).
  - LEAVE the 10 Horizon-only positions (UCorr_*, FauPre_*, fid2, Serial Number)
    untouched.
  - Fix the internal productName to 'SIPHOG-Plus'.
  - Back up the original first; print a change report. Writes nothing on dry-run.
"""
import json, os, sys, shutil

LEGACY_DIR = r"C:\SOFTWARE\SAA_SIPHOG_PLUS\Debug\config"
GANTRY_SRC = os.path.join(LEGACY_DIR, "PositionsGantry_SIPHOGPLUS_20260605.json")
HEX_SRC    = os.path.join(LEGACY_DIR, "PositionsHexapod_SIPHOGPLUS_20260605.json")
TARGET     = os.path.join(os.environ["APPDATA"], "Horizon", "products", "SIPHOG-Plus", "positions.json")

GANTRY_DEVICE = "gantry-main"
HEX_DEVICE    = "hex-right"

dry_run = "--apply" not in sys.argv

def load(p):
    with open(p, "r", encoding="utf-8-sig") as f:
        return json.load(f)

gantry = load(GANTRY_SRC)   # { name: {X,Y,Z} }
hexp   = load(HEX_SRC)      # { name: {X,Y,Z,U,V,W} }
doc    = load(TARGET)       # { productName, points:[...] }

changes, kept_extra, missing_in_legacy = [], [], []

def close(a, b, eps=1e-9):
    return abs(a - b) <= eps

for pt in doc["points"]:
    dev, name = pt["deviceName"], pt["positionName"]
    src = None
    if dev == GANTRY_DEVICE and name in gantry:
        src = gantry[name]; axes = ("X", "Y", "Z")
    elif dev == HEX_DEVICE and name in hexp:
        src = hexp[name]; axes = ("X", "Y", "Z", "U", "V", "W")

    if src is None:
        kept_extra.append(f"{dev}/{name}")
        continue

    diffs = []
    for ax in axes:
        new = float(src[ax])
        if not close(float(pt.get(ax, 0.0)), new):
            diffs.append(f"{ax} {pt.get(ax, 0.0):.6g} -> {new:.6g}")
        pt[ax] = new
    # gantry has no U/V/W in legacy: leave existing (already 0) as-is.
    if diffs:
        extras = []
        if pt.get("camera"): extras.append(f"camera={pt['camera']}")
        if pt.get("exposureUs") is not None: extras.append(f"exp={pt['exposureUs']}")
        if pt.get("Velocity"): extras.append(f"vel={pt['Velocity']}")
        tag = f"  [kept {', '.join(extras)}]" if extras else ""
        changes.append(f"{dev}/{name}: " + "; ".join(diffs) + tag)

# legacy names not present in the target (should be none) -> report
present = {(p["deviceName"], p["positionName"]) for p in doc["points"]}
for name in gantry:
    if (GANTRY_DEVICE, name) not in present: missing_in_legacy.append(f"{GANTRY_DEVICE}/{name}")
for name in hexp:
    if (HEX_DEVICE, name) not in present: missing_in_legacy.append(f"{HEX_DEVICE}/{name}")

doc["productName"] = "SIPHOG-Plus"

print(f"=== Migration {'(DRY RUN)' if dry_run else '(APPLY)'} ===")
print(f"Target: {TARGET}\n")
print(f"Coordinate updates ({len(changes)}):")
for c in changes: print("  " + c)
print(f"\nHorizon-only positions left untouched ({len(kept_extra)}):")
print("  " + ", ".join(kept_extra))
if missing_in_legacy:
    print(f"\nLegacy names NOT found in target (would be skipped): {missing_in_legacy}")
print(f"\nproductName -> SIPHOG-Plus")

if dry_run:
    print("\nDry run only. Re-run with --apply to write (a .bak backup is made first).")
else:
    bak = TARGET + ".bak"
    shutil.copy2(TARGET, bak)
    with open(TARGET, "w", encoding="utf-8") as f:
        json.dump(doc, f, indent=2)
    print(f"\nBackup written: {bak}")
    print(f"Wrote: {TARGET}")
