using System.Security.Cryptography;
using System.Text;

namespace Db2i.Protocol;

/// <summary>
/// IBM i password-substitute algorithms ported from JTOpen AS400ImplRemote.
/// SHA-1 and DES are required by the wire protocol for legacy QPWDLVL values.
/// </summary>
internal static class PasswordSubstitute
{
    private static readonly UnicodeEncoding Utf16BigEndian =
        new(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);

    private static readonly UTF8Encoding Utf8Strict =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    internal static byte[] Generate(
        string userId,
        string password,
        byte passwordLevel,
        ReadOnlySpan<byte> clientSeed,
        ReadOnlySpan<byte> serverSeed)
    {
        ArgumentNullException.ThrowIfNull(password);
        ValidateSeed(clientSeed, nameof(clientSeed));
        ValidateSeed(serverSeed, nameof(serverSeed));

        var userIdEbcdic = SignonEncoding.EncodeProfile(userId);
        try
        {
            return passwordLevel switch
            {
                0 or 1 => GenerateDes(userIdEbcdic, password, clientSeed, serverSeed),
                2 or 3 => GenerateSha1(userIdEbcdic, password, clientSeed, serverSeed),
                4 => GenerateSha512(userId.Trim().ToUpperInvariant(), password, clientSeed, serverSeed),
                _ => throw new Db2iException(
                    $"IBM i password level {passwordLevel} is not supported.",
                    Db2iErrorKind.Authentication),
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(userIdEbcdic);
        }
    }

    internal static byte[] GenerateDes(
        ReadOnlySpan<byte> userIdEbcdic,
        string password,
        ReadOnlySpan<byte> clientSeed,
        ReadOnlySpan<byte> serverSeed)
    {
        if (string.IsNullOrEmpty(password))
        {
            throw AuthenticationInput("The password is empty.");
        }

        var normalized = char.IsDigit(password[0]) ? string.Concat("Q", password) : password;
        var passwordEbcdic = SignonEncoding.EncodeLegacyPassword(normalized.AsSpan());
        try
        {
            var token = GenerateDesToken(userIdEbcdic, passwordEbcdic);
            try
            {
                return GenerateDesSubstitute(userIdEbcdic, token, clientSeed, serverSeed);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(token);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordEbcdic);
        }
    }

    internal static byte[] GenerateSha1(
        ReadOnlySpan<byte> userIdEbcdic,
        string password,
        ReadOnlySpan<byte> clientSeed,
        ReadOnlySpan<byte> serverSeed)
    {
        var trimmedPassword = TrimShaPassword(password);
        var normalizedUser = SignonEncoding.DecodeProfile(userIdEbcdic);
        var userBytes = Utf16BigEndian.GetBytes(normalizedUser);
        var passwordBytes = Utf16BigEndian.GetBytes(trimmedPassword);

        try
        {
            var token = Hash(HashAlgorithmName.SHA1, userBytes, passwordBytes);
            try
            {
                Span<byte> sequence = stackalloc byte[8];
                sequence[7] = 1;
                return Hash(HashAlgorithmName.SHA1, token, serverSeed, clientSeed, userBytes, sequence);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(token);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(userBytes);
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    internal static byte[] GenerateSha512(
        string userProfile,
        string password,
        ReadOnlySpan<byte> clientSeed,
        ReadOnlySpan<byte> serverSeed)
    {
        ValidateModernPassword(password);
        var paddedUser = (userProfile + "          ")[..10];

        var saltCharacters = new char[14];
        paddedUser.CopyTo(0, saltCharacters, 0, 10);
        var passwordStart = Math.Max(password.Length - 4, 0);
        var suffixLength = password.Length - passwordStart;
        password.CopyTo(passwordStart, saltCharacters, 10, suffixLength);
        saltCharacters.AsSpan(10 + suffixLength, 4 - suffixLength).Fill(' ');

        var saltInput = Utf16BigEndian.GetBytes(saltCharacters);
        var salt = SHA256.HashData(saltInput);
        var passwordBytes = Utf8Strict.GetBytes(password);
        byte[]? token = null;
        byte[]? userBytes = null;

        try
        {
            token = Rfc2898DeriveBytes.Pbkdf2(
                passwordBytes,
                salt,
                iterations: 10022,
                HashAlgorithmName.SHA512,
                outputLength: 64);
            userBytes = Utf16BigEndian.GetBytes(paddedUser);
            Span<byte> sequence = stackalloc byte[8];
            sequence[7] = 1;
            return Hash(HashAlgorithmName.SHA512, token, serverSeed, clientSeed, userBytes, sequence);
        }
        finally
        {
            Array.Fill(saltCharacters, '\0');
            CryptographicOperations.ZeroMemory(saltInput);
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(passwordBytes);
            if (token is not null)
            {
                CryptographicOperations.ZeroMemory(token);
            }

            if (userBytes is not null)
            {
                CryptographicOperations.ZeroMemory(userBytes);
            }
        }
    }

    private static byte[] GenerateDesToken(
        ReadOnlySpan<byte> userId,
        ReadOnlySpan<byte> password)
    {
        Span<byte> foldedUser = stackalloc byte[10];
        userId.CopyTo(foldedUser);
        var userLength = EbcdicLength(userId);
        if (userLength > 8)
        {
            foldedUser[0] ^= (byte)(foldedUser[8] & 0xC0);
            foldedUser[1] ^= (byte)((foldedUser[8] & 0x30) << 2);
            foldedUser[2] ^= (byte)((foldedUser[8] & 0x0C) << 4);
            foldedUser[3] ^= (byte)((foldedUser[8] & 0x03) << 6);
            foldedUser[4] ^= (byte)(foldedUser[9] & 0xC0);
            foldedUser[5] ^= (byte)((foldedUser[9] & 0x30) << 2);
            foldedUser[6] ^= (byte)((foldedUser[9] & 0x0C) << 4);
            foldedUser[7] ^= (byte)((foldedUser[9] & 0x03) << 6);
        }

        var passwordLength = EbcdicLength(password);
        Span<byte> first = stackalloc byte[10];
        Span<byte> second = stackalloc byte[10];
        first.Fill(SignonEncoding.EbcdicSpace);
        second.Fill(SignonEncoding.EbcdicSpace);

        if (passwordLength > 8)
        {
            password[..8].CopyTo(first);
            password[8..passwordLength].CopyTo(second);
            TransformDesTokenKey(first);
            TransformDesTokenKey(second);
            var firstToken = DesCipher.EncryptBlock(first[..8], foldedUser[..8]);
            var secondToken = DesCipher.EncryptBlock(second[..8], foldedUser[..8]);
            try
            {
                XorInPlace(firstToken, secondToken);
                return firstToken;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(secondToken);
            }
        }

        password[..passwordLength].CopyTo(first);
        TransformDesTokenKey(first);
        return DesCipher.EncryptBlock(first[..8], foldedUser[..8]);
    }

    private static byte[] GenerateDesSubstitute(
        ReadOnlySpan<byte> userId,
        ReadOnlySpan<byte> token,
        ReadOnlySpan<byte> clientSeed,
        ReadOnlySpan<byte> serverSeed)
    {
        Span<byte> sequence = stackalloc byte[8];
        sequence[7] = 1;
        Span<byte> serverSequence = stackalloc byte[8];
        AddBigEndian(sequence, serverSeed, serverSequence);

        var encrypted = DesCipher.EncryptBlock(token, serverSequence);
        try
        {
            XorInPlace(encrypted, clientSeed);
            Replace(ref encrypted, DesCipher.EncryptBlock(token, encrypted));

            Span<byte> next = stackalloc byte[8];
            userId[..8].CopyTo(next);
            XorInPlace(next, serverSequence);
            XorInPlace(next, encrypted);
            Replace(ref encrypted, DesCipher.EncryptBlock(token, next));

            next.Fill(SignonEncoding.EbcdicSpace);
            next[0] = userId[8];
            next[1] = userId[9];
            XorInPlace(next, serverSequence);
            XorInPlace(next, encrypted);
            Replace(ref encrypted, DesCipher.EncryptBlock(token, next));

            XorInPlace(encrypted, sequence);
            return DesCipher.EncryptBlock(token, encrypted);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    private static string TrimShaPassword(string password)
    {
        ValidateModernPassword(password);
        var end = password.Length;
        while (end > 0 && password[end - 1] is '\0' or '\u0020' or '\u3000')
        {
            end--;
        }

        if (end == 0)
        {
            throw AuthenticationInput("The password is empty after trimming trailing spaces.");
        }

        return password[..end];
    }

    private static void ValidateModernPassword(string password)
    {
        if (string.IsNullOrEmpty(password))
        {
            throw AuthenticationInput("The password is empty.");
        }

        if (password[0] == '*')
        {
            throw AuthenticationInput("A password beginning with '*' is not valid for IBM i sign-on.");
        }
    }

    private static byte[] Hash(
        HashAlgorithmName algorithm,
        ReadOnlySpan<byte> first,
        ReadOnlySpan<byte> second)
    {
        using var hash = IncrementalHash.CreateHash(algorithm);
        hash.AppendData(first);
        hash.AppendData(second);
        return hash.GetHashAndReset();
    }

    private static byte[] Hash(
        HashAlgorithmName algorithm,
        ReadOnlySpan<byte> first,
        ReadOnlySpan<byte> second,
        ReadOnlySpan<byte> third,
        ReadOnlySpan<byte> fourth,
        ReadOnlySpan<byte> fifth)
    {
        using var hash = IncrementalHash.CreateHash(algorithm);
        hash.AppendData(first);
        hash.AppendData(second);
        hash.AppendData(third);
        hash.AppendData(fourth);
        hash.AppendData(fifth);
        return hash.GetHashAndReset();
    }

    private static void TransformDesTokenKey(Span<byte> bytes)
    {
        for (var index = 0; index < 8; index++)
        {
            bytes[index] ^= 0x55;
        }

        for (var index = 0; index < 7; index++)
        {
            bytes[index] = (byte)((bytes[index] << 1) | ((bytes[index + 1] & 0x80) >> 7));
        }

        bytes[7] <<= 1;
    }

    private static int EbcdicLength(ReadOnlySpan<byte> value)
    {
        var index = 0;
        while (index < 10 && value[index] is not (SignonEncoding.EbcdicSpace or 0))
        {
            index++;
        }

        return index;
    }

    private static void AddBigEndian(
        ReadOnlySpan<byte> left,
        ReadOnlySpan<byte> right,
        Span<byte> result)
    {
        var carry = 0;
        for (var index = result.Length - 1; index >= 0; index--)
        {
            var sum = left[index] + right[index] + carry;
            result[index] = (byte)sum;
            carry = sum >> 8;
        }
    }

    private static void XorInPlace(Span<byte> target, ReadOnlySpan<byte> value)
    {
        for (var index = 0; index < 8; index++)
        {
            target[index] ^= value[index];
        }
    }

    private static void Replace(ref byte[] target, byte[] replacement)
    {
        CryptographicOperations.ZeroMemory(target);
        target = replacement;
    }

    private static void ValidateSeed(ReadOnlySpan<byte> seed, string parameterName)
    {
        if (seed.Length != 8)
        {
            throw new ArgumentException("An IBM i sign-on seed must contain 8 bytes.", parameterName);
        }
    }

    private static Db2iException AuthenticationInput(string message)
        => new(message, Db2iErrorKind.Authentication);
}
