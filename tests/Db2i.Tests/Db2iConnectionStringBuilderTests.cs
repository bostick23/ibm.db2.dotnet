using System.Data.Common;

namespace Db2i.Tests;

public sealed class Db2iConnectionStringBuilderTests
{
    [Fact]
    public void ParsesCommonAliasesAndDefaults()
    {
        var builder = new Db2iConnectionStringBuilder(
            "Data Source=my-system;UID=MYUSER;PWD=secret;Initial Catalog=RDBNAME");

        Assert.Equal("my-system", builder.Server);
        Assert.Equal("MYUSER", builder.UserId);
        Assert.Equal("secret", builder.Password);
        Assert.Equal("RDBNAME", builder.Database);
        Assert.Equal(Db2iConnectionStringBuilder.DefaultDatabasePort, builder.Port);
        Assert.False(builder.UseSsl);
        Assert.Equal(15, builder.ConnectTimeout);
    }

    [Fact]
    public void SecureConnectionsUseTheSecureDatabasePortByDefault()
    {
        var builder = new Db2iConnectionStringBuilder("Server=my-system;User ID=MYUSER;SSL=yes");

        Assert.True(builder.UseSsl);
        Assert.Equal(Db2iConnectionStringBuilder.DefaultSecureDatabasePort, builder.Port);
    }

    [Fact]
    public void ExplicitPortWinsOverSslDefault()
    {
        var builder = new Db2iConnectionStringBuilder("Server=my-system;User ID=MYUSER;SSL=true;Port=12345");

        Assert.Equal(12345, builder.Port);
    }

    [Fact]
    public void BuildSettingsRequiresServerAndUser()
    {
        var missingServer = new Db2iConnectionStringBuilder("User ID=MYUSER");
        var missingUser = new Db2iConnectionStringBuilder("Server=my-system");

        Assert.Throws<ArgumentException>(() => missingServer.BuildSettings());
        Assert.Throws<ArgumentException>(() => missingUser.BuildSettings());
    }

    [Fact]
    public void ImplementsTheStandardConnectionStringBuilderContract()
    {
        DbConnectionStringBuilder builder = Db2iProviderFactory.Instance.CreateConnectionStringBuilder();

        Assert.IsType<Db2iConnectionStringBuilder>(builder);
    }
}
