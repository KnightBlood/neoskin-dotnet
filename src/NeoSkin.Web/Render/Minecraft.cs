namespace NeoSkin.Web.Render;

/// <summary>
/// <c>Blessing\Minecraft</c> 的移植：渲染器门面（皮肤预览 / 2D、3D 头像 / 披风）。
/// </summary>
public sealed class Minecraft
{
    /// <summary><c>renderSkin($skin, $ratio = 7.0, $isAlex = false)</c>：正反两面并排。</summary>
    public Canvas? RenderSkin(byte[] skin, double ratio, bool isAlex)
    {
        int vp = 15; // vertical padding
        int hp = 30; // horizontal padding
        int ip = 15; // internal padding

        Canvas? front = new SkinRenderer(ratio, false, -45).Render(skin, isAlex);
        if (front is null)
        {
            return null;
        }

        Canvas? back = new SkinRenderer(ratio, false, 135).Render(skin, isAlex);
        if (back is null)
        {
            return null;
        }

        int width = front.Width;
        int height = front.Height;

        Canvas canvas = Canvas.CreateEmpty((hp + width + ip) * 2, (vp * 2) + height);
        canvas.CopyFrom(back, hp, vp);
        canvas.CopyFrom(front, hp + (width + ip * 2), vp);

        return canvas;
    }

    /// <summary><c>renderSkin($skin)</c>（默认参数）。</summary>
    public Canvas? RenderSkin(byte[] skin) => RenderSkin(skin, 7.0, false);

    /// <summary><c>renderCape($cape, $height)</c>。</summary>
    public Canvas? RenderCape(byte[] cape, int height)
    {
        int vp = 20; // vertical padding
        int hp = 40; // horizontal padding

        Canvas? rendered = new CapeRenderer().Render(cape, height);
        if (rendered is null)
        {
            return null;
        }

        int width = rendered.Width;
        int renderedHeight = rendered.Height;

        Canvas canvas = Canvas.CreateEmpty((hp * 2) + width, (vp * 2) + renderedHeight);
        canvas.CopyFrom(rendered, hp, vp);

        return canvas;
    }

    /// <summary><c>render2dAvatar($skin, $ratio = 15.0)</c>。</summary>
    public Canvas? Render2dAvatar(byte[] skin, double ratio) =>
        new SkinRenderer(ratio, true, 0, 0).Render(skin, false);

    public Canvas? Render2dAvatar(byte[] skin) => Render2dAvatar(skin, 15.0);

    /// <summary><c>render3dAvatar($skin, $ratio = 15.0)</c>（hR=45，vR 用构造默认 -25）。</summary>
    public Canvas? Render3dAvatar(byte[] skin, double ratio) =>
        new SkinRenderer(ratio, true, 45).Render(skin, false);

    public Canvas? Render3dAvatar(byte[] skin) => Render3dAvatar(skin, 15.0);
}
