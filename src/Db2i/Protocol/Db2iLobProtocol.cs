using System.Buffers.Binary;

namespace Db2i.Protocol;

internal sealed record Db2iLobValue(Db2iFieldDescriptor Field, int Handle, int ColumnIndex);

internal sealed record Db2iLobChunk(ReadOnlyMemory<byte> Data, long TotalLength);

// Protocol references: IBM/JTOpen JDLobLocator, DBSQLRequestDS, DBBaseReplyDS
// and DBLobData. Offsets/amounts count bytes except DBCLOB's two-byte units.
internal static partial class SqlProtocol
{
    internal const ushort RetrieveLobData = 0x1816;
    internal const ushort WriteLobData = 0x1817;
    internal const ushort FreeLob = 0x1819;
    internal const int LobChunkSize = 64 * 1024;

    internal static byte[] CreateReadLobRequest(
        ushort statementHandle, Db2iLobValue value, long offset, int length)
    {
        ValidateLobRange(value.Handle, value.Field.IsGraphicLob, offset, length);
        var builder = new DatabaseRequestBuilder(RetrieveLobData,
            DatabaseProtocol.ReturnData | DatabaseProtocol.ResultData,
            statementHandle, statementHandle);
        builder.AddInt32(0x3818, value.Handle);
        builder.AddInt32(0x3819, length);
        builder.AddInt32(0x381A, (int)offset);
        builder.AddByte(0x381B, 0xF0);
        builder.AddByte(0x3821, 0xF1);
        if (value.ColumnIndex >= 0)
        {
            builder.AddInt32(0x3828, checked(value.ColumnIndex + 1));
        }

        return builder.Build();
    }

    internal static byte[] CreateWriteLobRequest(
        ushort statementHandle, Db2iFieldDescriptor field, long offset,
        ReadOnlySpan<byte> data, bool truncate)
    {
        if (field.IsGraphicLob && (data.Length & 1) != 0)
        {
            throw new ArgumentException("DBCLOB data must contain complete two-byte units.", nameof(data));
        }

        var units = data.Length / (field.IsGraphicLob ? 2 : 1);
        ValidateLobRange(field.LobLocator, field.IsGraphicLob, offset, units);
        var builder = new DatabaseRequestBuilder(WriteLobData,
            DatabaseProtocol.ReturnData | DatabaseProtocol.ResultData,
            statementHandle, statementHandle);
        builder.AddByte(0x3822, truncate ? (byte)0xF0 : (byte)0xF1);
        builder.AddInt32(0x3818, field.LobLocator);
        builder.AddInt32(0x3819, units);
        builder.AddInt32(0x381A, (int)offset);
        builder.AddByte(0x381B, 0xF0);
        var payload = new byte[6 + data.Length];
        BinaryPrimitives.WriteUInt16BigEndian(payload, ushort.MaxValue);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(2), data.Length);
        data.CopyTo(payload.AsSpan(6));
        builder.AddBytes(0x381D, payload);
        return builder.Build();
    }

    internal static byte[] CreateFreeLobRequest(ushort statementHandle, int handle)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(handle);
        var builder = new DatabaseRequestBuilder(FreeLob, DatabaseProtocol.ReturnData,
            statementHandle, statementHandle);
        builder.AddInt32(0x3818, handle);
        return builder.Build();
    }

    internal static Db2iLobChunk ParseLobChunk(
        DatabaseReply reply, Db2iLobValue value, long offset, int requestedLength)
    {
        var totalPayload = FindFirst(reply, 0x3810)
            ?? throw ClientAccessCodePoints.ProtocolError("IBM i omitted the current LOB length.");
        if (totalPayload.Length < 2)
        {
            throw ClientAccessCodePoints.ProtocolError("The current LOB length is truncated.");
        }

        var width = BinaryPrimitives.ReadUInt16BigEndian(totalPayload.Span);
        if (width is not (0 or 4 or 8) || totalPayload.Length != width + 2)
        {
            throw ClientAccessCodePoints.ProtocolError("The current LOB length width is invalid.");
        }

        var total = width switch
        {
            0 => 0L,
            4 => BinaryPrimitives.ReadInt32BigEndian(totalPayload.Span[2..]),
            _ => BinaryPrimitives.ReadInt64BigEndian(totalPayload.Span[2..]),
        };
        if (total < 0)
        {
            throw ClientAccessCodePoints.ProtocolError("The current LOB length is negative.");
        }

        var dataPayload = FindFirst(reply, 0x380F);
        if (dataPayload is null || dataPayload.Value.Length == 0)
        {
            if (requestedLength != 0 && offset < total)
            {
                throw ClientAccessCodePoints.ProtocolError("IBM i omitted requested LOB data.");
            }

            return new Db2iLobChunk(ReadOnlyMemory<byte>.Empty, total);
        }

        var data = dataPayload.Value;
        if (data.Length < 6)
        {
            throw ClientAccessCodePoints.ProtocolError("The LOB data prefix is truncated.");
        }

        var ccsid = BinaryPrimitives.ReadUInt16BigEndian(data.Span);
        var amount = BinaryPrimitives.ReadInt32BigEndian(data.Span[2..]);
        var byteLength = (long)amount * (value.Field.IsGraphicLob ? 2 : 1);
        if (amount < 0 || amount > requestedLength || byteLength != data.Length - 6
            || amount > Math.Max(0, total - offset)
            || (amount == 0 && requestedLength > 0 && offset < total))
        {
            throw ClientAccessCodePoints.ProtocolError("The LOB data length is invalid or truncated.");
        }

        if (ccsid is not (0 or 65535) && value.Field.Ccsid != 0 && ccsid != value.Field.Ccsid)
        {
            throw ClientAccessCodePoints.ProtocolError("The LOB data CCSID differs from its descriptor.");
        }

        return new Db2iLobChunk(data[6..], total);
    }

    private static void ValidateLobRange(int handle, bool graphic, long offset, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(handle);
        if (offset < 0 || offset > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "IBM i supports 32-bit LOB offsets.");
        }

        if (length < 0 || (long)length * (graphic ? 2 : 1) > LobChunkSize
            || offset + length > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }
    }
}
