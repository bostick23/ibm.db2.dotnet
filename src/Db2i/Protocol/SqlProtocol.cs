using System.Buffers.Binary;
using System.Data;
using System.Globalization;

namespace Db2i.Protocol;

// Wire layouts and function/code-point identifiers are ported from the
// JTOpen DBBaseRequestDS, DBSQLRequestDS, DBSQLRPBDS, DBSQLDescriptorDS,
// DBExtendedDataFormat and DBSuperExtendedDataFormat classes.
internal static class SqlProtocol
{
    internal const ushort CreateRpb = 0x1D00;
    internal const ushort DeleteRpb = 0x1D02;
    internal const ushort ChangeDescriptor = 0x1E00;
    internal const ushort DeleteDescriptor = 0x1E01;
    internal const ushort SendResultSet = 0x1F00;
    internal const ushort DeleteResultSet = 0x1F01;
    internal const ushort PrepareDescribe = 0x1803;
    internal const ushort Execute = 0x1805;
    internal const ushort Commit = 0x1807;
    internal const ushort Rollback = 0x1808;
    internal const ushort Close = 0x180A;
    internal const ushort Fetch = 0x180B;
    internal const ushort OpenDescribeFetch = 0x180E;
    internal const ushort Cancel = 0x1818;

    internal const ushort ExtendedDataFormat = 0x380C;
    internal const ushort ExtendedParameterMarkerFormat = 0x380D;
    internal const ushort ExtendedResultData = 0x380E;
    internal const ushort ExtendedParameterMarkerData = 0x381F;
    internal const ushort SuperExtendedDataFormat = 0x3812;
    internal const ushort SuperExtendedParameterMarkerFormat = 0x3813;

    private const int DiagnosticResults =
        DatabaseProtocol.ReturnData
        | DatabaseProtocol.MessageId
        | DatabaseProtocol.FirstLevelText
        | DatabaseProtocol.SecondLevelText
        | DatabaseProtocol.Sqlca;

    internal static byte[] CreateRpbRequest(
        ushort handle,
        int serverCcsid,
        int commandTimeout)
    {
        var builder = new DatabaseRequestBuilder(
            CreateRpb,
            operationResultsBitmap: 0,
            correlationId: handle,
            rpbHandle: handle);
        builder.AddVariableString(0x3806, serverCcsid, $"STMT{handle:0000}");
        builder.AddVariableString(0x380B, serverCcsid, $"CRSR{handle:0000}");
        if (commandTimeout > 0)
        {
            builder.AddInt32(0x3817, commandTimeout);
        }

        return builder.Build();
    }

    internal static byte[] CreatePrepareRequest(
        ushort handle,
        string commandText,
        Db2iStatementKind statementKind)
    {
        var builder = new DatabaseRequestBuilder(
            PrepareDescribe,
            DiagnosticResults
            | DatabaseProtocol.DataFormat
            | DatabaseProtocol.ParameterMarkerFormat,
            correlationId: handle,
            rpbHandle: handle);
        builder.AddExtendedString(0x3831, 13488, commandText);
        builder.AddInt16(0x3812, GetNativeStatementType(statementKind));
        builder.AddByte(0x3808, 0);
        return builder.Build();
    }

    internal static byte[] CreateChangeDescriptorRequest(
        ushort handle,
        Db2iDataFormat descriptor)
    {
        var builder = new DatabaseRequestBuilder(
            ChangeDescriptor,
            operationResultsBitmap: 0,
            correlationId: handle,
            rpbHandle: handle,
            parameterMarkerDescriptorHandle: handle);
        builder.AddBytes(0x381E, EncodeExtendedDescriptor(descriptor));
        return builder.Build();
    }

    internal static byte[] CreateExecuteRequest(
        ushort handle,
        Db2iDataFormat? parameterFormat,
        IReadOnlyList<Db2iParameter> parameters,
        Db2iStatementKind statementKind)
    {
        var descriptorHandle = parameterFormat is null ? (ushort)0 : handle;
        var builder = new DatabaseRequestBuilder(
            Execute,
            DiagnosticResults,
            correlationId: handle,
            rpbHandle: handle,
            parameterMarkerDescriptorHandle: descriptorHandle);
        builder.AddInt16(0x3812, GetNativeStatementType(statementKind));
        if (parameterFormat is not null)
        {
            builder.AddBytes(
                ExtendedParameterMarkerData,
                Db2iTypeCodec.EncodeParameterRow(parameterFormat, parameters));
        }

        return builder.Build();
    }

    internal static byte[] CreateTransactionBoundaryRequest(
        ushort functionId,
        int correlationId)
    {
        if (functionId is not (Commit or Rollback))
        {
            throw new ArgumentOutOfRangeException(nameof(functionId));
        }

        var builder = new DatabaseRequestBuilder(
            functionId,
            DiagnosticResults,
            correlationId);
        builder.AddByte(0x380F, 0);
        return builder.Build();
    }

    internal static byte[] CreateCancelRequest(
        int correlationId,
        int serverCcsid,
        string targetJobIdentifier)
    {
        // JTOpen AS400JDBCConnectionImpl sends CANCEL from a second database
        // connection and identifies the target server job with code point 0x3826.
        ArgumentException.ThrowIfNullOrWhiteSpace(targetJobIdentifier);
        var builder = new DatabaseRequestBuilder(
            Cancel,
            DatabaseProtocol.ReturnData,
            correlationId);
        builder.AddVariableString(0x3826, serverCcsid, targetJobIdentifier);
        return builder.Build();
    }

    internal static byte[] CreateOpenRequest(
        ushort handle,
        Db2iDataFormat resultFormat,
        Db2iDataFormat? parameterFormat,
        IReadOnlyList<Db2iParameter> parameters,
        bool singleRow)
    {
        var descriptorHandle = parameterFormat is null ? (ushort)0 : handle;
        var builder = new DatabaseRequestBuilder(
            OpenDescribeFetch,
            DiagnosticResults | DatabaseProtocol.ResultData,
            correlationId: handle,
            rpbHandle: handle,
            parameterMarkerDescriptorHandle: descriptorHandle);

        builder.AddByte(0x3809, 0x80);
        builder.AddInt16(0x380D, 5);
        builder.AddInt16(0x3812, GetNativeStatementType(Db2iStatementKind.Query));

        var rowFootprint = checked(resultFormat.RecordSize + (resultFormat.Fields.Count * 2));
        var blockingFactor = singleRow
            ? 1
            : Math.Clamp(32 * 1024 / Math.Max(1, rowFootprint), 1, short.MaxValue);
        builder.AddInt32(0x380C, blockingFactor);

        if (parameterFormat is not null)
        {
            builder.AddBytes(
                ExtendedParameterMarkerData,
                Db2iTypeCodec.EncodeParameterRow(parameterFormat, parameters));
        }

        return builder.Build();
    }

    internal static byte[] CreateFetchRequest(
        ushort handle,
        Db2iDataFormat resultFormat,
        bool singleRow)
    {
        var builder = new DatabaseRequestBuilder(
            Fetch,
            DiagnosticResults | DatabaseProtocol.ResultData,
            correlationId: handle,
            rpbHandle: handle);
        builder.AddInt16(0x380E, 0);

        var rowFootprint = checked(resultFormat.RecordSize + (resultFormat.Fields.Count * 2));
        var blockingFactor = singleRow
            ? 1
            : Math.Clamp(32 * 1024 / Math.Max(1, rowFootprint), 1, short.MaxValue);
        builder.AddInt32(0x380C, blockingFactor);
        return builder.Build();
    }

    internal static byte[] CreateCloseRequest(ushort handle)
    {
        var builder = new DatabaseRequestBuilder(
            Close,
            DatabaseProtocol.ReplyRleCompression,
            correlationId: handle,
            rpbHandle: handle);
        builder.AddByte(0x3810, 0xF1);
        return builder.Build();
    }

    internal static byte[] CreateDeleteDescriptorRequest(ushort handle)
        => new DatabaseRequestBuilder(
                DeleteDescriptor,
                operationResultsBitmap: 0,
                correlationId: handle,
                rpbHandle: handle,
                parameterMarkerDescriptorHandle: handle)
            .Build();

    internal static byte[] CreateDeleteRpbRequest(ushort handle)
        => new DatabaseRequestBuilder(
                DeleteRpb,
                DatabaseProtocol.ReturnData | DatabaseProtocol.ReplyRleCompression,
                correlationId: handle,
                rpbHandle: handle)
            .Build();

    internal static byte[] CreateDeleteResultSetRequest(ushort handle)
        => new DatabaseRequestBuilder(
                DeleteResultSet,
                DatabaseProtocol.ReturnData | DatabaseProtocol.ReplyRleCompression,
                correlationId: handle,
                rpbHandle: handle)
            .Build();

    internal static byte[] CreateDiagnosticResultSetRequest(
        ushort handle,
        int correlationId)
        => new DatabaseRequestBuilder(
                SendResultSet,
                DiagnosticResults | DatabaseProtocol.ReplyRleCompression,
                correlationId,
                rpbHandle: handle)
            .Build();

    internal static Db2iDataFormat? ParseResultFormat(
        DatabaseReply reply,
        int serverCcsid)
    {
        var super = FindFirst(reply, SuperExtendedDataFormat);
        if (super is { } superPayload)
        {
            return superPayload.IsEmpty
                ? null
                : ParseSuperExtendedDescriptor(superPayload.Span, serverCcsid);
        }

        var extended = FindFirst(reply, ExtendedDataFormat);
        return extended is { } extendedPayload && !extendedPayload.IsEmpty
            ? ParseExtendedDescriptor(extendedPayload.Span, serverCcsid)
            : null;
    }

    internal static Db2iDataFormat? ParseParameterFormat(
        DatabaseReply reply,
        int serverCcsid)
    {
        var super = FindFirst(reply, SuperExtendedParameterMarkerFormat);
        if (super is { } superPayload)
        {
            if (superPayload.IsEmpty)
            {
                return new Db2iDataFormat(1, 0, 5, 2, []);
            }

            return ParseSuperExtendedDescriptor(superPayload.Span, serverCcsid);
        }

        var extended = FindFirst(reply, ExtendedParameterMarkerFormat);
        if (extended is not { } extendedPayload)
        {
            return null;
        }

        return extendedPayload.IsEmpty
            ? new Db2iDataFormat(1, 0, 5, 2, [])
            : ParseExtendedDescriptor(extendedPayload.Span, serverCcsid);
    }

    internal static Db2iResultBlock ParseResultBlock(
        DatabaseReply reply,
        Db2iDataFormat format)
    {
        var payload = FindFirst(reply, ExtendedResultData);
        if (payload is not { } result || result.IsEmpty)
        {
            return Db2iResultBlock.Empty;
        }

        return Db2iTypeCodec.DecodeResultBlock(result.Span, format);
    }

    internal static Db2iSqlDiagnostic ParseDiagnostic(DatabaseReply reply, int serverCcsid)
    {
        int? sqlCode = null;
        string? sqlState = null;
        int? rowsAffected = null;

        var sqlca = FindFirst(reply, 0x3807);
        if (sqlca is { } sqlcaPayload)
        {
            var span = sqlcaPayload.Span;
            if (span.Length >= 136)
            {
                sqlCode = BinaryPrimitives.ReadInt32BigEndian(span[12..]);
                rowsAffected = BinaryPrimitives.ReadInt32BigEndian(span[104..]);
                sqlState = IbmIEncoding.Decode(serverCcsid, span.Slice(131, 5));
            }
        }

        var messageId = DecodeFixedMessage(FindFirst(reply, 0x3801));
        var firstLevel = DecodeVariableMessage(FindFirst(reply, 0x3802));
        var secondLevel = DecodeVariableMessage(FindFirst(reply, 0x3803));
        return new Db2iSqlDiagnostic(
            sqlCode,
            sqlState,
            rowsAffected,
            messageId,
            firstLevel,
            secondLevel);
    }

    internal static bool IsEndOfData(DatabaseReply reply, Db2iSqlDiagnostic diagnostic)
        => diagnostic.SqlCode == 100
            || string.Equals(diagnostic.SqlState, "02000", StringComparison.Ordinal)
            || (reply.ErrorClass == 1 && reply.ReturnCode == 100)
            || (reply.ErrorClass == 2 && reply.ReturnCode is 700 or 701);

    internal static Db2iDataFormat ResolveParameterFormat(
        Db2iDataFormat? serverFormat,
        IReadOnlyList<Db2iParameter> parameters,
        int serverCcsid)
    {
        var serverFields = serverFormat?.Fields ?? [];
        if (serverFields.Count != parameters.Count)
        {
            throw new InvalidOperationException(
                $"Il comando contiene {serverFields.Count} marker posizionali, " +
                $"ma sono stati forniti {parameters.Count} parametri.");
        }

        var fields = new List<Db2iFieldDescriptor>(parameters.Count);
        var offset = 0;
        for (var index = 0; index < parameters.Count; index++)
        {
            var field = Db2iTypeCodec.ResolveParameter(
                parameters[index],
                serverFields[index],
                serverCcsid,
                offset);
            fields.Add(field);
            offset = checked(offset + field.Length);
        }

        return new Db2iDataFormat(
            ConsistencyToken: 1,
            RecordSize: offset,
            DateFormat: 5,
            TimeFormat: 2,
            fields);
    }

    internal static Db2iStatementKind ClassifyStatement(string commandText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandText);
        var tokens = ReadLeadingTokens(commandText, 2);
        if (tokens.Count == 0)
        {
            return Db2iStatementKind.NonQuery;
        }

        return tokens[0] switch
        {
            "SELECT" or "VALUES" or "WITH" => Db2iStatementKind.Query,
            "CALL" => Db2iStatementKind.Procedure,
            "COMMIT" or "ROLLBACK" => Db2iStatementKind.TransactionControl,
            "SET" when tokens.Count > 1 && tokens[1] == "TRANSACTION"
                => Db2iStatementKind.TransactionControl,
            _ => Db2iStatementKind.NonQuery,
        };
    }

    private static Db2iDataFormat ParseExtendedDescriptor(
        ReadOnlySpan<byte> payload,
        int serverCcsid)
    {
        EnsureDescriptorHeader(payload);
        var token = BinaryPrimitives.ReadInt32BigEndian(payload);
        var count = BinaryPrimitives.ReadInt32BigEndian(payload[4..]);
        var recordSize = BinaryPrimitives.ReadInt32BigEndian(payload[12..]);
        var expectedLength = checked(16 + (count * 64));
        if (payload.Length < expectedLength)
        {
            throw ClientAccessCodePoints.ProtocolError(
                "The extended SQL descriptor is truncated.");
        }

        var fields = new List<Db2iFieldDescriptor>(count);
        var rowOffset = 0;
        for (var index = 0; index < count; index++)
        {
            var field = payload.Slice(16 + (index * 64), 64);
            var nameLength = BinaryPrimitives.ReadUInt16BigEndian(field[30..]);
            if (nameLength > 30)
            {
                throw ClientAccessCodePoints.ProtocolError(
                    $"The SQL descriptor name at ordinal {index} is too long.");
            }

            var nameCcsid = BinaryPrimitives.ReadUInt16BigEndian(field[32..]);
            if (nameCcsid is 0 or 65535)
            {
                nameCcsid = checked((ushort)serverCcsid);
            }
            var name = nameLength == 0
                ? string.Empty
                : IbmIEncoding.DecodeExact(nameCcsid, field.Slice(34, nameLength));
            var length = BinaryPrimitives.ReadInt32BigEndian(field[4..]);
            fields.Add(new Db2iFieldDescriptor(
                BinaryPrimitives.ReadUInt16BigEndian(field[2..]),
                length,
                BinaryPrimitives.ReadInt16BigEndian(field[8..]),
                BinaryPrimitives.ReadInt16BigEndian(field[10..]),
                BinaryPrimitives.ReadUInt16BigEndian(field[12..]),
                name,
                rowOffset));
            rowOffset = checked(rowOffset + length);
        }

        ValidateRecordSize(recordSize, rowOffset);
        return new Db2iDataFormat(token, recordSize, 5, 2, fields);
    }

    private static Db2iDataFormat ParseSuperExtendedDescriptor(
        ReadOnlySpan<byte> payload,
        int serverCcsid)
    {
        EnsureDescriptorHeader(payload);
        var token = BinaryPrimitives.ReadInt32BigEndian(payload);
        var count = BinaryPrimitives.ReadInt32BigEndian(payload[4..]);
        var recordSize = BinaryPrimitives.ReadInt32BigEndian(payload[12..]);
        var fixedLength = checked(16 + (count * 48));
        if (payload.Length < fixedLength)
        {
            throw ClientAccessCodePoints.ProtocolError(
                "The super-extended SQL descriptor is truncated.");
        }

        var fields = new List<Db2iFieldDescriptor>(count);
        var rowOffset = 0;
        for (var index = 0; index < count; index++)
        {
            var fieldStart = 16 + (index * 48);
            var field = payload.Slice(fieldStart, 48);
            var name = ParseSuperExtendedFieldName(
                payload,
                fieldStart,
                field,
                serverCcsid);
            var length = BinaryPrimitives.ReadInt32BigEndian(field[4..]);
            fields.Add(new Db2iFieldDescriptor(
                BinaryPrimitives.ReadUInt16BigEndian(field[2..]),
                length,
                BinaryPrimitives.ReadInt16BigEndian(field[8..]),
                BinaryPrimitives.ReadInt16BigEndian(field[10..]),
                BinaryPrimitives.ReadUInt16BigEndian(field[12..]),
                name,
                rowOffset));
            rowOffset = checked(rowOffset + length);
        }

        ValidateRecordSize(recordSize, rowOffset);
        return new Db2iDataFormat(token, recordSize, payload[8], payload[9], fields);
    }

    private static string ParseSuperExtendedFieldName(
        ReadOnlySpan<byte> payload,
        int fieldStart,
        ReadOnlySpan<byte> field,
        int serverCcsid)
    {
        var variableOffset = BinaryPrimitives.ReadInt32BigEndian(field[32..]);
        var variableLength = BinaryPrimitives.ReadInt32BigEndian(field[36..]);
        if (variableOffset <= 0 || variableLength <= 0)
        {
            return string.Empty;
        }

        var cursor = checked(fieldStart + variableOffset);
        var end = checked(cursor + variableLength);
        if (cursor < 0 || end > payload.Length)
        {
            throw ClientAccessCodePoints.ProtocolError(
                "The variable section of a super-extended SQL descriptor is invalid.");
        }

        while (cursor < end)
        {
            if (end - cursor < 6)
            {
                throw ClientAccessCodePoints.ProtocolError(
                    "A super-extended SQL descriptor code point is truncated.");
            }

            var length = BinaryPrimitives.ReadInt32BigEndian(payload[cursor..]);
            var codePoint = BinaryPrimitives.ReadUInt16BigEndian(payload[(cursor + 4)..]);
            if (length < 6 || length > end - cursor)
            {
                throw ClientAccessCodePoints.ProtocolError(
                    "A super-extended SQL descriptor code point has an invalid length.");
            }

            if (codePoint == 0x3840)
            {
                if (length < 8)
                {
                    return string.Empty;
                }

                var ccsid = BinaryPrimitives.ReadUInt16BigEndian(payload[(cursor + 6)..]);
                if (ccsid is 0 or 65535)
                {
                    ccsid = checked((ushort)serverCcsid);
                }
                return IbmIEncoding.DecodeExact(
                    ccsid,
                    payload.Slice(cursor + 8, length - 8));
            }

            cursor += length;
        }

        return string.Empty;
    }

    private static byte[] EncodeExtendedDescriptor(Db2iDataFormat format)
    {
        var payload = new byte[checked(16 + (format.Fields.Count * 64))];
        BinaryPrimitives.WriteInt32BigEndian(payload, format.ConsistencyToken);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(4), format.Fields.Count);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(12), format.RecordSize);

        for (var index = 0; index < format.Fields.Count; index++)
        {
            var source = format.Fields[index];
            var field = payload.AsSpan(16 + (index * 64), 64);
            BinaryPrimitives.WriteUInt16BigEndian(field, 64);
            BinaryPrimitives.WriteUInt16BigEndian(field[2..], (ushort)(source.NativeType | 1));
            BinaryPrimitives.WriteInt32BigEndian(field[4..], source.Length);
            BinaryPrimitives.WriteInt16BigEndian(field[8..], checked((short)source.Scale));
            BinaryPrimitives.WriteInt16BigEndian(field[10..], checked((short)source.Precision));
            BinaryPrimitives.WriteUInt16BigEndian(field[12..], checked((ushort)source.Ccsid));
        }

        return payload;
    }

    private static ReadOnlyMemory<byte>? FindFirst(DatabaseReply reply, ushort codePoint)
        => reply.CodePoints
            .FirstOrDefault(value => value.CodePoint == codePoint)
            ?.Payload;

    private static string? DecodeFixedMessage(ReadOnlyMemory<byte>? payload)
    {
        if (payload is not { } value || value.Length < 2)
        {
            return null;
        }

        var span = value.Span;
        var ccsid = BinaryPrimitives.ReadUInt16BigEndian(span);
        return IbmIEncoding.Decode(ccsid, span[2..]);
    }

    private static string? DecodeVariableMessage(ReadOnlyMemory<byte>? payload)
    {
        if (payload is not { } value || value.Length < 4)
        {
            return null;
        }

        var span = value.Span;
        var ccsid = BinaryPrimitives.ReadUInt16BigEndian(span);
        var length = BinaryPrimitives.ReadUInt16BigEndian(span[2..]);
        if (length > span.Length - 4)
        {
            throw ClientAccessCodePoints.ProtocolError(
                "An IBM i diagnostic message is truncated.");
        }

        return IbmIEncoding.Decode(ccsid, span.Slice(4, length));
    }

    private static void EnsureDescriptorHeader(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 16)
        {
            throw ClientAccessCodePoints.ProtocolError(
                "The SQL data format descriptor is too short.");
        }

        var count = BinaryPrimitives.ReadInt32BigEndian(payload[4..]);
        var recordSize = BinaryPrimitives.ReadInt32BigEndian(payload[12..]);
        if (count < 0 || count > short.MaxValue || recordSize < 0)
        {
            throw ClientAccessCodePoints.ProtocolError(
                "The SQL data format descriptor contains invalid dimensions.");
        }
    }

    private static void ValidateRecordSize(int declared, int calculated)
    {
        if (declared < calculated)
        {
            throw ClientAccessCodePoints.ProtocolError(
                $"The SQL descriptor record size {declared} is smaller than {calculated}.");
        }
    }

    private static short GetNativeStatementType(Db2iStatementKind statementKind)
        => statementKind switch
        {
            Db2iStatementKind.Query => 2,
            Db2iStatementKind.Procedure => 3,
            Db2iStatementKind.NonQuery => 1,
            Db2iStatementKind.TransactionControl => 1,
            _ => throw new ArgumentOutOfRangeException(nameof(statementKind)),
        };

    private static IReadOnlyList<string> ReadLeadingTokens(string text, int maximum)
    {
        var result = new List<string>(maximum);
        var offset = 0;
        while (result.Count < maximum)
        {
            SkipTrivia(text, ref offset);
            var start = offset;
            while (offset < text.Length
                && (char.IsLetter(text[offset]) || text[offset] == '_'))
            {
                offset++;
            }

            if (start == offset)
            {
                break;
            }

            result.Add(text[start..offset].ToUpperInvariant());
        }

        return result;
    }

    private static void SkipTrivia(string text, ref int offset)
    {
        while (offset < text.Length)
        {
            if (char.IsWhiteSpace(text[offset]))
            {
                offset++;
                continue;
            }

            if (offset + 1 < text.Length
                && text[offset] == '-'
                && text[offset + 1] == '-')
            {
                offset += 2;
                while (offset < text.Length && text[offset] is not ('\r' or '\n'))
                {
                    offset++;
                }

                continue;
            }

            if (offset + 1 < text.Length
                && text[offset] == '/'
                && text[offset + 1] == '*')
            {
                var end = text.IndexOf("*/", offset + 2, StringComparison.Ordinal);
                offset = end < 0 ? text.Length : end + 2;
                continue;
            }

            break;
        }
    }
}

internal enum Db2iStatementKind
{
    Query,
    NonQuery,
    Procedure,
    TransactionControl,
}

internal sealed record Db2iDataFormat(
    int ConsistencyToken,
    int RecordSize,
    int DateFormat,
    int TimeFormat,
    IReadOnlyList<Db2iFieldDescriptor> Fields);

internal sealed record Db2iFieldDescriptor(
    int SqlType,
    int Length,
    int Scale,
    int Precision,
    int Ccsid,
    string Name,
    int Offset)
{
    internal int NativeType => SqlType & ~1;

    internal bool AllowNull => (SqlType & 1) != 0;

    internal Db2iColumn ToColumn()
    {
        var (fieldType, dataTypeName, columnSize) = NativeType switch
        {
            384 => (typeof(DateTime), "DATE", 10),
            388 => (typeof(TimeSpan), "TIME", 8),
            392 => (typeof(DateTime), "TIMESTAMP", Length),
            448 => (typeof(string), "VARCHAR", Math.Max(0, Length - 2)),
            452 => (typeof(string), "CHAR", Length),
            480 when Length == 4 => (typeof(float), "REAL", 4),
            480 => (typeof(double), "DOUBLE", 8),
            484 => (typeof(decimal), "DECIMAL", Precision),
            488 => (typeof(decimal), "NUMERIC", Precision),
            492 => (typeof(long), "BIGINT", 8),
            496 => (typeof(int), "INTEGER", 4),
            500 => (typeof(short), "SMALLINT", 2),
            908 => (typeof(byte[]), "VARBINARY", Math.Max(0, Length - 2)),
            912 => (typeof(byte[]), "BINARY", Length),
            _ => throw new Db2iException(
                $"IBM i returned unsupported SQL native type {NativeType}.",
                Db2iErrorKind.Protocol),
        };

        return new Db2iColumn(
            Name,
            fieldType,
            dataTypeName,
            AllowNull,
            columnSize,
            Precision,
            Scale);
    }
}

internal sealed record Db2iSqlDiagnostic(
    int? SqlCode,
    string? SqlState,
    int? RowsAffected,
    string? MessageId,
    string? FirstLevelText,
    string? SecondLevelText)
{
    internal string BuildMessage(string operation, short errorClass, int returnCode)
    {
        var texts = new[]
        {
            FirstLevelText,
            SecondLevelText,
        }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var message = texts.Length == 0
            ? $"IBM i database operation '{operation}' failed."
            : string.Join(" ", texts);
        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(MessageId))
        {
            details.Add($"message {MessageId}");
        }

        if (SqlCode is { } sqlCode)
        {
            details.Add($"SQLCODE {sqlCode.ToString(CultureInfo.InvariantCulture)}");
        }

        if (!string.IsNullOrWhiteSpace(SqlState))
        {
            details.Add($"SQLSTATE {SqlState}");
        }

        details.Add($"error class {errorClass.ToString(CultureInfo.InvariantCulture)}");
        details.Add($"return code {returnCode.ToString(CultureInfo.InvariantCulture)}");
        return $"{message} ({string.Join(", ", details)}).";
    }
}

internal sealed record Db2iResultBlock(IReadOnlyList<object?[]> Rows)
{
    internal static Db2iResultBlock Empty { get; } = new([]);
}
