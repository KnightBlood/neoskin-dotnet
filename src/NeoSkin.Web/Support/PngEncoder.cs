using System.IO.Compression;
using System.Text;

namespace NeoSkin.Web.Support;

/// <summary>
/// 与 Imagick（<c>png:compression-filter=0/level=9/strategy=0</c> + <c>stripImage()</c>）
/// 字节级对齐的 PNG 编码器（源 <c>com.neoskin.support.PngEncoder</c> 的移植）。
///
/// <ol>
///   <li>只写 IHDR / IDAT / IEND（<c>exclude-chunk all</c>）；</li>
///   <li>RGBA（颜色类型 6），8 位；</li>
///   <li>行过滤用 libpng 启发式：<c>sum(abs(signed byte))</c> 最小者，
///       第一行只测 None/Sub；</li>
///   <li>zlib：deflate level 9、默认策略；</li>
///   <li>IDAT 按 8192 字节分块。</li>
/// </ol>
/// </summary>
public static class PngEncoder
{
    private static readonly byte[] Signature = [0x89, (byte)'P', (byte)'N', (byte)'G',
        0x0D, 0x0A, 0x1A, 0x0A];

    private const int IdatChunk = 8192;

    /// <summary>把 RGBA 像素（行优先，每像素 4 字节）编码为 PNG。</summary>
    public static byte[] Encode(int width, int height, byte[] rgba)
    {
        int stride = width * 4;
        byte[] raw = Scanlines(width, height, rgba, stride);
        byte[] compressed = Deflate(raw);

        using var output = new MemoryStream(compressed.Length + 128);
        output.Write(Signature);

        byte[] ihdr = new byte[13];
        WriteInt(ihdr, 0, width);
        WriteInt(ihdr, 4, height);
        ihdr[8] = 8;   // bit depth
        ihdr[9] = 6;   // color type RGBA
        WriteChunk(output, "IHDR", ihdr);

        for (int offset = 0; offset < compressed.Length; offset += IdatChunk)
        {
            int length = Math.Min(IdatChunk, compressed.Length - offset);
            var part = new byte[length];
            Array.Copy(compressed, offset, part, 0, length);
            WriteChunk(output, "IDAT", part);
        }

        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static byte[] Scanlines(int width, int height, byte[] rgba, int stride)
    {
        var previous = new byte[stride];
        var current = new byte[stride];
        var output = new byte[height * (stride + 1)];

        int pos = 0;
        for (int y = 0; y < height; y++)
        {
            Array.Copy(rgba, y * stride, current, 0, stride);

            int filter = PickFilter(current, previous, y > 0, 4, stride);
            output[pos++] = (byte)filter;
            FilterRow(current, previous, y > 0, 4, stride, filter, output, pos);
            pos += stride;

            (previous, current) = (current, previous);
        }

        return output;
    }

    /// <summary>libpng <c>png_write_find_filter</c> 的启发式。</summary>
    private static int PickFilter(byte[] row, byte[] prev, bool hasPrev, int bpp, int stride)
    {
        var sums = new int[5];
        sums[0] = Sum(row, 0, stride);
        for (int i = 0; i < stride; i++)
        {
            int cur = row[i] & 0xFF;
            int left = i >= bpp ? row[i - bpp] & 0xFF : 0;
            int up = hasPrev ? prev[i] & 0xFF : 0;
            int upperLeft = hasPrev && i >= bpp ? prev[i - bpp] & 0xFF : 0;

            sums[1] += AbsSigned(cur - left);
            if (hasPrev)
            {
                sums[2] += AbsSigned(cur - up);
                sums[3] += AbsSigned(cur - ((left + up) >> 1));
                sums[4] += AbsSigned(cur - Paeth(left, up, upperLeft));
            }
        }

        int best = 0;
        int min = sums[0];
        for (int f = 1; f < 5; f++)
        {
            if (f >= 2 && !hasPrev)
            {
                continue;
            }

            if (sums[f] < min)
            {
                min = sums[f];
                best = f;
            }
        }

        return best;
    }

    private static void FilterRow(byte[] row, byte[] prev, bool hasPrev, int bpp, int stride,
        int filter, byte[] output, int offset)
    {
        for (int i = 0; i < stride; i++)
        {
            int cur = row[i] & 0xFF;
            int left = i >= bpp ? row[i - bpp] & 0xFF : 0;
            int up = hasPrev ? prev[i] & 0xFF : 0;
            int upperLeft = hasPrev && i >= bpp ? prev[i - bpp] & 0xFF : 0;
            int value = filter switch
            {
                0 => cur,
                1 => cur - left,
                2 => cur - up,
                3 => cur - ((left + up) >> 1),
                4 => cur - Paeth(left, up, upperLeft),
                _ => throw new InvalidOperationException("filter " + filter),
            };
            output[offset + i] = (byte)value;
        }
    }

    private static int Sum(byte[] row, int offset, int length)
    {
        int total = 0;
        for (int i = 0; i < length; i++)
        {
            total += AbsSigned(row[offset + i] & 0xFF);
        }

        return total;
    }

    /// <summary>libpng 的 <c>abs((signed char) v)</c>。</summary>
    private static int AbsSigned(int value)
    {
        int v = (sbyte)(byte)value;
        return v < 0 ? -v : v;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc)
        {
            return a;
        }

        return pb <= pc ? b : c;
    }

    // ------------------------------------------------------------------ 压缩 / 块

    private static byte[] Deflate(byte[] raw)
    {
        using var output = new MemoryStream(Math.Max(64, raw.Length / 2));
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize,
                   leaveOpen: true))
        {
            zlib.Write(raw, 0, raw.Length);
        }

        return output.ToArray();
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        var length = new byte[4];
        WriteInt(length, 0, data.Length);
        output.Write(length);

        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);

        uint crc = Crc32(typeBytes, data);
        var crcBytes = new byte[4];
        WriteInt(crcBytes, 0, (int)crc);
        output.Write(crcBytes);
    }

    private static void WriteInt(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)((value >> 24) & 0xFF);
        buffer[offset + 1] = (byte)((value >> 16) & 0xFF);
        buffer[offset + 2] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 3] = (byte)(value & 0xFF);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    private static uint Crc32(byte[] first, byte[] second)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in first)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        foreach (byte b in second)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }
}
