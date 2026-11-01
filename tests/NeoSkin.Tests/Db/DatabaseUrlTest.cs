using NeoSkin.Web.Config;
using NeoSkin.Web.Db;

namespace NeoSkin.Tests.Db;

/// <summary><c>DATABASE_URL</c> 解析与多驱动连接串。</summary>
public sealed class DatabaseUrlTest
{
    private static NeoSkinSettings.DatabaseSettings Settings(string url)
        => new() { Url = url };

    [Fact]
    public void MySqlUrlOverridesFields()
    {
        NeoSkinSettings.DatabaseSettings db = Settings(
            "mysql://alice:p%40ss@db.example:3307/skin?charset=utf8mb4");

        DatabaseUrl.Apply(db);

        Assert.Equal("mysql", db.Connection);
        Assert.Equal("db.example", db.Host);
        Assert.Equal("3307", db.Port);
        Assert.Equal("skin", db.Name);
        Assert.Equal("alice", db.Username);
        Assert.Equal("p@ss", db.Password);
    }

    [Theory]
    [InlineData("mariadb://u:p@h/db", "mariadb")]
    [InlineData("postgres://u:p@h/db", "pgsql")]
    [InlineData("pgsql://u:p@h/db", "pgsql")]
    [InlineData("sqlserver://u:p@h/db", "sqlsrv")]
    public void SchemeSelectsDriver(string url, string expected)
    {
        NeoSkinSettings.DatabaseSettings db = Settings(url);

        DatabaseUrl.Apply(db);

        Assert.Equal(expected, db.Connection);
    }

    [Fact]
    public void SqliteUrlUsesPath()
    {
        NeoSkinSettings.DatabaseSettings db = Settings("sqlite:///tmp/neoskin.sqlite");

        DatabaseUrl.Apply(db);

        Assert.Equal("sqlite", db.Connection);
        Assert.Equal("/tmp/neoskin.sqlite", db.Name);
    }

    [Fact]
    public void EmptyUrlKeepsConfiguredValues()
    {
        var db = new NeoSkinSettings.DatabaseSettings
        {
            Connection = "sqlite",
            Name = "x.sqlite",
        };

        DatabaseUrl.Apply(db);

        Assert.Equal("sqlite", db.Connection);
        Assert.Equal("x.sqlite", db.Name);
    }

    [Fact]
    public void CreateConnectionBuildsMySqlConnectionString()
    {
        var settings = new NeoSkinSettings
        {
            Database =
            {
                Connection = "mysql",
                Host = "127.0.0.1",
                Port = "3307",
                Name = "neoskin",
                Username = "u",
                Password = "p",
            },
        };

        using System.Data.Common.DbConnection connection = Database.CreateConnection(settings);
        var mySql = Assert.IsType<MySqlConnector.MySqlConnection>(connection);
        var builder = new MySqlConnector.MySqlConnectionStringBuilder(mySql.ConnectionString);

        Assert.Equal("127.0.0.1", builder.Server);
        Assert.Equal((uint)3307, builder.Port);
        Assert.Equal("neoskin", builder.Database);
        Assert.Equal("u", builder.UserID);
        Assert.Equal("utf8mb4", builder.CharacterSet);
    }

    [Fact]
    public void CreateConnectionBuildsPostgresConnectionString()
    {
        var settings = new NeoSkinSettings
        {
            Database =
            {
                Connection = "pgsql",
                Host = "pg",
                Port = "",
                Name = "neoskin",
                Username = "u",
                Password = "p",
            },
        };

        using System.Data.Common.DbConnection connection = Database.CreateConnection(settings);
        var npgsql = Assert.IsType<Npgsql.NpgsqlConnection>(connection);
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(npgsql.ConnectionString);

        Assert.Equal("pg", builder.Host);
        Assert.Equal(5432, builder.Port);
        Assert.Equal("neoskin", builder.Database);
        Assert.Equal("public", builder.SearchPath);
    }

    [Fact]
    public void CreateConnectionBuildsSqlServerConnectionString()
    {
        var settings = new NeoSkinSettings
        {
            Database =
            {
                Connection = "sqlsrv",
                Host = "sql",
                Port = "1435",
                Name = "neoskin",
                Username = "u",
                Password = "p",
            },
        };

        using System.Data.Common.DbConnection connection = Database.CreateConnection(settings);
        var sql = Assert.IsType<Microsoft.Data.SqlClient.SqlConnection>(connection);
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(sql.ConnectionString);

        Assert.Equal("sql,1435", builder.DataSource);
        Assert.Equal("neoskin", builder.InitialCatalog);
    }
}
