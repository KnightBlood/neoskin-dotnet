namespace NeoSkin.Web.Session;

/// <summary>会话 cookie 配置（源 <c>config/session.php</c>）。</summary>
public sealed record SessionCookieConfig(
    string Name,
    long LifetimeMinutes,
    string Path,
    string SameSite,
    bool Secure)
{
    public static readonly SessionCookieConfig Default = new("BS_SESSION", 120, "/", "lax", false);

    public long MaxAgeSeconds => LifetimeMinutes * 60;

    public SessionCookieConfig Normalized() => this with
    {
        Name = string.IsNullOrWhiteSpace(Name) ? Default.Name : Name,
        LifetimeMinutes = LifetimeMinutes <= 0 ? Default.LifetimeMinutes : LifetimeMinutes,
        Path = string.IsNullOrWhiteSpace(Path) ? Default.Path : Path,
        SameSite = string.IsNullOrWhiteSpace(SameSite) ? Default.SameSite : SameSite,
    };
}

/// <summary>会话存储（file / cache 型）。</summary>
public interface ISessionHandler
{
    /// <summary>读会话载荷；不存在/已过期返回空串。</summary>
    string Read(string? id);

    bool Write(string id, string data);

    bool Destroy(string id);

    /// <summary>清理过期会话，返回删除数。</summary>
    int Gc(long lifetimeSeconds);
}

/// <summary>
/// Laravel <c>Illuminate\Session\FileSessionHandler</c> 的移植。
///
/// <list type="bullet">
///   <item><c>read</c>：文件 mtime 在 lifetime 内才读，否则空串（过期文件留着等 gc）；</item>
///   <item>会话 id 只能是 <c>[A-Za-z0-9]{40}</c>，文件名无路径注入面；</item>
///   <item>载荷的序列化由 <see cref="SessionStore"/> 负责，这里只搬字节。</item>
/// </list>
/// </summary>
public sealed class FileSessionHandler(string directory, int lifetimeMinutes) : ISessionHandler
{
    public string Read(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return "";
        }

        string file = System.IO.Path.Combine(directory, id);
        if (!File.Exists(file))
        {
            return "";
        }

        try
        {
            DateTime lastModified = File.GetLastWriteTimeUtc(file);
            if (lastModified > DateTime.UtcNow.AddSeconds(-(long)lifetimeMinutes * 60))
            {
                return File.ReadAllText(file);
            }
        }
        catch (IOException)
        {
            // 共享读失败按「无数据」处理
        }

        return "";
    }

    public bool Write(string id, string data)
    {
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(System.IO.Path.Combine(directory, id), data);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public bool Destroy(string id)
    {
        try
        {
            File.Delete(System.IO.Path.Combine(directory, id));
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public int Gc(long lifetimeSeconds)
    {
        DateTime threshold = DateTime.UtcNow.AddSeconds(-lifetimeSeconds);
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        int removed = 0;
        foreach (string file in Directory.EnumerateFiles(directory))
        {
            try
            {
                if (System.IO.Path.GetFileName(file).Length == 40
                    && File.GetLastWriteTimeUtc(file) < threshold
                    && Delete(file))
                {
                    removed++;
                }
            }
            catch (IOException)
            {
                // 单个文件失败不中断 gc
            }
        }

        return removed;

        static bool Delete(string path)
        {
            File.Delete(path);
            return true;
        }
    }
}
