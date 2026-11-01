using NeoSkin.Web.Render;

namespace NeoSkin.Web.Service;

/// <summary>
/// Minecraft 图像渲染门面（对应 PHP <c>Blessing\Minecraft</c>）。
///
/// <p>头像、皮肤预览、披风预览全部走 <see cref="Render.Minecraft"/>
/// （<c>SkinRenderer</c> 的等距投影 + 多边形光栅化）。本类只负责
/// 「按路由参数渲染 → 缩放到目标尺寸 → PNG 编码」这一段。</p>
/// </summary>
public sealed class MinecraftRenderer
{
    /// <summary>Java 版头像统一先按 <c>ratio=25</c> 渲染，再 resize 到目标尺寸。</summary>
    private const double AvatarRatio = 25;

    /// <summary>Java 版皮肤预览：<c>renderSkin($skin, 12, $isAlex)</c>。</summary>
    private const double SkinPreviewRatio = 12;

    private readonly Render.Minecraft _minecraft = new();

    private readonly string _resourceRoot;

    public MinecraftRenderer(string? resourceRoot = null) =>
        _resourceRoot = resourceRoot ?? Path.Combine(AppContext.BaseDirectory, "Resources");

    /// <summary><c>render2dAvatar($skin, 25)-&gt;resize($size, $size)</c>。</summary>
    public byte[]? Render2dAvatar(byte[] skinPng, int size)
    {
        if (size <= 0)
        {
            size = 100;
        }

        Canvas? avatar = _minecraft.Render2dAvatar(skinPng, AvatarRatio);
        return avatar?.Resize(size, size).ToPng();
    }

    /// <summary><c>render3dAvatar($skin, 25)-&gt;resize($size, $size)</c>。</summary>
    public byte[]? Render3dAvatar(byte[] skinPng, int size)
    {
        if (size <= 0)
        {
            size = 100;
        }

        Canvas? avatar = _minecraft.Render3dAvatar(skinPng, AvatarRatio);
        return avatar?.Resize(size, size).ToPng();
    }

    /// <summary><c>renderSkin($skin, 12, $isAlex)</c>（皮肤预览，正反两面）。</summary>
    public byte[]? RenderSkinPreview(byte[] skinPng, bool isAlex) =>
        _minecraft.RenderSkin(skinPng, SkinPreviewRatio, isAlex)?.ToPng();

    /// <summary><c>renderCape($cape, $height)</c>。</summary>
    public byte[]? RenderCape(byte[] capePng, int height)
    {
        if (height <= 0)
        {
            height = 200;
        }

        return _minecraft.RenderCape(capePng, height)?.ToPng();
    }

    /// <summary>回退头像：<c>misc/textures/avatar{2d|3d}.png</c> → 缩放到目标尺寸。</summary>
    public byte[]? FallbackAvatar(int size, bool threeD)
    {
        if (size <= 0)
        {
            size = 100;
        }

        string file = Path.Combine(_resourceRoot, "misc", "textures",
            threeD ? "avatar3d.png" : "avatar2d.png");
        if (!File.Exists(file))
        {
            return null;
        }

        Canvas? source = Canvas.FromPng(File.ReadAllBytes(file));
        return source?.Resize(size, size).ToPng();
    }
}
