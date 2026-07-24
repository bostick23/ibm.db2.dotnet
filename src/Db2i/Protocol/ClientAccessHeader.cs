using System.Buffers.Binary;

namespace Db2i.Protocol;

/// <summary>
/// Header comune a 20 byte dei data stream Client Access.
/// Il layout deriva da ClientAccessDataStream e DBBaseRequestDS di JTOpen.
/// </summary>
internal readonly record struct ClientAccessHeader(
    int Length,
    ushort HeaderId,
    ushort ServerId,
    int ClientServerInstance,
    int CorrelationId,
    ushort TemplateLength,
    ushort RequestReplyId)
{
    internal const int Size = 20;
    internal const ushort SqlServerId = 0xE004;
    internal const ushort SignonServerId = 0xE009;

    internal static ClientAccessHeader Parse(ReadOnlySpan<byte> source)
    {
        if (source.Length < Size)
        {
            throw new InvalidDataException($"Un header Client Access richiede {Size} byte.");
        }

        var header = new ClientAccessHeader(
            BinaryPrimitives.ReadInt32BigEndian(source),
            BinaryPrimitives.ReadUInt16BigEndian(source[4..]),
            BinaryPrimitives.ReadUInt16BigEndian(source[6..]),
            BinaryPrimitives.ReadInt32BigEndian(source[8..]),
            BinaryPrimitives.ReadInt32BigEndian(source[12..]),
            BinaryPrimitives.ReadUInt16BigEndian(source[16..]),
            BinaryPrimitives.ReadUInt16BigEndian(source[18..]));

        if (header.Length < Size)
        {
            throw new InvalidDataException($"Lunghezza data stream non valida: {header.Length}.");
        }

        return header;
    }

    internal void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Size)
        {
            throw new ArgumentException($"La destinazione deve contenere almeno {Size} byte.", nameof(destination));
        }

        if (Length < Size)
        {
            throw new InvalidOperationException($"La lunghezza del data stream non può essere minore di {Size}.");
        }

        BinaryPrimitives.WriteInt32BigEndian(destination, Length);
        BinaryPrimitives.WriteUInt16BigEndian(destination[4..], HeaderId);
        BinaryPrimitives.WriteUInt16BigEndian(destination[6..], ServerId);
        BinaryPrimitives.WriteInt32BigEndian(destination[8..], ClientServerInstance);
        BinaryPrimitives.WriteInt32BigEndian(destination[12..], CorrelationId);
        BinaryPrimitives.WriteUInt16BigEndian(destination[16..], TemplateLength);
        BinaryPrimitives.WriteUInt16BigEndian(destination[18..], RequestReplyId);
    }
}
