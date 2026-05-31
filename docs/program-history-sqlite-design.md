# Program history → SQLite (query store)

Status: **Phase A done** (machine-global `%APPDATA%\Horizon\history.db`, SQLite-backed
via `Microsoft.Data.Sqlite`; `ProgramHistoryDb` replaces the JSON store behind the
unchanged `IProcessService` API; one-time import of the legacy JSON history; degrades
to a no-op if the DB can't be opened). The normalized `block_revision` / `block_param`
tables are populated now. **Phase B** — query methods on `IProcessService` + a FAAM
history search panel (by UID / parameter / operator / device) — is next. Original plan
below.

Today program revision history was JSON-per-program
(`processes/history/{name}.json`, see `ProgramHistoryStore`). That's fine for "show
the history of *this* program," but it can't answer cross-cutting questions —
"every change to block UID X", "all programs that touched `gantry-main`", "what did
operator A change last week". This upgrades the **history/audit store** to SQLite so
it's queryable, while leaving everything else (programs, positions, config) as files.

**Scope:** the *audit/history* store only. Programs stay editable `.hwf` files (the
source of truth — portable, git-diffable). DUT records stay as-is for now (a later,
separate migration could give cross-serial traceability the same treatment).

## Why SQLite
- Single file, no server, no install — same "local station" footprint as the JSON.
- Real SQL queries + indexes over revisions / blocks / parameters.
- `Microsoft.Data.Sqlite` (lightweight ADO.NET provider, bundles the native engine);
  hand-written SQL, **no EF Core** (overkill here).

## Where it lives
**Machine-global**: `%APPDATA%\Horizon\history.db` (one queryable store across all
products). Rationale: audit is inherently cross-cutting and station-local — "who
changed what at this station" — and a single DB makes the queries the user wants
convenient. Trade-off vs the current per-product JSON: history no longer travels
when a product folder is copied to another station (each station keeps its own
audit). That matches how audit trails normally work; the program `.hwf` still travels.

## Schema

```sql
CREATE TABLE program_version (
    id           INTEGER PRIMARY KEY,
    product      TEXT NOT NULL,
    program      TEXT NOT NULL,
    version      INTEGER NOT NULL,
    saved_utc    TEXT NOT NULL,          -- ISO-8601
    operator_id  TEXT, operator_name TEXT, station_id TEXT,
    snapshot_json TEXT NOT NULL,          -- full WorkflowProgram, for exact restore / diff
    UNIQUE(product, program, version)
);

-- One row per leaf block in a revision (parallel branches flattened). Queryable.
CREATE TABLE block_revision (
    version_id  INTEGER NOT NULL REFERENCES program_version(id),
    block_uid   TEXT NOT NULL,
    op          TEXT, device TEXT, lane TEXT, name TEXT,
    enabled     INTEGER, stage_index INTEGER,
    PRIMARY KEY(version_id, block_uid)
);

-- One row per parameter of a block in a revision. The key to UID-parameter queries.
CREATE TABLE block_param (
    version_id  INTEGER NOT NULL REFERENCES program_version(id),
    block_uid   TEXT NOT NULL,
    key         TEXT NOT NULL,
    value       TEXT,
    PRIMARY KEY(version_id, block_uid, key)
);

CREATE INDEX ix_block_rev_uid    ON block_revision(block_uid);
CREATE INDEX ix_block_rev_device ON block_revision(device);
CREATE INDEX ix_block_param_uid  ON block_param(block_uid, key);
CREATE INDEX ix_version_program  ON program_version(product, program);
CREATE INDEX ix_version_operator ON program_version(operator_id);
```

Each `SaveProgram` writes one `program_version` row plus the flattened
`block_revision` / `block_param` rows (one transaction). The `snapshot_json` keeps a
full copy so the existing snapshot-diff (`ProgramDiff`) and exact restore still work;
the normalized tables exist purely for querying.

### Queries this unlocks
- **History of a block UID:** `SELECT v.program, v.version, v.saved_utc, v.operator_name, p.key, p.value FROM block_param p JOIN program_version v ON v.id=p.version_id WHERE p.block_uid=? ORDER BY v.saved_utc;`
- **Every change to one parameter of a block:** add `AND p.key='distance'`.
- **All programs touching a device:** `SELECT DISTINCT program FROM block_revision r JOIN program_version v ON v.id=r.version_id WHERE r.device=?;`
- **An operator's edits in a date range:** filter `program_version` on `operator_id` + `saved_utc`.

## Layer boundary — unchanged API
SQLite is an **implementation detail of Layer 2's history store**. The seam:
- New `ProgramHistoryDb` (in `Horizon.Workflow`) replaces the JSON `ProgramHistoryStore`
  behind the same `Append` / `Load(program)` shape, so `ProcessService.SaveProgram`
  and `GetProgramHistory` are unchanged.
- Add query methods to `IProcessService` for the new power, returning plain records:
  - `IReadOnlyList<BlockParamHistory> GetBlockHistory(string uid)`
  - `IReadOnlyList<ProgramVersionInfo> FindVersions(ProgramHistoryQuery query)` (by program / operator / device / date)
- Layer 3 (FAAM.UI) gains a small query panel in `ProgramHistoryWindow` (search by UID
  / parameter / operator / device); the existing revision+diff view stays.

## Migration
- On first run, import any existing `processes/history/*.json` across all products into
  `history.db` (idempotent — skip versions already present), then the DB is the store.
  Keep the JSON files as a one-time backup (or delete after a release).
- The DB is created + schema-migrated on open (a `schema_version` pragma/table guards
  future changes).

## Phasing
- **A** — add `Microsoft.Data.Sqlite`; `ProgramHistoryDb` (schema, Append, Load); swap
  `ProcessService`; one-time JSON import. (Behaviour identical to today, SQLite-backed.)
- **B** — query methods on `IProcessService` + the FAAM history query panel.
- **C** (later, separate) — move DUT records to SQLite for cross-serial traceability.

## Decisions to confirm
- DB location: **machine-global** (recommended) vs per-product.
- Provider: **Microsoft.Data.Sqlite** (recommended) vs EF Core.
- Replace JSON history vs keep both. (Recommended: import once, then SQLite is the store.)
