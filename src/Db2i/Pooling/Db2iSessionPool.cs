using System.Collections.Concurrent;
using Db2i.Protocol;

namespace Db2i;

internal static class Db2iSessionPoolManager
{
    private static readonly ConcurrentDictionary<Db2iConnectionPoolKey, Db2iSessionPool> Pools = new();

    internal static Db2iSessionPool GetPool(Db2iConnectionSettings settings)
        => Pools.GetOrAdd(
            settings.PoolKey,
            static (_, value) => new Db2iSessionPool(value),
            settings);

    internal static Db2iSessionPool? TryGetPool(Db2iConnectionSettings settings)
        => Pools.TryGetValue(settings.PoolKey, out var pool) ? pool : null;

    internal static void ClearAll()
    {
        foreach (var pool in Pools.Values)
        {
            pool.Clear();
        }
    }
}

internal sealed class Db2iSessionPool : IDisposable, IAsyncDisposable
{
    private readonly object _syncRoot = new();
    private readonly Db2iConnectionSettings _settings;
    private readonly SemaphoreSlim _leaseGate;
    private readonly Stack<IdleSession> _idleSessions = [];
    private long _generation;
    private bool _disposed;

    internal Db2iSessionPool(Db2iConnectionSettings settings)
    {
        _settings = settings;
        _leaseGate = new SemaphoreSlim(settings.MaxPoolSize, settings.MaxPoolSize);
    }

    internal async ValueTask<Db2iConnectionLease> RentAsync(CancellationToken cancellationToken)
    {
        await _leaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                Db2iSession? session = null;
                long generation;
                lock (_syncRoot)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    generation = _generation;
                    while (_idleSessions.Count > 0)
                    {
                        var idle = _idleSessions.Pop();
                        if (idle.Generation == generation)
                        {
                            session = idle.Session;
                            break;
                        }

                        idle.Session.Dispose();
                    }
                }

                if (session is null)
                {
                    session = await Db2iSession.OpenAsync(_settings, cancellationToken)
                        .ConfigureAwait(false);
                    lock (_syncRoot)
                    {
                        if (_disposed)
                        {
                            session.Dispose();
                            throw new ObjectDisposedException(nameof(Db2iDataSource));
                        }

                        generation = _generation;
                    }

                    return new Db2iConnectionLease(session, this, generation);
                }

                if (session.IsPotentiallyUsable)
                {
                    return new Db2iConnectionLease(session, this, generation);
                }

                await session.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            _leaseGate.Release();
            throw;
        }
    }

    internal async ValueTask ReturnAsync(
        Db2iSession session,
        long generation,
        bool rollbackRequired)
    {
        var reusable = false;
        try
        {
            lock (_syncRoot)
            {
                reusable = !_disposed && generation == _generation;
            }

            if (reusable)
            {
                reusable = await session.ResetForPoolingAsync(rollbackRequired)
                    .ConfigureAwait(false);
            }

            if (reusable)
            {
                lock (_syncRoot)
                {
                    if (!_disposed && generation == _generation)
                    {
                        _idleSessions.Push(new IdleSession(session, generation));
                        session = null!;
                    }
                }
            }

            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _leaseGate.Release();
        }
    }

    internal async ValueTask DiscardAsync(Db2iSession session)
    {
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _leaseGate.Release();
        }
    }

    internal void Clear()
    {
        Db2iSession[] sessions;
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _generation++;
            sessions = _idleSessions.Select(value => value.Session).ToArray();
            _idleSessions.Clear();
        }

        foreach (var session in sessions)
        {
            session.Dispose();
        }
    }

    public void Dispose()
    {
        Db2iSession[] sessions;
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _generation++;
            sessions = _idleSessions.Select(value => value.Session).ToArray();
            _idleSessions.Clear();
        }

        foreach (var session in sessions)
        {
            session.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Db2iSession[] sessions;
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _generation++;
            sessions = _idleSessions.Select(value => value.Session).ToArray();
            _idleSessions.Clear();
        }

        foreach (var session in sessions)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed record IdleSession(Db2iSession Session, long Generation);
}

internal sealed class Db2iConnectionLease
{
    private Db2iSession? _session;
    private readonly Db2iSessionPool? _pool;
    private readonly long _generation;

    internal Db2iConnectionLease(
        Db2iSession session,
        Db2iSessionPool? pool,
        long generation)
    {
        _session = session;
        _pool = pool;
        _generation = generation;
    }

    internal Db2iSession Session
        => _session ?? throw new ObjectDisposedException(nameof(Db2iConnectionLease));

    internal Db2iSessionPool? Pool => _pool;

    internal static Db2iConnectionLease CreateUnpooled(Db2iSession session)
        => new(session, pool: null, generation: 0);

    internal async ValueTask ReturnAsync(bool rollbackRequired)
    {
        var session = Interlocked.Exchange(ref _session, null);
        if (session is null)
        {
            return;
        }

        if (_pool is null)
        {
            await session.DisposeAsync().ConfigureAwait(false);
            return;
        }

        await _pool.ReturnAsync(session, _generation, rollbackRequired).ConfigureAwait(false);
    }

    internal async ValueTask DiscardAsync()
    {
        var session = Interlocked.Exchange(ref _session, null);
        if (session is null)
        {
            return;
        }

        if (_pool is null)
        {
            await session.DisposeAsync().ConfigureAwait(false);
            return;
        }

        await _pool.DiscardAsync(session).ConfigureAwait(false);
    }
}
