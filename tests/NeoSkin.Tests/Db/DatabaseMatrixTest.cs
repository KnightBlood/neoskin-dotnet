using System.Data.Common;
using NeoSkin.Web.Config;
using NeoSkin.Web.Db;
using NeoSkin.Web.Service.Hash;
using NeoSkin.Web.Support;

namespace NeoSkin.Tests.Db;

/// <summary>
/// 数据库矩阵测试：由 CI（或本机容器）设置 <c>DB_CONNECTION=mysql|mariadb|pgsql|sqlsrv</c>
/// 后运行，对应 Java 版 <c>MatrixSpringBootTest</c>。未设置或为 sqlite 时自动跳过。
/// </summary>
public sealed class DatabaseMatrixTest
{
    /// <summary>仅当 <c>DB_CONNECTION</c> 指向真实数据库时执行。</summary>
    private sealed class MatrixFactAttribute : FactAttribute
    {
        public MatrixFactAttribute()
        {
            string value = Environment.GetEnvironmentVariable("DB_CONNECTION") ?? "";
            if (value is not ("mysql" or "mariadb" or "pgsql" or "sqlsrv"))
            {
                Skip = "DB_CONNECTION 不是 mysql/mariadb/pgsql/sqlsrv（本机默认跳过）";
            }
        }
    }

    [MatrixFact]
    public void InstallsSchemaAndRoundTripsUser()
    {
        string driver = Environment.GetEnvironmentVariable("DB_CONNECTION")!;
        NeoSkinSettings settings = NeoSkinSettings.FromEnvironment();
        string root = Path.Combine(Path.GetTempPath(),
            "neoskin-matrix-" + Guid.NewGuid().ToString("N"));
        settings.Storage.Root = root;
        Directory.CreateDirectory(root);

        try
        {
            EnsureSqlServerDatabase(settings);

            var db = new Database(settings);
            Assert.Equal(driver, db.Dialect.Name);

            var installer = new SchemaInstaller(db.Open, settings);
            int created = installer.Install();
            Assert.True(created > 0 || installer.IsInstalled());

            var users = new UserRepository(db);
            string email = "matrix-" + Guid.NewGuid().ToString("N") + "@" + driver + ".local";
            int uid = users.Insert(email, "matrix", 0,
                PhpBcrypt.Hash("secret123", 4), "127.0.0.1", "Asia/Shanghai");
            try
            {
                Assert.True(uid > 0);
                Assert.NotNull(users.FindByEmail(email));

                // timestamp 列在 Npgsql 下是 DateTime，必须统一格式化为 PHP 字符串
                Dictionary<string, object?>? json = users.FindJsonById(uid);
                Assert.NotNull(json);
                string registerAt = Convert.ToString(json["register_at"]) ?? "";
                Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$", registerAt);

                // boolean / timestamp / 自增主键 / 分页方言
                var textures = new TextureRepository(db);
                string hash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
                const string uploadAt = "2026-01-02 03:04:05";
                long tid = textures.Insert("matrix", "steve", hash, 1, uid, true, uploadAt);
                try
                {
                    TextureRow? texture = textures.FindByTid(tid);
                    Assert.NotNull(texture);
                    Assert.Equal(uploadAt, texture.UploadAt);
                    Assert.True(texture.IsPublic);
                    Assert.True(textures.CountForLibrary("steve", null, null, uid, false) >= 1);
                }
                finally
                {
                    textures.DeleteByTid(tid);
                }
            }
            finally
            {
                users.DeleteWithPlayers(uid);
            }

            Assert.Null(users.FindByEmail(email));
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch (IOException)
            {
                // 临时目录清理失败不影响矩阵结论
            }
        }
    }

    /// <summary>
    /// SQL Server 目标库不存在时先连 <c>master</c> 建库（CI 已用 sqlcmd 建库则跳过）。
    /// 与 Java 版 <c>ensureSqlServerDatabase()</c> 一致。
    /// </summary>
    private static void EnsureSqlServerDatabase(NeoSkinSettings settings)
    {
        if (!string.Equals(settings.Database.Connection, "sqlsrv", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string database = settings.Database.Name;
        if (!System.Text.RegularExpressions.Regex.IsMatch(database, "^[A-Za-z0-9_]+$"))
        {
            return;
        }

        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder
        {
            DataSource = settings.Database.Host,
            UserID = settings.Database.Username,
            Password = settings.Database.Password,
            InitialCatalog = "master",
            TrustServerCertificate = settings.Database.TrustServerCertificate,
        };
        if (int.TryParse(settings.Database.Port, out int port) && port > 0)
        {
            builder.DataSource = settings.Database.Host + "," + port;
        }

        if (!settings.Database.Encrypt)
        {
            builder.Encrypt = Microsoft.Data.SqlClient.SqlConnectionEncryptOption.Optional;
        }

        try
        {
            using var connection = new Microsoft.Data.SqlClient.SqlConnection(builder.ConnectionString);
            connection.Open();
            using (var check = connection.CreateCommand())
            {
                check.CommandText = "SELECT 1 FROM sys.databases WHERE name = @name";
                check.Parameters.AddWithValue("@name", database);
                if (check.ExecuteScalar() is not null)
                {
                    return;
                }
            }

            using var create = connection.CreateCommand();
            create.CommandText = "CREATE DATABASE [" + database + "]";
            create.ExecuteNonQuery();
        }
        catch (DbException)
        {
            // 建库失败留给后续 SchemaInstaller 报错（权限/连接问题会暴露）
        }
    }
}
