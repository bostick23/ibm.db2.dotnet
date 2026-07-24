using System.Collections;
using System.Data.Common;
using System.Globalization;

namespace Db2i;

/// <summary>Builds and parses connection strings understood by <see cref="Db2iConnection"/>.</summary>
public sealed class Db2iConnectionStringBuilder : DbConnectionStringBuilder
{
    public const int DefaultDatabasePort = 8471;
    public const int DefaultSecureDatabasePort = 9471;

    private static readonly string[] ServerAliases = ["Server", "Data Source", "Host"];
    private static readonly string[] UserAliases = ["User ID", "UserID", "UID", "User"];
    private static readonly string[] PasswordAliases = ["Password", "PWD"];
    private static readonly string[] DatabaseAliases = ["Database", "Initial Catalog"];
    private static readonly string[] DefaultCollectionAliases = ["Default Collection", "Current Schema"];
    private static readonly string[] SslAliases = ["SSL", "Use SSL", "Secure"];
    private static readonly string[] TimeoutAliases = ["Connect Timeout", "Connection Timeout"];

    public Db2iConnectionStringBuilder()
    {
    }

    public Db2iConnectionStringBuilder(string connectionString)
    {
        ConnectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    }

    public string Server
    {
        get => GetString(ServerAliases);
        set => SetCanonical("Server", value, ServerAliases);
    }

    public string UserId
    {
        get => GetString(UserAliases);
        set => SetCanonical("User ID", value, UserAliases);
    }

    public string Password
    {
        get => GetString(PasswordAliases);
        set => SetCanonical("Password", value, PasswordAliases);
    }

    /// <summary>The relational database name. An empty value lets the server select its default RDB.</summary>
    public string Database
    {
        get => GetString(DatabaseAliases);
        set => SetCanonical("Database", value, DatabaseAliases);
    }

    public string DefaultCollection
    {
        get => GetString(DefaultCollectionAliases);
        set => SetCanonical("Default Collection", value, DefaultCollectionAliases);
    }

    public bool UseSsl
    {
        get => GetBoolean(false, SslAliases);
        set => SetCanonical("SSL", value, SslAliases);
    }

    /// <summary>The database host-server port. Defaults to 8471, or 9471 when SSL is enabled.</summary>
    public int Port
    {
        get => ContainsKey("Port")
            ? GetInt32("Port", 1, ushort.MaxValue)
            : (UseSsl ? DefaultSecureDatabasePort : DefaultDatabasePort);
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, ushort.MaxValue);
            this["Port"] = value;
        }
    }

    public int ConnectTimeout
    {
        get => GetInt32(TimeoutAliases, defaultValue: 15, minimum: 0, maximum: int.MaxValue);
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            SetCanonical("Connect Timeout", value, TimeoutAliases);
        }
    }

    internal Db2iConnectionSettings BuildSettings()
    {
        if (string.IsNullOrWhiteSpace(Server))
        {
            throw new ArgumentException("La connection string deve contenere 'Server'.");
        }

        if (string.IsNullOrWhiteSpace(UserId))
        {
            throw new ArgumentException("La connection string deve contenere 'User ID'.");
        }

        return new Db2iConnectionSettings(
            Server.Trim(),
            UserId.Trim(),
            Password,
            Database.Trim(),
            DefaultCollection.Trim(),
            Port,
            UseSsl,
            TimeSpan.FromSeconds(ConnectTimeout));
    }

    private string GetString(params string[] aliases)
    {
        foreach (var alias in aliases)
        {
            if (TryGetValue(alias, out var value))
            {
                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private bool GetBoolean(bool defaultValue, params string[] aliases)
    {
        foreach (var alias in aliases)
        {
            if (!TryGetValue(alias, out var value))
            {
                continue;
            }

            if (value is bool boolean)
            {
                return boolean;
            }

            var text = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (bool.TryParse(text, out var result))
            {
                return result;
            }

            if (string.Equals(text, "yes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "on", StringComparison.OrdinalIgnoreCase)
                || text == "1")
            {
                return true;
            }

            if (string.Equals(text, "no", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "off", StringComparison.OrdinalIgnoreCase)
                || text == "0")
            {
                return false;
            }

            throw new ArgumentException($"Il valore '{text}' di '{alias}' non è booleano.");
        }

        return defaultValue;
    }

    private int GetInt32(string alias, int minimum, int maximum)
        => GetInt32([alias], defaultValue: 0, minimum, maximum);

    private int GetInt32(string[] aliases, int defaultValue, int minimum, int maximum)
    {
        foreach (var alias in aliases)
        {
            if (!TryGetValue(alias, out var value))
            {
                continue;
            }

            if (!int.TryParse(
                    Convert.ToString(value, CultureInfo.InvariantCulture),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var result)
                || result < minimum
                || result > maximum)
            {
                throw new ArgumentOutOfRangeException(alias, value, $"Il valore deve essere compreso tra {minimum} e {maximum}.");
            }

            return result;
        }

        return defaultValue;
    }

    private void SetCanonical(string canonicalName, object? value, IEnumerable aliases)
    {
        foreach (string alias in aliases)
        {
            Remove(alias);
        }

        if (value is string text && string.IsNullOrEmpty(text))
        {
            return;
        }

        this[canonicalName] = value ?? string.Empty;
    }
}

internal sealed record Db2iConnectionSettings(
    string Server,
    string UserId,
    string Password,
    string Database,
    string DefaultCollection,
    int Port,
    bool UseSsl,
    TimeSpan ConnectTimeout);
