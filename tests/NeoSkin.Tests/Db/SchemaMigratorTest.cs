using Microsoft.Data.Sqlite;
using NeoSkin.Web.Config;
using NeoSkin.Web.Db;
using NeoSkin.Web.Paths;
using NeoSkin.Web.Service;

namespace NeoSkin.Tests.Db;

/// <summary>数据迁移（SchemaMigrator / Java DataMigrator）的幂等与副作用。</summary>
public sealed class SchemaMigratorTest : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "neoskin-migrator-" + Guid.NewGuid().ToString("N"));
    private readonly NeoSkinSettings _settings;
    private readonly Database _db;
    private readonly SchemaInstaller _schema;
    private readonly Option _option;

    public SchemaMigratorTest()
    {
        Directory.CreateDirectory(_dir);
        _settings = new NeoSkinSettings
        {
            Storage = { Root = Path.Combine(_dir, "storage") },
        };
        _settings.Database.Connection = "sqlite";
        _settings.Database.Name = Path.Combine(_dir, "db.sqlite");
        Directory.CreateDirectory(_settings.Storage.Root);

        _db = new Database(_settings);
        _schema = new SchemaInstaller(_db.Open, _settings);
        _schema.Install();
        _option = new Option(_db, _schema, new AppPaths(_dir, _settings));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, true);
            }
        }
        catch (IOException)
        {
            // 句柄延迟释放；临时目录留给系统清理
        }
    }

    [Fact]
    public void RunsDataMigrationsAndRecordsThem()
    {
        var migrator = new SchemaMigrator(_db, _schema, _option, _settings);

        IReadOnlyList<string> applied = migrator.Migrate();

        Assert.Contains(SchemaMigrator.ImportOptions, applied);
        Assert.Contains(SchemaMigrator.MaxTextureWidth, applied);
        Assert.Equal("8192", _option.Get("max_texture_width")?.ToString());
        Assert.True(_schema.IsMigrationRecorded(SchemaMigrator.ImportOptions));

        // 第二次运行不再执行任何迁移
        Assert.Empty(migrator.Migrate());
    }

    [Fact]
    public void ImportOptionsDoesNotOverwriteExistingSite()
    {
        _option.Set("site_name", "Custom Site");
        var migrator = new SchemaMigrator(_db, _schema, _option, _settings);

        migrator.Migrate();

        Assert.Equal("Custom Site", _option.Get("site_name")?.ToString());
    }

    [Fact]
    public void BackfillsLegacyAnnouncementsAndSiteMessages()
    {
        _option.Set("announcement", "<p>old announcement</p>");
        _option.Set("announcement_zh_CN", "中文公告");
        _db.Execute("INSERT INTO notifications"
                    + " (id, type, notifiable_type, notifiable_id, data, read_at, created_at,"
                    + " updated_at) VALUES ('n1', @t, 'App\\\\Models\\\\User', 7, @d, NULL,"
                    + " '2026-01-01 00:00:00', '2026-01-01 00:00:00')",
            ("@t", NotificationRepository.SiteMessageType),
            ("@d", "{\"title\":\"Hello\",\"content\":\"World\"}"));

        var migrator = new SchemaMigrator(_db, _schema, _option, _settings);
        migrator.Migrate();

        Assert.Equal(2, _db.Long(
            "SELECT COUNT(*) FROM communications WHERE kind = 'announcement'"));
        Assert.Equal(1, _db.Long(
            "SELECT COUNT(*) FROM communications WHERE kind = 'message'"));
        Assert.Equal("中文公告", _db.Scalar(
            "SELECT source FROM communications WHERE legacy_key = 'option:announcement_zh_CN'"));
        Assert.Equal("Hello", _db.Scalar(
            "SELECT title FROM communications WHERE legacy_key = 'notification:n1'"));
        string? data = _db.Scalar("SELECT data FROM notifications WHERE id = 'n1'")?.ToString();
        Assert.Contains("communication_id", data);

        // 幂等：重跑不新增行
        migrator.Migrate();
        Assert.Equal(3, _db.Long("SELECT COUNT(*) FROM communications"));
    }

    [Fact]
    public void HashesPlaintextOAuthClientSecrets()
    {
        _db.Execute("INSERT INTO oauth_clients"
                    + " (name, secret, redirect, personal_access_client, password_client, revoked)"
                    + " VALUES ('c', 'plain-secret', 'https://x', 0, 0, 0)");
        var migrator = new SchemaMigrator(_db, _schema, _option, _settings);

        migrator.Migrate();

        string secret = _db.Scalar("SELECT secret FROM oauth_clients LIMIT 1")?.ToString() ?? "";
        Assert.StartsWith("$2", secret);
        Assert.True(BCrypt.Net.BCrypt.Verify("plain-secret", secret));
    }
}
