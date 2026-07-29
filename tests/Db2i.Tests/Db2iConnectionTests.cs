using System.Data;

namespace Db2i.Tests;

public sealed class Db2iConnectionTests
{
    [Fact]
    public async Task OpenNegotiatesACompleteDatabaseHostServerSession()
    {
        await using var server = new FakeIbmIHostServer(expectDefaultCollection: true);
        using var connection = CreateConnection(
            server,
            "Database=MYRDB;Default Collection=APPDATA");
        var stateChanges = new List<(ConnectionState Original, ConnectionState Current)>();
        connection.StateChange += (_, eventArgs) =>
            stateChanges.Add((eventArgs.OriginalState, eventArgs.CurrentState));

        await connection.OpenAsync(CancellationToken.None);

        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Equal("MYRDB", connection.Database);
        Assert.Equal("7.3.0", connection.ServerVersion);
        Assert.Equal(37, connection.ServerCcsid);
        Assert.Equal("V7R3M00", connection.ServerFunctionalLevel);
        Assert.Equal("123456/TESTUSER/QZDASOINIT", connection.ServerJobIdentifier);
        Assert.Equal(0x03, server.PasswordIndicator);
        Assert.Equal("APPDATA", server.RequestedDefaultCollection);

        await connection.CloseAsync();

        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.Equal(
            [
                (ConnectionState.Closed, ConnectionState.Open),
                (ConnectionState.Open, ConnectionState.Closed),
            ],
            stateChanges);
    }

    [Fact]
    public async Task AuthenticationFailurePreservesTheHostReturnCode()
    {
        const int passwordIncorrect = 0x0003000B;
        await using var server = new FakeIbmIHostServer(startReturnCode: passwordIncorrect);
        using var connection = CreateConnection(server);

        var exception = await Assert.ThrowsAsync<Db2iException>(
            () => connection.OpenAsync(CancellationToken.None));

        Assert.Equal(Db2iErrorKind.Authentication, exception.Kind);
        Assert.Equal(passwordIncorrect, exception.HostReturnCode);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task TlsRejectsAnUntrustedCertificateByDefault()
    {
        await using var server = new FakeIbmIHostServer(
            useTls: true,
            allowTlsAuthenticationFailure: true);
        using var connection = CreateConnection(server, "SSL=true");

        var exception = await Assert.ThrowsAsync<Db2iException>(
            () => connection.OpenAsync(CancellationToken.None));

        Assert.Equal(Db2iErrorKind.Tls, exception.Kind);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task TlsCanExplicitlyTrustTheServerCertificate()
    {
        await using var server = new FakeIbmIHostServer(useTls: true);
        using var connection = CreateConnection(
            server,
            "SSL=true;Trust Server Certificate=true");

        await connection.OpenAsync(CancellationToken.None);

        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Equal(37, connection.ServerCcsid);
    }

    [Fact]
    public async Task CallerCancellationStopsTheWholeOpenSequence()
    {
        await using var server = new FakeIbmIHostServer(stallAfterAccept: true);
        using var connection = CreateConnection(server, "Connect Timeout=0");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => connection.OpenAsync(cancellation.Token));

        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task ConnectTimeoutAppliesToTheWholeOpenSequence()
    {
        await using var server = new FakeIbmIHostServer(stallAfterAccept: true);
        using var connection = CreateConnection(server, "Connect Timeout=1");

        var exception = await Assert.ThrowsAsync<Db2iException>(
            () => connection.OpenAsync(CancellationToken.None));

        Assert.Equal(Db2iErrorKind.Timeout, exception.Kind);
        Assert.True(exception.IsTransient);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task CloseCancelsAnOpenInProgressAndLeavesTheConnectionReusable()
    {
        await using var server = new FakeIbmIHostServer(stallAfterAccept: true);
        using var connection = CreateConnection(server, "Connect Timeout=0");

        var openTask = connection.OpenAsync(CancellationToken.None);
        Assert.Equal(ConnectionState.Connecting, connection.State);

        connection.Close();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => openTask);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task SynchronousOpenUsesTheSameHandshake()
    {
        await using var server = new FakeIbmIHostServer();
        using var connection = CreateConnection(server);

        connection.Open();

        Assert.Equal(ConnectionState.Open, connection.State);
        connection.Close();
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    private static Db2iConnection CreateConnection(
        FakeIbmIHostServer server,
        string additionalSettings = "")
        => new(
            $"Server=127.0.0.1;Port={server.Port};User ID=TESTUSER;Password=PASS123;" +
            additionalSettings);
}
