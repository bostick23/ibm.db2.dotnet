namespace Db2i.Protocol;

internal static class Db2iErrorFactory
{
    internal static Db2iException FromHostReturnCode(int returnCode, string operation)
    {
        var (kind, message) = returnCode switch
        {
            0x00010001 => (Db2iErrorKind.Protocol, "The IBM i request data is invalid."),
            0x00010002 => (Db2iErrorKind.Protocol, "The IBM i server ID is invalid."),
            0x00010003 => (Db2iErrorKind.Protocol, "The IBM i request ID is invalid."),
            0x00010004 => (Db2iErrorKind.Protocol, "The IBM i random seed is invalid."),
            0x00010005 => (Db2iErrorKind.Protocol, "IBM i requires a random seed for password substitution."),
            0x00010006 => (Db2iErrorKind.Protocol, "The IBM i password encryption indicator is invalid."),
            0x00010007 => (Db2iErrorKind.Authentication, "The IBM i user profile length is invalid."),
            0x00010008 => (Db2iErrorKind.Authentication, "The IBM i password length is invalid."),
            0x00020001 => (Db2iErrorKind.Authentication, "The IBM i user profile is unknown."),
            0x00020002 => (Db2iErrorKind.Authentication, "The IBM i user profile is disabled."),
            0x00020003 => (Db2iErrorKind.Authentication, "The IBM i user profile does not match."),
            0x0003000B => (Db2iErrorKind.Authentication, "The IBM i password is incorrect."),
            0x0003000C => (Db2iErrorKind.Authentication, "The IBM i user profile will be disabled after another invalid password."),
            0x0003000D => (Db2iErrorKind.Authentication, "The IBM i password is expired."),
            0x00030010 => (Db2iErrorKind.Authentication, "The IBM i profile password is *NONE."),
            0x00040000 => (Db2iErrorKind.Server, "IBM i reported a general security error."),
            >= 0x00040001 and <= 0x00040014 => (Db2iErrorKind.Server, "IBM i could not start the database server job."),
            _ => (Db2iErrorKind.Server, "IBM i rejected the request."),
        };

        return new Db2iException(
            $"{message} Operation: {operation}; return code: 0x{returnCode:X8}.",
            kind,
            hostReturnCode: returnCode,
            isTransient: kind is Db2iErrorKind.Connection or Db2iErrorKind.Server);
    }

    internal static Db2iException FromDatabaseReply(DatabaseReply reply, string operation)
        => new(
            $"IBM i database operation '{operation}' failed with error class {reply.ErrorClass} " +
            $"and return code {reply.ReturnCode}.",
            Db2iErrorKind.Server,
            hostReturnCode: reply.ReturnCode,
            errorClass: reply.ErrorClass);

    internal static Db2iException FromDatabaseReply(
        DatabaseReply reply,
        string operation,
        Db2iSqlDiagnostic diagnostic)
        => new(
            diagnostic.BuildMessage(operation, reply.ErrorClass, reply.ReturnCode),
            Db2iErrorKind.Server,
            hostReturnCode: reply.ReturnCode,
            errorClass: reply.ErrorClass,
            sqlCode: diagnostic.SqlCode,
            sqlState: diagnostic.SqlState);
}
