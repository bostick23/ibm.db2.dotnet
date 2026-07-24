using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace Db2i;

/// <summary>Represents a connection to the database host server on IBM i.</summary>
public sealed class Db2iConnection : DbConnection
{
    private string _connectionString = string.Empty;
    private ConnectionState _state = ConnectionState.Closed;
    private bool _disposed;

    public Db2iConnection()
    {
    }

    public Db2iConnection(string connectionString)
    {
        ConnectionString = connectionString;
    }

    [AllowNull]
    public override string ConnectionString
    {
        get => _connectionString;
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state != ConnectionState.Closed)
            {
                throw new InvalidOperationException("La connection string non può essere modificata mentre la connessione è aperta.");
            }

            _connectionString = value ?? string.Empty;
        }
    }

    public override string Database => GetSettingsOrDefault()?.Database ?? string.Empty;

    public override string DataSource => GetSettingsOrDefault()?.Server ?? string.Empty;

    public override string ServerVersion => string.Empty;

    public override ConnectionState State => _state;

    public override int ConnectionTimeout => GetSettingsOrDefault() is { } settings
        ? checked((int)settings.ConnectTimeout.TotalSeconds)
        : 15;

    public override void ChangeDatabase(string databaseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        EnsureOpen();
        throw FeatureNotImplemented("ChangeDatabase");
    }

    public override void Close()
    {
        if (_state == ConnectionState.Closed)
        {
            return;
        }

        var previous = _state;
        _state = ConnectionState.Closed;
        OnStateChange(new StateChangeEventArgs(previous, _state));
    }

    public override void Open()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _ = new Db2iConnectionStringBuilder(_connectionString).BuildSettings();
        throw FeatureNotImplemented("handshake e autenticazione IBM i");
    }

    public override Task OpenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Open();
        return Task.CompletedTask;
    }

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
    {
        EnsureOpen();
        throw FeatureNotImplemented("transazioni");
    }

    protected override DbCommand CreateDbCommand() => new Db2iCommand { Connection = this };

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            Close();
            _disposed = true;
        }

        base.Dispose(disposing);
    }

    internal void EnsureOpen()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_state != ConnectionState.Open)
        {
            throw new InvalidOperationException("La connessione non è aperta.");
        }
    }

    internal static Db2iException FeatureNotImplemented(string feature)
        => new($"La funzionalità '{feature}' non è ancora implementata nel primo scaffold del provider.");

    private Db2iConnectionSettings? GetSettingsOrDefault()
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            return null;
        }

        try
        {
            return new Db2iConnectionStringBuilder(_connectionString).BuildSettings();
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
