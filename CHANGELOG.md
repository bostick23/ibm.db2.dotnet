# Changelog

All notable changes to this project are documented in this file.

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

[0.5.0-alpha.2]: https://github.com/bostick23/ibm.db2.dotnet/releases/tag/v0.5.0-alpha.2
[0.5.0-alpha.1]: https://github.com/bostick23/ibm.db2.dotnet/releases/tag/v0.5.0-alpha.1
