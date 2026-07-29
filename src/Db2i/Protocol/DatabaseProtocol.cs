using System.Buffers;
using System.Buffers.Binary;

namespace Db2i.Protocol;

internal static class DatabaseProtocol
{
    internal const ushort ReplyId = 0x2800;
    internal const ushort SetAttributesRequestId = 0x1F80;
    internal const int ReturnData = unchecked((int)0x80000000);
    internal const int MessageId = 0x40000000;
    internal const int FirstLevelText = 0x20000000;
    internal const int SecondLevelText = 0x10000000;
    internal const int DataFormat = 0x08000000;
    internal const int ResultData = 0x04000000;
    internal const int Sqlca = 0x02000000;
    internal const int ServerAttributes = 0x01000000;
    internal const int ParameterMarkerFormat = 0x00800000;
    internal const int ReplyRleCompression = 0x00040000;

    private const ushort ServerAttributesCodePoint = 0x3804;

    internal static byte[] CreateBootstrapAttributesRequest(
        Db2iConnectionSettings settings,
        int correlationId,
        string providerVersion)
    {
        var builder = new DatabaseRequestBuilder(
            SetAttributesRequestId,
            ReturnData | ServerAttributes,
            correlationId);

        builder.AddInt16(0x3801, 13488);
        builder.AddFixedString(0x3803, 37, "V7R2M01   ", trailingPadding: 2);
        builder.AddByte(0x3805, 0xF0);
        builder.AddInt16(0x3806, 1);
        builder.AddByte(0x3824, 0xE8);
        builder.AddInt16(0x380E, 0);
        builder.AddInt16(0x3807, 5);
        builder.AddInt16(0x3808, 1);
        builder.AddInt16(0x3809, 2);
        builder.AddInt16(0x380A, 0);
        builder.AddInt16(0x380C, 0);
        builder.AddInt16(0x3811, 1);
        builder.AddByte(0x3821, 0xF2);
        builder.AddInt32(0x3825, 0x40000000);

        if (!string.IsNullOrEmpty(settings.Database))
        {
            if (settings.Database.Length > 18)
            {
                throw new ArgumentException("Database cannot exceed 18 characters.", nameof(settings));
            }

            var rdbName = settings.Database.ToUpperInvariant().PadRight(18, ' ');
            builder.AddFixedString(0x3826, 37, rdbName);
        }

        builder.AddVariableString(0x383C, 37, "ADO.NET");
        builder.AddVariableString(0x383D, 37, "Db2i");
        builder.AddVariableString(0x383E, 37, providerVersion);
        return builder.Build();
    }

    internal static byte[] CreateTransactionAttributesRequest(
        bool autoCommit,
        short commitmentControlLevel,
        int correlationId,
        bool includeLocatorPersistence = true)
    {
        // Ported from JTOpen JDTransactionManager.setAutoCommit/setIsolation.
        var builder = new DatabaseRequestBuilder(
            SetAttributesRequestId,
            ReturnData | ServerAttributes | ReplyRleCompression,
            correlationId);
        builder.AddByte(0x3824, autoCommit ? (byte)0xE8 : (byte)0xD5);
        builder.AddInt16(0x380E, commitmentControlLevel);
        if (includeLocatorPersistence)
        {
            builder.AddInt16(0x3830, commitmentControlLevel == 0 ? (short)0 : (short)1);
        }

        return builder.Build();
    }

    internal static byte[] CreateDefaultCollectionRequest(
        string defaultCollection,
        int ccsid,
        int correlationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultCollection);
        var builder = new DatabaseRequestBuilder(
            SetAttributesRequestId,
            ReturnData,
            correlationId);
        builder.AddVariableString(0x380F, ccsid, defaultCollection);
        return builder.Build();
    }

    internal static DatabaseReply ParseReply(ClientAccessPacket packet, int expectedCorrelationId)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (packet.Header.RequestReplyId != ReplyId)
        {
            throw ClientAccessCodePoints.ProtocolError(
                $"Unexpected database reply ID 0x{packet.Header.RequestReplyId:X4}.");
        }

        if (packet.Header.CorrelationId != expectedCorrelationId)
        {
            throw ClientAccessCodePoints.ProtocolError(
                $"Database reply correlation {packet.Header.CorrelationId} does not match request {expectedCorrelationId}.");
        }

        if (packet.Header.TemplateLength != 20 || packet.Bytes.Length < 40)
        {
            throw ClientAccessCodePoints.ProtocolError("The database reply template is invalid.");
        }

        var returnFunctionId = BinaryPrimitives.ReadUInt16BigEndian(packet.Bytes.AsSpan(30, 2));
        var errorClass = BinaryPrimitives.ReadInt16BigEndian(packet.Bytes.AsSpan(34, 2));
        var returnCode = BinaryPrimitives.ReadInt32BigEndian(packet.Bytes.AsSpan(36, 4));
        var codePoints = ClientAccessCodePoints.ParseAll(packet.Bytes, 40);
        var attributesPayload = codePoints
            .FirstOrDefault(codePoint => codePoint.CodePoint == ServerAttributesCodePoint)
            ?.Payload;

        return new DatabaseReply(returnFunctionId, errorClass, returnCode, codePoints, attributesPayload);
    }

    internal static Db2iServerInfo ParseServerInfo(ReadOnlyMemory<byte> codePointPayload)
    {
        if (codePointPayload.Length < 116)
        {
            throw ClientAccessCodePoints.ProtocolError(
                $"The server-attributes payload is too short: {codePointPayload.Length} bytes.");
        }

        var body = codePointPayload.Span[2..];
        var ccsid = BinaryPrimitives.ReadUInt16BigEndian(body[19..]);
        var version = new Version(
            body[51] & 0x0F,
            body[53] & 0x0F,
            body[55] & 0x0F);
        var functionalLevel = IbmIEncoding.Decode(ccsid, body.Slice(50, 10));
        var relationalDatabaseName = IbmIEncoding.Decode(ccsid, body.Slice(60, 18));
        var defaultCollection = IbmIEncoding.Decode(ccsid, body.Slice(78, 10));
        var jobName = IbmIEncoding.Decode(ccsid, body.Slice(88, 10));
        var jobUser = IbmIEncoding.Decode(ccsid, body.Slice(98, 10));
        var jobNumber = IbmIEncoding.Decode(ccsid, body.Slice(108, 6));

        return new Db2iServerInfo(
            version,
            ccsid,
            functionalLevel,
            relationalDatabaseName,
            defaultCollection,
            $"{jobNumber}/{jobUser}/{jobName}");
    }
}

internal sealed record DatabaseReply(
    ushort ReturnFunctionId,
    short ErrorClass,
    int ReturnCode,
    IReadOnlyList<ClientAccessCodePoint> CodePoints,
    ReadOnlyMemory<byte>? ServerAttributes);

internal sealed record Db2iServerInfo(
    Version Version,
    int Ccsid,
    string FunctionalLevel,
    string RelationalDatabaseName,
    string DefaultCollection,
    string JobIdentifier);

internal sealed class DatabaseRequestBuilder
{
    private readonly ArrayBufferWriter<byte> _writer = new();
    private readonly ushort _requestId;
    private readonly int _operationResultsBitmap;
    private readonly int _correlationId;
    private ushort _parameterCount;

    internal DatabaseRequestBuilder(
        ushort requestId,
        int operationResultsBitmap,
        int correlationId,
        ushort rpbHandle = 0,
        ushort parameterMarkerDescriptorHandle = 0)
    {
        _requestId = requestId;
        _operationResultsBitmap = operationResultsBitmap;
        _correlationId = correlationId;
        var template = _writer.GetSpan(40)[..40];
        template.Clear();
        BinaryPrimitives.WriteUInt16BigEndian(template[28..], rpbHandle);
        BinaryPrimitives.WriteUInt16BigEndian(template[30..], rpbHandle);
        BinaryPrimitives.WriteUInt16BigEndian(template[34..], rpbHandle);
        BinaryPrimitives.WriteUInt16BigEndian(template[36..], parameterMarkerDescriptorHandle);
        _writer.Advance(40);
    }

    internal void AddByte(ushort codePoint, byte value)
    {
        var payload = BeginParameter(codePoint, 1);
        payload[0] = value;
    }

    internal void AddInt16(ushort codePoint, short value)
    {
        var payload = BeginParameter(codePoint, 2);
        BinaryPrimitives.WriteInt16BigEndian(payload, value);
    }

    internal void AddInt32(ushort codePoint, int value)
    {
        var payload = BeginParameter(codePoint, 4);
        BinaryPrimitives.WriteInt32BigEndian(payload, value);
    }

    internal void AddBytes(ushort codePoint, ReadOnlySpan<byte> value)
    {
        var payload = BeginParameter(codePoint, value.Length);
        value.CopyTo(payload);
    }

    internal void AddExtendedString(ushort codePoint, int ccsid, string value)
    {
        var encoded = IbmIEncoding.Encode(ccsid, value);
        if (encoded.Length > 2 * 1024 * 1024)
        {
            throw new ArgumentException(
                "An extended database string parameter cannot exceed 2 MiB.",
                nameof(value));
        }

        var payload = BeginParameter(codePoint, 6 + encoded.Length);
        BinaryPrimitives.WriteUInt16BigEndian(payload, checked((ushort)ccsid));
        BinaryPrimitives.WriteInt32BigEndian(payload[2..], encoded.Length);
        encoded.CopyTo(payload[6..]);
    }

    internal void AddFixedString(ushort codePoint, int ccsid, string value, int trailingPadding = 0)
    {
        var encoded = IbmIEncoding.Encode(ccsid, value);
        var payload = BeginParameter(codePoint, 2 + encoded.Length + trailingPadding);
        BinaryPrimitives.WriteUInt16BigEndian(payload, checked((ushort)ccsid));
        encoded.CopyTo(payload[2..]);
    }

    internal void AddVariableString(ushort codePoint, int ccsid, string value)
    {
        var encoded = IbmIEncoding.Encode(ccsid, value);
        if (encoded.Length > ushort.MaxValue)
        {
            throw new ArgumentException("A database string parameter cannot exceed 65535 bytes.", nameof(value));
        }

        var payload = BeginParameter(codePoint, 4 + encoded.Length);
        BinaryPrimitives.WriteUInt16BigEndian(payload, checked((ushort)ccsid));
        BinaryPrimitives.WriteUInt16BigEndian(payload[2..], (ushort)encoded.Length);
        encoded.CopyTo(payload[4..]);
    }

    internal byte[] Build()
    {
        var bytes = _writer.WrittenSpan.ToArray();
        new ClientAccessHeader(
                bytes.Length,
                HeaderId: 0,
                ClientAccessHeader.SqlServerId,
                ClientServerInstance: 0,
                CorrelationId: _correlationId,
                TemplateLength: 20,
                RequestReplyId: _requestId)
            .WriteTo(bytes);

        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), _operationResultsBitmap);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(38, 2), _parameterCount);
        return bytes;
    }

    private Span<byte> BeginParameter(ushort codePoint, int payloadLength)
    {
        var parameter = _writer.GetSpan(6 + payloadLength);
        BinaryPrimitives.WriteInt32BigEndian(parameter, 6 + payloadLength);
        BinaryPrimitives.WriteUInt16BigEndian(parameter[4..], codePoint);
        var payload = parameter.Slice(6, payloadLength);
        payload.Clear();
        _writer.Advance(6 + payloadLength);
        _parameterCount++;
        return payload;
    }

}
