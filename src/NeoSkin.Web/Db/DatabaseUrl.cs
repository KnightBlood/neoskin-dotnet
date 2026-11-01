using NeoSkin.Web.Config;

namespace NeoSkin.Web.Db;

/// <summary>
/// <c>DATABASE_URL</c> 解析（Laravel <c>DatabaseUrlParser</c> 的子集）：
/// scheme 决定驱动与默认端口，userinfo/host/port/path 覆盖 <c>DB_*</c>。
/// </summary>
public static class DatabaseUrl
{
    /// <summary>把 URL 解析结果写回 <paramref name="database"/>（无 URL 时不动）。</summary>
    public static void Apply(NeoSkinSettings.DatabaseSettings database)
    {
        if (string.IsNullOrWhiteSpace(database.Url)
            || !Uri.TryCreate(database.Url, UriKind.Absolute, out Uri? uri))
        {
            return;
        }

        string scheme = uri.Scheme.ToLowerInvariant();
        string? driver = scheme switch
        {
            "sqlite" => "sqlite",
            "mysql" => "mysql",
            "mariadb" => "mariadb",
            "pgsql" or "postgres" or "postgresql" => "pgsql",
            "sqlsrv" or "sqlserver" => "sqlsrv",
            _ => null,
        };
        if (driver is null)
        {
            return;
        }

        database.Connection = driver;

        if (driver == "sqlite")
        {
            string path = uri.AbsolutePath;
            // sqlite:///C:/x.sqlite → /C:/x.sqlite；sqlite:///tmp/x → /tmp/x
            path = Uri.UnescapeDataString(path);
            if (path.Length >= 3 && path[0] == '/' && path[2] == ':')
            {
                path = path[1..];
            }

            if (path.Length > 0)
            {
                database.Name = path;
            }

            return;
        }

        if (!string.IsNullOrEmpty(uri.Host))
        {
            database.Host = uri.Host;
        }

        if (!uri.IsDefaultPort && uri.Port > 0)
        {
            database.Port = uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        string databaseName = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
        if (databaseName.Length > 0)
        {
            database.Name = databaseName;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            string[] parts = uri.UserInfo.Split(':', 2);
            database.Username = Uri.UnescapeDataString(parts[0]);
            if (parts.Length > 1)
            {
                database.Password = Uri.UnescapeDataString(parts[1]);
            }
        }
    }
}
