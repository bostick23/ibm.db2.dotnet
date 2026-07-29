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
- `DB2I_TEST_DML_DECIMAL_COLUMN`.

Unset variables do not cause external connections, and connection strings are
never written to test output.

DML tests remain disabled unless explicitly enabled with
`DB2I_TEST_DML_ENABLED=true`. They accept only simple SQL identifiers, validate
the configured table structure before writing, and remove every row they
create. Real transaction tests also require the table to be journaled.

Run this command before opening a pull request:

```powershell
dotnet test Db2i.sln --configuration Release
```
