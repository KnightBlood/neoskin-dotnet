using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using NeoSkin.Web.Render;

namespace NeoSkin.Tests.Render;

/// <summary>
/// <c>SkinRenderer</c> 移植的烟测护栏：用源项目的 steve.png / cape.png
/// 走全部渲染路径，确认尺寸计算、关键像素与 bug-for-bug 分支不抛异常。
/// </summary>
public sealed class RenderSmokeTest
{
    private static byte[] Resource(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Resources", "png", name));

    private static void AssertPng(byte[] bytes)
    {
        Assert.True(bytes.Length > 8);
        Assert.Equal(0x89, bytes[0]);
        Assert.Equal((byte)'P', bytes[1]);
        Assert.Equal((byte)'N', bytes[2]);
        Assert.Equal((byte)'G', bytes[3]);
    }

    private static int OpaqueColors(Canvas canvas)
    {
        var colors = new HashSet<int>();
        for (int y = 0; y < canvas.Height; y++)
        {
            for (int x = 0; x < canvas.Width; x++)
            {
                int pixel = canvas.PixelAt(x, y);
                if ((pixel >>> 24) > 0x10)
                {
                    colors.Add(pixel);
                }
            }
        }

        return colors.Count;
    }

    /// <summary>确定性测试皮肤（尺寸可变，全部不透明）。</summary>
    private static byte[] PatternSkin(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                image[x, y] = new Rgba32(
                    (byte)((x * 7) & 0xFF),
                    (byte)((y * 11) & 0xFF),
                    (byte)(((x + y) * 13) & 0xFF),
                    255);
            }
        }

        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }

    [Fact]
    public void StevesIsOldSkinType()
    {
        Canvas? canvas = Canvas.FromPng(Resource("steve.png"));

        Assert.NotNull(canvas);
        Assert.Equal(64, canvas!.Width);
        Assert.Equal(64, canvas.Height);
    }

    [Fact]
    public void Render2dAvatarHeadOnly()
    {
        Canvas? avatar = new Minecraft().Render2dAvatar(Resource("steve.png"));

        Assert.NotNull(avatar);
        // bounds: 头/helmet 包围盒 x/y ∈ [-0.5, 8.5] → 宽高 9，src = 30*9+1 = 271，缩半 135
        Assert.Equal(135, avatar!.Width);
        Assert.Equal(135, avatar.Height);
        AssertPng(avatar.ToPng());
        Assert.True(OpaqueColors(avatar) > 30,
            "rendered avatar looks blank, distinct colors = " + OpaqueColors(avatar));
    }

    [Fact]
    public void Render3dAvatar()
    {
        Canvas? avatar = new Minecraft().Render3dAvatar(Resource("steve.png"));

        Assert.NotNull(avatar);
        Assert.True(avatar!.Width > 0 && avatar.Height > 0);
        AssertPng(avatar.ToPng());
        Assert.True(OpaqueColors(avatar) > 30);
    }

    [Fact]
    public void Render3dAvatarDiffersFrom2d()
    {
        var minecraft = new Minecraft();
        Canvas? flat = minecraft.Render2dAvatar(Resource("steve.png"));
        Canvas? rotated = minecraft.Render3dAvatar(Resource("steve.png"));

        Assert.NotNull(flat);
        Assert.NotNull(rotated);
        // 2D 为正视图（hR=0），3D 为 hR=45 的等距视图，像素不应完全相同
        bool differs = flat!.Width != rotated!.Width || flat.Height != rotated.Height;
        for (int y = 0; !differs && y < flat.Height; y++)
        {
            for (int x = 0; x < flat.Width && !differs; x++)
            {
                differs = flat.PixelAt(x, y) != rotated.PixelAt(x, y);
            }
        }

        Assert.True(differs, "3D avatar equals the 2D avatar");
    }

    [Fact]
    public void RenderSkinJoinsFrontAndBack()
    {
        Canvas? rendered = new Minecraft().RenderSkin(Resource("steve.png"));

        Assert.NotNull(rendered);
        Assert.True(rendered!.Width > 0 && rendered.Height > 0);
        AssertPng(rendered.ToPng());

        // 反视图在左、正视图在右，垂直方向各留 15px 内边距
        Assert.Equal(0, (rendered.PixelAt(0, 0) >>> 24) & 0xFF);
    }

    [Fact]
    public void RenderCape()
    {
        Canvas? rendered = new Minecraft().RenderCape(Resource("cape.png"), 64);

        Assert.NotNull(rendered);
        // CapeRenderer: out 40x64；门面画布 (40*2+40) x (20*2+64)
        Assert.Equal(120, rendered!.Width);
        Assert.Equal(104, rendered.Height);
        AssertPng(rendered.ToPng());
    }

    [Fact]
    public void NewSkinTypeLayersDoNotThrow()
    {
        // 64x64 新皮肤会走 fixNewSkinTypeLayers 与 [8*hd]/[4*hd] 键控怪癖；
        // 键控语义错误会在这里抛「volume point missing」。
        Canvas? rendered = new Minecraft().RenderSkin(PatternSkin(64, 64), 7, false);

        Assert.NotNull(rendered);
        Assert.True(rendered!.Width > 0 && rendered.Height > 0);
    }

    [Fact]
    public void AlexModelRenders()
    {
        Canvas? rendered = new Minecraft().RenderSkin(PatternSkin(64, 64), 7, true);

        Assert.NotNull(rendered);
        Assert.True(rendered!.Width > 0 && rendered.Height > 0);
        Assert.True(OpaqueColors(rendered) > 30);
    }

    [Fact]
    public void OversizedSkinIsDownscaled()
    {
        // 512x256 → 宽度缩到 256（防内存溢出分支），hdRatio=4 → 头部包围盒 36
        Canvas? rendered = new Minecraft().Render2dAvatar(PatternSkin(512, 256), 15);

        Assert.NotNull(rendered);
        // 30*36+1 = 1081 → 缩半 540
        Assert.Equal(540, rendered!.Width);
        Assert.Equal(540, rendered.Height);
    }
}
