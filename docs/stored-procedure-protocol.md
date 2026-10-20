# Stored-procedure protocol groundwork (M4.4)

The protocol mapping and internal binary helpers are implemented. Public
`CommandType.StoredProcedure`, text `CALL`, output parameters, and procedure
result readers remain unsupported. The helpers have deterministic local tests;
no procedure has been executed against a real IBM i system.

## Reference implementation

This mapping uses IBM/JTOpen revision
`613335cea07e42679ddabaddcd51ce2cf5faaf24`. Source links are pinned so later
changes to JTOpen do not silently change the reference:

- [AS400JDBCCallableStatement](https://github.com/IBM/JTOpen/blob/613335cea07e42679ddabaddcd51ce2cf5faaf24/src/main/java/com/ibm/as400/access/AS400JDBCCallableStatement.java):
  constructor disables prefetch; `commonExecuteBefore` validates output registration.
- [AS400JDBCPreparedStatementImpl](https://github.com/IBM/JTOpen/blob/613335cea07e42679ddabaddcd51ce2cf5faaf24/src/main/java/com/ibm/as400/access/AS400JDBCPreparedStatementImpl.java):
  `commonExecuteBefore` requests output data;
  `commonExecuteAfter` installs that data in the original parameter row and
  reads the return status separately.
- [AS400JDBCStatement](https://github.com/IBM/JTOpen/blob/613335cea07e42679ddabaddcd51ce2cf5faaf24/src/main/java/com/ibm/as400/access/AS400JDBCStatement.java):
  `commonExecute` reads the dynamic-result count;
  `getMoreResults` closes the current result and opens/describes the next one.
- [DBExtendedDataFormat](https://github.com/IBM/JTOpen/blob/613335cea07e42679ddabaddcd51ce2cf5faaf24/src/main/java/com/ibm/as400/access/DBExtendedDataFormat.java),
  [DBSuperExtendedDataFormat](https://github.com/IBM/JTOpen/blob/613335cea07e42679ddabaddcd51ce2cf5faaf24/src/main/java/com/ibm/as400/access/DBSuperExtendedDataFormat.java),
  and [JDServerRow](https://github.com/IBM/JTOpen/blob/613335cea07e42679ddabaddcd51ce2cf5faaf24/src/main/java/com/ibm/as400/access/JDServerRow.java):
  field directions, row offsets, and lazy output-value decoding.
- [DBReplySQLCA](https://github.com/IBM/JTOpen/blob/613335cea07e42679ddabaddcd51ce2cf5faaf24/src/main/java/com/ibm/as400/access/DBReplySQLCA.java),
  [DBSQLRequestDS](https://github.com/IBM/JTOpen/blob/613335cea07e42679ddabaddcd51ce2cf5faaf24/src/main/java/com/ibm/as400/access/DBSQLRequestDS.java),
  and [JDCursor](https://github.com/IBM/JTOpen/blob/613335cea07e42679ddabaddcd51ce2cf5faaf24/src/main/java/com/ibm/as400/access/JDCursor.java):
  SQLCA offsets, request identifiers, and cursor reuse flags.

## Wire mapping

Offsets below exclude the six-byte code-point envelope. Field offsets are
relative to one repeated descriptor field, after its 16-byte format header.

| Item | Encoding | Meaning |
| --- | --- | --- |
| Parameter mode | Field byte 14 | `F0` IN, `F1` OUT, `F2` INOUT |
| Extended descriptor | `380D`, 64 bytes per field | Server parameter format |
| Super-extended descriptor | `3813`, 48 fixed bytes per field | Server format with variable metadata |
| Array flag | Super-extended field byte 21, mask `40` | Reject scalar binding; arrays are unsupported |
| Procedure statement type | Request code point `3812`, int16 `3` | CALL classification |
| Execute | Function `1805` | Invoke a prepared CALL |
| Output-data request | Operation-results bit `04000000` | Request the OUT/INOUT row |
| Parameter-marker data | Request code point `381F` | Encoded input/placeholder row |
| Output-parameter data | Reply code point `380E` | One row using the original server parameter descriptor |
| Procedure return status | SQLCA `3807`, int32 at byte 96 (`SQLERRD1`) | Signed status; not a positional marker |
| Dynamic-result count | SQLCA int32 at byte 100 (`SQLERRD2`) | Number of procedure results |
| DML row count | SQLCA int32 at byte 104 (`SQLERRD3`) | Separate from status and result count |
| Open/describe result | Function `1804` | Obtain each dynamic result's own descriptor |
| Close current procedure result | Function `180A`, code point `3810 = F2` | `REUSE_RESULT_SET` for advancing the chain |

Both descriptor parsers now retain parameter mode. Client descriptor encoding
preserves it, including when an input value changes its scalar wire type.
Zero/unknown modes remain representable for ordinary result columns but are
rejected when parsing a procedure completion. Arrays are identified in the
super-extended descriptor and rejected rather than rebound as scalar values.

`SqlProcedureProtocol.cs` provides request builders for execution, opening a
result, and closing it to advance the chain. The execute builder accepts an
already encoded marker row; direction-aware parameter binding remains a
separate implementation step. It requests `ResultData` only for output
parameters. A return-status-only invocation needs no output marker.

`ParseProcedureCompletion` reads status/count independently of the data row.
It retains zero-based marker ordinals for OUT/INOUT values. Input-only slots
still contribute to the row's size and offsets, but their bytes are ignored:
JTOpen only converts a parameter when its value is requested. This matters for
invalid or uninitialized input placeholders in a returned output row.

Do not use the client input descriptor to decode outputs. Its lengths or types
may differ from the original server parameter descriptor. Do not use a dynamic
result descriptor either. The output row is not the first public result set.
NULL remains `null` internally; the public binding layer must convert it to
`DBNull.Value`. Locator values remain handles and are not read or released by
these pure parsing helpers.

## Execution sequence to integrate

1. Create the RPB and prepare/describe a validated CALL, using statement type 3.
2. Preserve the server parameter descriptor; validate directions and marker
   count before sending an execute request. Resolve and send a separate client
   descriptor, then upload any input locators and encode the marker row.
3. Execute without prefetching a dynamic result. Check SQL errors before
   parsing completion. Stage outputs and the return status privately.
4. For each dynamic result, send OPEN/DESCRIBE and construct its reader from
   that result's descriptor. Fetch rows with the existing FETCH path.
5. Release that result's row locators and close it with `F2` before opening the
   next result. Do not substitute the ordinary-query close flag `F1`.
6. Complete/discard the remaining result chain before reexecution or pooled
   reuse. Integrate with existing descriptor/RPB/result-set cleanup and its
   failure handling; a partially cleaned chain must not reach the pool.

The first result may already have a descriptor in the execute reply. JTOpen
handles both cases. The future session path must retain that descriptor when
present and use OPEN/DESCRIBE when absent, without reading output bytes as rows.
Each subsequent result requires its own descriptor.

## Bounds and validation

The transport's existing 16 MiB packet limit applies. Descriptor field counts
are limited to 32,767 and their dimensions/storage are validated by the
existing parsers. Procedure output data must have exactly one complete row;
that check occurs before row allocation, even for zero-width records. Column
count, indicator width, and record size must match the server format. Input
slots are skipped only after the entire row's dimensions have been validated.

Completion requires an EXECUTE reply and a complete 136-byte SQLCA. Missing
SQLCA is not treated as a zero status. The provider bounds dynamic-result
counts to 0–32,767. Duplicate output-data code points, missing required output
data, data without an output descriptor, unknown directions, and unsupported
arrays fail explicitly. Record padding and rows without null indicators are
accepted, as in the ordinary uncompressed row decoder.

No new network I/O is wired into the session by this change. Timeout,
cancellation, cursor ownership, and pooled reuse must be tested when the
session path is connected; binary vectors alone do not validate those cases.

## ADO.NET implementation contract (pending)

- `CommandType.StoredProcedure` will accept a validated procedure identifier
  and construct a positional CALL. Define accepted qualified/quoted names
  explicitly; reject arbitrary SQL and JDBC escape forms before network I/O.
- Keep marker order independent of parameter names. `ReturnValue` is one
  optional `Int32` status outside the marker sequence. Validate each other
  direction against the server descriptor before execution.
- OUT values must not be consumed as inputs. INOUT output capacity must not
  shrink to the length of the current input value. Keep `Prepare` free of
  stream consumption and output assignment.
- Publish outputs together after successful reader/result completion. A reader
  holds the connection lease until its result chain is completed or closed.
  `ExecuteNonQuery` and `ExecuteScalar` must finish cleanup before returning;
  procedure `ExecuteNonQuery` returns `-1`, not the procedure status as a count.
- Define the same completion point for `NextResult(false)`, `Close`, and
  synchronous/asynchronous disposal. `SchemaOnly` must not invoke a procedure;
  `SingleRow`/`SingleResult` must still release undisplayed results.
- Keep a snapshot of output parameter references for each invocation. Do not
  publish partial outputs after SQL errors, cancellation, failed cleanup, or a
  replaced connection. Preserve batch command attribution and stop-on-error.
- Integrate reexecution, preparation invalidation, transaction boundaries,
  timeout/cancellation, and connectionless commands/batches. Classify LOB
  ownership before enabling LOB output parameters: descriptor-owned handles
  must never be freed individually, even if also seen in returned data.
- Add simulated-host tests for the entire lifecycle, then opt-in real tests
  against dedicated, explicitly configured procedures. Integration tests must
  never create procedures or tables implicitly.

`Db2iProcedureProtocolTests` covers binary requests, both descriptor formats,
mode preservation through a simulated input execution, mixed output values,
NULL/empty text, locator handles, SQLCA status/count separation, malformed
replies, allocation bounds, and continued rejection of unsupported public CALL
execution. The full M4.4 acceptance criteria remain open in `TODO.md`.
