using System.Data.Common;
using Microsoft.Data.Sqlite;
using NeoSkin.Web.Config;

namespace NeoSkin.Web.Db;

/// <summary>
/// 数据访问入口：持有连接工厂与方言，提供参数化查询的薄封装。
///
/// <p>对应 Java 版的 <c>DataSource + SqlDialect + Paging</c> 组合。仓库类只依赖本类，
/// 不直接碰驱动，方便 5 种数据库切换（以及 AOT 下避免反射加载驱动）。</p>
/// </summary>
public sealed class Database
{
    private readonly Func<DbConnection> _factory;

    public Database(NeoSkinSettings settings, Func<DbConnection>? factory = null)
    {
        Settings = settings;
        DatabaseUrl.Apply(settings.Database);
        Dialect = SqlDialect.Of(settings.Database.Connection);
        _factory = factory ?? (() => CreateConnection(settings));
    }

    public NeoSkinSettings Settings { get; }

    public SqlDialect Dialect { get; }

    public DbConnection Open()
    {
        DbConnection connection = _factory();
        connection.Open();
        return connection;
    }

    /// <summary>按配置创建连接（sqlite / mysql / mariadb / pgsql / sqlsrv）。</summary>
    public static DbConnection CreateConnection(NeoSkinSettings settings)
    {
        DatabaseUrl.Apply(settings.Database);
        NeoSkinSettings.DatabaseSettings db = settings.Database;
        return ResolveDialect(settings).Name switch
        {
            "sqlite" => new SqliteConnection("Data Source=" + ResolveSqlitePath(settings)),
            "mysql" or "mariadb" => new MySqlConnector.MySqlConnection(MySqlConnectionString(db)),
            "pgsql" => new Npgsql.NpgsqlConnection(PostgresConnectionString(db)),
            "sqlsrv" => new Microsoft.Data.SqlClient.SqlConnection(SqlServerConnectionString(db)),
            _ => throw new NotSupportedException(
                $"DB_CONNECTION={settings.Database.Connection} 的驱动尚未接入"),
        };
    }

    private static string MySqlConnectionString(NeoSkinSettings.DatabaseSettings db)
    {
        var builder = new MySqlConnector.MySqlConnectionStringBuilder
        {
            Server = db.Host,
            UserID = db.Username,
            Password = db.Password,
            Database = db.Name,
            CharacterSet = "utf8mb4",
            AllowPublicKeyRetrieval = true,
        };
        if (!string.IsNullOrWhiteSpace(db.Socket))
        {
            builder.Server = db.Socket;
            builder.ConnectionProtocol = MySqlConnector.MySqlConnectionProtocol.Unix;
        }

        if (!string.IsNullOrWhiteSpace(db.SslCa))
        {
            builder.SslCa = db.SslCa;
        }

        if (int.TryParse(db.Port, out int port) && port > 0)
        {
            builder.Port = (uint)port;
        }

        return builder.ConnectionString;
    }

    private static string PostgresConnectionString(NeoSkinSettings.DatabaseSettings db)
    {
        var builder = new Npgsql.NpgsqlConnectionStringBuilder
        {
            Host = db.Host,
            Username = db.Username,
            Password = db.Password,
            Database = db.Name,
            SearchPath = "public",
            ApplicationName = "blessing_skin",
        };
        if (int.TryParse(db.Port, out int port) && port > 0)
        {
            builder.Port = port;
        }

        return builder.ConnectionString;
    }

    private static string SqlServerConnectionString(NeoSkinSettings.DatabaseSettings db)
    {
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder
        {
            DataSource = db.Host,
            UserID = db.Username,
            Password = db.Password,
            InitialCatalog = db.Name,
            TrustServerCertificate = db.TrustServerCertificate,
        };
        if (int.TryParse(db.Port, out int port) && port > 0)
        {
            builder.DataSource = db.Host + "," + port;
        }

        if (!db.Encrypt)
        {
            builder.Encrypt = Microsoft.Data.SqlClient.SqlConnectionEncryptOption.Optional;
        }

        return builder.ConnectionString;
    }

    private static SqlDialect ResolveDialect(NeoSkinSettings settings) => SqlDialect.Of(settings.Database.Connection);

    /// <summary>
    /// SQLite 的相对路径按应用工作目录解析（对应 Laravel 的 <c>database_path()</c> 行为：
    /// <c>DB_DATABASE</c> 缺省是 <c>database/database.sqlite</c>）。
    /// </summary>
    private static string ResolveSqlitePath(NeoSkinSettings settings)
    {
        string name = string.IsNullOrWhiteSpace(settings.Database.Name)
            ? "database/database.sqlite"
            : settings.Database.Name;
        return Path.IsPathRooted(name) ? name : Path.GetFullPath(name);
    }

    // ------------------------------------------------------------------ 薄封装

    public int Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using DbConnection connection = Open();
        using DbCommand command = Command(connection, sql, parameters);
        return command.ExecuteNonQuery();
    }

    public object? Scalar(string sql, params (string Name, object? Value)[] parameters)
    {
        using DbConnection connection = Open();
        using DbCommand command = Command(connection, sql, parameters);
        object? value = command.ExecuteScalar();
        return value is DBNull ? null : value;
    }

    public long Long(string sql, params (string Name, object? Value)[] parameters)
    {
        object? value = Scalar(sql, parameters);
        return value is null ? 0 : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    public List<T> Query<T>(string sql, Func<DbDataReader, T> map,
        params (string Name, object? Value)[] parameters)
    {
        using DbConnection connection = Open();
        using DbCommand command = Command(connection, sql, parameters);
        using DbDataReader reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read())
        {
            rows.Add(map(reader));
        }

        return rows;
    }

    public T? QueryFirst<T>(string sql, Func<DbDataReader, T> map,
        params (string Name, object? Value)[] parameters) where T : class
    {
        List<T> rows = Query(sql, map, parameters);
        return rows.Count == 0 ? null : rows[0];
    }

    public static DbCommand Command(DbConnection connection, string sql,
        params (string Name, object? Value)[] parameters)
    {
        DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters)
        {
            DbParameter parameter;
            if (connection is Npgsql.NpgsqlConnection && value is string text)
            {
                // 源 PDO 的 pgsql 驱动把字符串参数交给服务端做隐式转换
                // （timestamp / bool / 枚举等）。Npgsql 默认发 text 会拒绝，
                // 这里按 stringtype=unspecified 的等价语义发 unknown。
                parameter = new Npgsql.NpgsqlParameter(name, NpgsqlTypes.NpgsqlDbType.Unknown)
                {
                    Value = text,
                };
            }
            else
            {
                parameter = command.CreateParameter();
                parameter.ParameterName = name;
                parameter.Value = value ?? DBNull.Value;
            }

            command.Parameters.Add(parameter);
        }

        return command;
    }

    /// <summary>
    /// 插入后取自增主键：SQLite <c>last_insert_rowid()</c>、MySQL <c>LAST_INSERT_ID()</c>、
    /// PostgreSQL <c>currval(pg_get_serial_sequence(...))</c>、SQL Server <c>SCOPE_IDENTITY()</c>。
    /// </summary>
    public long InsertReturningId(string table, string primaryKey, string sql,
        params (string Name, object? Value)[] parameters)
    {
        using DbConnection connection = Open();
        if (Dialect.Name == "sqlsrv")
        {
            // SCOPE_IDENTITY() 只对当前批次有效；单独一条命令会返回 NULL。
            using DbCommand batch = Command(connection, sql + "; SELECT SCOPE_IDENTITY()",
                parameters);
            object? identity = batch.ExecuteScalar();
            return identity is null or DBNull
                ? 0
                : Convert.ToInt64(identity, System.Globalization.CultureInfo.InvariantCulture);
        }

        using (DbCommand insert = Command(connection, sql, parameters))
        {
            insert.ExecuteNonQuery();
        }

        string idSql = Dialect.Name switch
        {
            "sqlite" => "SELECT last_insert_rowid()",
            "mysql" or "mariadb" => "SELECT LAST_INSERT_ID()",
            "pgsql" => $"SELECT currval(pg_get_serial_sequence('{table}', '{primaryKey}'))",
            _ => throw new NotSupportedException(Dialect.Name),
        };
        using DbCommand idCommand = Command(connection, idSql);
        object? value = idCommand.ExecuteScalar();
        return value is null or DBNull ? 0 : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }
}
