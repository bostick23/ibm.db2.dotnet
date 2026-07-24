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

        using var timeoutSource = settings.ConnectTimeout == Timeout.InfiniteTimeSpan
            || settings.ConnectTimeout == TimeSpan.Zero
            ? null
            : new CancellationTokenSource(settings.ConnectTimeout);
        using var linkedSource = timeoutSource is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        var effectiveToken = linkedSource?.Token ?? cancellationToken;

        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(settings.Server, settings.Port, effectiveToken).ConfigureAwait(false);
            Stream stream = client.GetStream();

            if (settings.UseSsl)
            {
                var sslStream = new SslStream(stream, leaveInnerStreamOpen: false);
                await sslStream.AuthenticateAsClientAsync(
                        new SslClientAuthenticationOptions { TargetHost = settings.Server },
                        effectiveToken)
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
