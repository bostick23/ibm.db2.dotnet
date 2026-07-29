using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace Db2i;

/// <summary>Represents a positional parameter in a Db2 for IBM i command.</summary>
public sealed class Db2iParameter : DbParameter
{
    private object? _value;
    private DbType _dbType = DbType.String;
    private bool _dbTypeWasSet;

    public Db2iParameter()
    {
    }

    public Db2iParameter(string parameterName, object? value)
    {
        ParameterName = parameterName;
        Value = value;
    }

    public override DbType DbType
    {
        get => _dbTypeWasSet ? _dbType : InferDbType(_value);
        set
        {
            _dbType = value;
            _dbTypeWasSet = true;
        }
    }

    public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;

    public override bool IsNullable { get; set; }

    [AllowNull]
    public override string ParameterName { get; set; } = string.Empty;

    [AllowNull]
    public override string SourceColumn { get; set; } = string.Empty;

    public override object? Value
    {
        get => _value;
        set => _value = value;
    }

    public override bool SourceColumnNullMapping { get; set; }

    public override int Size { get; set; }

    public override byte Precision { get; set; }

    public override byte Scale { get; set; }

    internal bool DbTypeWasSet => _dbTypeWasSet;

    public override void ResetDbType()
    {
        _dbType = DbType.String;
        _dbTypeWasSet = false;
    }

    private static DbType InferDbType(object? value) => value switch
    {
        null or DBNull => DbType.String,
        string => DbType.String,
        char => DbType.StringFixedLength,
        bool => DbType.Boolean,
        byte => DbType.Byte,
        sbyte => DbType.SByte,
        short => DbType.Int16,
        ushort => DbType.UInt16,
        int => DbType.Int32,
        uint => DbType.UInt32,
        long => DbType.Int64,
        ulong => DbType.UInt64,
        float => DbType.Single,
        double => DbType.Double,
        decimal => DbType.Decimal,
        DateTime => DbType.DateTime,
        DateOnly => DbType.Date,
        TimeOnly or TimeSpan => DbType.Time,
        Guid => DbType.Guid,
        byte[] => DbType.Binary,
        _ => DbType.Object,
    };
}
