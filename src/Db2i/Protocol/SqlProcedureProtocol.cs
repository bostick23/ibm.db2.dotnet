using System.Buffers.Binary;

namespace Db2i.Protocol;

// Protocol groundwork for M4.4; not yet used by the public command/session path.
// Ported from JTOpen AS400JDBCPreparedStatementImpl.commonExecuteBefore/After,
// AS400JDBCStatement.commonExecute/getMoreResults, JDCursor.openDescribe/close,
// and DBReplySQLCA.getErrd. See docs/stored-procedure-protocol.md for the pinned
// source revision, reply ownership, and the remaining ADO.NET lifecycle work.
internal static partial class SqlProtocol
{
    internal const ushort OpenDescribe = 0x1804;
    internal const int MaximumProcedureResultSets = short.MaxValue;

    // The payload is an already encoded extended parameter-marker row. Keeping
    // this separate from CreateExecuteRequest avoids enabling Output binding in
    // the existing input-only command path before reader lifetime is implemented.
    internal static byte[] CreateProcedureExecuteRequest(
        ushort handle,
        ReadOnlyMemory<byte>? parameterData,
        bool returnOutputParameters)
    {
        if (parameterData is { } data && (data.Length < 20
            || BinaryPrimitives.ReadInt32BigEndian(data.Span[4..]) != 1))
        {
            throw new ArgumentException("Procedure parameters require one extended data row.", nameof(parameterData));
        }

        if (returnOutputParameters && parameterData is null)
        {
            throw new ArgumentException("Output parameters require a parameter descriptor and row.", nameof(parameterData));
        }

        var builder = new DatabaseRequestBuilder(
            Execute,
            DiagnosticResults | (returnOutputParameters ? DatabaseProtocol.ResultData : 0),
            correlationId: handle,
            rpbHandle: handle,
            parameterMarkerDescriptorHandle: parameterData is null ? (ushort)0 : handle);
        builder.AddInt16(0x3812, GetNativeStatementType(Db2iStatementKind.Procedure));
        if (parameterData is { } payload)
        {
            builder.AddBytes(ExtendedParameterMarkerData, payload.Span);
        }

        return builder.Build();
    }

    internal static byte[] CreateProcedureOpenResultRequest(ushort handle)
    {
        // Do not fetch on CALL or OPEN: CALL's result data is the output row.
        // Each dynamic result has its own descriptor, followed by ordinary FETCH.
        var builder = new DatabaseRequestBuilder(
            OpenDescribe,
            DiagnosticResults | DatabaseProtocol.DataFormat,
            correlationId: handle,
            rpbHandle: handle);
        builder.AddByte(0x3809, 0x80);
        builder.AddInt16(0x380D, 5);
        return builder.Build();
    }

    internal static byte[] CreateProcedureCloseResultRequest(ushort handle)
    {
        // REUSE_RESULT_SET (F2) advances the procedure's result chain. Ordinary
        // query close uses REUSE_YES (F1). Neither close requests a reply.
        var builder = new DatabaseRequestBuilder(
            Close,
            DatabaseProtocol.ReplyRleCompression,
            correlationId: handle,
            rpbHandle: handle);
        builder.AddByte(0x3810, 0xF2);
        return builder.Build();
    }

    // Call only after the session has checked the execute reply for SQL errors.
    // Output values use the original server parameter format, never the client
    // input descriptor or a descriptor belonging to a dynamic result set.
    internal static Db2iProcedureCompletion ParseProcedureCompletion(
        DatabaseReply reply,
        Db2iDataFormat? serverParameterFormat)
    {
        if (reply.ReturnFunctionId != Execute)
        {
            throw ClientAccessCodePoints.ProtocolError("A procedure completion requires an EXECUTE reply.");
        }

        var sqlca = FindFirst(reply, 0x3807);
        if (sqlca is not { Length: >= 136 } sqlcaPayload)
        {
            throw ClientAccessCodePoints.ProtocolError("The procedure SQLCA is missing or truncated.");
        }

        var returnStatus = BinaryPrimitives.ReadInt32BigEndian(sqlcaPayload.Span[96..]);
        var resultSetCount = BinaryPrimitives.ReadInt32BigEndian(sqlcaPayload.Span[100..]);
        if (resultSetCount < 0 || resultSetCount > MaximumProcedureResultSets)
        {
            throw ClientAccessCodePoints.ProtocolError("The procedure result-set count exceeds the supported range.");
        }

        var fields = serverParameterFormat?.Fields ?? [];
        foreach (var field in fields)
        {
            if (field.IsArray)
            {
                throw new NotSupportedException("SQL array procedure parameters are not supported.");
            }

            if (field.ParameterMode is not (Db2iParameterMode.Input or Db2iParameterMode.Output or Db2iParameterMode.InputOutput))
            {
                throw ClientAccessCodePoints.ProtocolError("The procedure parameter descriptor has an unknown direction.");
            }
        }

        var data = FindFirst(reply, ExtendedResultData);
        if (reply.CodePoints.Count(value => value.CodePoint == ExtendedResultData) > 1)
        {
            throw ClientAccessCodePoints.ProtocolError("The procedure returned duplicate output-parameter data.");
        }

        if (!fields.Any(field => field.IsOutputParameter))
        {
            if (data is { Length: > 0 })
            {
                throw ClientAccessCodePoints.ProtocolError("The procedure returned data without an output-parameter descriptor.");
            }

            return new(returnStatus, resultSetCount, []);
        }

        if (data is not { } outputData)
        {
            throw ClientAccessCodePoints.ProtocolError("The procedure output-parameter data is missing.");
        }

        var row = Db2iTypeCodec.DecodeProcedureOutputRow(outputData.Span, serverParameterFormat!);
        var outputs = new List<Db2iProcedureOutput>();
        for (var index = 0; index < fields.Count; index++)
        {
            if (fields[index].IsOutputParameter)
            {
                outputs.Add(new(index, row[index]));
            }
        }

        return new(returnStatus, resultSetCount, outputs);
    }
}

// Ordinals are zero-based positions among CALL markers; ReturnValue is not a
// marker. Null values remain null internally, and locators remain unmaterialized.
internal sealed record Db2iProcedureOutput(int ParameterIndex, object? Value);

internal sealed record Db2iProcedureCompletion(
    int ReturnStatus,
    int ResultSetCount,
    IReadOnlyList<Db2iProcedureOutput> Outputs);
