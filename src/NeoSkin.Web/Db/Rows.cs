namespace NeoSkin.Web.Db;

/// <summary><c>players</c> 的一行（事件负载用；完整仓库随 PlayerController 补齐）。</summary>
public sealed record PlayerRow(int Pid, int Uid, string Name, int TidSkin, int TidCape, string Model);

/// <summary><c>textures</c> 的一行（事件负载用）。</summary>
public sealed record TextureRow(long Tid, string Name, string Type, string Hash, long Size,
    int Uploader, bool IsPublic, string UploadAt, int Likes)
{
    /// <summary><c>Texture::getModelAttribute()</c>：<c>alex</c> → <c>slim</c>。</summary>
    public string TextureModel => Type == "alex" ? "slim" : "default";
}
