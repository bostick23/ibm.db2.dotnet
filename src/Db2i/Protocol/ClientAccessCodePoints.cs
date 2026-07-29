using System.Buffers.Binary;

namespace Db2i.Protocol;

internal static class ClientAccessCodePoints
{
    internal static ReadOnlyMemory<byte>? Find(byte[] packet, int offset, ushort soughtCodePoint)
        => ParseAll(packet, offset)
            .FirstOrDefault(codePoint => codePoint.CodePoint == soughtCodePoint)
            ?.Payload;

    internal static IReadOnlyList<ClientAccessCodePoint> ParseAll(byte[] packet, int offset)
    {
        var result = new List<ClientAccessCodePoint>();
        while (offset < packet.Length)
        {
            if (packet.Length - offset < 6)
            {
                throw ProtocolError("A Client Access code point is truncated.");
            }

            var length = BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(offset, 4));
            if (length < 6 || length > packet.Length - offset)
            {
                throw ProtocolError($"Invalid Client Access code-point length: {length}.");
            }

            var codePoint = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(offset + 4, 2));
            result.Add(new ClientAccessCodePoint(
                codePoint,
                packet.AsMemory(offset + 6, length - 6)));

            offset += length;
        }

        return result;
    }

    internal static Db2iException ProtocolError(string message, Exception? innerException = null)
        => new(message, Db2iErrorKind.Protocol, innerException);
}

internal sealed record ClientAccessCodePoint(ushort CodePoint, ReadOnlyMemory<byte> Payload);
