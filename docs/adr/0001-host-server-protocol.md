# ADR 0001: Use the database host-server protocol

Date: 2026-07-24

Status: accepted

## Context

IBM i exposes several database access paths. JTOpen contains both Toolbox
components and DDM/DRDA-related code. This project's goal is a progressive port
of the Toolbox JDBC path.

## Decision

The MVP implements the Client Access `as-database` service:

- SQL server ID `0xE004`;
- clear-text port 8471;
- TLS port 9471;
- 20-byte Client Access headers;
- native database host-server SQL requests.

DRDA on ports 446/448 is outside the MVP.

The public API is based on `System.Data.Common`. No protocol detail should be
required to use `Db2iConnection`, `Db2iCommand`, or `Db2iDataReader`.

## Consequences

- behavior can be compared directly with JTOpen;
- no native IBM driver needs to be installed on the client;
- authentication, CCSID handling, and SQL formats must be implemented by the
  provider;
- the derived port remains subject to the IBM Public License 1.0.
