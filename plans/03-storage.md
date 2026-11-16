# 03 — Storage

Phase 3. Depends on: phase 2. Unblocks: scan engine, UI.

## Goal

A single SQLite database that holds the library index, baselines, findings, history, plans and the
operations journal; fast enough for a million files and safe against crashes mid-scan.

## Technology

- Packages: `Microsoft.Data.Sqlite.Core` 10.0.9 and `SQLitePCLRaw.bundle_e_sqlite3` 2.1.14. The bundle
  ships its own SQLite as `e_sqlite3.dll`, published beside `MediaVerifier.exe`; the app never uses
  the copy of SQLite inside Windows. Measured: the bundle reports SQLite **3.54.0**. (2.1.11, the version first planned, bundles a SQLite
  with CVE-2025-6965 and fails the NuGet audit; see the deviations log.)
- Call `SQLitePCL.Batteries_V2.Init()` once at startup before opening a connection.
- **SQL feature level: SQLite 3.54.** `RETURNING`, `STRICT` tables, upsert and window functions may be
  used. Do not use extensions that are not compiled into the bundle (test any you need, such as FTS5,
  before relying on it).
- At startup run `SELECT sqlite_version()`, log it, and refuse to open the database with a clear
  error if it is below 3.54 (that would mean the wrong `e_sqlite3.dll` was loaded).
- No ORM. Hand-written SQL in repository classes with cached prepared commands.

## Connections and concurrency

- One process writes at a time (the engine mutex from subplan 01 enforces this).
- Inside the process, **one writer connection** owned by a dedicated writer loop. All mutations are
  sent to it as messages over a bounded channel and applied in batched transactions (commit every
  250 ms or 500 operations, whichever comes first). This is the only code that executes `INSERT`,
  `UPDATE` or `DELETE`.
- A small pool of **read-only connections** (`Mode=ReadOnly`) serves the UI and the engine's lookups.
- Pragmas on open: `journal_mode=WAL`, `synchronous=NORMAL`, `foreign_keys=ON`, `busy_timeout=5000`,
  `temp_store=MEMORY`, `cache_size=-32768` (32 MB) on the writer and `-8192` on readers.
- `PRAGMA wal_checkpoint(TRUNCATE)` at the end of each run and on clean shutdown.

## Schema v1

Timestamps are UTC, stored as integer .NET ticks. Booleans are 0/1 integers. Paths are stored exactly
as the file system returned them, with default binary collation.

```sql
CREATE TABLE meta (
  key   TEXT PRIMARY KEY,
  value TEXT NOT NULL
) WITHOUT ROWID;                 -- 'schema_version' mirrors PRAGMA user_version; 'created_utc'; 'detector_set'

CREATE TABLE roots (
  id               INTEGER PRIMARY KEY,
  path             TEXT    NOT NULL UNIQUE,
  label            TEXT,
  role             INTEGER NOT NULL DEFAULT 0,   -- 0 Library, 1 Backup
  media_mask       INTEGER NOT NULL DEFAULT 7,   -- bit 0 photo, bit 1 video, bit 2 audio
  default_depth    INTEGER NOT NULL DEFAULT 2,
  include_patterns TEXT,                         -- JSON array of globs, NULL = all
  exclude_patterns TEXT,                         -- JSON array of globs
  volume_id        TEXT,                         -- volume serial or UNC share, for offline detection
  storage_class    INTEGER NOT NULL DEFAULT 0,   -- 0 unknown, 1 SSD, 2 HDD, 3 network, 4 removable
  enabled          INTEGER NOT NULL DEFAULT 1,
  added_utc        INTEGER NOT NULL,
  last_online_utc  INTEGER
);

CREATE TABLE dirs (
  id        INTEGER PRIMARY KEY,
  root_id   INTEGER NOT NULL REFERENCES roots(id) ON DELETE CASCADE,
  parent_id INTEGER REFERENCES dirs(id) ON DELETE CASCADE,
  rel_path  TEXT    NOT NULL,                    -- '' for the root directory itself
  name      TEXT    NOT NULL,
  UNIQUE (root_id, rel_path)
);
CREATE INDEX ix_dirs_parent ON dirs(parent_id);

CREATE TABLE files (
  id                INTEGER PRIMARY KEY,
  root_id           INTEGER NOT NULL REFERENCES roots(id) ON DELETE CASCADE,
  dir_id            INTEGER NOT NULL REFERENCES dirs(id) ON DELETE CASCADE,
  name              TEXT    NOT NULL,
  ext               TEXT    NOT NULL,            -- lower case, no dot
  media_kind        INTEGER NOT NULL,
  size              INTEGER NOT NULL,
  mtime_utc         INTEGER NOT NULL,
  ntfs_file_id      INTEGER,                     -- for rename detection; NULL when unavailable
  state             INTEGER NOT NULL DEFAULT 0,  -- 0 Pending, 1 Ok, 2 Issues, 3 Missing, 4 Skipped, 5 Error
  skip_reason       INTEGER NOT NULL DEFAULT 0,
  worst_severity    INTEGER NOT NULL DEFAULT 0,
  open_findings     INTEGER NOT NULL DEFAULT 0,
  scanned_depth     INTEGER NOT NULL DEFAULT 0,
  detector_set      INTEGER NOT NULL DEFAULT 0,  -- catalog version used for the last evaluation
  first_seen_utc    INTEGER NOT NULL,
  last_seen_run     INTEGER NOT NULL DEFAULT 0,
  last_scanned_utc  INTEGER,
  -- baseline
  hash              BLOB,                        -- XXH3-128, 16 bytes
  hash_size         INTEGER,
  hash_mtime_utc    INTEGER,
  hash_utc          INTEGER,
  stream_hash       BLOB,                        -- packet-level hash, 16 bytes (subplan 06)
  last_verified_utc INTEGER,
  verify_count      INTEGER NOT NULL DEFAULT 0,
  -- media summary, denormalised for list display and sorting
  container         TEXT,
  video_codec       TEXT,
  audio_codec       TEXT,
  width             INTEGER,
  height            INTEGER,
  duration_ms       INTEGER,
  bitrate           INTEGER,
  sample_rate       INTEGER,
  channels          INTEGER,
  bit_depth         INTEGER,
  fps_milli         INTEGER,
  UNIQUE (dir_id, name)
);
CREATE INDEX ix_files_root_state ON files(root_id, state, worst_severity);
CREATE INDEX ix_files_severity   ON files(worst_severity) WHERE worst_severity > 0;
CREATE INDEX ix_files_hash       ON files(hash) WHERE hash IS NOT NULL;
CREATE INDEX ix_files_verify     ON files(last_verified_utc) WHERE hash IS NOT NULL;
CREATE INDEX ix_files_run        ON files(root_id, last_seen_run);

CREATE TABLE findings (
  id            INTEGER PRIMARY KEY,
  file_id       INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
  rule_id       TEXT    NOT NULL,
  category      INTEGER NOT NULL,
  severity      INTEGER NOT NULL,
  confidence    REAL    NOT NULL,
  summary       TEXT    NOT NULL,
  evidence_json TEXT,
  status        INTEGER NOT NULL DEFAULT 0,     -- 0 Open, 1 Ignored, 2 Resolved
  first_run     INTEGER NOT NULL,
  last_run      INTEGER NOT NULL,
  first_utc     INTEGER NOT NULL,
  last_utc      INTEGER NOT NULL,
  resolved_utc  INTEGER,
  resolution    TEXT,
  UNIQUE (file_id, rule_id)
);
CREATE INDEX ix_findings_open ON findings(status, severity, rule_id);
CREATE INDEX ix_findings_run  ON findings(first_run);

CREATE TABLE artifacts (                         -- compact analysis summaries (subplan 05)
  file_id          INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
  kind             INTEGER NOT NULL,             -- 1 Structure, 2 Decode, 3 PcmStats, 4 FrameSamples, ...
  provider_version INTEGER NOT NULL,
  data             TEXT    NOT NULL,             -- JSON, numbers only, a few hundred bytes
  PRIMARY KEY (file_id, kind)
) WITHOUT ROWID;

CREATE TABLE file_events (                       -- per-file history timeline
  id          INTEGER PRIMARY KEY,
  file_id     INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
  utc         INTEGER NOT NULL,
  run_id      INTEGER,
  kind        INTEGER NOT NULL,                  -- 1 FirstSeen, 2 Baselined, 3 Verified, 4 Modified, 5 Rot,
                                                 -- 6 MetaDrift, 7 Moved, 8 Missing, 9 Returned, 10 Repaired, 11 Restored
  detail_json TEXT
);
CREATE INDEX ix_events_file ON file_events(file_id, utc);

CREATE TABLE runs (
  id                INTEGER PRIMARY KEY,
  plan_id           INTEGER,
  trigger           INTEGER NOT NULL,            -- 0 Manual, 1 Scheduled, 2 Resume
  depth             INTEGER NOT NULL,
  scope_json        TEXT    NOT NULL,
  status            INTEGER NOT NULL,            -- 0 Running, 1 Completed, 2 Cancelled, 3 Failed, 4 Interrupted, 5 BudgetReached
  started_utc       INTEGER NOT NULL,
  ended_utc         INTEGER,
  files_seen        INTEGER NOT NULL DEFAULT 0,
  files_analysed    INTEGER NOT NULL DEFAULT 0,
  files_verified    INTEGER NOT NULL DEFAULT 0,
  bytes_read        INTEGER NOT NULL DEFAULT 0,
  new_findings      INTEGER NOT NULL DEFAULT 0,
  resolved_findings INTEGER NOT NULL DEFAULT 0,
  errors            INTEGER NOT NULL DEFAULT 0,
  alerts_json       TEXT,
  summary_json      TEXT
);

CREATE TABLE plans (
  id              INTEGER PRIMARY KEY,
  name            TEXT    NOT NULL,
  enabled         INTEGER NOT NULL DEFAULT 1,
  definition_json TEXT    NOT NULL,              -- roots, depth, trigger, conditions, budget, notify (subplan 13)
  task_name       TEXT,
  created_utc     INTEGER NOT NULL,
  updated_utc     INTEGER NOT NULL,
  last_run_id     INTEGER
);

CREATE TABLE rule_settings (
  rule_id      TEXT PRIMARY KEY,
  enabled      INTEGER,                          -- NULL = default
  severity_cap INTEGER,                          -- NULL = default
  params_json  TEXT
) WITHOUT ROWID;

CREATE TABLE ignores (
  id          INTEGER PRIMARY KEY,
  root_id     INTEGER NOT NULL REFERENCES roots(id) ON DELETE CASCADE,
  dir_id      INTEGER REFERENCES dirs(id) ON DELETE CASCADE,   -- NULL = whole root
  rule_id     TEXT,                                            -- NULL = all rules
  reason      TEXT,
  created_utc INTEGER NOT NULL
);

CREATE TABLE operations (                        -- remediation journal (subplan 12)
  id           INTEGER PRIMARY KEY,
  batch_id     INTEGER NOT NULL,
  utc          INTEGER NOT NULL,
  action_id    TEXT    NOT NULL,
  file_id      INTEGER,
  status       INTEGER NOT NULL,                 -- 0 Planned, 1 Running, 2 Done, 3 Failed, 4 Undone, 5 Cancelled
  source_path  TEXT    NOT NULL,
  result_path  TEXT,
  backup_path  TEXT,                             -- where the original went
  undo_json    TEXT,
  detail_json  TEXT
);
CREATE INDEX ix_operations_batch ON operations(batch_id);

CREATE TABLE notifications (
  id         INTEGER PRIMARY KEY,
  utc        INTEGER NOT NULL,
  level      INTEGER NOT NULL,
  title      TEXT    NOT NULL,
  body       TEXT,
  action_uri TEXT,
  is_read    INTEGER NOT NULL DEFAULT 0
);
```

Notes:

- `files.worst_severity` and `open_findings` are maintained by the writer whenever findings for the
  file change, inside the same transaction.
- Probe JSON is not stored (a few KB per file would dominate the database). The detail view re-probes
  on demand.
- `artifacts` rows let rules be re-evaluated without touching the media again. Keep them small:
  numeric summaries only, no raw logs beyond the classified counts and a handful of sample lines.
- Expected size: about 350 bytes per file for the index plus 300–600 bytes of artifacts for analysed
  files, so roughly 1 GB at a million fully analysed files.

## Migrations

- `PRAGMA user_version` holds the schema version. Migrations are numbered, forward-only C# classes
  that each run in one transaction.
- Before applying any migration to an existing database, copy `library.db` to
  `library.pre-v<N>.db` (keep the two most recent).
- A database newer than the app refuses to open with a message that names both versions.

## Startup integrity

On open run `PRAGMA quick_check`. If it fails: close, move the files to
`library.damaged-<timestamp>.*`, create a fresh database, and add a notification explaining what
happened and where the old file is. The app must still start.

## Repository surface

Sketch of the interfaces the rest of the app uses (names indicative):

```csharp
interface IRootStore      { /* add, remove, update, list, mark online */ }
interface IFileIndex      { /* lookup by (dir, name); bulk upsert; mark seen; mark missing; find by hash */ }
interface IFindingStore   { /* replace findings for a file at a depth; set status; counts */ }
interface IArtifactStore  { /* get and put compact artifact summaries per (file, kind) */ }
interface IRunStore       { /* begin, progress checkpoint, end; list; mark interrupted */ }
interface IPlanStore      { /* CRUD */ }
interface IJournal        { /* operations */ }
interface ILibraryQueries { /* read-side queries for the UI (below) */ }
```

`IFindingStore.ReplaceForFile(fileId, depth, runId, findings)` implements the lifecycle from subplan 02
in one transaction: upsert raised findings, resolve open findings whose rule `MinDepth` is within the
scanned depth and that were not raised, keep ignored status, then recompute `files.worst_severity`
and `open_findings`.

## UI query patterns

Lists can hold a million rows; the UI never loads full rows for all of them.

1. **ID snapshot.** A filtered, sorted query returns only `id` values into an `int[]` or `long[]`
   (8 MB per million). The virtual list's row count is the array length.
2. **Row cache.** Visible rows are fetched by ID in chunks of about 200 (`WHERE id IN (...)`) on a
   background thread and kept in an LRU cache of a few thousand rows. Rows not yet loaded draw a
   skeleton placeholder.
3. **Counts.** Severity and kind counters come from grouped queries over indexed columns and are
   cached until the writer signals a change.
4. **Change signal.** The writer raises a coarse, debounced `LibraryChanged` event (at most twice a
   second) with flags for what changed. Pages decide whether to refresh.
5. **Folder rollup.** Aggregate `GROUP BY dir_id` once per refresh, then sum up the `dirs` tree in
   memory.

Text search over names uses `LIKE '%term%'` on `files.name` plus the directory path joined in; this is
acceptable up to a million rows on a background thread (measure; budget 300 ms).

## Maintenance

Tools page actions (subplan 09): back up the database (online backup API or checkpoint then copy),
`VACUUM`, run `integrity_check`, reset the library (confirmation required), change location.

## Tasks

| ID | Task | Acceptance |
|----|------|------------|
| S-01 | Add packages; `Database` class that initialises the bundle, opens, applies pragmas, checks SQLite version | Test logs the version (3.54.0 with the pinned packages); a version below 3.54 produces the specific error |
| S-02 | Schema v1 migration and migration runner with pre-migration backup | Tests: fresh create; idempotent re-open; simulated v1→v2 keeps data and writes a backup; newer-than-app refuses |
| S-03 | Writer loop with batching and back-pressure | Test: 200,000 file upserts complete at 5,000 rows/s or more; a crash (process kill) during a batch leaves a consistent database |
| S-04 | `IRootStore`, `IFileIndex` including directory interning and bulk upsert | Tests cover insert, update, move between directories, cascade delete of a root |
| S-05 | `IFindingStore` with lifecycle semantics | Table-driven tests for every lifecycle rule in subplan 02 |
| S-06 | `IRunStore`, `IPlanStore`, `IJournal`, `IArtifactStore`, notifications, rule settings, ignores | CRUD tests; runs left `Running` are marked `Interrupted` at startup; artifacts are deleted with their file |
| S-07 | `ILibraryQueries`: ID snapshots with filters (severity, kind, category, rule, root, folder subtree, status, text) and sorts; row fetch by IDs; counts; folder rollup | With a generated 1,000,000-file database: snapshot under 300 ms, 200-row fetch under 20 ms, counts under 100 ms |
| S-08 | Startup `quick_check` and damaged-database recovery | Test with a deliberately corrupted file: app-level open succeeds with a fresh database and a notification row |
| S-09 | Maintenance operations | Backup produces a database that passes `integrity_check` |
| S-10 | Synthetic library generator in the test project (N roots, N files, findings by distribution) for performance tests and UI demo data | Used by S-07 and by `--render-screens` |

## Exit criteria

- All acceptance tests pass, including the performance numbers on the build machine (record them in
  `docs/perf.md`).
- No code outside `Storage/` contains SQL.

## Pitfalls

- `WITHOUT ROWID` tables cannot be the target of `INTEGER PRIMARY KEY` aliasing; only use it for the
  small key/value tables above.
- Partial indexes require the query's `WHERE` clause to imply the index predicate; write queries
  accordingly and check plans with `EXPLAIN QUERY PLAN` in tests for the hot queries.
- Chunk `IN (...)` lists at 500 values so prepared statements can be cached and reused (SQLite 3.54
  allows far more parameters, but a statement per list length defeats the cache).
- Never hold a read transaction open across a user interaction; it blocks WAL checkpointing.
- Directory interning must be case-exact; do not use `COLLATE NOCASE` (it is ASCII-only and wrong for
  non-English names).
