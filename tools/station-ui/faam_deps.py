"""Dependency levels of FAAM.UI's own types: L0 = uses no other FAAM type."""
import collections
import os
import re
import sys

root = sys.argv[1]
files = []
for d, dirs, fs in os.walk(root):
    dirs[:] = [x for x in dirs if x not in ("obj", "bin")]
    for f in fs:
        if f.endswith(".cs") or f.endswith(".xaml"):
            files.append(os.path.relpath(os.path.join(d, f), root).replace(os.sep, "/"))

decl = re.compile(r"\b(?:class|record|struct|enum|interface)\s+([A-Z]\w*)")
text = {f: open(os.path.join(root, f), encoding="utf-8", errors="ignore").read() for f in files}


def unit_of(f):
    return f[:-3] if f.endswith(".xaml.cs") else f


units = collections.defaultdict(list)
for f in files:
    units[unit_of(f)].append(f)

owner = {}
for f in files:
    if f.endswith(".cs"):
        for m in decl.finditer(text[f]):
            owner.setdefault(m.group(1), unit_of(f))

deps = {}
for u, fs in units.items():
    t = "".join(text[f] for f in fs)
    used = set()
    for name, o in owner.items():
        if o == u:
            continue
        if re.search(r"\b" + re.escape(name) + r"\b", t):
            used.add(o)
    deps[u] = used

level = {}
for _ in range(60):
    for u, d in deps.items():
        if u not in level and all(x in level for x in d):
            level[u] = 1 + max([level[x] for x in d], default=-1)

by = collections.defaultdict(list)
for u, l in level.items():
    by[l].append(u)
for l in sorted(by):
    print(f"L{l} ({len(by[l])}): " + " ".join(sorted(by[l])))
cyc = sorted(set(deps) - set(level))
print(f"\nIn cycles / depending on cycles ({len(cyc)}):")
print(" ".join(cyc))

# --- strongly connected components (iterative Tarjan) ---
index, low, onstack, stack, sccs = {}, {}, set(), [], []
counter = [0]
for start in deps:
    if start in index:
        continue
    work = [(start, iter(sorted(deps[start])))]
    index[start] = low[start] = counter[0]; counter[0] += 1
    stack.append(start); onstack.add(start)
    while work:
        v, it = work[-1]
        nxt = next(it, None)
        if nxt is not None:
            if nxt not in index:
                index[nxt] = low[nxt] = counter[0]; counter[0] += 1
                stack.append(nxt); onstack.add(nxt)
                work.append((nxt, iter(sorted(deps[nxt]))))
            elif nxt in onstack:
                low[v] = min(low[v], index[nxt])
        else:
            work.pop()
            if work:
                low[work[-1][0]] = min(low[work[-1][0]], low[v])
            if low[v] == index[v]:
                comp = []
                while True:
                    w = stack.pop(); onstack.discard(w); comp.append(w)
                    if w == v:
                        break
                sccs.append(comp)
big = max(sccs, key=len)
print(f"\nLargest SCC: {len(big)} units")
print(" ".join(sorted(big)))
for hub in ("EngineHost.cs", "ShellWindow.xaml", "App.xaml"):
    print(f"\n{hub} -> " + " ".join(sorted(deps.get(hub, []))))
