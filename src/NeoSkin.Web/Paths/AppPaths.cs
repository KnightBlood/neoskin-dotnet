using NeoSkin.Web.Config;

namespace NeoSkin.Web.Paths;

/// <summary>
/// 应用目录解析（Laravel <c>base_path()</c> / <c>storage_path()</c> 等的等价物）。
/// 把所有「相对路径 → 绝对路径」的换算集中到一处。
/// </summary>
public sealed class AppPaths
{
    private readonly string _baseDir;
    private readonly string _storageRoot;
    private readonly string _textures;
    private readonly string _plugins;

    public AppPaths(string? baseDir, NeoSkinSettings settings)
    {
        _baseDir = Path.GetFullPath(baseDir ?? Directory.GetCurrentDirectory());
        _storageRoot = Resolve(settings.Storage.Root.Length == 0 ? "storage" : settings.Storage.Root);
        _textures = Resolve(settings.Storage.Textures);
        _plugins = Resolve(settings.Plugins.Directory);
    }

    /// <summary><c>base_path()</c>。</summary>
    public string Base => _baseDir;

    /// <summary><c>storage_path()</c>。</summary>
    public string Storage => _storageRoot;

    /// <summary><c>storage_path($path)</c>。</summary>
    public string StorageOf(string child) => Path.Combine(_storageRoot, child);

    /// <summary>纹理盘根目录（<c>Storage::disk('textures')</c>）。</summary>
    public string Textures => _textures;

    /// <summary><c>base_path('plugins')</c>。</summary>
    public string Plugins => _plugins;

    /// <summary><c>public_path()</c>。</summary>
    public string PublicDir => Path.Combine(_baseDir, "public");

    /// <summary><c>storage_path('install.lock')</c>。</summary>
    public string InstallLock => Path.Combine(_storageRoot, "install.lock");

    /// <summary><c>storage_path('options.php')</c>。</summary>
    public string OptionsCache => Path.Combine(_storageRoot, "options.php");

    /// <summary><c>storage_path('framework/sessions')</c>。</summary>
    public string Sessions => Path.Combine(_storageRoot, "framework", "sessions");

    /// <summary>相对路径按应用根目录解析；绝对路径原样返回。</summary>
    public string Resolve(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return _baseDir;
        }

        return Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(_baseDir, path));
    }
}
