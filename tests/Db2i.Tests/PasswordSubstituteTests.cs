using Db2i.Protocol;

namespace Db2i.Tests;

public sealed class PasswordSubstituteTests
{
    private static readonly byte[] ClientSeed = [1, 2, 3, 4, 5, 6, 7, 8];
    private static readonly byte[] ServerSeed = [17, 18, 19, 20, 21, 22, 23, 24];

    [Fact]
    public void DesCipherMatchesThePublishedDesVector()
    {
        var key = Convert.FromHexString("133457799BBCDFF1");
        var clearText = Convert.FromHexString("0123456789ABCDEF");

        var encrypted = DesCipher.EncryptBlock(key, clearText);

        Assert.Equal("85E813540F0AB405", Convert.ToHexString(encrypted));
    }

    [Fact]
    public void QpwdlvlZeroAndOneMatchJtOpen()
    {
        var substitute = PasswordSubstitute.Generate(
            "TESTUSER",
            "PASS123",
            passwordLevel: 0,
            ClientSeed,
            ServerSeed);

        Assert.Equal("296AF9F03F274B8C", Convert.ToHexString(substitute));
    }

    [Fact]
    public void LegacyNumericPasswordIsPrefixedWithQ()
    {
        var substitute = PasswordSubstitute.Generate(
            "TESTUSER",
            "123",
            passwordLevel: 1,
            ClientSeed,
            ServerSeed);

        Assert.Equal("BDF0A5D6CADED287", Convert.ToHexString(substitute));
    }

    [Fact]
    public void QpwdlvlTwoAndThreeMatchJtOpen()
    {
        var substitute = PasswordSubstitute.Generate(
            "TESTUSER",
            "Pässword",
            passwordLevel: 3,
            ClientSeed,
            ServerSeed);

        Assert.Equal("9DFAFF7F1ABC1662922085A185DB6A60BF060D16", Convert.ToHexString(substitute));
    }

    [Fact]
    public void QpwdlvlFourMatchesJtOpenForUnicodePassword()
    {
        var substitute = PasswordSubstitute.Generate(
            "USRÄ",
            "päss🔐",
            passwordLevel: 4,
            ClientSeed,
            ServerSeed);

        Assert.Equal(
            "08976842B15986CC6DDA50224183D9BACFF7A47D1147EF667579771A293B9F65" +
            "D7F2B8A92C0657D1D3F721A51788FBEFC2F2EFF2248B00A2F90B9CBEB80F09E4",
            Convert.ToHexString(substitute));
    }

    [Theory]
    [InlineData(2, "")]
    [InlineData(3, "*INVALID")]
    [InlineData(4, "")]
    public void ModernPasswordLevelsRejectInvalidPasswords(byte level, string password)
    {
        Assert.Throws<Db2iException>(
            () => PasswordSubstitute.Generate("TESTUSER", password, level, ClientSeed, ServerSeed));
    }
}
