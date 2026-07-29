using System.Data;
using System.Data.Common;

namespace Db2i;

/// <summary>Represents a transaction on a Db2 for IBM i connection.</summary>
public sealed class Db2iTransaction : DbTransaction
{
    private readonly object _syncRoot = new();
    private Db2iConnection? _connection;
    private TransactionState _state = TransactionState.Active;

    internal Db2iTransaction(Db2iConnection connection, IsolationLevel isolationLevel)
    {
        _connection = connection;
        IsolationLevel = isolationLevel;
    }

    public override IsolationLevel IsolationLevel { get; }

    public new Db2iConnection? Connection
    {
        get
        {
            lock (_syncRoot)
            {
                return _connection;
            }
        }
    }

    protected override DbConnection? DbConnection => Connection;

    public override void Commit()
        => CommitAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override Task CommitAsync(CancellationToken cancellationToken = default)
        => CompleteAsync(commit: true, cancellationToken);

    public override void Rollback()
        => RollbackAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override Task RollbackAsync(CancellationToken cancellationToken = default)
        => CompleteAsync(commit: false, cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            RollbackOnDispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (TryBeginCompletion(out var connection))
        {
            try
            {
                await connection.CompleteTransactionAsync(
                        this,
                        commit: false,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                MarkCompleted();
            }
            catch
            {
                await connection.AbortCommandAsync().ConfigureAwait(false);
                MarkCompleted();
            }
        }

        await base.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    internal bool BelongsTo(Db2iConnection connection)
    {
        lock (_syncRoot)
        {
            return ReferenceEquals(_connection, connection);
        }
    }

    internal bool IsUsable
    {
        get
        {
            lock (_syncRoot)
            {
                return _state == TransactionState.Active;
            }
        }
    }

    internal void ConnectionClosed()
    {
        lock (_syncRoot)
        {
            _connection = null;
            _state = TransactionState.Completed;
        }
    }

    private async Task CompleteAsync(bool commit, CancellationToken cancellationToken)
    {
        if (!TryBeginCompletion(out var connection))
        {
            throw new InvalidOperationException("La transazione è già stata completata.");
        }

        try
        {
            await connection.CompleteTransactionAsync(this, commit, cancellationToken)
                .ConfigureAwait(false);
            MarkCompleted();
        }
        catch
        {
            RestoreActiveIfConnected(connection);
            throw;
        }
    }

    private bool TryBeginCompletion(
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Db2iConnection? connection)
    {
        lock (_syncRoot)
        {
            if (_state != TransactionState.Active || _connection is null)
            {
                connection = null;
                return false;
            }

            _state = TransactionState.Completing;
            connection = _connection;
            return true;
        }
    }

    private void RestoreActiveIfConnected(Db2iConnection connection)
    {
        lock (_syncRoot)
        {
            if (_state == TransactionState.Completing
                && ReferenceEquals(_connection, connection))
            {
                _state = TransactionState.Active;
            }
        }
    }

    private void MarkCompleted()
    {
        lock (_syncRoot)
        {
            _state = TransactionState.Completed;
            _connection = null;
        }
    }

    private void RollbackOnDispose()
    {
        if (!TryBeginCompletion(out var connection))
        {
            return;
        }

        try
        {
            connection.CompleteTransactionAsync(
                    this,
                    commit: false,
                    CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();
        }
        catch
        {
            connection.Close();
        }
        finally
        {
            MarkCompleted();
        }
    }

    private enum TransactionState
    {
        Active,
        Completing,
        Completed,
    }
}
