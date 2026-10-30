"""Generic FAAM.UI units that still reference head-only (FAU-specific) units.

Usage: backedges.py <FAAM.UI dir>
"""
import collections
import os
import re
import sys

root = sys.argv[1]
HEAD_ONLY = {
    # SiPhOG + FAU-specific data / settings
    "Siphog/HillClimbToSpiralFeedback.cs", "Siphog/SiphogClient.cs", "Siphog/SiphogHillClimbFeedback.cs",
    "Siphog/SiphogPanelViewModel.cs", "Siphog/SiphogSheetSink.cs", "Siphog/SiphogSignalSource.cs",
    "DutUvData.cs", "FindFirstLightSettingsExtensions.cs", "ScanPanelStore.cs", "Process/TeachPointStore.cs",
    "ViewModels/FindFirstLightSettingsViewModel.cs", "Views/FindFirstLightSettingsView.xaml",
    # Run page + its panels + the utility-app launcher
    "Views/RunView.xaml", "Views/FaamBuiltInSteps.cs", "Views/AlignmentPanel.xaml", "Views/AutoWPanel.xaml",
    "Views/FauPreAlignPanel.xaml", "Views/HillClimbPanel.xaml", "Views/PsuQuickPanel.xaml",
    "Views/RasterClimbPanel.xaml", "Views/ScanningPanel.xaml", "Views/ScanPanel.xaml", "Views/SpiralRunPanel.xaml",
    "Views/UCorrectionPanel.xaml", "Views/UtilityAppHost.xaml", "Apps/IUtilityApp.cs", "Apps/UtilityAppAdapters.cs",
    "Views/SiphogChart.xaml", "Views/SiphogPanel.xaml",
    # DUT Data page (UV + SiPhOG snapshots)
    "Views/DutDataView.xaml", "Views/DutTrendChart.xaml", "Views/SiphogSnapshotsView.xaml",
    # the head itself
    "FaamHead.cs", "FaamActivity.cs", "ViewModels/FaamOps.cs", "ViewModels/FaamOpHelp.cs",
    "ViewModels/Sections/ScanPanelSettingsViewModel.cs", "ViewModels/Sections/StartCorrectionSettingsViewModel.cs",
    "Views/Sections/ScanPanelSettingsView.xaml", "Views/Sections/StartCorrectionSettingsView.xaml",
    "App.xaml", "Program.cs", "Views/TestingGroundView.xaml",
}

files = []
for d, dirs, fs in os.walk(root):
    dirs[:] = [x for x in dirs if x not in ("obj", "bin")]
    for f in fs:
        if f.endswith(".cs") or f.endswith(".xaml"):
            files.append(os.path.relpath(os.path.join(d, f), root).replace(os.sep, "/"))


def unit_of(f):
    return f[:-3] if f.endswith(".xaml.cs") else f


text = {f: open(os.path.join(root, f), encoding="utf-8", errors="ignore").read() for f in files}
# Strip comments so a type named in a doc comment is not a dependency.
strip = re.compile(r"//[^\n]*|/\*.*?\*/|<!--.*?-->", re.S)
code = {f: strip.sub("", t) for f, t in text.items()}
units = collections.defaultdict(list)
for f in files:
    units[unit_of(f)].append(f)
decl = re.compile(r"\b(?:class|record|struct|enum|interface)\s+([A-Z]\w*)")
owner = {}
for f in files:
    if f.endswith(".cs"):
        for m in decl.finditer(code[f]):
            owner.setdefault(m.group(1), unit_of(f))

missing = sorted(u for u in HEAD_ONLY if u not in units)
if missing:
    print("NOTE: listed head-only units not found:", missing)
edges = collections.defaultdict(set)
for u, fs in units.items():
    if u in HEAD_ONLY:
        continue
    t = "".join(code[f] for f in fs)
    for name, o in owner.items():
        if o in HEAD_ONLY and o != u and re.search(r"\b" + re.escape(name) + r"\b", t):
            edges[u].add(f"{name} ({o})")
print(f"generic units: {sum(1 for u in units if u not in HEAD_ONLY)}, head-only: {len(HEAD_ONLY)}")
print(f"generic -> head-only back-edges: {len(edges)}")
for u in sorted(edges):
    print(f"  {u} -> {', '.join(sorted(edges[u]))}")
