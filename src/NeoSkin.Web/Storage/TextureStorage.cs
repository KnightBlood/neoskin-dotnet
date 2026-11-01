namespace NeoSkin.Web.Storage;

/// <summary>
/// 纹理文件存储（源 <c>Storage::disk('textures')</c> 的抽象）。
///
/// <p>键是纹理的 SHA-256 文件名（32/64 位十六进制，见 <c>hash</c> 列）。
/// 当前实现本地盘 <c>storage/textures/&lt;hash&gt;</c>；S3 随部署配置补齐。</p>
/// </summary>
public interface ITextureStorage
{
    bool Exists(string hash);

    byte[] Read(string hash);

    void Write(string hash, byte[] bytes);

    void Delete(string hash);

    long LastModifiedMillis(string hash);
}

/// <summary>本地纹理存储（默认盘，与 PHP 版一致）。</summary>
public sealed class LocalTextureStorage(string root) : ITextureStorage
{
    public string Root { get; } = root;

    public bool Exists(string hash) => File.Exists(PathFor(hash));

    public byte[] Read(string hash) => File.ReadAllBytes(PathFor(hash));

    public void Write(string hash, byte[] bytes)
    {
        Directory.CreateDirectory(Root);
        File.WriteAllBytes(PathFor(hash), bytes);
    }

    public void Delete(string hash)
    {
        string path = PathFor(hash);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public long LastModifiedMillis(string hash) =>
        new DateTimeOffset(File.GetLastWriteTimeUtc(PathFor(hash))).ToUnixTimeMilliseconds();

    private string PathFor(string hash) => Path.Combine(Root, hash);
}
