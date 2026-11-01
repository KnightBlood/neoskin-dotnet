using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using NeoSkin.Web.Support;

namespace NeoSkin.Web.Service;

/// <summary>
/// PNG「净化器 + 尺寸读取」，对应源上传流程里的
/// <c>Intervention\Image</c> + <c>getimagesize()</c>。
///
/// <p>源用 Imagick 重编码（compression-filter 0 / level 9 / strategy 0 /
/// exclude-chunk all / stripImage）。这里用 ImageSharp 解码像素，再交给
/// <see cref="PngEncoder"/> 重编码：只含关键块、丢弃全部元数据，
/// 与原实现结构性一致（字节级对齐见 PORT-NOTES）。</p>
/// </summary>
public static class PngSanitizer
{
    public readonly record struct Size(int Width, int Height);

    /// <summary>PNG 魔数（<c>mimes:png</c> 的文件内容嗅探）。</summary>
    public static bool IsPng(byte[]? data)
    {
        if (data is null || data.Length < 8)
        {
            return false;
        }

        return data[0] == 0x89 && data[1] == 'P' && data[2] == 'N' && data[3] == 'G'
               && data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A;
    }

    /// <summary><c>getimagesize()</c>：读 IHDR 的宽高；非法返回 null。</summary>
    public static Size? ReadSize(byte[]? data)
    {
        if (!IsPng(data) || data!.Length < 24)
        {
            return null;
        }

        // 8 字节签名 + 4 长度 + "IHDR" + width(4) + height(4)
        if (data[12] != 'I' || data[13] != 'H' || data[14] != 'D' || data[15] != 'R')
        {
            return null;
        }

        int width = (data[16] << 24) | (data[17] << 16) | (data[18] << 8) | data[19];
        int height = (data[20] << 24) | (data[21] << 16) | (data[22] << 8) | data[23];
        return width <= 0 || height <= 0 ? null : new Size(width, height);
    }

    /// <summary>解码并重编码为标准 PNG；失败返回 null（等效无效图）。</summary>
    public static byte[]? Sanitize(byte[]? data)
    {
        if (data is null || data.Length == 0)
        {
            return null;
        }

        try
        {
            using Image<Rgba32> image = Image.Load<Rgba32>(data);
            int width = image.Width;
            int height = image.Height;
            var rgba = new byte[width * height * 4];
            image.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < height; y++)
                {
                    Span<Rgba32> row = accessor.GetRowSpan(y);
                    for (int x = 0; x < width; x++)
                    {
                        Rgba32 pixel = row[x];
                        int offset = (y * width + x) * 4;
                        rgba[offset] = pixel.R;
                        rgba[offset + 1] = pixel.G;
                        rgba[offset + 2] = pixel.B;
                        rgba[offset + 3] = pixel.A;
                    }
                }
            });

            return PngEncoder.Encode(width, height, rgba);
        }
        catch (Exception)
        {
            // 解码失败 → 等效于无效图片
            return null;
        }
    }
}
