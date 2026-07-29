using System.Net.Security;
using System.Net.Sockets;

namespace Db2i.Protocol;

internal sealed class Db2iTransport : IAsyncDisposable, IDisposable
{
    private readonly TcpClient _client;
    private readonly Stream _stream;

    private Db2iTransport(TcpClient client, Stream stream)
    {
        _client = client;
        _stream = stream;
    }

    internal static async ValueTask<Db2iTransport> ConnectAsync(
        Db2iConnectionSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(settings.Server, settings.Port, cancellationToken).ConfigureAwait(false);
            Stream stream = client.GetStream();

            if (settings.UseSsl)
            {
                var sslStream = new SslStream(
                    stream,
                    leaveInnerStreamOpen: false,
                    settings.TrustServerCertificate
                        ? static (_, _, _, _) => true
                        : null);
                await sslStream.AuthenticateAsClientAsync(
                        new SslClientAuthenticationOptions { TargetHost = settings.Server },
                        cancellationToken)
                    .ConfigureAwait(false);
                stream = sslStream;
            }

            return new Db2iTransport(client, stream);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    internal ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
        => ClientAccessPacketCodec.WriteAsync(_stream, packet, cancellationToken);

    internal ValueTask<ClientAccessPacket> ReceiveAsync(CancellationToken cancellationToken)
        => ClientAccessPacketCodec.ReadAsync(_stream, cancellationToken);

    internal bool IsPotentiallyUsable
    {
        get
        {
            try
            {
                var socket = _client.Client;
                return _client.Connected
                    && !(socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0);
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            catch (SocketException)
            {
                return false;
            }
        }
    }

    public void Dispose()
    {
        _stream.Dispose();
        _client.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync().ConfigureAwait(false);
        _client.Dispose();
    }
}
