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
        Assert.True(builder.Pooling);
        Assert.Equal(100, builder.MaxPoolSize);
    }

    [Fact]
    public void ParsesCompactDataSourceAlias()
    {
        var builder = new Db2iConnectionStringBuilder(
            "DataSource=my-system;UserId=MYUSER;Password=secret");

        Assert.Equal("my-system", builder.Server);
        Assert.Equal("MYUSER", builder.UserId);
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

    [Fact]
    public void ParsesAndCanonicalizesPoolingSettings()
    {
        var builder = new Db2iConnectionStringBuilder(
            "Server=my-system;User ID=MYUSER;Pooling=no;Maximum Pool Size=7");

        Assert.False(builder.Pooling);
        Assert.Equal(7, builder.MaxPoolSize);

        builder.Pooling = true;
        builder.MaxPoolSize = 12;

        Assert.DoesNotContain("Maximum Pool Size", builder.ConnectionString);
        Assert.Contains("Max Pool Size=12", builder.ConnectionString);
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.MaxPoolSize = 0);
    }
}
