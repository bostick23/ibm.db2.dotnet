using System.Data.Common;

namespace Db2i;

/// <summary>Represents an immutable IBM i data source with an owned session pool.</summary>
public sealed class Db2iDataSource : DbDataSource
{
    private readonly string _connectionString;
    private readonly Db2iSessionPool? _pool;
    private readonly Db2iDataSourceLifetime _lifetime = new();

    public Db2iDataSource(string connectionString)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        var settings = new Db2iConnectionStringBuilder(connectionString).BuildSettings();
        _connectionString = connectionString;
        _pool = settings.Pooling ? new Db2iSessionPool(settings) : null;
    }

    public override string ConnectionString => _connectionString;

    public new Db2iConnection CreateConnection()
        => CreateDbConnection();

    public new Db2iConnection OpenConnection()
    {
        var connection = CreateDbConnection();
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public new async ValueTask<Db2iConnection> OpenConnectionAsync(
        CancellationToken cancellationToken = default)
    {
        var connection = CreateDbConnection();
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    protected override Db2iConnection CreateDbConnection()
    {
        ObjectDisposedException.ThrowIf(_lifetime.IsDisposed, this);
        return new Db2iConnection(_connectionString, _pool, _lifetime);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _lifetime.TryDispose())
        {
            _pool?.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        if (_lifetime.TryDispose() && _pool is not null)
        {
            await _pool.DisposeAsync().ConfigureAwait(false);
        }

        await base.DisposeAsyncCore().ConfigureAwait(false);
    }
}

internal sealed class Db2iDataSourceLifetime
{
    private int _disposed;

    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    internal bool TryDispose()
        => Interlocked.Exchange(ref _disposed, 1) == 0;
}
