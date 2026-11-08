using System.Security.Cryptography;
using NeoSkin.Web.Config;
using NeoSkin.Web.Db;
using NeoSkin.Web.Paths;
using NeoSkin.Web.Service;
using NeoSkin.Web.Service.Cipher;
using NeoSkin.Web.Support;

namespace NeoSkin.Web.Cli;

/// <summary>
/// <c>php artisan ...</c> 的等价物（<c>--neoskin.command=&lt;name&gt;</c>）。
///
/// <p>覆盖源项目的 6 个命令：</p>
/// <list type="bullet">
///   <item><c>bs:install</c>（别名 <c>install</c>）</item>
///   <item><c>update</c></item>
///   <item><c>options:cache</c></item>
///   <item><c>plugin:enable</c> / <c>plugin:disable</c></item>
///   <item><c>salt:random [--show]</c></item>
/// </list>
///
/// <p>CLI 模式不启动 HTTP 监听；成功返回 0，参数/执行失败返回 1。</p>
/// </summary>
public static class CliRunner
{
    private const string CommandPrefix = "--neoskin.command=";

    /// <summary>是否请求了 CLI 命令。</summary>
    public static bool TryCommand(string[] args, out string command)
    {
        foreach (string arg in args)
        {
            if (arg.StartsWith(CommandPrefix, StringComparison.Ordinal))
            {
                command = arg[CommandPrefix.Length..];
                return !string.IsNullOrWhiteSpace(command);
            }
        }

        command = "";
        return false;
    }

    /// <summary>执行命令并返回进程退出码。</summary>
    public static int Run(string command, string[] args)
    {
        try
        {
            NeoSkinSettings settings = NeoSkinSettings.FromEnvironment();
            var database = new Database(settings);
            var schema = new SchemaInstaller(database.Open, settings);
            var paths = new AppPaths(Directory.GetCurrentDirectory(), settings);
            var option = new Option(database, schema, paths);
            var updates = new UpdateRunner(database, schema, option, settings, paths);

            return command switch
            {
                "install" or "bs:install" => Install(args, settings, database, schema, paths, option),
                "update" => Update(updates, option),
                "options:cache" => OptionsCache(option),
                "plugin:enable" => Plugin(args, settings, paths, option, enable: true),
                "plugin:disable" => Plugin(args, settings, paths, option, enable: false),
                "salt:random" => SaltRandom(args),
                _ => Unknown(command),
            };
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Command failed: {e.Message}");
            return 1;
        }
    }

    private static int Install(string[] args, NeoSkinSettings settings, Database database,
        SchemaInstaller schema, AppPaths paths, Option option)
    {
        if (File.Exists(paths.InstallLock))
        {
            Console.WriteLine("You have installed NeoSkin. Nothing to do.");
            return 0;
        }

        string email = Required(args, "--email", 0);
        string password = Required(args, "--password", 1);
        string nickname = Required(args, "--nickname", 2);
        string siteName = Value(args, "--site-name") ?? "NeoSkin";

        EnsureAppKey(settings);

        schema.Install();
        new SchemaMigrator(database, schema, option, settings).Migrate();

        option.Set("site_name", siteName);
        option.Set("site_url", Environment.GetEnvironmentVariable("APP_URL") ?? "http://localhost/");

        string now = PhpDate.Now(settings.Timezone);
        var cipher = CipherConfig.Create(settings);
        new UserRepository(database).InsertSuperAdmin(email, nickname,
            option.GetInt("user_initial_score", 1000),
            cipher.Hash(password, option.GetString("pwd_salt", null)),
            "127.0.0.1", now,
            PhpDate.At(DateTimeOffset.UtcNow.AddDays(-1), settings.Timezone));

        Directory.CreateDirectory(Path.GetDirectoryName(paths.InstallLock)!);
        File.WriteAllText(paths.InstallLock, "");

        Console.WriteLine("Installation completed!");
        Console.WriteLine("We recommend to modify your \"Site URL\" option if incorrect.");
        return 0;
    }

    private static int Update(UpdateRunner updates, Option option)
    {
        updates.Run();
        option.ClearCache();
        Console.WriteLine("Update completed.");
        return 0;
    }

    private static int OptionsCache(Option option)
    {
        option.ClearCache();
        option.WriteCache();
        Console.WriteLine("Options cached successfully.");
        return 0;
    }

    private static int Plugin(string[] args, NeoSkinSettings settings, AppPaths paths,
        Option option, bool enable)
    {
        string name = Required(args, "--name", 0);
        var manager = new PluginManager(option, settings, paths);
        if (!manager.All().ContainsKey(name))
        {
            // PHP `plugin:enable` 对不存在的插件返回 false 并输出 not-found。
            Console.Error.WriteLine($"Plugin [{name}] not found.");
            return 1;
        }

        if (enable)
        {
            var failure = manager.Enable(name);
            if (failure is { } problems)
            {
                Console.Error.WriteLine(
                    $"Plugin [{name}] cannot be enabled (unsatisfied: {problems.Unsatisfied.Count}, conflicts: {problems.Conflicts.Count}).");
                return 1;
            }

            Console.WriteLine($"Plugin [{name}] enabled.");
        }
        else
        {
            manager.Disable(name);
            Console.WriteLine($"Plugin [{name}] disabled.");
        }

        return 0;
    }

    private static int SaltRandom(string[] args)
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(16);
        string salt = Convert.ToHexStringLower(bytes);
        if (args.Contains("--show", StringComparer.Ordinal))
        {
            Console.WriteLine(salt);
            return 0;
        }

        WriteEnv("SALT", salt);
        Console.WriteLine($"Application salt [{salt}] set successfully.");
        return 0;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        return 1;
    }

    // ------------------------------------------------------------- 参数/环境

    /// <summary>取值：优先 <c>--name=value</c>，否则回退到第 <paramref name="position"/> 个位置参数。</summary>
    private static string Required(string[] args, string name, int position)
    {
        string? value = Value(args, name) ?? Positional(args, position);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"missing required option {name}");
        }

        return value;
    }

    private static string? Value(string[] args, string name)
    {
        string prefix = name + "=";
        foreach (string arg in args)
        {
            if (arg.StartsWith(prefix, StringComparison.Ordinal))
            {
                return arg[prefix.Length..];
            }
        }

        return null;
    }

    private static string? Positional(string[] args, int index)
    {
        int seen = 0;
        foreach (string arg in args)
        {
            if (arg.StartsWith('-'))
            {
                continue;
            }

            if (seen == index)
            {
                return arg;
            }

            seen++;
        }

        return null;
    }

    /// <summary>与 <c>php artisan key:generate</c> 等价：缺 <c>APP_KEY</c> 时生成并写入 <c>.env</c>。</summary>
    private static void EnsureAppKey(NeoSkinSettings settings)
    {
        if (settings.KeyBytes() is not null)
        {
            return;
        }

        string key = "base64:" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        settings.AppKey = key;
        WriteEnv("APP_KEY", key);
    }

    /// <summary>替换或追加 <c>.env</c> 中的键。</summary>
    private static void WriteEnv(string name, string value)
    {
        string path = Path.Combine(Directory.GetCurrentDirectory(), ".env");
        var lines = File.Exists(path)
            ? File.ReadAllLines(path).ToList()
            : new List<string>();

        bool replaced = false;
        for (int index = 0; index < lines.Count; index++)
        {
            if (lines[index].TrimStart().StartsWith(name + "=", StringComparison.Ordinal))
            {
                lines[index] = $"{name}={value}";
                replaced = true;
                break;
            }
        }

        if (!replaced)
        {
            lines.Add($"{name}={value}");
        }

        File.WriteAllLines(path, lines);
    }
}
