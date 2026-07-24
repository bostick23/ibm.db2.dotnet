namespace Db2i.Protocol;

internal static class ClientAccessPacketCodec
{
    private const int DefaultMaximumPacketSize = 16 * 1024 * 1024;

    internal static async ValueTask<ClientAccessPacket> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken,
        int maximumPacketSize = DefaultMaximumPacketSize)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumPacketSize, ClientAccessHeader.Size);

        var bytes = new byte[ClientAccessHeader.Size];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);

        var header = ClientAccessHeader.Parse(bytes);
        if (header.Length > maximumPacketSize)
        {
            throw new InvalidDataException(
                $"Il data stream dichiara {header.Length} byte, oltre il limite di {maximumPacketSize}.");
        }

        if (header.Length > ClientAccessHeader.Size)
        {
            Array.Resize(ref bytes, header.Length);
            await stream.ReadExactlyAsync(
                    bytes.AsMemory(ClientAccessHeader.Size, header.Length - ClientAccessHeader.Size),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return new ClientAccessPacket(bytes);
    }

    internal static async ValueTask WriteAsync(
        Stream stream,
        ReadOnlyMemory<byte> packet,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = ClientAccessHeader.Parse(packet.Span);
        if (header.Length != packet.Length)
        {
            throw new InvalidDataException(
                $"La lunghezza dichiarata ({header.Length}) non coincide con i byte da inviare ({packet.Length}).");
        }

        await stream.WriteAsync(packet, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
