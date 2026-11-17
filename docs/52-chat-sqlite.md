# Chat SQLite adapter

Chat can use a local SQLite file for development and a single-process deployment without running MySQL. MySQL remains the default. It supports the current Chat goal inbox, versioned scheduler and notification outbox along with the rest of Chat persistence. Core memory and the legacy Workbench import have separate storage.

## Configuration

```sh
export ChatDatabase__Provider=Sqlite
export ConnectionStrings__Chat='Data Source=/absolute/private/path/chat.db'
dotnet run --project src/LittleLoop.Chat
```

Create the parent directory first and restrict its access to the service account. The file contains accounts, password hashes, messages, attachments and device credentials. Do not place it in a public/static directory, source control, or network filesystem. Existing authentication and invite settings still apply. No MySQL credentials are needed in SQLite mode.

For existing deployments, keep `ChatDatabase__Provider=MySql` (or omit it) and retain the existing `ConnectionStrings__Chat`. An unknown provider fails startup rather than silently selecting a new database.

## Boundaries

- This is selection of a backing store, **not an automatic database migration**. Switching providers does not copy accounts/history. No production data conversion is performed. Keep the old database intact until any future explicit export/import has been separately tested.
- SQLite requires a private local on-disk database. In-memory mode and shared-cache mode are rejected. One Chat server process per database is supported.
- Connections enable foreign keys and have a 15-second busy timeout. Transactions use SQLite's immediate write reservation through the provider's default transaction implementation; SQL row locks are removed only on SQLite. Writes serialize, unlike MySQL's row-level locking.
- A held local file lease fences the Bot worker, released when its connection is disposed. The schema upgrade also has a local file lease and a concurrent startup fails clearly. Lock files can remain on disk after shutdown; existence alone does not mean a lease is held.
- SQL values are parameterized. The small dialect adapter translates the fixed SQL statements used by Chat, preserves secondary indexes, and maps existing upserts, timestamps and functions. It is not a general MySQL interpreter. New MySQL-specific statements require adapter coverage and tests.
- MySQL commands and connector settings remain unchanged, except the read-cursor update uses a portable correlated query. Legacy Workbench import still requires its existing MySQL source.
- The optional SQLite package is pinned to the .NET 10 servicing line. Review package audit output when upgrading.

## Verification

Run the deterministic store checks with generated temporary accounts and files:

```sh
dotnet run --project tests/LittleLoop.ChatChecks
```

They cover schema replay, accounts, direct rooms, message persistence/idempotency, unauthorized history access, room previews/read cursors, settings upserts, device expiry, password compare-and-swap, Bot lease exclusivity/release, reopening the database, concurrent message sequence allocation, push/FCM queue storage and JSON boolean notification preferences, foreign-key/integrity checks and retained indexes. These are not a substitute for a real MySQL integration run or every notification/cloud-provider integration.

### Pre-merge execution result

The generated SQLite store suite and all 8 Chat commitment host checks passed. ChatChecks build completed with zero warnings and errors in the implementation environment. Synthetic push/FCM data was tested locally without sending notifications. A live MySQL service was not available, so MySQL runtime compatibility remains a verification item; the default provider path and existing SQL are retained rather than replaced. No production database was read or migrated.

### Integration with the current task scheduler

Both providers use the current goal inbox, version guard, scheduler and notification outbox. SQLite allocates the outbox sequence using an integer primary key and compares the stored goal with a null-safe text equality check during a claim. Startup upgrades an older SQLite database by adding the nullable goal and the three goal tables. Existing task records with no goal remain readable as legacy tasks.

The merged Chat checks ran 89 goal checks per provider, plus the SQLite store suite and nine synthetic commitment host checks. The SQLite suite reopens a database with the previous task schema and verifies that an existing completed task survives the upgrade. A separate live SQLite run on an isolated RelaxKonOS copy exercised real Codex and DeepSeek, restarting while the second patch awaited approval. The old approval was rejected and the final task retained two approved patches, two test receipts and one completion notification (`data/continuous-http/2b5eef83/relaxkonos-sqlite-rework-result.json`). The application still defaults to MySQL. No production migration or deployment was performed.
