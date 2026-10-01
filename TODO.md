# Work status

Last updated: October 1, 2026.

This file records the project's operational status. The roadmap defines the
milestones; active implementation work is tracked here.

## Current status

- Active milestone: **M4 — Application compatibility**.
- Current phase: **M4.2 sequential ADO.NET batching, implemented and tested
  locally; real IBM i validation pending**.
- Most recently completed milestone: **M3 — DML and transactions**,
  version `0.4.0-alpha.1`.
- Current package version: **`0.5.0-alpha.3`**; batching changes are unreleased.
- Current verification: 148 Release tests passed and .NET 8/.NET 10 builds
  succeeded. This includes 133 local tests (41 for batching) and 15 opt-in
  integration cases that performed no external connections because the IBM i
  environment variables were not configured. Real IBM i batching validation
  remains pending.
- Previous M4.1 verification: 104 Release tests passed; .NET 8 and .NET 10 builds;
  Integration category 12/12, including 11 real TCP cases covering queries,
  DML, transactions, job reuse, and rollback on pool return. The TLS case
  performs no connection because its environment variable is not configured.

## Milestones

- [x] M0 — Provider foundations.
- [x] M1 — Authenticated SQL connection.
- [x] M2 — First query path.
- [x] M3 — DML and transactions.
- [ ] M4 — Application compatibility.

TLS against a real IBM i system still needs separate verification; this did not
block M3.

## M4.2 — Sequential ADO.NET batching

- [x] Add `Db2iBatch`, `Db2iBatchCommand`, and the batch-command collection.
- [x] Expose batches through `Db2iConnection`, `Db2iProviderFactory`, and
  connectionless `Db2iDataSource` execution.
- [x] Support per-command positional parameters, reusable preparation on an
  explicit connection, and synchronous/asynchronous execution.
- [x] Sum DML row counts, return the first query scalar, and stream multiple
  query results through `NextResult`.
- [x] Execute remaining commands on reader close/disposal, except after
  failure or cancellation; keep `SchemaOnly` free of DML execution.
- [x] Stop on the first error, identify `Db2iException.BatchCommand`, and
  support explicit transactions, batch timeout, and cancellation.
- [x] Cover batching behavior and lifecycle with local simulated-server tests.
- [x] Add opt-in real batch query and transactional DML rollback tests.
- [x] Update Source Link to `10.0.303` to address `GHSA-23fw-v26w-5fgq`.
- [ ] Verify batching against a real IBM i system.
- [ ] Release the batching implementation after validation.

Batches execute sequentially without array binding or reduced network round
trips. They do not start an implicit transaction: callers own explicit commit
and rollback, and autocommit retains earlier successful commands on failure.
Connectionless data-source batches do not support preparation or explicit
transactions.

## M4.1 — Pooling and DbDataSource

### 1. Pooling

- [x] Add `Pooling` and `Max Pool Size` to the connection string.
- [x] Implement global pools for equivalent effective settings.
- [x] Apply the limit to waiters and include pool-slot waits in the connection
  timeout.
- [x] Handle cancellation, generation-based clearing, and disposal of unhealthy
  sessions.
- [x] Roll back, restore autocommit, and clean up statements before reuse.

### 2. DbDataSource

- [x] Implement `Db2iDataSource` with a dedicated pool and immutable connection
  string.
- [x] Connect `Db2iProviderFactory.CreateDataSource`.
- [x] Support connectionless commands supplied by `DbDataSource`.
- [x] Close idle sessions deterministically on disposal.

### 3. Verification and release

- [x] Cover reuse, capacity, timeout, cancellation, reset, clear, and disposal
  with the simulated server.
- [x] Add opt-in tests for IBM i job reuse and transaction rollback on pool
  return.
- [x] Run 104 Release tests and build for .NET 8 and .NET 10.
- [x] Run both pooling tests against a real IBM i system.
- [x] Generate and verify the `0.5.0-alpha.1` NuGet package.
- [x] Publish English project documentation and an English NuGet README in
  `0.5.0-alpha.2`.
- [x] Rename the NuGet package to `Db2i.AdoNet` and foreground its open-source,
  fully managed, client-free IBM i connectivity in `0.5.0-alpha.3`.

On July 29, 2026, the pool reused the same IBM i job and rolled back an
abandoned transaction on the authorized journaled table before handing the
session to the next connection.

## M3 — DML and transactions

### 1. Protocol analysis

- [x] Identify the JTOpen requests and replies for immediate/prepared execution,
  commit, rollback, and cancellation.
- [x] Define how the affected-row count is read from SQLCA.
- [x] Define the mapping between ADO.NET `IsolationLevel` and IBM i commitment
  control.
- [x] Define valid connection, command, transaction, and reader states.

### 2. ExecuteNonQuery

- [x] Implement `ExecuteNonQuery` and `ExecuteNonQueryAsync`.
- [x] Support `?` markers and the same input parameters as M2.
- [x] Return the correct affected-row count.
- [x] Handle warnings, SQL errors, timeouts, and cancellation.
- [x] Add simulated-server tests.

### 3. Transactions

- [x] Implement `BeginTransaction` and the asynchronous ADO.NET path.
- [x] Implement autocommit.
- [x] Implement `Commit`/`CommitAsync`.
- [x] Implement `Rollback`/`RollbackAsync`.
- [x] Apply and validate supported isolation levels.
- [x] Prevent concurrent transactions on the same connection.
- [x] Add tests for commit, rollback, disposal, and interrupted connections.

### 4. Cancellation and lifecycle

- [x] Implement the host-server `CANCEL` command.
- [x] Keep the connection usable after successful cancellation.
- [x] Close statements and cursors consistently during rollback/disposal.
- [x] Preserve safety closure when the protocol cannot be resynchronized.

### 5. Verification and release

- [x] Run the full Release suite on .NET 8 and also build for .NET 10.
- [x] Add and verify the real TCP autocommit DML test against the authorized
  table.
- [x] Verify real commit/rollback after journaling was enabled.
- [x] Update the README and roadmap.
- [x] Generate the M3 NuGet package `0.4.0-alpha.1`.

## Real M3 test prerequisites

The authorized table and its four columns are configured only through local
environment variables. Before every write, the test validates its signature
and journaling, uses a unique ID and marker, and cleans up in `finally`. On
July 24, 2026, autocommit DML, rollback with no persisted row, and
`Serializable` commit with a persisted row were verified.

IBM i may return code `-601` when changing locator persistence after a statement
has executed. The provider mirrors JTOpen's fallback: it disables that session
option and retries the transaction-mode change without the corresponding code
point. No credentials are stored in project files.

## M3 completion criterion

Complete: `ExecuteNonQuery`, autocommit, transactions, and `CANCEL` are exposed
through `System.Data.Common`, covered by the simulated server, and verified
against a real IBM i system in the authorized data area.
