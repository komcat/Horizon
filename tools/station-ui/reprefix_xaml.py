"""Re-point FAAM.UI XAML references to types that moved into Horizon.Station.UI.

For every FAAM .xaml: find xmlns prefixes mapped to clr-namespace:FAAM.UI.<ns>; any
'<prefix>:<MovedType>' use (element, x:Type, attached property) is rewritten to 'st:<MovedType>'
and xmlns:st="urn:horizon-station-ui" is added. Prefixes left with no use are reported.
Usage: reprefix_xaml.py <src dir> [--apply]
"""
import os
import re
import sys

src = sys.argv[1]
apply = "--apply" in sys.argv
lib = os.path.join(src, "Horizon.Station.UI")
head = os.path.join(src, "FAAM.UI")

decl = re.compile(r"\b(?:class|record|struct|enum|interface)\s+([A-Z]\w*)")
moved = set()
for d, dirs, fs in os.walk(lib):
    dirs[:] = [x for x in dirs if x not in ("obj", "bin")]
    for f in fs:
        if f.endswith(".cs"):
            moved |= set(decl.findall(open(os.path.join(d, f), encoding="utf-8").read()))

xmlns_re = re.compile(r'xmlns:(\w+)="clr-namespace:(FAAM\.UI[\w.]*)"')
for d, dirs, fs in os.walk(head):
    dirs[:] = [x for x in dirs if x not in ("obj", "bin")]
    for f in fs:
        if not f.endswith(".xaml"):
            continue
        p = os.path.join(d, f)
        s = open(p, encoding="utf-8").read()
        prefixes = dict(xmlns_re.findall(s))
        if not prefixes:
            continue
        changed = []

        def fix(m):
            pre, name = m.group(1), m.group(2)
            if pre in prefixes and name in moved:
                changed.append(f"{pre}:{name}")
                return f"st:{name}"
            return m.group(0)

        n = re.sub(r"\b(\w+):([A-Z]\w*)", fix, s)
        if not changed:
            continue
        if 'xmlns:st="urn:horizon-station-ui"' not in n:
            n = re.sub(r'(xmlns:x="http://schemas\.microsoft\.com/winfx/2006/xaml")',
                       r'\1\n             xmlns:st="urn:horizon-station-ui"', n, count=1)
        unused = [pre for pre in prefixes if not re.search(r"\b" + pre + r":[A-Z]", n.split(">", 1)[1] if ">" in n else n)]
        rel = os.path.relpath(p, src)
        print(f"{rel}: {sorted(set(changed))}" + (f"  (now-unused prefixes: {unused})" if unused else ""))
        if apply:
            open(p, "w", encoding="utf-8", newline="").write(n)
