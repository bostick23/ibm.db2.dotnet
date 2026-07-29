using System.Data.Common;

namespace Db2i;

/// <summary>Identifies the subsystem that produced a provider error.</summary>
public enum Db2iErrorKind
{
    Unknown,
    Connection,
    Tls,
    Timeout,
    Authentication,
    Protocol,
    Server,
    Sql,
    UnsupportedCcsid,
}

/// <summary>Represents an error reported by the provider or by Db2 for IBM i.</summary>
public sealed class Db2iException : DbException
{
    internal Db2iException(
        string message,
        Db2iErrorKind kind = Db2iErrorKind.Unknown,
        Exception? innerException = null,
        int? hostReturnCode = null,
        short? errorClass = null,
        int? sqlCode = null,
        string? sqlState = null,
        bool isTransient = false)
        : base(message, innerException)
    {
        Kind = kind;
        HostReturnCode = hostReturnCode;
        ErrorClass = errorClass;
        SqlCode = sqlCode;
        SqlStateValue = sqlState;
        IsTransientValue = isTransient;
    }

    /// <summary>The provider subsystem that detected the error.</summary>
    public Db2iErrorKind Kind { get; }

    /// <summary>The raw return code from an IBM i host-server reply, when available.</summary>
    public int? HostReturnCode { get; }

    /// <summary>The database host-server error class, when available.</summary>
    public short? ErrorClass { get; }

    /// <summary>The SQLCODE returned by Db2, when available.</summary>
    public int? SqlCode { get; }

    public override string? SqlState => SqlStateValue;

    public override bool IsTransient => IsTransientValue;

    private string? SqlStateValue { get; }

    private bool IsTransientValue { get; }
}
