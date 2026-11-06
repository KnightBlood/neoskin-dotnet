using NeoSkin.Web.Config;
using NeoSkin.Web.Db;
using NeoSkin.Web.Event;
using NeoSkin.Web.Paths;

namespace NeoSkin.Web.Service;

/// <summary>
/// 版本升级编排（对应 PHP <c>App\Console\Commands\UpdateCommand</c> 与
/// <c>RedirectToSetup</c> 里的 <c>Artisan::call('update')</c>）。
///
/// <para>PHP 的更新流程：</para>
/// <list type="number">
///   <item>按版本执行数据过程：<c>0.0.1</c> → <c>event('__0.0.1')</c>；
///     <c>5.0.0</c> → 把 <c>home_pic_url</c> 从 <c>./app/bg.jpg</c> 改成
///     <c>./app/bg.webp</c>；</item>
///   <item><c>option(['version' =&gt; config('app.version')])</c>；</item>
///   <item><c>migrate --force</c>（本工程为 <see cref="SchemaInstaller"/> + <see cref="SchemaMigrator"/>）；</item>
///   <item><c>view:clear</c>；</item>
///   <item>写 <c>storage/install.lock</c>；</item>
///   <item><c>Cache::flush()</c>。</item>
/// </list>
///
/// <para>并发：多个请求可能同时发现站点过期，用 <see cref="Gate"/> 串行化，
/// 拿到锁后重新检查版本，保证更新只执行一次。</para>
/// </summary>
public sealed class UpdateRunner(
    Database db,
    SchemaInstaller schema,
    Option option,
    NeoSkinSettings settings,
    AppPaths paths,
    Action? flushCaches = null)
{
    /// <summary>进程内更新锁（PHP 靠 Web 进程模型 + Artisan 单进程，这里用静态锁）。</summary>
    private static readonly object Gate = new();

    /// <summary>PHP <c>UpdateCommand::procedures()</c> 里的过程版本。</summary>
    public const string EventProcedureVersion = "0.0.1";

    public const string HomePictureProcedureVersion = "5.0.0";

    /// <summary>站点版本是否落后于当前程序版本（<c>Comparator::greaterThan</c>）。</summary>
    public bool NeedsUpdate()
    {
        string installed = InstalledVersion();
        return Semver.LessThan(installed, settings.Version);
    }

    /// <summary>安装记录里的版本；缺失时按空串处理（与 PHP <c>option('version', ...)</c> 一致）。</summary>
    public string InstalledVersion() => option.GetString("version", settings.Version) ?? "";

    /// <summary>需要更新时执行一次；返回是否真的执行了更新。</summary>
    public bool RunIfNeeded()
    {
        lock (Gate)
        {
            if (!NeedsUpdate())
            {
                return false;
            }

            RunLocked();
            return true;
        }
    }

    /// <summary>无条件执行一次更新（CLI <c>update</c> / 更新安装后的收尾）。</summary>
    public void Run()
    {
        lock (Gate)
        {
            RunLocked();
        }
    }

    private void RunLocked()
    {
        RunProcedures();
        option.Set("version", settings.Version);

        // php artisan migrate --force：结构迁移 + 4 个数据迁移（幂等）
        schema.Install();
        new SchemaMigrator(db, schema, option, settings).Migrate();

        ClearViews();
        WriteInstallLock();

        // Cache::flush()（本工程的缓存载体：options.php、翻译缓存、登录失败计数等）
        option.ClearCache();
        try
        {
            flushCaches?.Invoke();
        }
        catch (Exception)
        {
            // 清缓存失败不应让升级失败
        }
    }

    /// <summary>
    /// <c>UpdateCommand::procedures()</c>：仅当已安装版本低于过程版本时执行。
    /// 注意：PHP 在更新 <c>options.version</c> 之前执行这些过程，所以这里也用旧版本判断。
    /// </summary>
    private void RunProcedures()
    {
        string installed = InstalledVersion();

        if (Semver.LessThan(installed, EventProcedureVersion))
        {
            EventRuntime.Fire("__0.0.1");
        }

        if (Semver.LessThan(installed, HomePictureProcedureVersion)
            && string.Equals(option.GetString("home_pic_url", null), "./app/bg.jpg",
                StringComparison.Ordinal))
        {
            option.Set("home_pic_url", "./app/bg.webp");
        }
    }

    /// <summary><c>php artisan view:clear</c>：清理 <c>storage/framework/views</c> 下的编译产物。</summary>
    private void ClearViews()
    {
        string views = Path.Combine(paths.Storage, "framework", "views");
        if (!Directory.Exists(views))
        {
            return;
        }

        try
        {
            foreach (string file in Directory.EnumerateFiles(views, "*", SearchOption.AllDirectories))
            {
                File.Delete(file);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 与 Artisan 一样尽力而为
        }
    }

    private void WriteInstallLock()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(paths.InstallLock)!);
        File.WriteAllText(paths.InstallLock, "");
    }
}
