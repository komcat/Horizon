# Version repository (program / deck commit + checkout)

Status: **v1 done** — explicit **commit** (with note) + **checkout**, surfaced as a
dedicated **Repository** page in FAAM.UI. Builds directly on the existing SQLite
history store (`docs/program-history-sqlite-design.md`); no file-format change.

## Idea — Save vs Commit (git-style)

The store can record full, operator-stamped, monotonically-versioned snapshots into
the machine-global `%APPDATA%\Horizon\history.db` (`ProgramHistoryDb`). We split the
two operations so the version list isn't flooded with a new version on every keystroke-save:

- **Save** (`SaveProgram` / `SaveDeck`) writes the working `.hwf` / `.hwd` file only —
  the working copy. It does **not** create a version. (Save As / Import are also
  working-copy writes — a brand-new artifact is untracked until its first commit.)
- **Commit** (`CommitProgram` / `CommitDeck`) saves the working file *and* records a
  new numbered version with an optional note. This is the only thing that bumps `vN`.

```
edit → Save                  → writes working file       (no version)
edit → Commit "msg"          → writes working file + vN  (explicit, numbered, noted)
Repository page → pick vN → Checkout → working file = vN  (no new version)
```

**Checkout** writes a stored snapshot back as the current working file (decks also
re-activate). It does **not** create a version — the operator commits afterwards if
they want to keep it (git "checkout updates the working tree" semantics). History is
append-only and never rewritten.

## Layer 2 (`Horizon.Workflow`)

- **DB** (`ProgramHistoryDb`): `program_version` and `deck_version` gain a nullable
  `note` column (idempotent `EnsureColumn` migration via `PRAGMA table_info`, since
  SQLite has no `ADD COLUMN IF NOT EXISTS`). `Append` / `AppendDeck` take an optional
  `note`; `ProgramVersion` / `DeckVersion` carry `Note`. New single-version reads
  `LoadVersion(product, program, version)` and `LoadDeckVersion(product, deckUid, version)`.
- **`IProcessService` / `ProcessService`**:
  - `SaveProgram` / `SaveDeck` write the working file only (no history append).
  - `CommitProgram(program, note)` / `CommitDeck(deck, note)` save **and** append a
    numbered version (the only path that bumps `vN`).
  - `RestoreProgramVersion(program, version)` / `RestoreDeckVersion(deckUid, version)` —
    load the stored snapshot and write it back as the working file (no new version;
    decks also re-activate).
  - `GetDeckUid(name)` — reads a deck's UID **without** activating it (unlike
    `LoadDeck`), so browsing the repository doesn't disturb the active deck.

## Layer 3 (`FAAM.UI`)

- **Repository page** (`Views/RepositoryView`, reached from a Home tile): a
  Programs/Decks toggle, an item list, a newest-first **version timeline** (each row
  `v{n} · time · operator · "note"`), the per-block / per-entry **diff vs previous**
  (reusing `ProgramDiff` / `DeckDiff`), and a **Checkout** button. Browse-only
  otherwise; no background work (`Activate` just refreshes, `Deactivate` is a no-op).
  Wired into `ShellWindow` exactly like the Calibration page.
- **Commit button + note box** beside Save in the Timeline editor and the Deck editor
  (`CommitNote` + a `Commit*Command` on each VM). Save persists the working file;
  Commit persists + versions with the note, then clears the note. Blank note → an
  unlabelled commit.

## Edge cases
- **Save As / Import / new artifact**: working-copy writes only — untracked (empty
  timeline) until the first Commit, exactly like a new file in git.
- **Open editor**: Checkout writes the file on disk; an editor already showing that
  item won't auto-refresh — the confirm dialog says to re-open it. (Live reload = v2.)
- **Legacy UID-less decks**: no history (`SaveDeck` skips `AppendDeck` without a UID).
  The page shows an empty timeline + a "save once to start versioning" hint; Checkout
  is disabled. No backfill.
- **DB unavailable**: the store already degrades to a no-op — empty timelines,
  Checkout disabled, saves still write files. No crash.
- The existing read-only `ProgramHistoryWindow` / `DeckHistoryWindow` (with their
  cross-program/deck **Query** tab) are kept as-is; the Repository page adds
  checkout + notes, it doesn't replace the audit-query feature.

## Deferred (v2)
- Named tags / golden baselines (`release`, `qualified`, …).
- Compare any two arbitrary versions (today's diff is vs the immediate predecessor).
- Program-rename lineage (programs key history by **name**; would need a UID on
  `WorkflowProgram`, which has none today). Decks already key by UID.
- Live editor reload-on-checkout.
