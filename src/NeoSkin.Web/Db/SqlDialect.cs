namespace NeoSkin.Web.Db;

/// <summary>
/// SQL 方言抽象：把 Laravel 的列类型映射成 5 种数据库的真实类型。
///
/// <p>类型映射对照 Laravel 13 的 <c>Schema\Grammars\*Grammar</c>（由
/// <c>conformance/data/schema.json</c> 的 <c>quirk.*</c> 锁定）。</p>
///
/// <p>与 PHP 的已知差异：本实现直接建**最终**表结构，而不是回放 27 个 migration 的
/// <c>alter table</c> / 临时表重建，所以语句序列不同、表结构语义相同
///（见 <c>PORT-NOTES.md</c>）。</p>
/// </summary>
public abstract class SqlDialect
{
    protected SqlDialect(string name) => Name = name;

    public string Name { get; }

    public static SqlDialect Of(string? connection)
    {
        string c = (connection ?? "").Trim().ToLowerInvariant();
        return c switch
        {
            "sqlite" => new Sqlite(),
            "mariadb" => new MariaDb(),
            "mysql" => new MySql(),
            "pgsql" or "postgres" or "postgresql" => new Postgres(),
            "sqlsrv" or "mssql" or "sqlserver" => new SqlServer(),
            _ => throw new ArgumentException(
                $"Unsupported DB_CONNECTION: {connection} (expected sqlite / mysql / mariadb / pgsql / sqlsrv)"),
        };
    }

    public abstract string Quote(string identifier);

    public abstract string JsonText(string column, string key);

    public abstract string CastText(string expr);

    public abstract string JsonInt(string column, string key);

    /// <summary><c>increments()</c> / <c>bigIncrements()</c> 的列定义。</summary>
    public abstract string AutoIncrementPk(string column, bool big);

    public abstract string Varchar(int length);

    /// <summary><c>uuid()</c>：SQLite 只能 varchar，MySQL char(36)，MariaDB/PG 原生 uuid。</summary>
    public abstract string Uuid();

    public abstract string Text();

    public abstract string LongText();

    public abstract string IntType();

    public abstract string UnsignedInt();

    public abstract string BigInt();

    public abstract string UnsignedBigInt();

    public abstract string TinyInt();

    public abstract string UnsignedTinyInt();

    public abstract string DateTime();

    public abstract string Timestamp();

    public abstract string Bool();

    public abstract string BoolLiteral(bool value);

    /// <summary>
    /// 字符串主键的位置：MySQL/MariaDB/SQLite 在 CREATE TABLE 末尾写
    /// <c>primary key (...)</c>；PostgreSQL/SQL Server 拆成独立的
    /// <c>alter table … add primary key</c>（语料 <c>quirk.primary_key_placement</c>）。
    /// </summary>
    public virtual bool InlineTablePrimaryKey => true;

    /// <summary>PostgreSQL/SQL Server 的独立主键语句；其他方言返回 null。</summary>
    public virtual string PrimaryKeyStatement(string table, string column) => "";

    /// <summary>建表语句后缀（仅 MySQL 族需要字符集；communications 另外带引擎）。</summary>
    public virtual string TableSuffix(bool explicitEngine = false) => "";

    /// <summary>
    /// 是否支持列位置（<c>after</c>）：只有 MySQL/MariaDB 支持；其余方言把后加的列
    /// 放在表尾（语料 <c>quirk.mysql_after_is_noop_elsewhere</c>）。
    /// </summary>
    public virtual bool SupportsColumnPosition => false;

    /// <summary>普通索引 DDL。</summary>
    public string Index(string name, string table, params string[] columns) =>
        "CREATE INDEX " + Quote(name) + " ON " + Quote(table) + " ("
        + string.Join(", ", columns.Select(Quote)) + ")";

    // ------------------------------------------------------------- 分页 / 行数限制

    public virtual string PagingClause() => " LIMIT ? OFFSET ?";

    public virtual bool OffsetBeforeLimit() => false;

    public virtual string FirstRowPrefix() => "";

    public virtual string FirstRowSuffix() => " LIMIT 1";

    public virtual string LimitPrefix() => "";

    public virtual string LimitSuffix() => " LIMIT ?";

    public virtual bool LimitParameterFirst() => false;

    public virtual string LimitLiteralPrefix(int n) => "";

    public virtual string LimitLiteralSuffix(int n) => " LIMIT " + n;

    public virtual bool FilteredUniqueIndex() => false;

    // ================================================================== MySQL / MariaDB

    public class MySql(string name = "mysql") : SqlDialect(name)
    {
        public override string Quote(string id) => "`" + id + "`";

        public override string AutoIncrementPk(string col, bool big) =>
            Quote(col) + (big
                ? " BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY"
                : " INT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY");

        public override string Varchar(int len) => "VARCHAR(" + len + ")";
        public override string Uuid() => "CHAR(36)";
        public override string Text() => "TEXT";
        public override string LongText() => "LONGTEXT";
        public override string IntType() => "INT";
        public override string UnsignedInt() => "INT UNSIGNED";
        public override string BigInt() => "BIGINT";
        public override string UnsignedBigInt() => "BIGINT UNSIGNED";
        public override string TinyInt() => "TINYINT";
        public override string UnsignedTinyInt() => "TINYINT UNSIGNED";
        public override string DateTime() => "DATETIME";
        public override string Timestamp() => "TIMESTAMP";
        public override string Bool() => "TINYINT(1)";
        public override string BoolLiteral(bool v) => v ? "1" : "0";

        public override string JsonText(string col, string key) =>
            "JSON_UNQUOTE(JSON_EXTRACT(" + col + ", '$." + key + "'))";

        public override string CastText(string expr) => "CAST(" + expr + " AS CHAR)";

        public override string JsonInt(string col, string key) =>
            "JSON_EXTRACT(" + col + ", '$." + key + "')";

        public override bool SupportsColumnPosition => true;

        /// <summary>Laravel 的 MySQL grammar 输出（collate 在前、engine 在后）。</summary>
        public override string TableSuffix(bool explicitEngine = false) =>
            " DEFAULT CHARACTER SET utf8mb4 COLLATE 'utf8mb4_unicode_ci'"
            + (explicitEngine ? " ENGINE = InnoDB" : "");
    }

    public sealed class MariaDb() : MySql("mariadb")
    {
        public override string Uuid() => "UUID";
    }

    // ================================================================== PostgreSQL

    public sealed class Postgres() : SqlDialect("pgsql")
    {
        public override string Quote(string id) => "\"" + id + "\"";

        public override string AutoIncrementPk(string col, bool big) =>
            Quote(col) + (big ? " BIGSERIAL NOT NULL PRIMARY KEY" : " SERIAL NOT NULL PRIMARY KEY");

        public override string Varchar(int len) => "VARCHAR(" + len + ")";
        public override string Uuid() => "UUID";
        public override string Text() => "TEXT";
        public override string LongText() => "TEXT";
        public override string IntType() => "INTEGER";
        public override string UnsignedInt() => "INTEGER";
        public override string BigInt() => "BIGINT";
        public override string UnsignedBigInt() => "BIGINT";
        public override string TinyInt() => "SMALLINT";
        public override string UnsignedTinyInt() => "SMALLINT";
        public override string DateTime() => "TIMESTAMP(0) WITHOUT TIME ZONE";
        public override string Timestamp() => "TIMESTAMP(0) WITHOUT TIME ZONE";
        public override string Bool() => "BOOLEAN";
        public override string BoolLiteral(bool v) => v ? "TRUE" : "FALSE";

        public override bool InlineTablePrimaryKey => false;

        public override string PrimaryKeyStatement(string table, string column) =>
            "ALTER TABLE " + Quote(table) + " ADD PRIMARY KEY (" + Quote(column) + ")";

        public override string JsonText(string col, string key) => "(" + col + "->>'" + key + "')";

        public override string CastText(string expr) => "CAST(" + expr + " AS TEXT)";

        public override string JsonInt(string col, string key) =>
            "(CAST(" + col + "->>'" + key + "' AS BIGINT))";
    }

    // ================================================================== SQL Server

    public sealed class SqlServer() : SqlDialect("sqlsrv")
    {
        public override string Quote(string id) => "\"" + id + "\"";

        public override string AutoIncrementPk(string col, bool big) =>
            Quote(col) + (big ? " BIGINT NOT NULL IDENTITY PRIMARY KEY" : " INT NOT NULL IDENTITY PRIMARY KEY");

        public override string Varchar(int len) => "NVARCHAR(" + len + ")";
        public override string Uuid() => "UNIQUEIDENTIFIER";
        public override string Text() => "NVARCHAR(MAX)";
        public override string LongText() => "NVARCHAR(MAX)";
        public override string IntType() => "INT";
        public override string UnsignedInt() => "INT";
        public override string BigInt() => "BIGINT";
        public override string UnsignedBigInt() => "BIGINT";
        public override string TinyInt() => "TINYINT";
        public override string UnsignedTinyInt() => "TINYINT";
        public override string DateTime() => "DATETIME";
        public override string Timestamp() => "DATETIME";
        public override string Bool() => "BIT";
        public override string BoolLiteral(bool v) => v ? "1" : "0";

        public override bool InlineTablePrimaryKey => false;

        public override string PrimaryKeyStatement(string table, string column) =>
            "ALTER TABLE " + Quote(table) + " ADD CONSTRAINT " + Quote(table + "_" + column + "_primary")
            + " PRIMARY KEY (" + Quote(column) + ")";

        public override string JsonText(string col, string key) =>
            "JSON_VALUE(" + col + ", '$." + key + "')";

        public override string CastText(string expr) => "CAST(" + expr + " AS NVARCHAR(4000))";

        public override string JsonInt(string col, string key) =>
            "CAST(JSON_VALUE(" + col + ", '$." + key + "') AS BIGINT)";

        public override string PagingClause() => " OFFSET ? ROWS FETCH NEXT ? ROWS ONLY";
        public override bool OffsetBeforeLimit() => true;
        public override string FirstRowPrefix() => "TOP 1 ";
        public override string FirstRowSuffix() => "";
        public override string LimitPrefix() => "TOP (?) ";
        public override string LimitSuffix() => "";
        public override bool LimitParameterFirst() => true;
        public override string LimitLiteralPrefix(int n) => "TOP " + n + " ";
        public override string LimitLiteralSuffix(int n) => "";
        public override bool FilteredUniqueIndex() => true;
    }

    // ================================================================== SQLite

    public sealed class Sqlite() : SqlDialect("sqlite")
    {
        public override string Quote(string id) => "\"" + id + "\"";

        public override string AutoIncrementPk(string col, bool big) =>
            Quote(col) + " INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL";

        /// <summary>SQLite 的 <c>varchar(n)</c> 只写 varchar（长度无意义），与 PHP 一致。</summary>
        public override string Varchar(int len) => "VARCHAR";

        public override string Uuid() => "VARCHAR";
        public override string Text() => "TEXT";
        public override string LongText() => "TEXT";
        public override string IntType() => "INTEGER";
        public override string UnsignedInt() => "INTEGER";
        public override string BigInt() => "INTEGER";
        public override string UnsignedBigInt() => "INTEGER";

        /// <summary>PHP 的 SQLite grammar 把 <c>tinyInteger()</c> 写成 integer（语料 quirk.tinyint_mapping）。</summary>
        public override string TinyInt() => "INTEGER";

        public override string UnsignedTinyInt() => "INTEGER";
        public override string DateTime() => "DATETIME";
        public override string Timestamp() => "DATETIME";
        public override string Bool() => "TINYINT(1)";
        public override string BoolLiteral(bool v) => v ? "1" : "0";

        public override string JsonText(string col, string key) =>
            "JSON_EXTRACT(" + col + ", '$." + key + "')";

        public override string CastText(string expr) => "CAST(" + expr + " AS TEXT)";

        public override string JsonInt(string col, string key) =>
            "JSON_EXTRACT(" + col + ", '$." + key + "')";
    }
}
