namespace NeoSkin.Web.Render;

/// <summary>
/// <c>Blessing\Renderer\CapeRenderer</c> 的移植：从披风整图裁出可显示区域。
/// </summary>
public sealed class CapeRenderer
{
    /// <summary><c>render($source, int $height)</c>。</summary>
    public Canvas? Render(byte[] source, int height)
    {
        Canvas? resource = Canvas.FromPng(source);
        if (resource is null)
        {
            return null;
        }

        int srcWidth = resource.Width;
        int srcHeight = resource.Height;
        double hdRatio = srcWidth / 64.0;
        int outHeight = height;
        double outWidthD = outHeight / 16.0 * 10;
        int outWidth = (int)outWidthD;

        Canvas canvas = Canvas.CreateEmpty(outWidth, outHeight);
        canvas.ResampleRegion(resource, 0, 0, outWidth, outHeight,
            (int)(1 * hdRatio), (int)(1 * hdRatio),
            (int)(srcWidth * 10 / 64.0), (int)(srcHeight * 16 / 32.0));

        return canvas;
    }
}
