"""Slice 3: move every generic FAAM.UI unit into Horizon.Station.UI (git mv + namespace rewrite).

Generic = every .cs / .xaml unit not in backedges.HEAD_ONLY. Files keep their folder layout.
In moved files: namespace / using / x:Class / clr-namespace / global:: FAAM.UI -> Horizon.Station.UI.
Usage: move_slice3.py <src dir> [--apply]
"""
import os
import re
import subprocess
import sys

src = sys.argv[1]
apply = "--apply" in sys.argv
head = os.path.join(src, "FAAM.UI")
lib = os.path.join(src, "Horizon.Station.UI")

# HEAD_ONLY lives in backedges.py; read it without running that script.
here = os.path.dirname(os.path.abspath(__file__))
bsrc = open(os.path.join(here, "backedges.py"), encoding="utf-8").read()
HEAD_ONLY = eval(re.search(r"HEAD_ONLY = (\{.*?\n\})", bsrc, re.S).group(1))

files = []
for d, dirs, fs in os.walk(head):
    dirs[:] = [x for x in dirs if x not in ("obj", "bin")]
    for f in fs:
        if f.endswith(".cs") or f.endswith(".xaml"):
            files.append(os.path.relpath(os.path.join(d, f), head).replace(os.sep, "/"))


def unit_of(f):
    return f[:-3] if f.endswith(".xaml.cs") else f


moving = sorted(f for f in files if unit_of(f) not in HEAD_ONLY)
print(f"moving {len(moving)} files, keeping {len(files) - len(moving)}")

REWRITES = [
    (re.compile(r"\bnamespace FAAM\.UI\b"), "namespace Horizon.Station.UI"),
    (re.compile(r"\busing FAAM\.UI\b"), "using Horizon.Station.UI"),
    (re.compile(r'x:Class="FAAM\.UI\b'), 'x:Class="Horizon.Station.UI'),
    (re.compile(r"clr-namespace:FAAM\.UI\b"), "clr-namespace:Horizon.Station.UI"),
    (re.compile(r"\bglobal::FAAM\.UI\b"), "global::Horizon.Station.UI"),
]

for f in moving:
    s = os.path.join(head, f)
    t = os.path.join(lib, f)
    if os.path.exists(t):
        sys.exit(f"target exists: {t}")
    if not apply:
        print("  ", f)
        continue
    os.makedirs(os.path.dirname(t), exist_ok=True)
    subprocess.run(["git", "mv", s, t], check=True)
    txt = open(t, encoding="utf-8-sig").read()
    bom = open(t, "rb").read(3) == b"\xef\xbb\xbf"
    for rx, rep in REWRITES:
        txt = rx.sub(rep, txt)
    with open(t, "w", encoding="utf-8-sig" if bom else "utf-8", newline="") as o:
        o.write(txt)
