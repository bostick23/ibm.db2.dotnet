# Contributing

Every wire-protocol change must include:

1. a reference to the JTOpen class or method used for comparison;
2. at least one test with deterministic expected bytes;
3. explicit limits for lengths and allocations derived from network input;
4. verified cancellation and cleanup for I/O paths;
5. no passwords, seeds, or tokens in logs.

Never commit credentials for tests against a real IBM i system. Integration
tests read the following variables on an opt-in basis:

- `DB2I_TEST_TCP_CONNECTION_STRING`;
- `DB2I_TEST_TLS_CONNECTION_STRING`;
- `DB2I_TEST_DML_ENABLED`;
- `DB2I_TEST_DML_TABLE`;
- `DB2I_TEST_DML_ID_COLUMN`;
- `DB2I_TEST_DML_DESCRIPTION_COLUMN`;
- `DB2I_TEST_DML_DATE_COLUMN`;
- `DB2I_TEST_DML_DECIMAL_COLUMN`;
- `DB2I_TEST_LOB_ENABLED`;
- `DB2I_TEST_LOB_TABLE`.

Unset variables do not cause external connections, and connection strings are
never written to test output.

DML tests remain disabled unless explicitly enabled with
`DB2I_TEST_DML_ENABLED=true`. They accept only simple SQL identifiers, validate
the configured table structure before writing, and remove every row they
create. Real transaction tests also require the table to be journaled.

LOB tests require `DB2I_TEST_LOB_ENABLED=true` and a dedicated journaled table
with the signature documented in the README. They use the existing TCP
connection setting, verify structure and journaling, use unique identifiers,
and clean up their data. Tests never create the table.

To run against the server configured in the ignored
`tests/Db2i.Tests/.env.local`, use PowerShell 7:

```powershell
./scripts/Test-IbmI.ps1
./scripts/Test-IbmI.ps1 -Filter 'FullyQualifiedName~RealIbmILobIntegrationTests' -NoBuild
```

The runner loads only `DB2I_TEST_*` assignments, accepts optional `export`
and single or double quotes, and treats values literally without expanding
variables or evaluating expressions. Quoted values may contain spaces and
`#`, including quoted ADO.NET values inside a quoted connection string;
comments after values are supported. The previous process environment
is restored even when tests fail, and the script returns the test exit code.
It runs Release tests with normal console output and writes a uniquely named
TRX report under `TestResults/ibmi`. Use `-ResultsDirectory` or `-EnvFile` to
override these paths; relative paths are resolved from the repository root,
so the script can also be invoked by its absolute path from another directory.
`-NoBuild` requires an existing Release build. See the
[IBM i setup instructions](docs/ricreare-tabella-test-ibmi.md) for the required
tables, journaling and connection settings.

Run this command before opening a pull request:

```powershell
dotnet test Db2i.sln --configuration Release
```
