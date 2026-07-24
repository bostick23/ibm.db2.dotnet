namespace Db2i.Protocol;

internal sealed class ClientAccessPacket
{
    internal ClientAccessPacket(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        Header = ClientAccessHeader.Parse(bytes);
        if (Header.Length != bytes.Length)
        {
            throw new InvalidDataException(
                $"La lunghezza dichiarata ({Header.Length}) non coincide con i byte ricevuti ({bytes.Length}).");
        }

        Bytes = bytes;
    }

    internal ClientAccessHeader Header { get; }

    internal byte[] Bytes { get; }

    internal ReadOnlyMemory<byte> Payload => Bytes.AsMemory(ClientAccessHeader.Size);
}
