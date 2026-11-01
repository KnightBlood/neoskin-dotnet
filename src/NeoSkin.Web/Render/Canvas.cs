using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using NeoSkin.Web.Support;

namespace NeoSkin.Web.Render;

/// <summary>
/// GD 风格画布（对应 Java <c>GdImage</c> 的最小子集）。
///
/// <p>内部像素是 <b>ARGB</b>（Java 语义：0 全透明、255 不透明）；「GD int」沿用源
/// 语义 <c>(gdAlpha&lt;&lt;24)|(r&lt;&lt;16)|(g&lt;&lt;8)|b</c>（gdAlpha 0=不透明，127=透明）。</p>
/// </summary>
public sealed class Canvas
{
    /// <summary>全透明白（GD 的 <c>fill(white, alpha=127)</c>）。</summary>
    private const int TransparentWhite = 0x00FFFFFF;

    private readonly int[] _pixels;

    private Canvas(int width, int height, int[] pixels)
    {
        Width = width;
        Height = height;
        _pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    public static Canvas CreateEmpty(int width, int height)
    {
        int w = Math.Max(1, width);
        int h = Math.Max(1, height);
        var pixels = new int[w * h];
        Array.Fill(pixels, TransparentWhite);
        return new Canvas(w, h, pixels);
    }

    /// <summary><c>ImageManager-&gt;make()</c>：PNG 字节 → 画布。</summary>
    public static Canvas? FromPng(byte[]? data)
    {
        if (data is null)
        {
            return null;
        }

        try
        {
            using Image<Rgba32> image = Image.Load<Rgba32>(data);
            int w = image.Width;
            int h = image.Height;
            var pixels = new int[w * h];
            image.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < h; y++)
                {
                    Span<Rgba32> row = accessor.GetRowSpan(y);
                    for (int x = 0; x < w; x++)
                    {
                        Rgba32 p = row[x];
                        pixels[y * w + x] = (p.A << 24) | (p.R << 16) | (p.G << 8) | p.B;
                    }
                }
            });
            return new Canvas(w, h, pixels);
        }
        catch (Exception e) when (e is ImageFormatException or InvalidOperationException
                                      or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public byte[] ToPng()
    {
        var rgba = new byte[Width * Height * 4];
        for (int i = 0; i < _pixels.Length; i++)
        {
            int argb = _pixels[i];
            int offset = i * 4;
            rgba[offset] = (byte)((argb >> 16) & 0xFF);
            rgba[offset + 1] = (byte)((argb >> 8) & 0xFF);
            rgba[offset + 2] = (byte)(argb & 0xFF);
            rgba[offset + 3] = (byte)((argb >>> 24) & 0xFF);
        }

        return PngEncoder.Encode(Width, Height, rgba);
    }

    public void PixelSet(int x, int y, int argb)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height)
        {
            return;
        }

        _pixels[(y * Width) + x] = argb;
    }

    /// <summary>
    /// <c>imagecopy</c> 的单像素语义：GD 默认 <c>imagealphablending</c> 开启，
    /// 源覆盖 = source-over；全透明源像素不改变目标。
    /// </summary>
    public void BlendPixel(int x, int y, int argb)
    {
        int alpha = (argb >>> 24) & 0xFF;
        if (alpha <= 0)
        {
            return;
        }

        if (alpha == 255)
        {
            PixelSet(x, y, argb);
        }
        else
        {
            Blend(x, y, argb, alpha);
        }
    }

    public int PixelAt(int x, int y) =>
        _pixels[(SafeY(y) * Width) + SafeX(x)];

    /// <summary><c>imagecolorat</c>：ARGB → GD int。</summary>
    public int ColorAtGd(int x, int y)
    {
        int java = PixelAt(x, y);
        int gdAlpha = 127 - (int)Math.Round(((java >>> 24) * 127) / 255.0);
        return (gdAlpha << 24) | (java & 0x00FFFFFF);
    }

    /// <summary><c>imagefilledrectangle</c>（GD int 颜色，源覆盖合成）。</summary>
    public void FilledRectangle(int x1, int y1, int x2, int y2, int gdColor)
    {
        int alpha = Gdk.Alpha255(gdColor);
        if (alpha <= 0)
        {
            return;
        }

        int left = Math.Min(x1, x2);
        int top = Math.Min(y1, y2);
        int right = Math.Max(x1, x2);
        int bottom = Math.Max(y1, y2);
        for (int y = Math.Max(0, top); y <= Math.Min(Height - 1, bottom); y++)
        {
            for (int x = Math.Max(0, left); x <= Math.Min(Width - 1, right); x++)
            {
                Blend(x, y, Gdk.ToArgb(gdColor), alpha);
            }
        }
    }

    /// <summary><c>imagefilledpolygon</c>（GD int 颜色；扫描线、无抗锯齿）。</summary>
    public void FilledPolygon(IReadOnlyList<double> points, int gdColor)
    {
        int alpha = Gdk.Alpha255(gdColor);
        int count = points.Count / 2;
        if (alpha <= 0 || count < 3)
        {
            return;
        }

        var xs = new int[count];
        var ys = new int[count];
        for (int i = 0; i < count; i++)
        {
            xs[i] = (int)Math.Round(points[i * 2]);
            ys[i] = (int)Math.Round(points[(i * 2) + 1]);
        }

        int minY = Math.Max(0, ys.Min());
        int maxY = Math.Min(Height - 1, ys.Max());
        int argb = Gdk.ToArgb(gdColor);

        for (int y = minY; y <= maxY; y++)
        {
            var crossings = new List<double>();
            for (int i = 0; i < count; i++)
            {
                int j = (i + 1) % count;
                int y1 = ys[i];
                int y2 = ys[j];
                if (y1 == y2)
                {
                    continue;
                }

                double scan = y + 0.5;
                if ((y1 <= scan && y2 > scan) || (y2 <= scan && y1 > scan))
                {
                    double x = xs[i] + ((scan - y1) * (xs[j] - xs[i]) / (double)(y2 - y1));
                    crossings.Add(x);
                }
            }

            crossings.Sort();
            for (int k = 0; k + 1 < crossings.Count; k += 2)
            {
                int from = Math.Max(0, (int)Math.Ceiling(crossings[k]));
                int to = Math.Min(Width - 1, (int)Math.Floor(crossings[k + 1]));
                for (int x = from; x <= to; x++)
                {
                    Blend(x, y, argb, alpha);
                }
            }
        }
    }

    /// <summary><c>imagecopyresampled</c> 带源区域（双线性，近似 GDI+）。</summary>
    public void ResampleRegion(Canvas source, int dstX, int dstY, int dstW, int dstH,
        int srcX, int srcY, int srcW, int srcH)
    {
        if (dstW <= 0 || dstH <= 0 || srcW <= 0 || srcH <= 0)
        {
            return;
        }

        for (int y = 0; y < dstH; y++)
        {
            double sy = srcY + ((y + 0.5) * srcH / dstH);
            int y0 = Math.Clamp((int)Math.Floor(sy - 0.5), 0, source.Height - 1);
            int y1 = Math.Min(y0 + 1, source.Height - 1);
            double fy = Math.Clamp(sy - 0.5 - y0, 0, 1);
            for (int x = 0; x < dstW; x++)
            {
                double sx = srcX + ((x + 0.5) * srcW / dstW);
                int x0 = Math.Clamp((int)Math.Floor(sx - 0.5), 0, source.Width - 1);
                int x1 = Math.Min(x0 + 1, source.Width - 1);
                double fx = Math.Clamp(sx - 0.5 - x0, 0, 1);

                int argb = Bilinear(source.PixelAt(x0, y0), source.PixelAt(x1, y0),
                    source.PixelAt(x0, y1), source.PixelAt(x1, y1), fx, fy);
                PixelSet(dstX + x, dstY + y, argb);
            }
        }
    }

    /// <summary><c>resize($w, $h)</c>：双线性缩放到新画布。</summary>
    public Canvas Resize(int width, int height)
    {
        Canvas output = CreateEmpty(width, height);
        output.ResampleRegion(this, 0, 0, width, height, 0, 0, Width, Height);
        return output;
    }

    /// <summary><c>imagecopy</c>：整块直拷（源覆盖）。</summary>
    public void CopyFrom(Canvas source, int dstX, int dstY)
    {
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                int argb = source.PixelAt(x, y);
                int alpha = (argb >>> 24) & 0xFF;
                if (alpha == 0)
                {
                    continue;
                }

                if (alpha == 255)
                {
                    PixelSet(dstX + x, dstY + y, argb);
                }
                else
                {
                    Blend(dstX + x, dstY + y, argb, alpha);
                }
            }
        }
    }

    private void Blend(int x, int y, int argb, int alpha)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height)
        {
            return;
        }

        int index = (y * Width) + x;
        int dst = _pixels[index];
        double sa = alpha / 255.0;
        double da = ((dst >>> 24) & 0xFF) / 255.0;
        double outA = sa + (da * (1 - sa));
        if (outA <= 0)
        {
            _pixels[index] = 0;
            return;
        }

        double BlendChannel(byte s, byte d) =>
            (((s / 255.0) * sa) + ((d / 255.0) * da * (1 - sa))) / outA;

        _pixels[index] = ((int)Math.Round(outA * 255) << 24)
                         | ((int)Math.Round(BlendChannel((byte)((argb >> 16) & 0xFF),
                             (byte)((dst >> 16) & 0xFF)) * 255) << 16)
                         | ((int)Math.Round(BlendChannel((byte)((argb >> 8) & 0xFF),
                             (byte)((dst >> 8) & 0xFF)) * 255) << 8)
                         | (int)Math.Round(BlendChannel((byte)(argb & 0xFF),
                             (byte)(dst & 0xFF)) * 255);
    }

    private static int Bilinear(int p00, int p10, int p01, int p11, double fx, double fy)
    {
        int Channel(int shift)
        {
            double c00 = (p00 >> shift) & 0xFF;
            double c10 = (p10 >> shift) & 0xFF;
            double c01 = (p01 >> shift) & 0xFF;
            double c11 = (p11 >> shift) & 0xFF;
            double top = c00 + ((c10 - c00) * fx);
            double bottom = c01 + ((c11 - c01) * fx);
            return (int)Math.Round(top + ((bottom - top) * fy));
        }

        return (Channel(24) << 24) | (Channel(16) << 16) | (Channel(8) << 8) | Channel(0);
    }

    private int SafeX(int x) => Math.Clamp(x, 0, Width - 1);

    private int SafeY(int y) => Math.Clamp(y, 0, Height - 1);
}

/// <summary>GD int ↔ ARGB 换算。</summary>
public static class Gdk
{
    /// <summary>GD alpha → 0..255 不透明度。</summary>
    public static int Alpha255(int gdColor)
    {
        int gdAlpha = (gdColor >>> 24) & 0x7F;
        return (int)Math.Round((127 - gdAlpha) * 255.0 / 127);
    }

    /// <summary>GD int → ARGB（用换算后的不透明度）。</summary>
    public static int ToArgb(int gdColor) =>
        (Alpha255(gdColor) << 24) | (gdColor & 0x00FFFFFF);
}
