namespace Db2i.Protocol;

internal sealed partial class Db2iSession
{
    private bool _lobCleanupUncertain;

    private Db2iResultBlock ParseOwnedResultBlock(DatabaseReply reply, Db2iDataFormat format)
    {
        try
        {
            return SqlProtocol.ParseResultBlock(reply, format);
        }
        catch when (format.Fields.Any(field => field.IsLobLocator))
        {
            // An unreadable block may contain locator handles we could not decode
            // and therefore cannot release individually before returning to a pool.
            _lobCleanupUncertain = true;
            throw;
        }
    }

    internal async ValueTask<Db2iLobChunk> ReadLobAsync(
        ushort statementHandle, Db2iLobValue value, long offset, int length,
        CancellationToken cancellationToken)
    {
        EnsureLobStatementHandle(statementHandle);
        await SendCancelableRequestAsync(
            SqlProtocol.CreateReadLobRequest(statementHandle, value, offset, length),
            cancellationToken).ConfigureAwait(false);
        var reply = await ReceiveCancelableReplyAsync(statementHandle, cancellationToken)
            .ConfigureAwait(false);
        EnsureSqlSuccessful(reply, SqlProtocol.RetrieveLobData, "read LOB", allowEndOfData: false);
        return SqlProtocol.ParseLobChunk(reply, value, offset, length);
    }

    internal async ValueTask WriteLobAsync(
        Db2iPreparedStatement statement, Db2iFieldDescriptor field, long offset,
        ReadOnlyMemory<byte> data, bool truncate, CancellationToken cancellationToken)
    {
        EnsureOwnedStatement(statement);
        if (statement.IsInvalidated || !field.IsLobLocator
            || statement.ServerParameterFormat?.Fields.Any(
                candidate => candidate.LobLocator == field.LobLocator && candidate.IsLob) != true)
        {
            throw new InvalidOperationException("Il locator LOB non appartiene allo statement preparato attivo.");
        }

        try
        {
            await SendCancelableRequestAsync(
                SqlProtocol.CreateWriteLobRequest(statement.Handle, field, offset, data.Span, truncate),
                cancellationToken).ConfigureAwait(false);
            var reply = await ReceiveCancelableReplyAsync(statement.Handle, cancellationToken)
                .ConfigureAwait(false);
            EnsureSqlSuccessful(reply, SqlProtocol.WriteLobData, "write LOB", allowEndOfData: false);
        }
        catch
        {
            statement.InvalidateLobPreparation();
            throw;
        }
    }

    internal async ValueTask FreeLobAsync(
        ushort statementHandle, int handle, CancellationToken cancellationToken)
    {
        EnsureLobStatementHandle(statementHandle);
        try
        {
            await SendCancelableRequestAsync(SqlProtocol.CreateFreeLobRequest(statementHandle, handle),
                cancellationToken).ConfigureAwait(false);
            var reply = await ReceiveCancelableReplyAsync(statementHandle, cancellationToken)
                .ConfigureAwait(false);
            // JTOpen JDLobLocator.free documents this specific already-freed result.
            if (reply.ReturnFunctionId == SqlProtocol.FreeLob && reply.ErrorClass == 7 && reply.ReturnCode == -401)
            {
                return;
            }

            EnsureSqlSuccessful(reply, SqlProtocol.FreeLob, "free LOB locator", allowEndOfData: false);
        }
        catch
        {
            _lobCleanupUncertain = true;
            throw;
        }
    }

    private void EnsureLobStatementHandle(ushort handle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_statementLock)
        {
            if (!_statements.Any(statement => statement.Handle == handle && !statement.IsDeleted))
            {
                throw new InvalidOperationException("Il locator LOB appartiene a uno statement chiuso o a un'altra sessione.");
            }
        }
    }

    private async ValueTask ReleaseStatementLobsAsync(
        Db2iPreparedStatement statement, CancellationToken cancellationToken)
    {
        if (statement.LobLocatorsReleased || statement.ServerParameterFormat is not { } format)
        {
            return;
        }

        foreach (var handle in format.Fields.Where(field => field.IsLob && field.LobLocator > 0)
                     .Select(field => field.LobLocator).Distinct())
        {
            await FreeLobAsync(statement.Handle, handle, cancellationToken).ConfigureAwait(false);
        }

        statement.LobLocatorsReleased = true;
    }

    private async ValueTask InvalidateLobStatementsAsync(CancellationToken cancellationToken)
    {
        Db2iPreparedStatement[] statements;
        lock (_statementLock)
        {
            statements = _statements.Where(statement =>
                statement.ServerParameterFormat?.Fields.Any(field => field.IsLob) == true).ToArray();
        }

        foreach (var statement in statements)
        {
            statement.InvalidateLobPreparation();
            await ReleaseStatementLobsAsync(statement, cancellationToken).ConfigureAwait(false);
        }
    }
}
