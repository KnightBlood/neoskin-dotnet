namespace NeoSkin.Web.Config;

/// <summary>
/// 运行时配置（对应源项目 <c>.env</c> + <c>config/*.php</c>）。
///
/// <p>Phase 2 只落地数据层需要的字段；其余分组（http/session/cache/storage/mail/plugins）
/// 随对应模块补齐。</p>
/// </summary>
public sealed class NeoSkinSettings
{
    public DatabaseSettings Database { get; } = new();

    /// <summary><c>APP_KEY</c>（<c>base64:...</c>）；用于 cookie 加密与 CSRF。</summary>
    public string AppKey { get; set; } = "";

    /// <summary>安全（对应 <c>config/secure.php</c> 的 cipher）。</summary>
    public SecureSettings Secure { get; } = new();

    /// <summary>哈希（对应 <c>config/hashing.php</c>）。</summary>
    public HashingSettings Hashing { get; } = new();

    /// <summary>存储（对应 <c>config/filesystems.php</c> 的 textures 盘）。</summary>
    public StorageSettings Storage { get; } = new();

    /// <summary>邮件（对应 <c>config/mail.php</c>）。</summary>
    public MailSettings Mail { get; } = new();

    /// <summary>插件目录（对应 <c>config/plugins.php</c>）。</summary>
    public PluginSettings Plugins { get; } = new();

    /// <summary>解析 APP_KEY 的原始字节；非法/为空返回 null。</summary>
    public byte[]? KeyBytes()
    {
        if (string.IsNullOrWhiteSpace(AppKey))
        {
            return null;
        }

        string raw = AppKey.StartsWith("base64:", StringComparison.Ordinal)
            ? AppKey["base64:".Length..]
            : AppKey;
        try
        {
            byte[] bytes = Convert.FromBase64String(raw);
            return bytes.Length is 16 or 32 ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>从环境变量读取（对应源项目的 <c>.env</c>）。</summary>
    public static NeoSkinSettings FromEnvironment()
    {
        var settings = new NeoSkinSettings
        {
            AppKey = Environment.GetEnvironmentVariable("APP_KEY")
                     ?? Environment.GetEnvironmentVariable("NEOSKIN_KEY") ?? "",

        };
        settings.Database.Connection = Environment.GetEnvironmentVariable("DB_CONNECTION") ?? settings.Database.Connection;
        settings.Database.Host = Environment.GetEnvironmentVariable("DB_HOST") ?? settings.Database.Host;
        settings.Database.Port = Environment.GetEnvironmentVariable("DB_PORT") ?? settings.Database.Port;
        settings.Database.Name = Environment.GetEnvironmentVariable("DB_DATABASE") ?? settings.Database.Name;
        settings.Database.Username = Environment.GetEnvironmentVariable("DB_USERNAME") ?? settings.Database.Username;
        settings.Database.Password = Environment.GetEnvironmentVariable("DB_PASSWORD") ?? settings.Database.Password;
        settings.Database.Prefix = Environment.GetEnvironmentVariable("DB_PREFIX") ?? settings.Database.Prefix;
        settings.Database.Socket = Environment.GetEnvironmentVariable("DB_SOCKET") ?? settings.Database.Socket;
        settings.Database.SslCa = Environment.GetEnvironmentVariable("MYSQL_ATTR_SSL_CA")
                                  ?? settings.Database.SslCa;
        settings.Database.Encrypt = BoolEnv("DB_ENCRYPT", settings.Database.Encrypt);
        settings.Database.TrustServerCertificate = BoolEnv("DB_TRUST_SERVER_CERTIFICATE",
            settings.Database.TrustServerCertificate);
        settings.Database.Url = Environment.GetEnvironmentVariable("DATABASE_URL")
                                ?? settings.Database.Url;
        Db.DatabaseUrl.Apply(settings.Database);
        settings.Storage.Root = Environment.GetEnvironmentVariable("NEOSKIN_STORAGE_ROOT")
                                ?? Path.Combine(Directory.GetCurrentDirectory(), "storage");
        settings.Env = Environment.GetEnvironmentVariable("APP_ENV") ?? settings.Env;
        settings.UpdateSource = Environment.GetEnvironmentVariable("UPDATE_SOURCE")
                                 ?? settings.UpdateSource;
        settings.Debug = bool.TryParse(Environment.GetEnvironmentVariable("APP_DEBUG"),
            out bool debug) && debug;
        return settings;
    }

    /// <summary>应用时区（源项目固定 <c>Asia/Shanghai</c>）。</summary>
    public string Timezone { get; set; } = "Asia/Shanghai";

    /// <summary>读取布尔环境变量（兼容 Laravel <c>env()</c> 的 <c>(true)</c> 等写法）。</summary>
    private static bool BoolEnv(string name, bool fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        string value = raw.Trim().Trim('(', ')').ToLowerInvariant();
        return value switch
        {
            "true" or "1" or "yes" or "on" => true,
            "false" or "0" or "no" or "off" or "null" or "empty" => false,
            _ => fallback,
        };
    }

    /// <summary><c>config('app.update_source')</c>。</summary>
    public string UpdateSource { get; set; } = "";

    /// <summary><c>config('app.version')</c>。</summary>
    public string Version { get; set; } = "6.0.11_patch";

    /// <summary><c>APP_ENV</c>（local / production / …）。</summary>
    public string Env { get; set; } = "production";

    /// <summary><c>APP_DEBUG</c>。</summary>
    public bool Debug { get; set; }

    public sealed class SecureSettings
    {
        /// <summary><c>PWD_METHOD</c>（BCRYPT / ARGON2I / ...）。</summary>
        public string Cipher { get; set; } = "BCRYPT";

        /// <summary><c>PWD_SALT</c>（SALTED2* 用）。</summary>
        public string Salt { get; set; } = "";
    }

    public sealed class HashingSettings
    {
        /// <summary><c>BCRYPT_ROUNDS</c>（用户密码；PHP 8.4+ 默认 12）。</summary>
        public int PasswordRounds { get; set; } = 12;
    }

    public sealed class StorageSettings
    {
        /// <summary><c>storage_path()</c>。</summary>
        public string Root { get; set; } = "";

        /// <summary>纹理盘根目录（相对 <c>base_path()</c> 或绝对路径）。</summary>
        public string Textures { get; set; } = "storage/textures";
    }

    public sealed class PluginSettings
    {
        /// <summary><c>base_path('plugins')</c>（可逗号分隔多目录）。</summary>
        public string Directory { get; set; } = "plugins";

        /// <summary><c>PLUGINS_REGISTRY</c>：插件市场源（可逗号分隔多仓库，含 <c>{lang}</c>）。</summary>
        public string Registry { get; set; } =
            "https://plugin.blessing.dreamcraft.eu.org/registry_{lang}.json";
    }

    /// <summary>邮件（对应 <c>config/mail.php</c>）。</summary>
    public sealed class MailSettings
    {
        /// <summary><c>MAIL_MAILER</c>：smtp / log / array。</summary>
        public string Mailer { get; set; } = "smtp";

        /// <summary><c>MAIL_HOST</c>。</summary>
        public string? Host { get; set; }

        /// <summary><c>MAIL_PORT</c>。</summary>
        public int Port { get; set; } = 587;

        /// <summary><c>MAIL_USERNAME</c>。</summary>
        public string? Username { get; set; }

        /// <summary><c>MAIL_PASSWORD</c>。</summary>
        public string? Password { get; set; }

        /// <summary><c>MAIL_ENCRYPTION</c>：tls / ssl / 空。</summary>
        public string? Encryption { get; set; }

        /// <summary><c>MAIL_FROM_ADDRESS</c>。</summary>
        public string? FromAddress { get; set; }

        /// <summary><c>MAIL_FROM_NAME</c>。</summary>
        public string? FromName { get; set; }
    }

    public sealed class DatabaseSettings
    {
        /// <summary><c>DB_CONNECTION</c>：sqlite / mysql / mariadb / pgsql / sqlsrv。</summary>
        public string Connection { get; set; } = "mysql";

        /// <summary><c>DB_HOST</c>。</summary>
        public string Host { get; set; } = "127.0.0.1";

        /// <summary><c>DB_PORT</c>；空表示用方言默认端口。</summary>
        public string Port { get; set; } = "";

        /// <summary><c>DB_DATABASE</c>（sqlite 时是文件路径）。</summary>
        public string Name { get; set; } = "blessingskin";

        /// <summary><c>DB_USERNAME</c>。</summary>
        public string Username { get; set; } = "root";

        /// <summary><c>DB_PASSWORD</c>。</summary>
        public string Password { get; set; } = "";

        /// <summary><c>DB_PREFIX</c>。</summary>
        public string Prefix { get; set; } = "";

        /// <summary><c>DB_SOCKET</c>（MySQL/MariaDB 的 Unix socket）。</summary>
        public string Socket { get; set; } = "";

        /// <summary><c>MYSQL_ATTR_SSL_CA</c>（MySQL 的 CA 证书路径）。</summary>
        public string SslCa { get; set; } = "";

        /// <summary><c>DB_ENCRYPT</c>（SQL Server，默认 true，与源配置一致）。</summary>
        public bool Encrypt { get; set; } = true;

        /// <summary><c>DB_TRUST_SERVER_CERTIFICATE</c>（SQL Server，默认 false）。</summary>
        public bool TrustServerCertificate { get; set; }

        /// <summary><c>DATABASE_URL</c>（优先级最高，与 Laravel 的 url 配置一致）。</summary>
        public string Url { get; set; } = "";
    }
}
