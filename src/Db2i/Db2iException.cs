using System.Data.Common;

namespace Db2i;

/// <summary>Represents an error reported by the provider or by Db2 for IBM i.</summary>
public sealed class Db2iException : DbException
{
    internal Db2iException(string message)
        : base(message)
    {
    }

    internal Db2iException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
