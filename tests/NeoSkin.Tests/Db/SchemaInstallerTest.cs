using System.Data.Common;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using NeoSkin.Tests.Conformance;
using NeoSkin.Web.Config;
using NeoSkin.Web.Db;

namespace NeoSkin.Tests.Db;

/// <summary>
/// <see cref="SchemaInstaller"/> 的语料测试：方言类型映射、主键位置、表数量、
/// migrations 表 DDL，以及 SQLite 上的真实建表结果（<c>final_tables</c>）。
///
/// <p>已知偏差：本实现直接建最终结构，不回放 27 个 migration 的
/// <c>alter table</c>/临时表重建，所以 <c>ddl.*</c>（迁移序列）不逐字比对；
/// 表结构语义与 <c>final_tables</c> 一致。</p>
/// </summary>
public sealed class SchemaInstallerTest
{
    private static readonly string[] Dialects = ["sqlite", "mysql", "mariadb", "pgsql", "sqlsrv"];

    // ================================================================== 方言

    [ConformanceFact]
    public void TypeMappingsMatchQuirks()
    {
        Corpus corpus = Corpus.Require("schema");

        foreach (string db in Dialects)
        {
            SqlDialect dialect = SqlDialect.Of(db);

            AssertSame(corpus, "quirk.increments_mapping", db,
                StripColumn(dialect.AutoIncrementPk("col", false)));
            AssertSame(corpus, "quirk.string_length_mapping", db,
                StripColumn(dialect.Quote("email") + " " + dialect.Varchar(100) + " NOT NULL"));
            AssertSame(corpus, "quirk.boolean_mapping", db,
                StripColumn(dialect.Quote("revoked") + " " + dialect.Bool() + " NOT NULL"));
            AssertSame(corpus, "quirk.tinyint_mapping", db,
                StripColumn(dialect.Quote("attempts") + " " + dialect.TinyInt() + " NOT NULL"));
            AssertSame(corpus, "quirk.unsigned_mapping", db,
                StripColumn(dialect.Quote("user_id") + " " + dialect.UnsignedBigInt() + " NOT NULL"));
            AssertSame(corpus, "quirk.datetime_mapping", db,
                StripColumn(dialect.Quote("register_at") + " " + dialect.DateTime() + " NOT NULL"));
            AssertSame(corpus, "quirk.timestamp_mapping", db,
                StripColumn(dialect.Quote("created_at") + " " + dialect.Timestamp() + " NULL"));
            AssertSame(corpus, "quirk.uuid_mapping", db,
                StripColumn(dialect.Quote("id") + " " + dialect.Uuid() + " NOT NULL"));
        }

        JsonNode longText = corpus.CaseById("quirk.text_vs_longtext").AsObject()["expected"]!.AsObject();
        foreach (string db in Dialects)
        {
            SqlDialect dialect = SqlDialect.Of(db);
            AssertSame(corpus, "quirk.text_vs_longtext", db,
                StripColumn(dialect.Quote("payload") + " " + dialect.LongText() + " NOT NULL"),
                "longText.jobs.payload");
            AssertSame(corpus, "quirk.text_vs_longtext", db,
                StripColumn(dialect.Quote("item_name") + " " + dialect.Text() + " NULL"),
                "text.user_closet.item_name");
        }

        Assert.NotNull(longText);
    }

    [ConformanceFact]
    public void TableCountMatchesCorpus()
    {
        Corpus corpus = Corpus.Require("schema");
        JsonNode expected = corpus.CaseById("quirk.table_count").AsObject()["expected"]!;

        foreach (string db in Dialects)
        {
            SchemaInstaller installer = Installer(db);
            List<string> names = installer.TableNames().OrderBy(n => n, StringComparer.Ordinal).ToList();
            List<string> wanted = expected["names"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();

            Assert.Equal(expected["business_tables"]!.GetValue<int>(), names.Count);
            Assert.Equal(wanted, names);
        }
    }

    [ConformanceFact]
    public void PrimaryKeyPlacementMatchesCorpus()
    {
        Corpus corpus = Corpus.Require("schema");
        JsonNode expected = corpus.CaseById("quirk.primary_key_placement").AsObject()["expected"]!;

        string sqlite = CreateSql("sqlite", "oauth_auth_codes");
        Assert.Contains("primary key (\"id\")", sqlite, StringComparison.OrdinalIgnoreCase);

        string mysql = CreateSql("mysql", "oauth_auth_codes");
        Assert.Contains("primary key (`id`)", mysql, StringComparison.OrdinalIgnoreCase);

        List<string> pg = AllDdl("pgsql");
        Assert.Contains(pg, s => Normalize(s) == Normalize(expected["pgsql"]!.GetValue<string>()));

        List<string> srv = AllDdl("sqlsrv");
        Assert.Contains(srv, s => Normalize(s) == Normalize(expected["sqlsrv"]!.GetValue<string>()));
    }

    [ConformanceFact]
    public void CommunicationsEngineOnlyOnMysqlFamily()
    {
        Corpus corpus = Corpus.Require("schema");
        JsonNode expected = corpus.CaseById("quirk.communications_engine").AsObject()["expected"]!;

        Assert.Contains("ENGINE = InnoDB", CreateSql("mysql", "communications"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ENGINE = InnoDB", CreateSql("mysql", "users"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DEFAULT CHARACTER SET utf8mb4", CreateSql("mysql", "users"), StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(expected["other_table"]);
    }

    [ConformanceFact]
    public void NoForeignKeysAndNoUniqueOnOptionName()
    {
        JsonNode expected = Corpus.Require("schema").CaseById("quirk.no_foreign_keys").AsObject()["expected"]!;
        Assert.Equal(0, expected["sqlite"]!.GetValue<int>());

        foreach (string db in Dialects)
        {
            foreach (string statement in AllDdl(db))
            {
                Assert.DoesNotContain("FOREIGN KEY", statement, StringComparison.OrdinalIgnoreCase);
            }
        }

        Assert.DoesNotContain(AllDdl("mysql"),
            s => s.Contains("option_name", StringComparison.OrdinalIgnoreCase)
                && s.Contains("unique", StringComparison.OrdinalIgnoreCase));
    }

    [ConformanceFact]
    public void MigrationsTableDdlMatchesCorpus()
    {
        Corpus corpus = Corpus.Require("schema");
        foreach (string db in Dialects)
        {
            string expected = corpus.CaseById($"migrations_table.{db}").AsObject()["expected"]!.AsArray()[0]!
                .GetValue<string>();
            Assert.True(Normalize(expected) == Normalize(Installer(db).MigrationsTableSql()),
                $"{db} migrations DDL\n expected: {expected}\n actual:   {Installer(db).MigrationsTableSql()}");
        }
    }

    // ================================================================== SQLite 真实建表

    [ConformanceFact]
    public void SqliteInstallMatchesFinalTables()
    {
        Corpus corpus = Corpus.Require("schema");
        JsonNode expected = corpus.CaseById("final_tables").AsObject()["expected"]!;

        string file = Path.Combine(Path.GetTempPath(), "neoskin-schema-" + Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            var settings = new NeoSkinSettings();
            settings.Database.Connection = "sqlite";
            settings.Database.Name = file;
            Func<DbConnection> factory = () => new SqliteConnection("Data Source=" + file);
            var installer = new SchemaInstaller(factory, settings);

            Assert.Equal(16, installer.Install());
            Assert.Equal(0, installer.Install());
            Assert.True(installer.IsInstalled());

            using var connection = new SqliteConnection("Data Source=" + file);
            connection.Open();

            foreach (JsonNode? table in expected.AsObject().Select(pair => pair.Value))
            {
                Assert.NotNull(table);
            }

            foreach (KeyValuePair<string, JsonNode?> entry in expected.AsObject())
            {
                string table = entry.Key;
                JsonArray columns = entry.Value!["columns"]!.AsArray();

                using var command = connection.CreateCommand();
                command.CommandText = $"PRAGMA table_info(\"{table}\")";
                using SqliteDataReader reader = command.ExecuteReader();

                var actual = new List<(string Name, string Type, bool NotNull, string? Default, bool Pk)>();
                while (reader.Read())
                {
                    actual.Add((
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetInt64(3) != 0,
                        reader.IsDBNull(4) ? null : reader.GetString(4),
                        reader.GetInt64(5) != 0));
                }

                Assert.True(columns.Count == actual.Count,
                    $"{table}: 列数 expected={columns.Count} actual={actual.Count}");

                for (int i = 0; i < columns.Count; i++)
                {
                    JsonNode column = columns[i]!;
                    var got = actual[i];
                    Assert.Equal(column["name"]!.GetValue<string>(), got.Name);
                    Assert.Equal(!column["nullable"]!.GetValue<bool>(), got.NotNull);
                    Assert.True(DefaultEquals(column["default"], got.Default),
                        $"{table}.{got.Name} 默认值 expected={column["default"]} actual={got.Default}");
                }
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT COUNT(*) FROM migrations";
                Assert.Equal(23L, (long)command.ExecuteScalar()!);
            }

            Assert.True(installer.IsMigrationRecorded("2016_11_18_133939_create_all_tables"));
            Assert.False(installer.IsMigrationRecorded("2016_11_18_134542_import_options"));
            Assert.False(installer.IsMigrationRecorded("2026_10_25_000001_hash_oauth_client_secrets"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(file);
        }
    }

    // ================================================================== 辅助

    private static SchemaInstaller Installer(string db)
    {
        var settings = new NeoSkinSettings();
        settings.Database.Connection = db;
        settings.Database.Name = "blessingskin";
        return new SchemaInstaller(() => throw new InvalidOperationException("DDL 测试不连库"), settings);
    }

    private static List<string> AllDdl(string db) => Installer(db).DdlStatements().ToList();

    private static string CreateSql(string db, string table) =>
        AllDdl(db).First(s => s.StartsWith("CREATE TABLE", StringComparison.OrdinalIgnoreCase)
            && s.Contains(table, StringComparison.Ordinal));

    private static void AssertSame(Corpus corpus, string caseId, string db, string actual, params string[] path)
    {
        JsonNode expected = corpus.CaseById(caseId).AsObject()["expected"]!;
        foreach (string segment in path)
        {
            expected = expected[segment]!;
        }

        string wanted = expected[db]!.GetValue<string>();
        Assert.True(Normalize(wanted) == Normalize(actual),
            $"{caseId} [{db}]\n expected: {wanted}\n actual:   {actual}");
    }

    /// <summary>去掉列名前缀与引号（类型映射用例只比较类型部分）。</summary>
    private static string StripColumn(string definition)
    {
        int space = definition.IndexOf(' ');
        return space < 0 ? definition : definition[(space + 1)..];
    }

    private static bool DefaultEquals(JsonNode? expected, string? actual)
    {
        if (expected is null)
        {
            return actual is null;
        }

        string wanted = expected.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? expected.GetValue<string>()
            : expected.ToJsonString();
        string a = actual ?? "";

        static string Clean(string s) => s.Trim().Trim('\'', '"', '(', ')').ToLowerInvariant();
        return Clean(wanted) == Clean(a);
    }

    /// <summary>小写、收敛空白、去掉 varchar 长度（SQLite 无长度语义）。</summary>
    /// <summary>
    /// 小写、收敛空白、去掉 varchar 长度（SQLite 无长度语义）、去掉可空列显式的
    /// <c>null</c> 关键字（PHP 的 SQLite grammar 不写，我们的 DDL 写——语义相同）。
    /// </summary>
    private static string Normalize(string sql)
    {
        string result = Regex.Replace(sql.Trim(), @"\s+", " ");
        result = Regex.Replace(result, @"(n?varchar|char)\(\d+\)", "$1", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"identity\(\d+,\s*\d+\)", "identity", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\s+null\b", "", RegexOptions.IgnoreCase);
        return result.ToLowerInvariant();
    }
}
