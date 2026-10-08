# Work status

Last updated: October 8, 2026.

This file records the project's operational status. The roadmap defines the
milestones; active implementation work is tracked here.

## Current status

- Active milestone: **M4 — Application compatibility**.
- Current phase: **M4.3 LOB locator streaming, implemented and tested locally;
  real IBM i LOB and M4.2 batching validation pending**.
- Most recently completed milestone: **M3 — DML and transactions**,
  version `0.4.0-alpha.1`.
- Prepared package version: **`0.5.0-alpha.4`**; publication will be triggered
  by a GitHub Release after the NuGet Trusted Publishing policy is configured.
- Current verification: 226 Release tests passed and .NET 8/.NET 10 builds
  succeeded without warnings or errors. This includes 210 local tests and 16 opt-in
  integration cases that performed no external connections because the IBM i
  environment variables were not configured. Real IBM i batching and LOB
  validation remain pending.
- Subsequent integration run loading `tests/Db2i.Tests/.env.local`: 16 cases,
  11 reported passed and 5 failed. Eight TCP cases actually exercised the host,
  including read-only batching; two TLS cases and the LOB case performed no
  connection because their settings were absent. Five table-dependent cases
  failed before writing because the configured DML table was not found.
  Transactional batch DML and LOB verification remain pending.
- The user subsequently reported 16/16 integration cases passing after
  recreating the DML table. Journaling and actual execution of every case were
  not independently confirmed. LOB/TLS configuration and setup instructions
  have since been added locally; their real verification remains pending.
- Trusted Publishing implementation: release-triggered build/publish jobs,
  tag/version/main-ancestry gates, package and Source Link checks, and separate
  retryable package/symbol pushes. Seven release-gate tests and actionlint
  validation pass. Both packages contain the .NET 8/.NET 10 targets and Source
  Link metadata; explicit Git/common build references restore Source Link
  generation with version 10.0.303. OIDC verification awaits the first release.
  See `docs/nuget-release.md` for setup.
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

## M4.3 — LOB locator streaming

- [x] Negotiate locators and keep cursors open until their locators are released.
- [x] Support inline types 404/408/412 and locator types 960/964/968, with
  distinct row footprints, declared maxima, and input handles.
- [x] Preserve server parameter descriptors separately from client descriptors.
- [x] Implement read, write, and release requests with JTOpen-based binary
  vectors, length/offset validation, and truncated-response checks.
- [x] Add non-seekable `GetStream` and `GetTextReader`, partial getters,
  synchronous/asynchronous reads, and LOB metadata.
- [x] Transfer in 64 KiB blocks and limit byte-array/string materialization
  to 64 MiB of managed content.
- [x] Preserve multibyte text, split surrogate pairs, and trailing spaces with
  incremental codecs; map UTF-8 CCSID 1208.
- [x] Accept caller-owned `Stream`/`TextReader` inputs from their current
  position, leave them open, and keep `Prepare` free of input consumption.
- [x] Enforce positive parameter `Size` without silent truncation and replace
  prior locator contents completely, including shorter and empty inputs.
- [x] Invalidate LOB preparation after incomplete uploads or transaction changes.
- [x] Integrate command/batch timeout, cancellation, session identity, and error
  attribution with getters and returned streams.
- [x] Release unread block locators and discard sessions whose cleanup or
  malformed locator replies make reuse unverifiable.
- [x] Cover local uploads/downloads larger than one packet, input reuse, NULL,
  empty values, multiple LOBs, rollback, connection replacement, and pooling.
- [x] Add an opt-in real LOB test using a dedicated journaled table, unique
  identifiers, structure validation, rollback, and cleanup without table creation.
- [x] Restore compilation after the interrupted reader implementation, run
  the full Release suite, and compile both target frameworks.
- [x] Update README, roadmap, contribution guide, and the release changelog.
- [ ] Run real IBM i LOB validation with `DB2I_TEST_LOB_ENABLED=true` and
  `DB2I_TEST_LOB_TABLE` configured alongside the existing TCP connection setting.
- [ ] Publish `0.5.0-alpha.4` through Trusted Publishing with pending real-host
  verification explicitly stated; M4.2 transaction verification remains separate.

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
- [ ] Publish the batching implementation in `0.5.0-alpha.4` with validation limits documented.

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
