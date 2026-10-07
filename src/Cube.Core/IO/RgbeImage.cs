using System.Globalization;
using System.Text;

namespace Cube.Core.IO;

/// <summary>
/// Radiance HDR(RGBE) 이미지. <see cref="Rgb"/>는 픽셀당 float 3개, 행 우선, 맨 위 행부터.
/// 디코드는 <c>#?RADIANCE</c>/<c>#?RGBE</c> 헤더, <c>FORMAT=32-bit_rle_rgbe</c>, 해상도 줄 <c>-Y h +X w</c>(<c>+Y</c>는 행 뒤집기),
/// 새 방식 RLE(0x02 0x02 hi lo + 채널 4개 평면 RLE)와 평면/옛 방식 스캔라인을 지원한다. 변환은 성분마다 <c>v · 2^(e-136)</c>.
/// </summary>
public sealed class RgbeImage
{
    public int Width;
    public int Height;
    public float[] Rgb = Array.Empty<float>();

    public RgbeImage() { }
    public RgbeImage(int width, int height, float[] rgb) { Width = width; Height = height; Rgb = rgb; }

    public static RgbeImage Load(string path) => Decode(File.ReadAllBytes(path));

    // ------------------------------------------------------------------ Decode

    public static RgbeImage Decode(byte[] data)
    {
        int pos = 0;
        string first = ReadLine(data, ref pos);
        if (!first.StartsWith("#?", StringComparison.Ordinal))
            throw new InvalidDataException("Not a Radiance HDR file (missing #? signature).");

        while (true)
        {
            if (pos >= data.Length) throw new InvalidDataException("Unexpected end of HDR header.");
            string line = ReadLine(data, ref pos);
            if (line.Length == 0) break; // 빈 줄 = 헤더 끝
            if (line.StartsWith("FORMAT=", StringComparison.Ordinal))
            {
                string fmt = line.Substring(7).Trim();
                if (fmt != "32-bit_rle_rgbe") throw new InvalidDataException($"Unsupported HDR format: {fmt}");
            }
        }

        string res = ReadLine(data, ref pos);
        var tok = res.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tok.Length != 4) throw new InvalidDataException($"Bad HDR resolution line: '{res}'");
        int width, height; bool flipY;
        if ((tok[0] == "-Y" || tok[0] == "+Y") && tok[2] == "+X")
        {
            height = int.Parse(tok[1], CultureInfo.InvariantCulture);
            width = int.Parse(tok[3], CultureInfo.InvariantCulture);
            flipY = tok[0] == "+Y";
        }
        else throw new InvalidDataException($"Unsupported HDR orientation: '{res}' (only -Y h +X w / +Y h +X w).");
        if (width <= 0 || height <= 0) throw new InvalidDataException("Bad HDR size.");

        var rgb = new float[width * height * 3];
        var scan = new byte[width * 4];
        for (int y = 0; y < height; y++)
        {
            ReadScanline(data, ref pos, width, scan);
            int row = flipY ? height - 1 - y : y;
            int o = row * width * 3;
            for (int x = 0; x < width; x++)
            {
                int e = scan[x * 4 + 3];
                if (e == 0) { rgb[o] = rgb[o + 1] = rgb[o + 2] = 0; }
                else
                {
                    float scale = (float)Math.ScaleB(1.0, e - 136);
                    rgb[o] = scan[x * 4] * scale;
                    rgb[o + 1] = scan[x * 4 + 1] * scale;
                    rgb[o + 2] = scan[x * 4 + 2] * scale;
                }
                o += 3;
            }
        }
        return new RgbeImage(width, height, rgb);
    }

    private static string ReadLine(byte[] data, ref int pos)
    {
        int start = pos;
        while (pos < data.Length && data[pos] != (byte)'\n') pos++;
        int end = pos;
        if (pos < data.Length) pos++; // '\n' 건너뜀
        if (end > start && data[end - 1] == (byte)'\r') end--;
        return Encoding.ASCII.GetString(data, start, end - start);
    }

    /// <summary>스캔라인 하나를 RGBE 4바이트×width로 읽는다.</summary>
    private static void ReadScanline(byte[] data, ref int pos, int width, byte[] scan)
    {
        if (width >= 8 && width < 32768 && pos + 4 <= data.Length
            && data[pos] == 2 && data[pos + 1] == 2 && (data[pos + 2] & 0x80) == 0)
        {
            int w = (data[pos + 2] << 8) | data[pos + 3];
            if (w != width) throw new InvalidDataException("HDR RLE scanline width mismatch.");
            pos += 4;
            // 채널 4개 평면 RLE
            for (int c = 0; c < 4; c++)
            {
                int x = 0;
                while (x < width)
                {
                    if (pos >= data.Length) throw new InvalidDataException("Unexpected end of HDR RLE data.");
                    int count = data[pos++];
                    if (count > 128)
                    {
                        count -= 128;
                        if (pos >= data.Length || x + count > width) throw new InvalidDataException("Bad HDR RLE run.");
                        byte v = data[pos++];
                        for (int i = 0; i < count; i++) scan[(x + i) * 4 + c] = v;
                    }
                    else
                    {
                        if (count == 0 || pos + count > data.Length || x + count > width) throw new InvalidDataException("Bad HDR RLE literal.");
                        for (int i = 0; i < count; i++) scan[(x + i) * 4 + c] = data[pos + i];
                        pos += count;
                    }
                    x += count;
                }
            }
            return;
        }

        // 평면 / 옛 방식 RLE(1,1,1,n = 이전 픽셀 n<<shift번 반복)
        int px = 0, shift = 0;
        while (px < width)
        {
            if (pos + 4 > data.Length) throw new InvalidDataException("Unexpected end of HDR pixel data.");
            byte r = data[pos], g = data[pos + 1], b = data[pos + 2], e = data[pos + 3];
            pos += 4;
            if (r == 1 && g == 1 && b == 1)
            {
                int count = e << shift;
                if (px == 0 || px + count > width) throw new InvalidDataException("Bad HDR old-style RLE run.");
                for (int i = 0; i < count; i++)
                {
                    scan[(px + i) * 4] = scan[(px - 1) * 4];
                    scan[(px + i) * 4 + 1] = scan[(px - 1) * 4 + 1];
                    scan[(px + i) * 4 + 2] = scan[(px - 1) * 4 + 2];
                    scan[(px + i) * 4 + 3] = scan[(px - 1) * 4 + 3];
                }
                px += count;
                shift += 8;
            }
            else
            {
                scan[px * 4] = r; scan[px * 4 + 1] = g; scan[px * 4 + 2] = b; scan[px * 4 + 3] = e;
                px++;
                shift = 0;
            }
        }
    }

    // ------------------------------------------------------------------ Encode

    /// <summary>평면(비 RLE) RGBE로 인코딩한다. 헤더는 <c>#?RADIANCE</c>, <c>-Y h +X w</c>.</summary>
    public static byte[] Encode(int width, int height, float[] rgb)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException("Bad image size.");
        if (rgb.Length < width * height * 3) throw new ArgumentException("rgb buffer too small.", nameof(rgb));
        var header = Encoding.ASCII.GetBytes($"#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y {height.ToString(CultureInfo.InvariantCulture)} +X {width.ToString(CultureInfo.InvariantCulture)}\n");
        var outp = new byte[header.Length + width * height * 4];
        Buffer.BlockCopy(header, 0, outp, 0, header.Length);
        int o = header.Length;
        for (int i = 0; i < width * height; i++)
        {
            FloatToRgbe(rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2], outp, o);
            o += 4;
        }
        return outp;
    }

    public byte[] Encode() => Encode(Width, Height, Rgb);

    /// <summary>float RGB → RGBE 4바이트(Ward 방식: 최대 성분의 지수 + 128, 가수 = c·256/2^exp 내림). 디코드 오차는 최대 성분의 1/128 이하.</summary>
    public static void FloatToRgbe(float r, float g, float b, byte[] dst, int offset)
    {
        float max = MathF.Max(r, MathF.Max(g, b));
        if (!(max > 1e-32f) || float.IsNaN(max) || float.IsInfinity(max))
        {
            dst[offset] = dst[offset + 1] = dst[offset + 2] = dst[offset + 3] = 0;
            return;
        }
        int exp = Math.ILogB(max) + 1;              // max = f · 2^exp, f ∈ [0.5, 1)
        float scale = (float)Math.ScaleB(256.0, -exp);
        dst[offset] = (byte)Math.Clamp((int)(MathF.Max(r, 0) * scale), 0, 255);
        dst[offset + 1] = (byte)Math.Clamp((int)(MathF.Max(g, 0) * scale), 0, 255);
        dst[offset + 2] = (byte)Math.Clamp((int)(MathF.Max(b, 0) * scale), 0, 255);
        dst[offset + 3] = (byte)Math.Clamp(exp + 128, 0, 255);
    }
}
