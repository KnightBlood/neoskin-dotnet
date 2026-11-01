using System.Data.Common;
using NeoSkin.Web.Config;

namespace NeoSkin.Web.Db;

/// <summary>
/// 表结构安装器：用纯 ADO.NET 复刻源项目 27 个 Laravel migration 建出的 16 张业务表
/// + Laravel 的 <c>migrations</c> 记账表。
///
/// <p>与 PHP 的差异：直接建**最终**结构，不回放 <c>alter table</c>/临时表重建，
/// 所以 DDL 文本不同、表结构语义相同（`PORT-NOTES.md` 记录）。</p>
/// </summary>
public sealed class SchemaInstaller
{
    /// <summary>
    /// 只负责建表的 migration（23 个）。源项目 27 个里有 4 个写数据
    ///（import_options / max_texture_width / create_communications / hash_oauth_client_secrets），
    /// 由 <c>DataMigrator</c> 登记；在这里提前登记会让数据迁移被误判为已完成。
    /// </summary>
    public static readonly IReadOnlyList<string> SchemaMigrations =
    [
        "2016_06_01_000001_create_oauth_auth_codes_table",
        "2016_06_01_000002_create_oauth_access_tokens_table",
        "2016_06_01_000003_create_oauth_refresh_tokens_table",
        "2016_06_01_000004_create_oauth_clients_table",
        "2016_06_01_000005_create_oauth_personal_access_clients_table",
        "2016_11_18_133939_create_all_tables",
        "2018_07_26_130617_add_verification_to_users_table",
        "2018_08_21_105514_add_remember_token_to_users_table",
        "2019_03_01_131420_add_tid_skin",
        "2019_03_13_130311_rename_players_table_columns",
        "2019_03_14_174727_create_closet",
        "2019_03_16_162603_remove_likes_field",
        "2019_03_23_171728_create_report_table",
        "2019_05_05_103143_add_likes_field",
        "2019_07_03_094434_create_notifications_table",
        "2019_07_05_222912_create_jobs_table",
        "2019_09_05_130811_create_language_lines_table",
        "2019_12_14_095751_update_ip_field",
        "2020_03_10_145738_lengthen_ip_field",
        "2020_06_26_090510_add_o_auth_provider_field",
        "2020_06_28_155519_add_user_locale_field",
        "2021_04_14_181300_create_scope_table",
        "2021_06_06_111049_add_is_dark_mode_field",
    ];

    private readonly Func<DbConnection> _connectionFactory;

    public SchemaInstaller(Func<DbConnection> connectionFactory, NeoSkinSettings settings)
    {
        _connectionFactory = connectionFactory;
        Dialect = SqlDialect.Of(settings.Database.Connection);
        Prefix = settings.Database.Prefix ?? "";
    }

    public SqlDialect Dialect { get; }

    public string Prefix { get; }

    /// <summary>带前缀的表名。</summary>
    public string Table(string name) => Prefix + name;

    /// <summary>幂等建表；返回本次新建的表数量。</summary>
    public int Install()
    {
        int created = 0;
        using DbConnection connection = Open();
        foreach (TableSpec spec in Tables())
        {
            if (Exists(connection, spec.Name))
            {
                continue;
            }

            Execute(connection, spec.CreateSql());
            foreach (string index in spec.Indexes)
            {
                Execute(connection, index);
            }

            foreach (string statement in spec.PostCreate)
            {
                Execute(connection, statement);
            }

            created++;
        }

        RecordMigrations(connection);
        return created;
    }

    /// <summary>是否已安装（users 表存在即视为已安装）。</summary>
    public bool IsInstalled()
    {
        try
        {
            using DbConnection connection = Open();
            return Exists(connection, Table("users"));
        }
        catch (DbException)
        {
            return false;
        }
    }

    /// <summary>建 <c>migrations</c> 表（Laravel bookkeeping），已存在则跳过。</summary>
    public void EnsureMigrationsTable()
    {
        using DbConnection connection = Open();
        EnsureMigrationsTable(connection);
    }

    /// <summary>某个 migration 是否已登记。</summary>
    public bool IsMigrationRecorded(string migration)
    {
        string sql = "SELECT 1 FROM " + Q(Table("migrations")) + " WHERE " + Q("migration") + " = @m";
        try
        {
            using DbConnection connection = Open();
            using DbCommand command = connection.CreateCommand();
            command.CommandText = sql;
            AddParameter(command, "@m", migration);
            using DbDataReader reader = command.ExecuteReader();
            return reader.Read();
        }
        catch (DbException)
        {
            return false;
        }
    }

    /// <summary>登记一个已执行的 migration（幂等）。</summary>
    public void RecordMigration(string migration)
    {
        using DbConnection connection = Open();
        EnsureMigrationsTable(connection);
        RecordMigration(connection, migration);
    }

    /// <summary>全部建表与索引 DDL（不含 migrations 表），供静态核对。</summary>
    public IReadOnlyList<string> DdlStatements()
    {
        var output = new List<string>();
        foreach (TableSpec spec in Tables())
        {
            output.Add(spec.CreateSql());
            output.AddRange(spec.Indexes);
            output.AddRange(spec.PostCreate);
        }

        return output;
    }

    /// <summary>全部业务表名（不含 <c>migrations</c>）。</summary>
    public IReadOnlyList<string> TableNames() => Tables().Select(spec => spec.Name).ToList();

    /// <summary><c>migrations</c> 表的建表 DDL（供语料比对）。</summary>
    public string MigrationsTableSql() =>
        "CREATE TABLE " + Q(Table("migrations")) + " ("
        + Dialect.AutoIncrementPk("id", false)
        + ", " + Q("migration") + " " + Dialect.Varchar(255) + " NOT NULL"
        + ", " + Q("batch") + " " + Dialect.IntType() + " NOT NULL"
        + ")" + Dialect.TableSuffix();

    private DbConnection Open()
    {
        DbConnection connection = _connectionFactory();
        // 工厂可能已经 Open（Database.Open），也可能返回未打开的连接；
        // 除 SQLite 外重复 Open 会抛 InvalidOperationException。
        if (connection.State != System.Data.ConnectionState.Open)
        {
            connection.Open();
        }

        return connection;
    }

    private void RecordMigrations(DbConnection connection)
    {
        EnsureMigrationsTable(connection);
        foreach (string migration in SchemaMigrations)
        {
            RecordMigration(connection, migration);
        }
    }

    private void EnsureMigrationsTable(DbConnection connection)
    {
        string migrations = Table("migrations");
        if (Exists(connection, migrations))
        {
            return;
        }

        Execute(connection, MigrationsTableSql());
    }

    private void RecordMigration(DbConnection connection, string migration)
    {
        string migrations = Table("migrations");
        string check = "SELECT 1 FROM " + Q(migrations) + " WHERE " + Q("migration") + " = @m";
        using (DbCommand command = connection.CreateCommand())
        {
            command.CommandText = check;
            AddParameter(command, "@m", migration);
            using DbDataReader reader = command.ExecuteReader();
            if (reader.Read())
            {
                return;
            }
        }

        string insert = "INSERT INTO " + Q(migrations) + " (" + Q("migration") + ", " + Q("batch")
            + ") VALUES (@m, 1)";
        using (DbCommand command = connection.CreateCommand())
        {
            command.CommandText = insert;
            AddParameter(command, "@m", migration);
            command.ExecuteNonQuery();
        }
    }

    private bool Exists(DbConnection connection, string table)
    {
        try
        {
            using DbCommand command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM " + Q(table) + " WHERE 1 = 0";
            using DbDataReader reader = command.ExecuteReader();
            reader.Read();
            return true;
        }
        catch (DbException)
        {
            return false;
        }
    }

    private static void Execute(DbConnection connection, string sql)
    {
        using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private string Q(string identifier) => Dialect.Quote(identifier);

    // ================================================================== 表定义

    private sealed class TableSpec(string name)
    {
        private readonly List<string> _columns = [];
        private readonly List<string> _indexes = [];

        internal string Name { get; } = name;

        internal IReadOnlyList<string> Indexes => _indexes;

        internal IReadOnlyList<string> PostCreate => _postCreate;

        private readonly List<string> _postCreate = [];
        private readonly List<string> _pkColumns = [];
        private SqlDialect _dialect = null!;
        private bool _explicitEngine;

        internal TableSpec Col(string definition)
        {
            _columns.Add(definition);
            return this;
        }

        internal TableSpec Idx(params string[] columns)
        {
            string indexName = Name + "_" + string.Join("_", columns) + "_index";
            _indexes.Add(CreateIndex(indexName, columns));
            return this;
        }

        internal TableSpec Uniq(params string[] columns)
        {
            string indexName = Name + "_" + string.Join("_", columns) + "_unique";
            string sql = "CREATE UNIQUE INDEX " + QuoteIdentifier(indexName) + " ON "
                + QuoteIdentifier(Name) + " (" + string.Join(", ", columns.Select(QuoteIdentifier)) + ")";
            if (FilteredUniqueIndex)
            {
                List<string> filters = columns.Where(IsNullable)
                    .Select(column => QuoteIdentifier(column) + " IS NOT NULL").ToList();
                if (filters.Count > 0)
                {
                    sql += " WHERE " + string.Join(" AND ", filters);
                }
            }

            _indexes.Add(sql);
            return this;
        }

        /// <summary>字符串主键：MySQL/SQLite 在表尾写 primary key，PG/SQL Server 生成独立语句。</summary>
        internal TableSpec PkVarchar(string column, int length)
        {
            _columns.Add(QuoteIdentifier(column) + " " + _dialect.Varchar(length) + " NOT NULL");
            _pkColumns.Add(column);
            if (!_dialect.InlineTablePrimaryKey)
            {
                _postCreate.Add(_dialect.PrimaryKeyStatement(Name, column));
            }

            return this;
        }

        private bool IsNullable(string column)
        {
            string quoted = QuoteIdentifier(column);
            foreach (string definition in _columns)
            {
                if (definition.StartsWith(quoted + " ", StringComparison.Ordinal))
                {
                    return !definition.Contains("NOT NULL", StringComparison.Ordinal);
                }
            }

            return true;
        }

        internal string CreateSql()
        {
            string body = string.Join(", ", _columns);
            if (_pkColumns.Count > 0 && _dialect.InlineTablePrimaryKey)
            {
                body += ", primary key (" + string.Join(", ", _pkColumns.Select(QuoteIdentifier)) + ")";
            }

            return "CREATE TABLE " + QuoteIdentifier(Name) + " (" + body + ")"
                + _dialect.TableSuffix(_explicitEngine);
        }

        private Func<string, string> QuoteIdentifier { get; set; } = id => id;


        private bool FilteredUniqueIndex { get; set; }

        private string CreateIndex(string indexName, string[] columns)
        {
            return "CREATE INDEX " + QuoteIdentifier(indexName) + " ON " + QuoteIdentifier(Name)
                + " (" + string.Join(", ", columns.Select(QuoteIdentifier)) + ")";
        }

        internal TableSpec WithDialect(SqlDialect dialect, bool explicitEngine = false)
        {
            _dialect = dialect;
            _explicitEngine = explicitEngine;
            QuoteIdentifier = dialect.Quote;
            FilteredUniqueIndex = dialect.FilteredUniqueIndex();
            return this;
        }
    }

    private List<TableSpec> Tables()
    {
        return
        [
            Users(), Players(), Textures(), Options(), UserCloset(), Reports(), Notifications(),
            Jobs(), LanguageLines(), Scopes(), OAuthAuthCodes(), OAuthAccessTokens(),
            OAuthRefreshTokens(), OAuthClients(), OAuthPersonalAccessClients(), Communications(),
        ];
    }

    private TableSpec Spec(string name, bool explicitEngine = false) =>
        new TableSpec(Table(name)).WithDialect(Dialect, explicitEngine);

    /// <summary>
    /// users 表的最终列顺序与「迁移历史」一致：<c>locale</c>（after nickname）与
    /// <c>is_dark_mode</c>（after ip）只在 MySQL/MariaDB 插到中间，其余方言落在表尾
    /// （SQLite 忽略 after，见语料 <c>final_tables.users</c>）。
    /// </summary>
    private TableSpec Users()
    {
        bool positioned = Dialect.SupportsColumnPosition;
        TableSpec spec = Spec("users")
            .Col(Dialect.AutoIncrementPk("uid", false))
            .Col(Q("email") + " " + Dialect.Varchar(100) + " NOT NULL")
            .Col(Q("nickname") + " " + Dialect.Varchar(50) + " NOT NULL DEFAULT ''");
        if (positioned)
        {
            spec.Col(Q("locale") + " " + Dialect.Varchar(255) + " NULL");
        }

        spec.Col(Q("score") + " " + Dialect.IntType() + " NOT NULL")
            .Col(Q("avatar") + " " + Dialect.IntType() + " NOT NULL DEFAULT 0")
            .Col(Q("password") + " " + Dialect.Varchar(255) + " NOT NULL")
            .Col(Q("ip") + " " + Dialect.Varchar(45) + " NOT NULL");
        if (positioned)
        {
            spec.Col(Q("is_dark_mode") + " " + Dialect.Bool() + " NOT NULL DEFAULT "
                + Dialect.BoolLiteral(false));
        }

        spec.Col(Q("permission") + " " + Dialect.IntType() + " NOT NULL DEFAULT 0")
            .Col(Q("last_sign_at") + " " + Dialect.DateTime() + " NOT NULL")
            .Col(Q("register_at") + " " + Dialect.DateTime() + " NOT NULL")
            .Col(Q("verified") + " " + Dialect.Bool() + " NOT NULL DEFAULT " + Dialect.BoolLiteral(false))
            .Col(Q("verification_token") + " " + Dialect.Varchar(255) + " NOT NULL DEFAULT ''")
            .Col(Q("remember_token") + " " + Dialect.Varchar(100) + " NULL");

        if (!positioned)
        {
            spec.Col(Q("locale") + " " + Dialect.Varchar(255) + " NULL")
                .Col(Q("is_dark_mode") + " " + Dialect.Bool() + " NOT NULL DEFAULT "
                    + Dialect.BoolLiteral(false));
        }

        return spec;
    }

    private TableSpec Players() => Spec("players")
        .Col(Dialect.AutoIncrementPk("pid", false))
        .Col(Q("uid") + " " + Dialect.IntType() + " NOT NULL")
        .Col(Q("name") + " " + Dialect.Varchar(50) + " NOT NULL")
        .Col(Q("tid_cape") + " " + Dialect.IntType() + " NOT NULL DEFAULT 0")
        .Col(Q("last_modified") + " " + Dialect.DateTime() + " NOT NULL")
        .Col(Q("tid_skin") + " " + Dialect.IntType() + " NOT NULL DEFAULT -1");

    private TableSpec Textures() => Spec("textures")
        .Col(Dialect.AutoIncrementPk("tid", false))
        .Col(Q("name") + " " + Dialect.Varchar(50) + " NOT NULL")
        .Col(Q("type") + " " + Dialect.Varchar(10) + " NOT NULL")
        .Col(Q("hash") + " " + Dialect.Varchar(64) + " NOT NULL")
        .Col(Q("size") + " " + Dialect.IntType() + " NOT NULL")
        .Col(Q("uploader") + " " + Dialect.IntType() + " NOT NULL")
        .Col(Q("public") + " " + Dialect.TinyInt() + " NOT NULL")
        .Col(Q("upload_at") + " " + Dialect.DateTime() + " NOT NULL")
        .Col(Q("likes") + " " + Dialect.UnsignedInt() + " NOT NULL DEFAULT 0");

    private TableSpec Options() => Spec("options")
        .Col(Dialect.AutoIncrementPk("id", false))
        .Col(Q("option_name") + " " + Dialect.Varchar(50) + " NOT NULL")
        .Col(Q("option_value") + " " + Dialect.LongText() + " NOT NULL");

    private TableSpec UserCloset() => Spec("user_closet")
        .Col(Q("user_uid") + " " + Dialect.IntType() + " NOT NULL")
        .Col(Q("texture_tid") + " " + Dialect.IntType() + " NOT NULL")
        .Col(Q("item_name") + " " + Dialect.Text() + " NULL");

    private TableSpec Reports() => Spec("reports")
        .Col(Dialect.AutoIncrementPk("id", false))
        .Col(Q("tid") + " " + Dialect.IntType() + " NOT NULL")
        .Col(Q("uploader") + " " + Dialect.IntType() + " NOT NULL")
        .Col(Q("reporter") + " " + Dialect.IntType() + " NOT NULL")
        .Col(Q("reason") + " " + Dialect.LongText() + " NOT NULL")
        .Col(Q("status") + " " + Dialect.IntType() + " NOT NULL")
        .Col(Q("report_at") + " " + Dialect.DateTime() + " NOT NULL");

    private TableSpec Notifications() => Spec("notifications")
        .PkVarchar("id", 36)
        .Col(Q("type") + " " + Dialect.Varchar(255) + " NOT NULL")
        .Col(Q("notifiable_type") + " " + Dialect.Varchar(255) + " NOT NULL")
        .Col(Q("notifiable_id") + " " + Dialect.UnsignedBigInt() + " NOT NULL")
        .Col(Q("data") + " " + Dialect.Text() + " NOT NULL")
        .Col(Q("read_at") + " " + Dialect.Timestamp() + " NULL")
        .Col(Q("created_at") + " " + Dialect.Timestamp() + " NULL")
        .Col(Q("updated_at") + " " + Dialect.Timestamp() + " NULL")
        .Idx("notifiable_type", "notifiable_id");

    private TableSpec Jobs() => Spec("jobs")
        .Col(Dialect.AutoIncrementPk("id", true))
        .Col(Q("queue") + " " + Dialect.Varchar(255) + " NOT NULL")
        .Col(Q("payload") + " " + Dialect.LongText() + " NOT NULL")
        .Col(Q("attempts") + " " + Dialect.UnsignedTinyInt() + " NOT NULL")
        .Col(Q("reserved_at") + " " + Dialect.UnsignedInt() + " NULL")
        .Col(Q("available_at") + " " + Dialect.UnsignedInt() + " NOT NULL")
        .Col(Q("created_at") + " " + Dialect.UnsignedInt() + " NOT NULL")
        .Idx("queue");

    private TableSpec LanguageLines() => Spec("language_lines")
        .Col(Dialect.AutoIncrementPk("id", false))
        .Col(Q("group") + " " + Dialect.Varchar(255) + " NOT NULL")
        .Col(Q("key") + " " + Dialect.Varchar(255) + " NOT NULL")
        .Col(Q("text") + " " + Dialect.Text() + " NOT NULL")
        .Col(Q("created_at") + " " + Dialect.Timestamp() + " NULL")
        .Col(Q("updated_at") + " " + Dialect.Timestamp() + " NULL")
        .Idx("group");

    private TableSpec Scopes() => Spec("scopes")
        .Col(Dialect.AutoIncrementPk("id", false))
        .Col(Q("name") + " " + Dialect.Varchar(255) + " NOT NULL")
        .Col(Q("description") + " " + Dialect.Varchar(255) + " NOT NULL")
        .Uniq("name");

    private TableSpec OAuthAuthCodes() => Spec("oauth_auth_codes")
        .PkVarchar("id", 100)
        .Col(Q("user_id") + " " + Dialect.UnsignedBigInt() + " NOT NULL")
        .Col(Q("client_id") + " " + Dialect.UnsignedBigInt() + " NOT NULL")
        .Col(Q("scopes") + " " + Dialect.Text() + " NULL")
        .Col(Q("revoked") + " " + Dialect.Bool() + " NOT NULL")
        .Col(Q("expires_at") + " " + Dialect.DateTime() + " NULL")
        .Idx("user_id");

    private TableSpec OAuthAccessTokens() => Spec("oauth_access_tokens")
        .PkVarchar("id", 100)
        .Col(Q("user_id") + " " + Dialect.UnsignedBigInt() + " NULL")
        .Col(Q("client_id") + " " + Dialect.UnsignedBigInt() + " NOT NULL")
        .Col(Q("name") + " " + Dialect.Varchar(255) + " NULL")
        .Col(Q("scopes") + " " + Dialect.Text() + " NULL")
        .Col(Q("revoked") + " " + Dialect.Bool() + " NOT NULL")
        .Col(Q("created_at") + " " + Dialect.Timestamp() + " NULL")
        .Col(Q("updated_at") + " " + Dialect.Timestamp() + " NULL")
        .Col(Q("expires_at") + " " + Dialect.DateTime() + " NULL")
        .Idx("user_id");

    private TableSpec OAuthRefreshTokens() => Spec("oauth_refresh_tokens")
        .PkVarchar("id", 100)
        .Col(Q("access_token_id") + " " + Dialect.Varchar(100) + " NOT NULL")
        .Col(Q("revoked") + " " + Dialect.Bool() + " NOT NULL")
        .Col(Q("expires_at") + " " + Dialect.DateTime() + " NULL")
        .Idx("access_token_id");

    private TableSpec OAuthClients() => Spec("oauth_clients")
        .Col(Dialect.AutoIncrementPk("id", true))
        .Col(Q("user_id") + " " + Dialect.UnsignedBigInt() + " NULL")
        .Col(Q("name") + " " + Dialect.Varchar(255) + " NOT NULL")
        .Col(Q("secret") + " " + Dialect.Varchar(100) + " NULL")
        .Col(Q("provider") + " " + Dialect.Varchar(255) + " NULL")
        .Col(Q("redirect") + " " + Dialect.Text() + " NOT NULL")
        .Col(Q("personal_access_client") + " " + Dialect.Bool() + " NOT NULL")
        .Col(Q("password_client") + " " + Dialect.Bool() + " NOT NULL")
        .Col(Q("revoked") + " " + Dialect.Bool() + " NOT NULL")
        .Col(Q("created_at") + " " + Dialect.Timestamp() + " NULL")
        .Col(Q("updated_at") + " " + Dialect.Timestamp() + " NULL")
        .Idx("user_id");

    private TableSpec OAuthPersonalAccessClients() => Spec("oauth_personal_access_clients")
        .Col(Dialect.AutoIncrementPk("id", true))
        .Col(Q("client_id") + " " + Dialect.UnsignedBigInt() + " NOT NULL")
        .Col(Q("created_at") + " " + Dialect.Timestamp() + " NULL")
        .Col(Q("updated_at") + " " + Dialect.Timestamp() + " NULL");

    private TableSpec Communications() => Spec("communications", explicitEngine: true)
        .Col(Dialect.AutoIncrementPk("id", true))
        .Col(Q("author_id") + " " + Dialect.UnsignedInt() + " NULL")
        .Col(Q("legacy_key") + " " + Dialect.Varchar(100) + " NULL")
        .Col(Q("delivery_token") + " " + Dialect.Varchar(32) + " NULL")
        .Col(Q("title") + " " + Dialect.Varchar(100) + " NOT NULL")
        .Col(Q("kind") + " " + Dialect.Varchar(16) + " NOT NULL DEFAULT 'announcement'")
        .Col(Q("status") + " " + Dialect.Varchar(16) + " NOT NULL DEFAULT 'draft'")
        .Col(Q("locale") + " " + Dialect.Varchar(20) + " NOT NULL DEFAULT ''")
        .Col(Q("editor") + " " + Dialect.Varchar(16) + " NOT NULL DEFAULT 'blocks'")
        .Col(Q("source") + " " + Dialect.LongText() + " NULL")
        .Col(Q("blocks") + " " + Dialect.LongText() + " NULL")
        .Col(Q("audience") + " " + Dialect.Text() + " NULL")
        .Col(Q("published_at") + " " + Dialect.Timestamp() + " NULL")
        .Col(Q("created_at") + " " + Dialect.Timestamp() + " NULL")
        .Col(Q("updated_at") + " " + Dialect.Timestamp() + " NULL")
        .Uniq("legacy_key")
        .Idx("status");
}
