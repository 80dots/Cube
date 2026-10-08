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
    /// <summary>이미지 너비(픽셀).</summary>
    public int Width;
    /// <summary>이미지 높이(픽셀).</summary>
    public int Height;
    /// <summary>선형 HDR 픽셀 값. 길이 = Width × Height × 3, (y·Width + x)·3 위치에 R, G, B. 1.0을 넘는 값도 그대로 보존한다.</summary>
    public float[] Rgb = Array.Empty<float>();

    /// <summary>빈 이미지(0×0).</summary>
    public RgbeImage() { }
    /// <summary>이미 디코드된 버퍼로 이미지를 만든다(버퍼는 복사하지 않고 참조한다).</summary>
    public RgbeImage(int width, int height, float[] rgb) { Width = width; Height = height; Rgb = rgb; }

    /// <summary>파일 전체를 읽어 <see cref="Decode"/>한다. IBL용 사용자 .hdr 파일 로드에 쓴다.</summary>
    public static RgbeImage Load(string path) => Decode(File.ReadAllBytes(path));

    // ------------------------------------------------------------------ Decode

    /// <summary>
    /// .hdr 바이트 배열을 디코드한다.
    /// 순서: ① <c>#?</c> 서명 확인 → ② 빈 줄까지 헤더 줄을 읽으며 FORMAT 검사(다른 키는 무시) →
    /// ③ 해상도 줄 파싱(-Y면 위에서 아래, +Y면 아래에서 위로 저장된 것이라 행을 뒤집음) →
    /// ④ 스캔라인마다 RGBE 4바이트를 읽어 float RGB로 변환.
    /// </summary>
    /// <exception cref="InvalidDataException">서명/형식/해상도/데이터가 잘못되었을 때.</exception>
    public static RgbeImage Decode(byte[] data)
    {
        // pos = data에서 다음에 읽을 바이트 위치(모든 하위 함수가 ref로 전진시킨다).
        int pos = 0;
        string first = ReadLine(data, ref pos);
        if (!first.StartsWith("#?", StringComparison.Ordinal))
            throw new InvalidDataException("Not a Radiance HDR file (missing #? signature).");

        // 헤더 키=값 줄을 빈 줄이 나올 때까지 읽는다. FORMAT만 검사하고 EXPOSURE 등 나머지는 무시한다.
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

        // 해상도 줄은 "-Y h +X w" 형식의 토큰 4개. 회전/좌우 뒤집힘 형식(+X가 아닌 경우)은 지원하지 않는다.
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

        // 출력 버퍼와 스캔라인 하나 분량의 RGBE 버퍼(width×4 바이트, 재사용).
        var rgb = new float[width * height * 3];
        var scan = new byte[width * 4];
        for (int y = 0; y < height; y++)
        {
            ReadScanline(data, ref pos, width, scan);
            // +Y(아래→위 저장)면 출력 행을 뒤집어 항상 맨 위 행부터 저장한다.
            int row = flipY ? height - 1 - y : y;
            int o = row * width * 3;
            for (int x = 0; x < width; x++)
            {
                // 공유 지수 e가 0이면 검정. 아니면 성분 = 가수 · 2^(e-128-8) (가수 0..255를 0..1로 보는 8비트 시프트 포함).
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

    /// <summary>
    /// ASCII 한 줄을 읽고 <paramref name="pos"/>를 다음 줄 시작으로 옮긴다. 끝의 '\r'(CRLF)은 제거한다.
    /// 헤더와 해상도 줄 읽기에만 쓴다.
    /// </summary>
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
        // 새 방식 RLE 판별: 스캔라인이 0x02 0x02로 시작하고 다음 2바이트가 너비(최상위 비트 0)인 경우.
        // 이 방식은 너비 8 이상 32767 이하에서만 쓰인다.
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
                    // count > 128이면 반복 런(다음 바이트를 count-128번), 아니면 리터럴(뒤 count바이트를 그대로).
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
        // shift = 연속된 옛 방식 반복 마커마다 8비트씩 늘어나는 반복 횟수 자리수.
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
    /// <param name="width">너비.</param>
    /// <param name="height">높이.</param>
    /// <param name="rgb">선형 RGB float 버퍼(행 우선, 맨 위 행부터).</param>
    /// <returns>헤더 + 픽셀당 4바이트 RGBE 데이터. HDRI 캐시/테스트용.</returns>
    public static byte[] Encode(int width, int height, float[] rgb)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException("Bad image size.");
        if (rgb.Length < width * height * 3) throw new ArgumentException("rgb buffer too small.", nameof(rgb));
        // 헤더(서명, FORMAT, 빈 줄, 해상도 줄) 뒤에 픽셀을 순서대로 붙인다.
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

    /// <summary>이 이미지의 Width/Height/Rgb를 평면 RGBE로 인코딩한다.</summary>
    public byte[] Encode() => Encode(Width, Height, Rgb);

    /// <summary>float RGB → RGBE 4바이트(Ward 방식: 최대 성분의 지수 + 128, 가수 = c·256/2^exp 내림). 디코드 오차는 최대 성분의 1/128 이하.</summary>
    public static void FloatToRgbe(float r, float g, float b, byte[] dst, int offset)
    {
        float max = MathF.Max(r, MathF.Max(g, b));
        // 모든 성분이 0 근처이거나 NaN/무한대면 검정(지수 0)으로 쓴다.
        if (!(max > 1e-32f) || float.IsNaN(max) || float.IsInfinity(max))
        {
            dst[offset] = dst[offset + 1] = dst[offset + 2] = dst[offset + 3] = 0;
            return;
        }
        int exp = Math.ILogB(max) + 1;              // max = f · 2^exp, f ∈ [0.5, 1)
        float scale = (float)Math.ScaleB(256.0, -exp);
        // 음수 성분은 0으로, 가수는 0..255로 클램프하고 지수 바이트는 exp + 128.
        dst[offset] = (byte)Math.Clamp((int)(MathF.Max(r, 0) * scale), 0, 255);
        dst[offset + 1] = (byte)Math.Clamp((int)(MathF.Max(g, 0) * scale), 0, 255);
        dst[offset + 2] = (byte)Math.Clamp((int)(MathF.Max(b, 0) * scale), 0, 255);
        dst[offset + 3] = (byte)Math.Clamp(exp + 128, 0, 255);
    }
}
