# Db2i

[![CI](https://github.com/bostick23/ibm.db2.dotnet/actions/workflows/ci.yml/badge.svg)](https://github.com/bostick23/ibm.db2.dotnet/actions/workflows/ci.yml)

An open-source ADO.NET provider for Db2 for IBM i (AS/400, iSeries), based on
the host-server protocol used by [JTOpen](https://github.com/IBM/JTOpen).

> Status: **pre-alpha**. `0.5.0-alpha.2` is a documentation-only follow-up to
> `0.5.0-alpha.1`; the provider includes connection pooling and
> `Db2iDataSource`, both verified against a real IBM i system.

## Connecting

The provider opens a session directly with the IBM i database host server,
without requiring an IBM driver on the client:

```csharp
await using var connection = new Db2iConnection(
    "Server=ibmi.example.test;User ID=MYUSER;Password=secret;Default Collection=MYLIB");

await connection.OpenAsync();

Console.WriteLine(connection.ServerVersion);
Console.WriteLine(connection.ServerCcsid);
Console.WriteLine(connection.ServerJobIdentifier);
```

Parameterized queries use positional `?` markers. Parameter names are accepted
by the ADO.NET API but do not change their position on the wire:

```csharp
await using var connection = new Db2iConnection(
    "Server=ibmi.example.test;User ID=MYUSER;Password=secret;Default Collection=MYLIB");

await connection.OpenAsync();

await using var command = connection.CreateCommand();
command.CommandText = "select CUSNUM, LSTNAM from QCUSTCDT where CUSNUM = ?";
command.Parameters.Add("p1", 938472);

await using var reader = await command.ExecuteReaderAsync();
while (await reader.ReadAsync())
{
    Console.WriteLine($"{reader.GetInt32(0)} {reader.GetString(1)}");
}
```

`ExecuteNonQuery` uses the same positional markers and returns the affected-row
count reported by Db2 for i:

```csharp
await using var command = connection.CreateCommand();
command.CommandText = "update MYLIB.CUSTOMERS set STATUS = ? where CUSNUM = ?";
command.Parameters.Add("status", "A");
command.Parameters.Add("customer", 938472);

var affected = await command.ExecuteNonQueryAsync();
```

Within a local transaction, every command must explicitly reference the active
transaction:

```csharp
await using var transaction =
    (Db2iTransaction)await connection.BeginTransactionAsync(
        IsolationLevel.ReadCommitted);

await using var command = new Db2iCommand(
    "delete from MYLIB.WORK_ROWS where BATCH_ID = ?",
    connection)
{
    Transaction = transaction,
};
command.Parameters.Add("batch", 42);

await command.ExecuteNonQueryAsync();
await transaction.CommitAsync();
```

Use `Db2iDataSource` when you want explicit ownership and disposal of a
dedicated connection pool:

```csharp
await using var dataSource = new Db2iDataSource(
    "Server=ibmi.example.test;User ID=MYUSER;Password=secret;Max Pool Size=20");

await using var connection = await dataSource.OpenConnectionAsync();
```

`DbDataSource.CreateCommand()` is supported. It automatically opens a pooled
connection for each execution and returns it to the pool afterward.

## Architecture

The provider uses the IBM i database host server rather than DRDA:

```text
DbConnection / DbCommand / DbDataReader
                 |
          IBM i SQL session
                 |
   Client Access data stream (big-endian)
                 |
        TCP 8471 or TLS 9471
```

Responsibilities are separated as follows:

- `Db2i*`: the public ADO.NET contract built on `System.Data.Common`;
- `Db2i.Protocol`: framing, authentication, SQL requests, and reply decoding;
- unit tests: deterministic binary vectors and a simulated host server, with no
  IBM i dependency;
- real integration tests: opt-in through environment variables.

## Implementation status

- [x] `DbProviderFactory`, `DbConnection`, `DbCommand`, `DbParameter`,
  `DbParameterCollection`, `DbTransaction`, and `DbDataReader`;
- [x] connection strings with common ADO.NET aliases, ports 8471/9471, and
  connection timeout;
- [x] 20-byte Client Access headers and packet codecs with explicit safety
  limits;
- [x] TCP and TLS transport;
- [x] `exchange random seeds` request/reply (`0x7001`/`0xF001`);
- [x] password substitution for QPWDLVL 0-4 and the `start server` request;
- [x] SQL attribute negotiation and CCSID/VRM/job discovery;
- [x] `Open`/`OpenAsync`/`Close` lifecycle, timeout, and cancellation;
- [x] TLS certificate validation enabled by default;
- [x] synchronous and asynchronous `Prepare`, `ExecuteReader`, and
  `ExecuteScalar`;
- [x] positional `?` markers with explicit, inferred, and typed NULL input
  parameters;
- [x] streaming fetch in approximately 32 KiB blocks and one active reader per
  connection;
- [x] `CommandBehavior.SingleRow`, `SchemaOnly`, `SequentialAccess`, and
  `CloseConnection`;
- [x] `SMALLINT`, `INTEGER`, `BIGINT`, `DECIMAL`, `REAL`, `DOUBLE`, `CHAR`,
  `VARCHAR`, `DATE`, `TIME`, `TIMESTAMP`, `BINARY`, and `VARBINARY`;
- [x] IBM i `SQLCODE`, `SQLSTATE`, and diagnostic text exposed through
  `Db2iException`;
- [x] `ExecuteNonQuery`, affected-row counts from SQLCA, and autocommit;
- [x] local transactions with commit, rollback, and isolation levels;
- [x] commit and rollback verified against a real journaled IBM i table;
- [x] `DbCommand.Cancel`, command timeout, and cancellation through an
  auxiliary session;
- [x] global connection pools and dedicated `Db2iDataSource` pools;
- [x] `DbProviderFactory.CreateDataSource`, pool clearing, and safe session
  reset;
- [ ] LOBs, stored procedures, output parameters, and batching.

Acceptance criteria are tracked in [docs/roadmap.md](docs/roadmap.md).
Current work and operational status are tracked in [TODO.md](TODO.md).

## Build

.NET SDK 10 is required to build every target:

```powershell
dotnet test Db2i.sln --configuration Release
dotnet pack src/Db2i/Db2i.csproj --configuration Release
```

The library targets both `net8.0` and `net10.0`.

## Connection string

```text
Server=my-ibmi;
User ID=MYUSER;
Password=secret;
Database=RDBNAME;
Default Collection=MYLIB;
SSL=true;
Trust Server Certificate=false;
Port=9471;
Connect Timeout=15;
Pooling=true;
Max Pool Size=100
```

`Port` is optional. Its default is 8471, or 9471 when `SSL=true`. Common aliases
such as `Data Source`, `UID`, `PWD`, `Initial Catalog`, and `Current Schema` are
also recognized.

TLS certificate validation is required by default.
`Trust Server Certificate=true` explicitly disables it and should only be used
in controlled environments. `Connect Timeout=0` means no timeout.

Pooling is enabled by default. `Max Pool Size` limits the total number of idle
and active physical sessions, and `Connect Timeout` also covers the wait for an
available pool slot. `Pooling=false` restores physical close behavior on every
`Close`. Regular connections share global pools for equivalent effective
settings, while each `Db2iDataSource` owns an isolated pool that is closed when
the data source is disposed. `Db2iConnection.ClearPool` and `ClearAllPools`
also invalidate sessions currently in use; those sessions are discarded on
their next `Close`.

Before a session is reused, the provider rolls back when necessary, restores
autocommit, and releases any remaining statements and descriptors. Busy,
interrupted, or unsynchronized sessions are discarded. M4.1 does not implement
prewarming, a minimum pool size, or automatic idle-session expiration.

The provider uses SQL naming and supports IBM i 7.3 or later with system
password levels QPWDLVL 0-4. DRDA, `*SYS` naming, MFA, Kerberos, and profile
tokens are outside this milestone.

`CommandTimeout=0` means no timeout. M3 sends the host-server `CANCEL` command
from a second authenticated session and drains the primary reply. When
resynchronization succeeds, the connection remains open. If `CANCEL` or reply
draining fails, the connection is closed for safety.

`ReadUncommitted`, `ReadCommitted`, `RepeatableRead`, and `Serializable` are
supported; `Unspecified` uses `ReadCommitted`. Tables must be journaled on
IBM i to use commitment control. `Chaos`, `Snapshot`,
`CommandType.StoredProcedure`, `CALL`, batching, savepoints, distributed
transactions, and non-input parameters are not supported.

## Testing against IBM i

Local tests use a simulated database host server, including TLS. To enable real
integration tests, set one or both connection variables without storing them
in the repository:

```powershell
$env:DB2I_TEST_TCP_CONNECTION_STRING = "Server=...;User ID=...;Password=..."
$env:DB2I_TEST_TLS_CONNECTION_STRING = "Server=...;User ID=...;Password=...;SSL=true"
$env:DB2I_TEST_DML_ENABLED = "true"
$env:DB2I_TEST_DML_TABLE = "MYLIB.DB2I_DML_TEST"
$env:DB2I_TEST_DML_ID_COLUMN = "ID"
$env:DB2I_TEST_DML_DESCRIPTION_COLUMN = "DESCRIPTION"
$env:DB2I_TEST_DML_DATE_COLUMN = "DATE_VALUE"
$env:DB2I_TEST_DML_DECIMAL_COLUMN = "DECIMAL_VALUE"
dotnet test Db2i.sln --configuration Release --filter Category=Integration
```

When a variable is unset, its corresponding test case performs no network
connection. The M2 TCP test runs read-only queries. M3 tests write only when
`DB2I_TEST_DML_ENABLED=true` and all identifiers for an authorized table are
configured. Identifiers are restricted to simple SQL names. Before writing,
the tests verify a four-column signature (`DECIMAL(5,0)`, `CHAR(100)`,
`DECIMAL(5,0)`, `DECIMAL(9,4)`), use unique markers, and clean up in `finally`.
Transaction tests also verify journaling before writing. Credentials and
identifiers from a real environment must never be stored in the project.

M4 tests add reuse of the same IBM i job and rollback before a session returns
to the pool. Both behaviors were verified against a real IBM i system on
July 29, 2026.

## Provenance and license

This project is a port derived from JTOpen and is therefore distributed under
the IBM Public License 1.0, like the original code. Protocol files identify the
specific JTOpen classes from which they were derived.

This project is not affiliated with or endorsed by IBM. IBM, IBM i, AS/400,
iSeries, and Db2 are trademarks of their respective owners.
