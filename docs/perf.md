# Performance measurements

Budgets are in the [master plan](../plans/00-master-plan.md#8-budgets). Each section records the
command, the machine and the numbers; re-run after changing the code it covers.

Machine: AMD Ryzen 9 5950X 16-core, 64 GB RAM, NVMe SSD (Sabrent Rocket 4.0), Windows 11 Pro 26200.

## Storage (subplan 03, task S-07)

`dotnet test -c Release --filter Category=Perf` (`QueryPerformanceTests`), 2026-11-16. A synthetic
library of 1,000,000 files in 4 roots (100 files per folder, 2 % with an open finding), SQLite 3.54.0.

| Measurement | Result | Budget |
|-------------|--------|--------|
| Generate 1,000,000 files with findings (writer, 20,000 rows per operation) | 17.8 s | under 60 s (T-10) |
| Writer upserts, `UpsertAsync` in 1,000-file operations (unit test, 200,000 files) | over 100,000 rows/s | 5,000 rows/s |
| Issues snapshot, all open findings by severity (20,110 ids) | 105 ms | under 300 ms |
| Library folder snapshot, a top-level folder's subtree by name (27,775 ids) | 172 ms | under 300 ms |
| Search snapshot, text over names and folders of all 1,000,000 files (1,300 ids) | 155 ms | under 300 ms |
| Fetch 200 rows by id | 6 ms | under 20 ms |
| Open findings per tier | 2 ms | under 100 ms |
| Whole library, 1,000,000 ids sorted by size (no view does this; recorded for reference) | 809 ms | — |

Finding: reading an `INSERT … RETURNING` row inside the writer's per-operation savepoint was about
20 times slower than `ExecuteNonQuery` plus `last_insert_rowid()`, and got slower as the batch grew
(160,000 rows: 27.4 s against 1.2 s). The writer never uses `RETURNING`.
