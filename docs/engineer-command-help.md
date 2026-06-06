# Engineer command help (palette help panel)

Status: **Implemented for the App category; other categories pending.** The Engineer
timeline editor shows inline help for commands: **left-click a command in the
"Commands" palette** and the right-hand panel (normally "Select a block to edit")
shows that command's description, how-to-use, and per-parameter notes. When a placed
block is selected for editing, a one-line summary of the command appears at the top of
the parameter editor.

## Why

The command palette grew (Motion / IO / Pneumatic / Flow / Signal / Vision / Align /
**App** / DUT) and several ops have non-obvious semantics (e.g. `app.start` *activates*
an app but does not wait for completion, vs `hillclimb.start` which blocks). Operators
and authors need that explanation where they build, not only in docs.

## How it works (data flow)

```
OpCatalog.All  ──(op key + params + category)──┐
                                               ├─► OpHelpViewModel ─► right panel (palette click)
OpCatalogHelp.For(opKey) ──(summary/usage/notes)┘
                                               └─► ProcessBlockViewModel.HelpSummary ─► editor header (block selected)
```

- **`OpCatalogHelp`** (`src/FAAM.UI/ViewModels/OpCatalogHelp.cs`) — **the single,
  consolidated place to author help.** A `Dictionary<opKey, OpHelp>`; `OpHelp` carries
  `Summary` (one line), `Usage` (multi-line "how to use"), and `ParamNotes`
  (`(key, note)[]`, keyed by `ParamSpec.Key`). `For(opKey)` returns null when none is
  authored — **no help is not an error**, the op just shows its param labels.
- **`OpHelpViewModel`** (`src/FAAM.UI/ViewModels/OpHelpViewModel.cs`) — merges an
  `OpSpec` (the op's params) with its `OpHelp` into what the panel binds to: `Display`,
  `Category`, `Summary`, `Usage`/`HasUsage`, and `ParamLines` (`ParamHelpLine{Label,
  Note,HasNote}`)/`HasParams`. Built fresh per selection.
- **`TimelineEditorViewModel`** — `ShowHelp(OpSpec)` sets `SelectedHelp` and clears the
  block selection. Three right-panel states (mutually exclusive):
  `ShowEmptyHint` (nothing selected) · `ShowPaletteHelp` (palette command clicked) ·
  `HasSelectedBlock` (placed block → parameter editor). Selecting a block clears the
  help; clicking a palette command clears the block.
- **`TimelineEditorView`** — the palette item's single left-click handler
  (`OnPaletteMouseDown`) calls `_vm.ShowHelp(item.Spec)`. Double-click still adds the
  block; drag still works. The right panel's three states are
  `Visibility`-bound to the flags above. `ProcessBlockViewModel.HelpSummary`/`HasHelp`
  drive the one-line summary atop the editor.

## Authoring help for a command

Add (or extend) one entry in `OpCatalogHelp.ByKey`, keyed by the op key from
`OpCatalog`. Keep `Summary` to a single sentence; put detail and caveats in `Usage`
(use `\n\n` for paragraph breaks). Add a `ParamNotes` entry per parameter whose meaning
isn't obvious from its label — the `Key` must match the op's `ParamSpec.Key`.

```csharp
["motion.moveToPosition"] = new()
{
    Summary = "Move a motion device to a named taught position.",
    Usage   = "The position's stored velocity/camera/exposure are used unless you set "
            + "an override below (0 / blank = use the position's value).",
    ParamNotes = new[]
    {
        ("position", "The taught position name on the selected device."),
        ("velocity", "Per-move speed override in mm/s; 0 = use the position's stored velocity."),
    },
};
```

That's all — it shows up automatically in the palette help panel and as the editor
header summary. No XAML or view-model changes are needed to add help for a new op.

## Coverage

- **App** — `app.open`, `app.home`, `app.start`, `app.stop`. Done.
- **Everything else** — TODO. Add entries to `OpCatalogHelp` in the same shape; the
  `// TODO: other categories` marker in that file lists them.

## Conventions

- One source of truth: write help **only** in `OpCatalogHelp`. This doc explains the
  *mechanism*; it deliberately does not duplicate the help strings.
- Match the theme: the panel uses the shared `SystemColors` brushes (`Accent`/`Text`/
  `Subtle`/`Edge`/`Bg`), no hardcoded colors.
- The App help mirrors the semantics in `docs/utility-app-launcher-design.md`
  (activate-vs-await, the `app.start` ↔ `hillclimb.start` distinction). Keep the two in
  sync if those semantics change.
