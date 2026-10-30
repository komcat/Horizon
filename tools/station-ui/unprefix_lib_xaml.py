"""Inside Horizon.Station.UI, rewrite 'st:<Type>' (the library's own urn: namespace, which the
XAML compiler cannot resolve in the defining assembly) to a clr-namespace prefix.

Reuses a prefix the file already maps to the type's namespace, else adds one; drops xmlns:st
when nothing uses it any more.
Usage: unprefix_lib_xaml.py <Horizon.Station.UI dir> [--apply]
"""
import os
import re
import sys

lib = sys.argv[1]
apply = "--apply" in sys.argv

ns_re = re.compile(r"^\s*namespace\s+([\w.]+)", re.M)
decl = re.compile(r"\b(?:class|record|struct|enum|interface)\s+([A-Z]\w*)")
type_ns = {}
for d, dirs, fs in os.walk(lib):
    dirs[:] = [x for x in dirs if x not in ("obj", "bin")]
    for f in fs:
        if f.endswith(".cs"):
            s = open(os.path.join(d, f), encoding="utf-8-sig").read()
            m = ns_re.search(s)
            if m:
                for t in decl.findall(s):
                    type_ns.setdefault(t, m.group(1))

NEW_PREFIX = {
    "Horizon.Station.UI": "sui", "Horizon.Station.UI.Mvvm": "smvvm",
    "Horizon.Station.UI.Telemetry": "stel", "Horizon.Station.UI.Process": "sproc",
    "Horizon.Station.UI.ViewModels": "svm", "Horizon.Station.UI.ViewModels.Sections": "svms",
    "Horizon.Station.UI.ViewModels.Timeline": "svmt", "Horizon.Station.UI.Views": "sv",
    "Horizon.Station.UI.Views.Sections": "svs",
}
ST = 'xmlns:st="urn:horizon-station-ui"'

for d, dirs, fs in os.walk(lib):
    dirs[:] = [x for x in dirs if x not in ("obj", "bin")]
    for f in fs:
        if not f.endswith(".xaml"):
            continue
        p = os.path.join(d, f)
        s = open(p, encoding="utf-8-sig").read()
        if ST not in s:
            continue
        existing = {ns: pre for pre, ns in re.findall(r'xmlns:(\w+)="clr-namespace:([\w.]+)"', s)}
        added = {}

        def fix(m):
            t = m.group(1)
            ns = type_ns.get(t)
            if ns is None:
                print(f"  ?? {f}: st:{t} not found in library")
                return m.group(0)
            pre = existing.get(ns) or added.setdefault(ns, NEW_PREFIX[ns])
            return f"{pre}:{t}"

        n = re.sub(r"\bst:([A-Z]\w*)", fix, s)
        decls = "".join(f'\n             xmlns:{pre}="clr-namespace:{ns}"' for ns, pre in added.items())
        if re.search(r"\bst:[A-Z]", n):
            n = n.replace(ST, ST + decls, 1)
        else:
            n = re.sub(r"\s*" + re.escape(ST), decls, n, count=1)
        print(f"{os.path.relpath(p, lib)}: added {sorted(added.values())}")
        if apply:
            open(p, "w", encoding="utf-8", newline="").write(n)
