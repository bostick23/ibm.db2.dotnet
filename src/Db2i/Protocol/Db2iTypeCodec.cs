using System.Buffers.Binary;
using System.Data;
using System.Globalization;

namespace Db2i.Protocol;

// Native type identifiers and byte conversions follow JTOpen SQLDataFactory
// and its SQLDate/SQLTime/SQLTimestamp/SQLDecimal/SQL* scalar implementations.
internal static class Db2iTypeCodec
{
    internal static Db2iFieldDescriptor ResolveParameter(
        Db2iParameter parameter,
        Db2iFieldDescriptor serverField,
        int serverCcsid,
        int offset)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        if (parameter.Direction != ParameterDirection.Input)
        {
            throw new NotSupportedException(
                "M2 supporta soltanto parametri con Direction=Input.");
        }

        var value = parameter.Value;
        if ((value is null or DBNull) && !parameter.DbTypeWasSet)
        {
            return serverField with { Name = string.Empty, Offset = offset };
        }

        var dbType = parameter.DbType;
        var descriptor = dbType switch
        {
            DbType.Int16 or DbType.Byte or DbType.SByte
                => Create(500, 2, 0, 5),
            DbType.Int32 or DbType.UInt16
                => Create(496, 4, 0, 10),
            DbType.Int64 or DbType.UInt32
                => Create(492, 8, 0, 19),
            DbType.Decimal or DbType.Currency or DbType.VarNumeric
                => CreateDecimal(parameter, value),
            DbType.Single
                => Create(480, 4, 0, 24),
            DbType.Double
                => Create(480, 8, 0, 53),
            DbType.StringFixedLength
                => CreateCharacter(parameter, value, serverCcsid, fixedLength: true),
            DbType.String or DbType.AnsiString or DbType.AnsiStringFixedLength
                => CreateCharacter(
                    parameter,
                    value,
                    serverCcsid,
                    fixedLength: dbType == DbType.AnsiStringFixedLength),
            DbType.Date
                => Create(384, 10, 0, 10),
            DbType.Time
                => Create(388, 8, 0, 8),
            DbType.DateTime or DbType.DateTime2
                => CreateTimestamp(parameter),
            DbType.Binary
                => CreateBinary(parameter, value),
            _ => throw new NotSupportedException(
                $"DbType.{dbType} non è supportato dal percorso query M2."),
        };

        return descriptor with
        {
            SqlType = descriptor.NativeType | 1,
            Ccsid = descriptor.NativeType is 448 or 452 or 384 or 388 or 392
                ? serverCcsid
                : descriptor.Ccsid,
            Name = string.Empty,
            Offset = offset,
        };
    }

    internal static byte[] EncodeParameterRow(
        Db2iDataFormat format,
        IReadOnlyList<Db2iParameter> parameters)
    {
        if (format.Fields.Count != parameters.Count)
        {
            throw new ArgumentException(
                "The parameter descriptor and collection have different counts.",
                nameof(parameters));
        }

        var indicatorBytes = checked(parameters.Count * 2);
        var payload = new byte[checked(20 + indicatorBytes + format.RecordSize)];
        BinaryPrimitives.WriteInt32BigEndian(payload, format.ConsistencyToken);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16BigEndian(
            payload.AsSpan(8),
            checked((ushort)parameters.Count));
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(10), 2);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(16), format.RecordSize);

        var dataOffset = 20 + indicatorBytes;
        for (var index = 0; index < parameters.Count; index++)
        {
            var value = parameters[index].Value;
            if (value is null or DBNull)
            {
                BinaryPrimitives.WriteInt16BigEndian(payload.AsSpan(20 + (index * 2)), -1);
                continue;
            }

            var field = format.Fields[index];
            EncodeValue(
                payload.AsSpan(dataOffset + field.Offset, field.Length),
                field,
                value);
        }

        return payload;
    }

    internal static Db2iResultBlock DecodeResultBlock(
        ReadOnlySpan<byte> payload,
        Db2iDataFormat format)
    {
        if (payload.Length < 20)
        {
            throw ClientAccessCodePoints.ProtocolError(
                "The extended SQL result-data header is truncated.");
        }

        var rowCount = BinaryPrimitives.ReadInt32BigEndian(payload[4..]);
        var columnCount = BinaryPrimitives.ReadUInt16BigEndian(payload[8..]);
        var indicatorSize = BinaryPrimitives.ReadUInt16BigEndian(payload[10..]);
        var rowSize = BinaryPrimitives.ReadInt32BigEndian(payload[16..]);
        if (rowCount < 0
            || columnCount != format.Fields.Count
            || indicatorSize is not (0 or 2)
            || rowSize < format.RecordSize)
        {
            throw ClientAccessCodePoints.ProtocolError(
                "The extended SQL result-data dimensions do not match the descriptor.");
        }

        var indicatorsLength = checked(rowCount * columnCount * indicatorSize);
        var dataLength = checked(rowCount * rowSize);
        if (payload.Length < checked(20 + indicatorsLength + dataLength))
        {
            throw ClientAccessCodePoints.ProtocolError(
                "The extended SQL result data is truncated.");
        }

        var rows = new List<object?[]>(rowCount);
        var dataStart = 20 + indicatorsLength;
        for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            var row = new object?[columnCount];
            var rowStart = dataStart + (rowIndex * rowSize);
            for (var columnIndex = 0; columnIndex < columnCount; columnIndex++)
            {
                var isNull = indicatorSize == 2
                    && BinaryPrimitives.ReadInt16BigEndian(
                        payload.Slice(
                            20 + (((rowIndex * columnCount) + columnIndex) * 2),
                            2)) < 0;
                if (!isNull)
                {
                    var field = format.Fields[columnIndex];
                    row[columnIndex] = DecodeValue(
                        payload.Slice(rowStart + field.Offset, field.Length),
                        field,
                        format);
                }
            }

            rows.Add(row);
        }

        return new Db2iResultBlock(rows);
    }

    private static void EncodeValue(
        Span<byte> destination,
        Db2iFieldDescriptor field,
        object value)
    {
        switch (field.NativeType)
        {
            case 384:
                EncodeText(destination, field.Ccsid, ConvertDate(value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                break;
            case 388:
                var time = ConvertTime(value);
                EncodeText(
                    destination,
                    field.Ccsid,
                    $"{(int)time.TotalHours:00}.{time.Minutes:00}.{time.Seconds:00}");
                break;
            case 392:
                EncodeTimestamp(destination, field.Ccsid, ConvertDateTime(value));
                break;
            case 448:
                EncodeVaryingText(destination, field.Ccsid, Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
                break;
            case 452:
                EncodeFixedText(destination, field.Ccsid, Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
                break;
            case 480 when field.Length == 4:
                BinaryPrimitives.WriteInt32BigEndian(
                    destination,
                    BitConverter.SingleToInt32Bits(Convert.ToSingle(value, CultureInfo.InvariantCulture)));
                break;
            case 480:
                BinaryPrimitives.WriteInt64BigEndian(
                    destination,
                    BitConverter.DoubleToInt64Bits(Convert.ToDouble(value, CultureInfo.InvariantCulture)));
                break;
            case 484:
                EncodePackedDecimal(
                    destination,
                    Convert.ToDecimal(value, CultureInfo.InvariantCulture),
                    field.Precision,
                    field.Scale);
                break;
            case 488:
                EncodeZonedDecimal(
                    destination,
                    Convert.ToDecimal(value, CultureInfo.InvariantCulture),
                    field.Precision,
                    field.Scale);
                break;
            case 492:
                BinaryPrimitives.WriteInt64BigEndian(
                    destination,
                    Convert.ToInt64(value, CultureInfo.InvariantCulture));
                break;
            case 496:
                BinaryPrimitives.WriteInt32BigEndian(
                    destination,
                    Convert.ToInt32(value, CultureInfo.InvariantCulture));
                break;
            case 500:
                BinaryPrimitives.WriteInt16BigEndian(
                    destination,
                    Convert.ToInt16(value, CultureInfo.InvariantCulture));
                break;
            case 908:
                EncodeVaryingBinary(destination, GetBytes(value));
                break;
            case 912:
                EncodeFixedBinary(destination, GetBytes(value));
                break;
            default:
                throw new NotSupportedException(
                    $"IBM i SQL native type {field.NativeType} is not supported for parameters.");
        }
    }

    private static object DecodeValue(
        ReadOnlySpan<byte> source,
        Db2iFieldDescriptor field,
        Db2iDataFormat format)
        => field.NativeType switch
        {
            384 => DecodeDate(source, format.DateFormat),
            388 => DecodeTime(source, format.TimeFormat),
            392 => DecodeTimestamp(source),
            448 => DecodeVaryingText(source, field.Ccsid),
            452 => IbmIEncoding.DecodeExact(field.Ccsid, source),
            480 when field.Length == 4 => BitConverter.Int32BitsToSingle(
                BinaryPrimitives.ReadInt32BigEndian(source)),
            480 => BitConverter.Int64BitsToDouble(
                BinaryPrimitives.ReadInt64BigEndian(source)),
            484 => DecodePackedDecimal(source, field.Precision, field.Scale),
            488 => DecodeZonedDecimal(source, field.Precision, field.Scale),
            492 => BinaryPrimitives.ReadInt64BigEndian(source),
            496 => BinaryPrimitives.ReadInt32BigEndian(source),
            500 => BinaryPrimitives.ReadInt16BigEndian(source),
            908 => DecodeVaryingBinary(source),
            912 => source.ToArray(),
            _ => throw new Db2iException(
                $"IBM i returned unsupported SQL native type {field.NativeType}.",
                Db2iErrorKind.Protocol),
        };

    private static Db2iFieldDescriptor Create(
        int nativeType,
        int length,
        int scale,
        int precision)
        => new(nativeType | 1, length, scale, precision, 0, string.Empty, 0);

    private static Db2iFieldDescriptor CreateDecimal(Db2iParameter parameter, object? value)
    {
        var decimalValue = value is null or DBNull
            ? 0m
            : Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        var inferredScale = GetDecimalScale(decimalValue);
        var scale = parameter.Scale > 0 ? parameter.Scale : inferredScale;
        var inferredPrecision = GetDecimalPrecision(decimalValue);
        var precision = parameter.Precision > 0
            ? parameter.Precision
            : Math.Max(inferredPrecision, scale + 1);
        if (precision is < 1 or > 31 || scale > precision)
        {
            throw new ArgumentOutOfRangeException(
                nameof(parameter),
                "DECIMAL richiede Precision 1..31 e Scale non superiore a Precision.");
        }

        return Create(484, (precision + 2) / 2, scale, precision);
    }

    private static Db2iFieldDescriptor CreateCharacter(
        Db2iParameter parameter,
        object? value,
        int ccsid,
        bool fixedLength)
    {
        var text = value is null or DBNull
            ? string.Empty
            : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        var encodedLength = IbmIEncoding.Encode(ccsid, text).Length;
        var size = parameter.Size > 0 ? parameter.Size : encodedLength;
        if (encodedLength > size)
        {
            throw new ArgumentException(
                $"Il valore del parametro '{parameter.ParameterName}' richiede {encodedLength} byte, " +
                $"oltre Size={size}.",
                nameof(parameter));
        }

        return Create(fixedLength ? 452 : 448, fixedLength ? size : size + 2, 0, size)
            with
        { Ccsid = ccsid };
    }

    private static Db2iFieldDescriptor CreateBinary(Db2iParameter parameter, object? value)
    {
        var bytes = value is null or DBNull ? [] : GetBytes(value);
        var size = parameter.Size > 0 ? parameter.Size : bytes.Length;
        if (bytes.Length > size)
        {
            throw new ArgumentException(
                $"Il valore binario del parametro '{parameter.ParameterName}' richiede {bytes.Length} byte, " +
                $"oltre Size={size}.",
                nameof(parameter));
        }

        return Create(908, size + 2, 0, size);
    }

    private static Db2iFieldDescriptor CreateTimestamp(Db2iParameter parameter)
    {
        var scale = parameter.Scale > 0 ? Math.Min(parameter.Scale, (byte)7) : (byte)6;
        return Create(392, 20 + scale, scale, 26);
    }

    private static void EncodeFixedText(Span<byte> destination, int ccsid, string value)
    {
        var encoded = IbmIEncoding.Encode(ccsid, value);
        if (encoded.Length > destination.Length)
        {
            throw new ArgumentException("Il valore CHAR supera la dimensione dichiarata.");
        }

        var padding = IbmIEncoding.Encode(ccsid, " ");
        if (padding.Length != 1)
        {
            throw new NotSupportedException(
                "M2 supporta CHAR soltanto con CCSID a singolo byte.");
        }

        destination.Fill(padding[0]);
        encoded.CopyTo(destination);
    }

    private static void EncodeVaryingText(Span<byte> destination, int ccsid, string value)
    {
        var encoded = IbmIEncoding.Encode(ccsid, value);
        if (encoded.Length > destination.Length - 2 || encoded.Length > ushort.MaxValue)
        {
            throw new ArgumentException("Il valore VARCHAR supera la dimensione dichiarata.");
        }

        BinaryPrimitives.WriteUInt16BigEndian(destination, (ushort)encoded.Length);
        encoded.CopyTo(destination[2..]);
    }

    private static void EncodeText(Span<byte> destination, int ccsid, string value)
    {
        var encoded = IbmIEncoding.Encode(ccsid, value);
        if (encoded.Length != destination.Length)
        {
            throw new ArgumentException(
                $"Il valore temporale richiede {encoded.Length} byte anziché {destination.Length}.");
        }

        encoded.CopyTo(destination);
    }

    private static void EncodeTimestamp(Span<byte> destination, int ccsid, DateTime value)
    {
        var fractionalDigits = Math.Max(0, destination.Length - 20);
        var baseValue = value.ToString("yyyy-MM-dd-HH.mm.ss", CultureInfo.InvariantCulture);
        var ticks = (value.Ticks % TimeSpan.TicksPerSecond).ToString("D7", CultureInfo.InvariantCulture);
        var fraction = fractionalDigits == 0
            ? string.Empty
            : $".{ticks[..Math.Min(7, fractionalDigits)].PadRight(fractionalDigits, '0')}";
        EncodeText(destination, ccsid, baseValue + fraction);
    }

    private static void EncodeVaryingBinary(Span<byte> destination, byte[] value)
    {
        if (value.Length > destination.Length - 2 || value.Length > ushort.MaxValue)
        {
            throw new ArgumentException("Il valore VARBINARY supera la dimensione dichiarata.");
        }

        BinaryPrimitives.WriteUInt16BigEndian(destination, (ushort)value.Length);
        value.CopyTo(destination[2..]);
    }

    private static void EncodeFixedBinary(Span<byte> destination, byte[] value)
    {
        if (value.Length > destination.Length)
        {
            throw new ArgumentException("Il valore BINARY supera la dimensione dichiarata.");
        }

        value.CopyTo(destination);
    }

    private static string DecodeVaryingText(ReadOnlySpan<byte> source, int ccsid)
    {
        var length = ReadVaryingLength(source);
        return IbmIEncoding.DecodeExact(ccsid, source.Slice(2, length));
    }

    private static byte[] DecodeVaryingBinary(ReadOnlySpan<byte> source)
    {
        var length = ReadVaryingLength(source);
        return source.Slice(2, length).ToArray();
    }

    private static int ReadVaryingLength(ReadOnlySpan<byte> source)
    {
        if (source.Length < 2)
        {
            throw ClientAccessCodePoints.ProtocolError(
                "A varying SQL value is missing its length.");
        }

        var length = BinaryPrimitives.ReadUInt16BigEndian(source);
        if (length > source.Length - 2)
        {
            throw ClientAccessCodePoints.ProtocolError(
                "A varying SQL value has an invalid length.");
        }

        return length;
    }

    private static void EncodePackedDecimal(
        Span<byte> destination,
        decimal value,
        int precision,
        int scale)
    {
        var rounded = decimal.Round(value, scale, MidpointRounding.ToEven);
        if (rounded != value)
        {
            throw new ArgumentException(
                $"Il valore DECIMAL richiede più di {scale} cifre decimali.");
        }

        var absolute = Math.Abs(rounded);
        var digits = absolute
            .ToString($"F{scale}", CultureInfo.InvariantCulture)
            .Replace(".", string.Empty, StringComparison.Ordinal);
        if (digits.Length > precision)
        {
            throw new OverflowException(
                $"Il valore DECIMAL supera la precisione {precision}.");
        }

        digits = digits.PadLeft(precision, '0');
        var nibbles = new List<int>(destination.Length * 2);
        if ((precision & 1) == 0)
        {
            nibbles.Add(0);
        }

        nibbles.AddRange(digits.Select(character => character - '0'));
        nibbles.Add(value < 0 ? 0x0D : 0x0C);
        if (nibbles.Count != destination.Length * 2)
        {
            throw new InvalidOperationException("Invalid packed-decimal descriptor.");
        }

        for (var index = 0; index < destination.Length; index++)
        {
            destination[index] = (byte)((nibbles[index * 2] << 4) | nibbles[(index * 2) + 1]);
        }
    }

    private static decimal DecodePackedDecimal(
        ReadOnlySpan<byte> source,
        int precision,
        int scale)
    {
        decimal result = 0;
        var totalNibbles = source.Length * 2;
        var firstDigitNibble = totalNibbles - 1 - precision;
        for (var digitIndex = 0; digitIndex < precision; digitIndex++)
        {
            var nibbleIndex = firstDigitNibble + digitIndex;
            var value = (nibbleIndex & 1) == 0
                ? (source[nibbleIndex / 2] >> 4) & 0x0F
                : source[nibbleIndex / 2] & 0x0F;
            if (value > 9)
            {
                throw ClientAccessCodePoints.ProtocolError(
                    "IBM i returned an invalid packed-decimal digit.");
            }

            result = checked((result * 10) + value);
        }

        for (var index = 0; index < scale; index++)
        {
            result /= 10;
        }

        var sign = source[^1] & 0x0F;
        return sign is 0x0B or 0x0D ? -result : result;
    }

    private static void EncodeZonedDecimal(
        Span<byte> destination,
        decimal value,
        int precision,
        int scale)
    {
        var packedLength = (precision + 2) / 2;
        Span<byte> packed = stackalloc byte[packedLength];
        EncodePackedDecimal(packed, value, precision, scale);
        var digits = Math.Abs(value)
            .ToString($"F{scale}", CultureInfo.InvariantCulture)
            .Replace(".", string.Empty, StringComparison.Ordinal)
            .PadLeft(precision, '0');
        if (destination.Length != precision)
        {
            throw new InvalidOperationException("Invalid zoned-decimal descriptor.");
        }

        for (var index = 0; index < digits.Length; index++)
        {
            destination[index] = (byte)(0xF0 | (digits[index] - '0'));
        }

        destination[^1] = (byte)((value < 0 ? 0xD0 : 0xF0) | (destination[^1] & 0x0F));
    }

    private static decimal DecodeZonedDecimal(
        ReadOnlySpan<byte> source,
        int precision,
        int scale)
    {
        if (source.Length < precision)
        {
            throw ClientAccessCodePoints.ProtocolError(
                "IBM i returned a truncated zoned-decimal value.");
        }

        decimal result = 0;
        for (var index = 0; index < precision; index++)
        {
            var digit = source[index] & 0x0F;
            if (digit > 9)
            {
                throw ClientAccessCodePoints.ProtocolError(
                    "IBM i returned an invalid zoned-decimal digit.");
            }

            result = checked((result * 10) + digit);
        }

        for (var index = 0; index < scale; index++)
        {
            result /= 10;
        }

        var sign = (source[precision - 1] >> 4) & 0x0F;
        return sign is 0x0B or 0x0D ? -result : result;
    }

    private static DateTime DecodeDate(ReadOnlySpan<byte> source, int format)
    {
        int year;
        int month;
        int day;
        switch (format)
        {
            case 4:
                month = TwoDigits(source, 0);
                day = TwoDigits(source, 3);
                year = FourDigits(source, 6);
                break;
            case 6:
                day = TwoDigits(source, 0);
                month = TwoDigits(source, 3);
                year = FourDigits(source, 6);
                break;
            case 5:
            case 7:
            default:
                year = FourDigits(source, 0);
                month = TwoDigits(source, 5);
                day = TwoDigits(source, 8);
                break;
        }

        return new DateTime(year, month, day);
    }

    private static TimeSpan DecodeTime(ReadOnlySpan<byte> source, int format)
    {
        _ = format;
        return new TimeSpan(
            TwoDigits(source, 0),
            TwoDigits(source, 3),
            TwoDigits(source, 6));
    }

    private static DateTime DecodeTimestamp(ReadOnlySpan<byte> source)
    {
        var value = new DateTime(
            FourDigits(source, 0),
            TwoDigits(source, 5),
            TwoDigits(source, 8),
            TwoDigits(source, 11),
            TwoDigits(source, 14),
            TwoDigits(source, 17));
        var fractionalDigits = Math.Max(0, source.Length - 20);
        long ticks = 0;
        var multiplier = 1_000_000L;
        for (var index = 0; index < Math.Min(7, fractionalDigits); index++)
        {
            ticks += Digit(source[20 + index]) * multiplier;
            multiplier /= 10;
        }

        return value.AddTicks(ticks);
    }

    private static int FourDigits(ReadOnlySpan<byte> source, int offset)
        => (Digit(source[offset]) * 1000)
            + (Digit(source[offset + 1]) * 100)
            + (Digit(source[offset + 2]) * 10)
            + Digit(source[offset + 3]);

    private static int TwoDigits(ReadOnlySpan<byte> source, int offset)
        => (Digit(source[offset]) * 10) + Digit(source[offset + 1]);

    private static int Digit(byte value)
    {
        var digit = value & 0x0F;
        if (digit > 9)
        {
            throw ClientAccessCodePoints.ProtocolError(
                "IBM i returned an invalid temporal digit.");
        }

        return digit;
    }

    private static DateTime ConvertDateTime(object value)
        => value switch
        {
            DateTime dateTime => dateTime,
            DateOnly dateOnly => dateOnly.ToDateTime(TimeOnly.MinValue),
            _ => Convert.ToDateTime(value, CultureInfo.InvariantCulture),
        };

    private static DateTime ConvertDate(object value)
        => ConvertDateTime(value).Date;

    private static TimeSpan ConvertTime(object value)
    {
        var result = value switch
        {
            TimeSpan timeSpan => timeSpan,
            TimeOnly timeOnly => timeOnly.ToTimeSpan(),
            DateTime dateTime => dateTime.TimeOfDay,
            _ => throw new InvalidCastException(
                $"Il valore {value.GetType().Name} non può essere convertito in TIME."),
        };

        if (result < TimeSpan.Zero || result >= TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                "Un valore TIME deve essere compreso tra 00:00:00 e 23:59:59.9999999.");
        }

        return result;
    }

    private static byte[] GetBytes(object value)
        => value as byte[]
            ?? throw new InvalidCastException(
                $"Il valore {value.GetType().Name} non può essere convertito in byte[].");

    private static byte GetDecimalScale(decimal value)
        => (byte)((decimal.GetBits(value)[3] >> 16) & 0x7F);

    private static byte GetDecimalPrecision(decimal value)
    {
        var text = Math.Abs(value)
            .ToString(CultureInfo.InvariantCulture)
            .Replace(".", string.Empty, StringComparison.Ordinal)
            .TrimStart('0');
        return checked((byte)Math.Max(1, text.Length));
    }
}
