# Roadmap

## M0 — Provider foundations

Completed in the initial scaffold:

- an ADO.NET surface that builds for .NET 8 and .NET 10;
- connection-string parsing;
- Client Access framing, TCP/TLS transport, and random-seed exchange;
- deterministic unit tests for the wire format.

## M1 — Authenticated SQL connection

Implementation completed in `0.2.0-alpha.1`:

- [x] support QPWDLVL 0/1 (DES), 2/3 (SHA-1), and 4
  (PBKDF2-HMAC-SHA-512);
- [x] send `start server` to the database service and validate its return code;
- [x] send the minimum SQL attribute set;
- [x] obtain CCSID, VRM, functional level, and job identifier;
- [x] change `Db2iConnection.State` to `Open` only after a valid SQL reply;
- [x] close the socket and job on every error or cancellation path;
- [x] verify TCP and TLS with a simulated host server;
- [x] verify TCP against a real IBM i system, including CCSID 280;
- [ ] verify TLS against a real IBM i 7.3+ system.

## M2 — First query path

Implementation completed in `0.3.0-alpha.1`:

- [x] synchronous and asynchronous `Prepare`, `ExecuteReader`, and
  `ExecuteScalar`;
- [x] prepare/execute with positional `?` markers, `DbType` inference,
  `Size`/`Precision`/`Scale`, and typed NULL values;
- [x] block-based streaming fetch and `CommandBehavior.CloseConnection`;
- [x] initial types: `SMALLINT`, `INTEGER`, `BIGINT`, `DECIMAL`, `REAL`,
  `DOUBLE`, `CHAR`, `VARCHAR`, `DATE`, `TIME`, `TIMESTAMP`, `BINARY`, and
  `VARBINARY`;
- [x] expose SQLSTATE, SQLCODE, and diagnostic text through `Db2iException`;
- [x] one exclusive forward-only reader per connection and cleanup of cursors,
  descriptors, and RPBs;
- [x] I/O timeout/cancellation with conservative connection closure;
- [x] simulated TCP/TLS tests and real read-only TCP tests for NULL values,
  CCSID 280, column names, parameters, and multi-block fetch.

## M3 — DML and transactions

Implementation available in `0.4.0-alpha.1`:

- [x] synchronous/asynchronous `ExecuteNonQuery`, input markers, and
  affected-row counts read from `SQLERRD3`;
- [x] explicit autocommit, commit, rollback, and rollback on disposal;
- [x] `ReadUncommitted`, `ReadCommitted`, `RepeatableRead`, and `Serializable`
  mapped to IBM i commitment control;
- [x] one local transaction per connection and mandatory
  `DbCommand.Transaction` association;
- [x] host-server `CANCEL` through an authenticated auxiliary session, reply
  draining, and safe connection reuse;
- [x] simulated tests for DML, transactions, timeout/cancellation, and loss of
  synchronization;
- [x] real IBM i autocommit DML tests with table-signature validation and
  cleanup;
- [x] real commit and rollback against the authorized journaled table,
  including data verification after each boundary;
- [x] a JTOpen-compatible fallback when IBM i rejects a locator-persistence
  change while commitment control is starting.

## M4 — Application compatibility

- [x] global pooling with capacity, timeout, reset, and clear operations;
- [x] `DbDataSource` with a dedicated pool and `DbProviderFactory` support;
- [x] real IBM i TCP and transactional pooling verification;
- [ ] batching;
- [ ] LOBs and locators;
- [ ] stored procedures and output parameters;
- [ ] metadata/schema;
- [ ] Dapper integration and, separately, an EF Core provider.

## Deferred decisions

- DRDA support on ports 446/448;
- Kerberos, profile-token, and identity-token authentication;
- `*SYS` naming convention;
- record-level access and other Toolbox services.
