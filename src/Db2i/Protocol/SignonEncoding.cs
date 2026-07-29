namespace Db2i.Protocol;

/// <summary>
/// The restricted CCSID 37 mapping used by JTOpen SignonConverter.
/// National aliases intentionally map to the IBM i profile special characters.
/// </summary>
internal static class SignonEncoding
{
    internal const byte EbcdicSpace = 0x40;

    internal static byte[] EncodeProfile(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var normalized = userId.Trim().ToUpperInvariant();
        if (normalized.Length > 10)
        {
            throw AuthenticationInput("An IBM i user profile cannot exceed 10 characters.");
        }

        return EncodeFixed(normalized.AsSpan(), upperCase: true);
    }

    internal static byte[] EncodeLegacyPassword(ReadOnlySpan<char> password)
    {
        if (password.Length > 10)
        {
            throw AuthenticationInput("A QPWDLVL 0/1 password cannot exceed 10 characters.");
        }

        return EncodeFixed(password, upperCase: true);
    }

    internal static string DecodeProfile(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 10)
        {
            throw new ArgumentException("A profile value must contain exactly 10 EBCDIC bytes.", nameof(bytes));
        }

        Span<char> result = stackalloc char[10];
        for (var index = 0; index < bytes.Length; index++)
        {
            result[index] = DecodeProfileCharacter(bytes[index]);
        }

        return new string(result);
    }

    private static byte[] EncodeFixed(ReadOnlySpan<char> value, bool upperCase)
    {
        var bytes = new byte[10];
        bytes.AsSpan().Fill(EbcdicSpace);
        for (var index = 0; index < value.Length; index++)
        {
            var character = upperCase ? char.ToUpperInvariant(value[index]) : value[index];
            bytes[index] = EncodeCharacter(character);
        }

        return bytes;
    }

    private static byte EncodeCharacter(char value) => value switch
    {
        ' ' => 0x40,
        '"' => 0x7F,
        '#' or '\u00A3' or '\u00C4' or '\u00C6' or '\u00D1' => 0x7B,
        '$' or '\u00A5' or '\u00C5' or '\u0130' => 0x5B,
        '%' => 0x6C,
        '&' => 0x50,
        '\'' => 0x7D,
        '(' => 0x4D,
        ')' => 0x5D,
        '*' => 0x5C,
        '+' => 0x4E,
        ',' => 0x6B,
        '-' => 0x60,
        '.' => 0x4B,
        '/' => 0x61,
        '0' => 0xF0,
        '1' => 0xF1,
        '2' => 0xF2,
        '3' => 0xF3,
        '4' => 0xF4,
        '5' => 0xF5,
        '6' => 0xF6,
        '7' => 0xF7,
        '8' => 0xF8,
        '9' => 0xF9,
        ':' => 0x7A,
        ';' => 0x5E,
        '<' => 0x4C,
        '=' => 0x7E,
        '>' => 0x6E,
        '?' => 0x6F,
        '!' => 0x5A,
        '@' or '\u00A7' or '\u00D0' or '\u00D6' or '\u00D8' or '\u00E0' or '\u015E' => 0x7C,
        '_' => 0x6D,
        >= 'A' and <= 'I' => (byte)(0xC1 + value - 'A'),
        >= 'J' and <= 'R' => (byte)(0xD1 + value - 'J'),
        >= 'S' and <= 'Z' => (byte)(0xE2 + value - 'S'),
        >= 'a' and <= 'i' => (byte)(0x81 + value - 'a'),
        >= 'j' and <= 'r' => (byte)(0x91 + value - 'j'),
        >= 's' and <= 'z' => (byte)(0xA2 + value - 's'),
        _ => throw AuthenticationInput($"Character U+{(int)value:X4} is not valid for IBM i sign-on."),
    };

    private static char DecodeProfileCharacter(byte value) => value switch
    {
        0x40 => ' ',
        0x5B => '$',
        0x6D => '_',
        0x7B => '#',
        0x7C => '@',
        >= 0xC1 and <= 0xC9 => (char)('A' + value - 0xC1),
        >= 0xD1 and <= 0xD9 => (char)('J' + value - 0xD1),
        >= 0xE2 and <= 0xE9 => (char)('S' + value - 0xE2),
        >= 0xF0 and <= 0xF9 => (char)('0' + value - 0xF0),
        _ => throw AuthenticationInput($"EBCDIC byte 0x{value:X2} is not valid in an IBM i user profile."),
    };

    private static Db2iException AuthenticationInput(string message)
        => new(message, Db2iErrorKind.Authentication);
}
