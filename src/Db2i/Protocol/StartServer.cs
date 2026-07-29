using System.Buffers.Binary;

namespace Db2i.Protocol;

/// <summary>
/// Start-server request/reply ported from JTOpen AS400StrSvrDS and
/// AS400StrSvrReplyDS.
/// </summary>
internal static class StartServer
{
    internal const ushort RequestId = 0x7002;
    internal const ushort ReplyId = 0xF002;
    private const ushort PasswordCodePoint = 0x1105;
    private const ushort UserIdCodePoint = 0x1104;
    private const ushort JobNameCodePoint = 0x111F;

    internal static byte[] CreateRequest(
        ReadOnlySpan<byte> userIdEbcdic,
        ReadOnlySpan<byte> passwordSubstitute,
        ushort serverId = ClientAccessHeader.SqlServerId)
    {
        if (userIdEbcdic.Length != 10)
        {
            throw new ArgumentException("The IBM i user profile must contain 10 EBCDIC bytes.", nameof(userIdEbcdic));
        }

        var passwordIndicator = passwordSubstitute.Length switch
        {
            8 => (byte)0x01,
            20 => (byte)0x03,
            64 => (byte)0x07,
            _ => throw new ArgumentException(
                "An IBM i password substitute must contain 8, 20, or 64 bytes.",
                nameof(passwordSubstitute)),
        };

        var bytes = new byte[44 + passwordSubstitute.Length];
        new ClientAccessHeader(
                bytes.Length,
                HeaderId: 0x0200,
                serverId,
                ClientServerInstance: 0,
                CorrelationId: 0,
                TemplateLength: 2,
                RequestReplyId: RequestId)
            .WriteTo(bytes);

        bytes[20] = passwordIndicator;
        bytes[21] = 0x01;

        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(22, 4), 6 + passwordSubstitute.Length);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(26, 2), PasswordCodePoint);
        passwordSubstitute.CopyTo(bytes.AsSpan(28));

        var userOffset = 28 + passwordSubstitute.Length;
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(userOffset, 4), 16);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(userOffset + 4, 2), UserIdCodePoint);
        userIdEbcdic.CopyTo(bytes.AsSpan(userOffset + 6));

        return bytes;
    }

    internal static StartServerReply ParseReply(ClientAccessPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (packet.Header.RequestReplyId != ReplyId)
        {
            throw ClientAccessCodePoints.ProtocolError(
                $"Unexpected start-server reply ID 0x{packet.Header.RequestReplyId:X4}.");
        }

        if (packet.Bytes.Length < 24)
        {
            throw ClientAccessCodePoints.ProtocolError("The start-server reply is shorter than 24 bytes.");
        }

        var returnCode = BinaryPrimitives.ReadInt32BigEndian(packet.Bytes.AsSpan(20, 4));
        var jobCodePoint = ClientAccessCodePoints.Find(packet.Bytes, 24, JobNameCodePoint);
        var jobNameBytes = jobCodePoint is { } jobPayload
            ? ParseTextPayload(jobPayload, JobNameCodePoint)
            : [];

        return new StartServerReply(returnCode, jobNameBytes);
    }

    private static byte[] ParseTextPayload(ReadOnlyMemory<byte> payload, ushort codePoint)
    {
        if (payload.Length < 4)
        {
            throw ClientAccessCodePoints.ProtocolError(
                $"Text code point 0x{codePoint:X4} does not contain a CCSID.");
        }

        return payload[4..].ToArray();
    }
}

internal sealed record StartServerReply(int ReturnCode, byte[] JobNameBytes);
