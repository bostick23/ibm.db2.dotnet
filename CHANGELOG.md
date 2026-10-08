# Changelog

All notable changes to this project are documented in this file.

## [Unreleased]

## [0.5.0-alpha.4] - 2026-10-08

### Added

- sequential ADO.NET batching through `Db2iBatch`, `Db2iBatchCommand`, the
  batch-command collection, connections, the provider factory, and
  `Db2iDataSource`;
- per-command positional parameters, reusable preparation on explicit
  connections, synchronous/asynchronous DML and scalar execution, and
  streaming query results through `NextResult`;
- batch timeout and cancellation, explicit transaction association, and
  `Db2iException.BatchCommand` attribution for the first failing command;
- local simulated-server tests for batching behavior and lifecycle;
- BLOB, CLOB, and DBCLOB locator reads and writes through single commands,
  batches, transactions, and `Db2iDataSource`;
- non-seekable `GetStream`/`GetTextReader`, partial `GetBytes`/`GetChars`, LOB
  metadata, and asynchronous materialization capped at 64 MiB of managed content;
- `Stream`/`TextReader` parameters, 64 KiB transfers, incremental text
  conversion (including UTF-8 CCSID 1208), and strict parameter size limits;
- locator lifetime management, statement reuse with complete replacement of
  input contents, transaction invalidation, and cancellation-aware cleanup;
- deterministic JTOpen-based LOB wire vectors, simulated-host lifecycle tests,
  and opt-in journaled-table LOB integration coverage;
- a GitHub Release-triggered NuGet Trusted Publishing workflow using OIDC,
  release/package validation, separate symbol publication, and SHA-256 checksums;
- release-setup documentation and IBM i test-table/journaling instructions.

### Changed

- updated Source Link to `10.0.303` to address `GHSA-23fw-v26w-5fgq`.

### Batching limitations

- read-only TCP batching was verified; transactional batching verification
  remains pending;
- execution is sequential, with no array binding, reduced network round trips,
  or implicit transaction; callers manage explicit commit/rollback, and prior
  autocommit commands remain committed on failure;
- closing or disposing a reader executes remaining commands unless the batch
  has failed or been canceled; `SingleRow` and `SingleResult` limit returned
  results, while `SchemaOnly` executes no DML;
- connectionless data-source batches do not support preparation or explicit
  transactions; stored procedures and output parameters remain
  unsupported.

### LOB validation

- real IBM i LOB verification remains pending; local simulation does not replace
  host compatibility testing;
- real LOB tests require `DB2I_TEST_LOB_ENABLED=true`, the existing TCP
  connection setting, and a dedicated journaled `DB2I_TEST_LOB_TABLE`;
- streaming inputs are consumed from their current position and remain open;
  callers must reposition or replace them before another execution.

## [0.5.0-alpha.3] - 2026-07-29

Package-identity and positioning follow-up to `0.5.0-alpha.2`. Provider
behavior is unchanged.

### Changed

- renamed the NuGet package from `Db2i` to `Db2i.AdoNet` before its first
  publication to NuGet.org;
- clarified the project's primary value: open-source, fully managed, direct
  Db2 for IBM i connectivity on modern .NET without IBM client software, ODBC,
  native libraries, or a Db2 Connect license;
- expanded the package title, description, tags, and README for NuGet search
  discoverability.

The assembly name and root namespace remain `Db2i`; the public API and provider
behavior are unchanged.

## [0.5.0-alpha.2] - 2026-07-29

Documentation-only follow-up to `0.5.0-alpha.1`. Provider behavior is
unchanged.

### Changed

- translated the README, roadmap, ADR, contribution guide, and work-status
  documentation into English;
- updated the README embedded in the NuGet package to English;
- clarified English NuGet metadata and release notes.

## [0.5.0-alpha.1] - 2026-07-29

Experimental pre-release. Not recommended for production workloads.

### Added

- authenticated IBM i database host-server sessions over TCP and TLS;
- ADO.NET query, scalar, DML, transaction, cancellation, and reader support;
- positional parameters and the initial numeric, text, temporal, and binary
  type set;
- global connection pooling, pool reset/clear operations, and dedicated
  `Db2iDataSource` pools;
- deterministic simulated-host coverage and opt-in real IBM i integration
  tests;
- multi-target packages for .NET 8 and .NET 10 with symbols and Source Link.

### Known limitations

- TLS has not yet been verified against a real IBM i system;
- LOBs, stored procedures, output parameters, batch, schema metadata, DRDA,
  Kerberos, MFA, and system naming are not supported;
- the public API and wire behavior may change before a stable release.

[0.5.0-alpha.4]: https://github.com/bostick23/ibm.db2.dotnet/releases/tag/v0.5.0-alpha.4
[0.5.0-alpha.3]: https://github.com/bostick23/ibm.db2.dotnet/releases/tag/v0.5.0-alpha.3
[0.5.0-alpha.2]: https://github.com/bostick23/ibm.db2.dotnet/releases/tag/v0.5.0-alpha.2
[0.5.0-alpha.1]: https://github.com/bostick23/ibm.db2.dotnet/releases/tag/v0.5.0-alpha.1
