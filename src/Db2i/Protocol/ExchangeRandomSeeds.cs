using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Db2i.Protocol;

/// <summary>
/// Exchange-random-seeds request/reply used before starting an IBM i host-server job.
/// Ported from JTOpen AS400XChgRandSeedDS and AS400XChgRandSeedReplyDS.
/// </summary>
internal static class ExchangeRandomSeeds
{
    internal const ushort RequestId = 0x7001;
    internal const ushort ReplyId = 0xF001;
    private const ushort AdditionalAuthenticationFactorCodePoint = 0x112E;

    internal static ExchangeRandomSeedsRequest CreateRequest(ushort serverId = ClientAccessHeader.SqlServerId)
    {
        var clientSeed = RandomNumberGenerator.GetBytes(8);
        return CreateRequest(clientSeed, serverId);
    }

    internal static ExchangeRandomSeedsRequest CreateRequest(
        ReadOnlySpan<byte> clientSeed,
        ushort serverId = ClientAccessHeader.SqlServerId)
    {
        if (clientSeed.Length != 8)
        {
            throw new ArgumentException("Il client seed deve essere lungo 8 byte.", nameof(clientSeed));
        }

        var bytes = new byte[28];
        new ClientAccessHeader(
                bytes.Length,
                HeaderId: 0x0300,
                serverId,
                ClientServerInstance: 0,
                CorrelationId: 0,
                TemplateLength: 8,
                RequestReplyId: RequestId)
            .WriteTo(bytes);
        clientSeed.CopyTo(bytes.AsSpan(ClientAccessHeader.Size));

        return new ExchangeRandomSeedsRequest(bytes, clientSeed.ToArray());
    }

    internal static ExchangeRandomSeedsReply ParseReply(ClientAccessPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (packet.Header.RequestReplyId != ReplyId)
        {
            throw new InvalidDataException(
                $"Reply ID non valido per exchange random seeds: 0x{packet.Header.RequestReplyId:X4}.");
        }

        if (packet.Bytes.Length < 32)
        {
            throw new InvalidDataException("La reply exchange random seeds è più corta di 32 byte.");
        }

        var returnCode = BinaryPrimitives.ReadInt32BigEndian(packet.Bytes.AsSpan(20, 4));
        var serverSeed = packet.Bytes.AsSpan(24, 8).ToArray();
        var passwordLevel = (byte)(packet.Header.HeaderId & 0x00FF);
        var acceptsAdditionalAuthenticationFactor = FindBooleanCodePoint(
            packet.Bytes,
            offset: 32,
            AdditionalAuthenticationFactorCodePoint);

        return new ExchangeRandomSeedsReply(
            returnCode,
            serverSeed,
            passwordLevel,
            acceptsAdditionalAuthenticationFactor);
    }

    private static bool FindBooleanCodePoint(byte[] bytes, int offset, ushort soughtCodePoint)
    {
        while (offset < bytes.Length)
        {
            if (bytes.Length - offset < 6)
            {
                throw new InvalidDataException("Code point Client Access troncato.");
            }

            var length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset, 4));
            if (length < 6 || length > bytes.Length - offset)
            {
                throw new InvalidDataException($"Lunghezza code point Client Access non valida: {length}.");
            }

            var codePoint = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 4, 2));
            if (codePoint == soughtCodePoint)
            {
                if (length < 7)
                {
                    throw new InvalidDataException("Il code point booleano non contiene un valore.");
                }

                return bytes[offset + 6] == 1;
            }

            offset += length;
        }

        return false;
    }
}

internal sealed record ExchangeRandomSeedsRequest(byte[] Bytes, byte[] ClientSeed);

internal sealed record ExchangeRandomSeedsReply(
    int ReturnCode,
    byte[] ServerSeed,
    byte PasswordLevel,
    bool AcceptsAdditionalAuthenticationFactor);
