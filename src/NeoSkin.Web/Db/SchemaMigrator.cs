using NeoSkin.Web.Config;
using NeoSkin.Web.Service;
using NeoSkin.Web.Service.Hash;
using NeoSkin.Web.Support;

namespace NeoSkin.Web.Db;

/// <summary>
/// 数据迁移执行器（对应 Java <c>DataMigrator</c>）。
///
/// <para>与应用启动、更新安装后调用：按 <c>migrations</c> 记账表跳过已执行项。
/// 当前包含 4 个数据迁移；结构迁移仍由 <see cref="SchemaInstaller"/> 负责。</para>
///
/// <para>幂等性：所有步骤都做了存在性判断，重复执行不会重复写入；
/// <c>create_communications</c> 会回填旧 PHP 的公告与站内信（按 <c>legacy_key</c> 去重）。</para>
/// </summary>
public sealed class SchemaMigrator(
    Database db,
    SchemaInstaller schema,
    Option option,
    NeoSkinSettings settings)
{
    public const string ImportOptions = "2016_11_18_134542_import_options";
    public const string MaxTextureWidth = "2025_06_29_064944_add_max_texture_width_option";
    public const string CreateCommunications = "2026_10_24_000001_create_communications";
    public const string HashOauthSecrets = "2026_10_25_000001_hash_oauth_client_secrets";

    public static readonly IReadOnlyList<string> DataMigrations =
    [
        ImportOptions,
        MaxTextureWidth,
        CreateCommunications,
        HashOauthSecrets,
    ];

    /// <summary>执行所有未登记的数据迁移，返回本次实际执行的 migration。</summary>
    public IReadOnlyList<string> Migrate()
    {
        schema.EnsureMigrationsTable();
        var applied = new List<string>();

        Run(ImportOptions, ImportOptionsData, applied);
        Run(MaxTextureWidth, AddMaxTextureWidth, applied);
        Run(CreateCommunications, BackfillCommunications, applied);
        Run(HashOauthSecrets, HashOauthClientSecrets, applied);

        return applied;
    }

    private void Run(string name, Action body, List<string> applied)
    {
        if (schema.IsMigrationRecorded(name))
        {
            return;
        }

        body();
        schema.RecordMigration(name);
        applied.Add(name);
    }

    /// <summary>
    /// <c>import_options</c>：只在 options 表为空时灌入默认值，避免升级时覆盖站点配置。
    /// </summary>
    private void ImportOptionsData()
    {
        if (db.Long("SELECT COUNT(*) FROM " + Q("options")) > 0)
        {
            return;
        }

        string version = settings.Version;
        int year = int.Parse(PhpDate.Now(settings.Timezone)[..4]);
        foreach ((string key, string value) in OptionDefaults.All)
        {
            string resolved = key switch
            {
                "version" => version,
                "announcement" => value.Replace("{version}", version),
                "copyright_text" => value.Replace("{year}", year.ToString()),
                _ => value,
            };
            option.Set(key, resolved);
        }
    }

    private void AddMaxTextureWidth()
    {
        if (option.Get("max_texture_width", null, true) is null)
        {
            option.Set("max_texture_width", 8192);
        }
    }

    /// <summary>
    /// <c>create_communications</c> 的数据回填：老 options 公告 + 老 notifications 站内信，
    /// 并给通知 data 写入 <c>communication_id</c>。幂等（legacy_key 唯一）。
    /// </summary>
    private void BackfillCommunications()
    {
        string options = Q("options");
        string now = PhpDate.Now(settings.Timezone);
        List<(string Name, string Value)> announcements = db.Query(
            "SELECT option_name, option_value FROM " + options
            + " WHERE option_name = @a OR option_name LIKE @p ORDER BY id",
            reader => (DbRead.Text(reader, 0), DbRead.Text(reader, 1)),
            ("@a", "announcement"), ("@p", "announcement_%"));
        foreach ((string name, string value) in announcements)
        {
            if (PhpString.Trim(value).Length == 0)
            {
                continue;
            }

            string legacyKey = "option:" + name;
            if (ExistsLegacyKey(legacyKey))
            {
                continue;
            }

            string locale = name == "announcement"
                ? ""
                : PhpString.SubstrBytes(name, 13);
            InsertCommunication(legacyKey, "站点公告", "announcement", "published", locale,
                "source", value, null, now, now, now);
        }

        string lastId = "";
        while (true)
        {
            string sql = "SELECT id, notifiable_id, data, created_at, updated_at"
                         + " FROM " + Q("notifications")
                         + " WHERE type = @type"
                         + (lastId.Length == 0 ? "" : " AND id > @last")
                         + " ORDER BY id ASC LIMIT 200";
            List<(string Id, long NotifiableId, string? Data, string? CreatedAt,
                string? UpdatedAt)> page = lastId.Length == 0
                ? db.Query(sql,
                    reader => (DbRead.Text(reader, 0), DbRead.Long(reader, 1),
                        reader.IsDBNull(2) ? null : DbRead.Text(reader, 2),
                        reader.IsDBNull(3) ? null : DbRead.Text(reader, 3),
                        reader.IsDBNull(4) ? null : DbRead.Text(reader, 4)),
                    ("@type", NotificationRepository.SiteMessageType))
                : db.Query(sql,
                    reader => (DbRead.Text(reader, 0), DbRead.Long(reader, 1),
                        reader.IsDBNull(2) ? null : DbRead.Text(reader, 2),
                        reader.IsDBNull(3) ? null : DbRead.Text(reader, 3),
                        reader.IsDBNull(4) ? null : DbRead.Text(reader, 4)),
                    ("@type", NotificationRepository.SiteMessageType), ("@last", lastId));

            if (page.Count == 0)
            {
                break;
            }

            foreach ((string id, long notifiableId, string? dataJson, string? createdAt,
                         string? updatedAt) in page)
            {
                lastId = id;
                BackfillMessage(id, notifiableId, dataJson, createdAt, updatedAt);
            }

            if (page.Count < 200)
            {
                break;
            }
        }
    }

    private void BackfillMessage(string id, long notifiableId, string? dataJson,
        string? createdAt, string? updatedAt)
    {
        if (string.IsNullOrEmpty(dataJson))
        {
            return;
        }

        PhpArray? data;
        try
        {
            data = PhpJson.Decode(dataJson) as PhpArray;
        }
        catch (Exception e) when (e is ArgumentException or FormatException)
        {
            return;
        }

        if (data is null || CommunicationIdExists(data.Get("communication_id")))
        {
            return;
        }

        string legacyKey = "notification:" + id;
        long? communicationId = FindIdByLegacyKey(legacyKey);
        if (communicationId is null)
        {
            string title = data.Get("title") is { } rawTitle
                ? PhpString.MbSubstr(PhpString.Cast(rawTitle), 0, 100)
                : "站内信";
            string content = data.Get("content") is { } rawContent
                ? PhpString.Cast(rawContent)
                : "";
            string audience = PhpJson.Encode(new PhpArray()
                .With("receiver", "uid")
                .With("uid", notifiableId), 0);
            InsertCommunication(legacyKey, title, "message", "published", "", "source",
                content, audience, DbDate(createdAt), DbDate(createdAt), DbDate(updatedAt));
            communicationId = FindIdByLegacyKey(legacyKey);
        }

        if (communicationId is null)
        {
            return;
        }

        data.Put("communication_id", communicationId.Value);
        db.Execute("UPDATE " + Q("notifications") + " SET data = @d WHERE id = @id",
            ("@d", PhpJson.Encode(data, PhpJson.JsonUnescapedUnicode)), ("@id", id));
    }

    private bool ExistsLegacyKey(string legacyKey) =>
        db.Long("SELECT COUNT(*) FROM " + Q("communications") + " WHERE legacy_key = @k",
            ("@k", legacyKey)) > 0;

    private long? FindIdByLegacyKey(string legacyKey)
    {
        object? value = db.Scalar(
            "SELECT id FROM " + Q("communications") + " WHERE legacy_key = @k",
            ("@k", legacyKey));
        return value is null ? null : Convert.ToInt64(value);
    }

    private bool CommunicationIdExists(object? value)
    {
        if (value is null)
        {
            return false;
        }

        long id;
        try
        {
            id = Convert.ToInt64(value);
        }
        catch (Exception e) when (e is FormatException or InvalidCastException
                                      or OverflowException)
        {
            return false;
        }

        return db.Long("SELECT COUNT(*) FROM " + Q("communications") + " WHERE id = @id",
            ("@id", id)) > 0;
    }

    private void InsertCommunication(string legacyKey, string title, string kind,
        string status, string locale, string editor, string source, string? audience,
        string? publishedAt, string? createdAt, string? updatedAt)
    {
        db.Execute("INSERT INTO " + Q("communications")
                   + " (legacy_key, title, kind, status, locale, editor, source, audience,"
                   + " published_at, created_at, updated_at)"
                   + " VALUES (@k,@t,@kind,@s,@l,@e,@src,@a,@p,@c,@u)",
            ("@k", legacyKey), ("@t", title), ("@kind", kind), ("@s", status),
            ("@l", locale), ("@e", editor), ("@src", source), ("@a", audience),
            ("@p", publishedAt), ("@c", createdAt), ("@u", updatedAt));
    }

    private static string? DbDate(string? raw)
    {
        if (raw is null)
        {
            return null;
        }

        string trimmed = PhpString.Trim(raw);
        return trimmed.Length > 19 && trimmed[10] == ' '
            ? trimmed[..19]
            : trimmed;
    }

    /// <summary><c>hash_oauth_client_secrets</c>：把明文 secret 用 bcrypt 哈希。</summary>
    private void HashOauthClientSecrets()
    {
        string table = Q("oauth_clients");
        List<(long Id, string Secret)> rows = db.Query(
            "SELECT id, secret FROM " + table + " WHERE secret IS NOT NULL AND secret <> ''",
            reader => (DbRead.Long(reader, 0), DbRead.Text(reader, 1)));
        foreach ((long id, string secret) in rows)
        {
            if (secret.StartsWith("$2", StringComparison.Ordinal))
            {
                continue;
            }

            db.Execute("UPDATE " + table + " SET secret = @s WHERE id = @id",
                ("@s", PhpBcrypt.Hash(secret, 10)), ("@id", id));
        }
    }

    private string Q(string name) => schema.Dialect.Quote(schema.Table(name));
}
