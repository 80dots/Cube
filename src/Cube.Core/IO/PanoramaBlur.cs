namespace Cube.Core.IO;

/// <summary>
/// 등장방형(equirect) HDR 파노라마를 흐리게 만든다(Render Settings → Background Blur 1~9단계).
/// 단계마다 2^(0.6·단계)배로 면적 평균 축소한 뒤 [1 4 6 4 1] 가우시안을 가로(감싸기)·세로(가장자리 고정)로 두 번 적용한다.
/// 결과는 작은 이미지라 선형 필터로 크게 그리면 부드럽게 흐려 보인다. 입력·출력은 픽셀당 RGB float.
/// </summary>
public static class PanoramaBlur
{
    /// <summary>최대 블러 단계(0 = 원본 그대로).</summary>
    public const int MaxLevel = 9;

    /// <summary>단계별 축소 배율(1단계 ≈ 1.5배, 9단계 ≈ 42배).</summary>
    public static float Factor(int level) => MathF.Pow(2f, 0.6f * Math.Clamp(level, 0, MaxLevel));

    /// <summary>
    /// 파노라마를 <paramref name="level"/> 단계만큼 흐리게 한 새 버퍼를 만든다.
    /// 단계 0이면 원본 복사본을 돌려주고, 아니면 축소 → 가로/세로 가우시안 2회 반복.
    /// </summary>
    /// <param name="rgb">입력 픽셀(행 우선, 픽셀당 RGB float 3개).</param>
    /// <param name="w">입력 너비.</param>
    /// <param name="h">입력 높이.</param>
    /// <param name="level">블러 단계(0~<see cref="MaxLevel"/>로 클램프).</param>
    /// <param name="ow">출력 너비(최소 8).</param>
    /// <param name="oh">출력 높이(최소 4).</param>
    /// <returns>출력 픽셀 버퍼(ow×oh×3).</returns>
    public static float[] Blur(float[] rgb, int w, int h, int level, out int ow, out int oh)
    {
        if (rgb.Length < w * h * 3) throw new ArgumentException("rgb too short");
        level = Math.Clamp(level, 0, MaxLevel);
        if (level == 0) { ow = w; oh = h; return (float[])rgb.Clone(); }
        // 단계 배율로 출력 크기를 정한다. 너무 작아지지 않도록 최소 8×4를 보장한다.
        float f = Factor(level);
        ow = Math.Max(8, (int)MathF.Round(w / f));
        oh = Math.Max(4, (int)MathF.Round(h / f));
        // 면적 평균으로 먼저 줄여 고주파를 없앤 뒤, 작은 이미지에서 가우시안을 반복해 더 부드럽게 만든다.
        // small → tmp(가로) → small(세로) 순으로 버퍼를 번갈아 쓴다.
        var small = Downsample(rgb, w, h, ow, oh);
        var tmp = new float[small.Length];
        for (int pass = 0; pass < 2; pass++)
        {
            Gauss(small, tmp, ow, oh, horizontal: true);
            Gauss(tmp, small, ow, oh, horizontal: false);
        }
        return small;
    }

    /// <summary>면적 평균 축소(대상 픽셀이 덮는 원본 픽셀들의 평균).</summary>
    private static float[] Downsample(float[] src, int w, int h, int ow, int oh)
    {
        var dst = new float[ow * oh * 3];
        for (int y = 0; y < oh; y++)
        {
            // 출력 행 y가 덮는 원본 행 범위 [y0, y1). 최소 한 행은 포함한다.
            int y0 = y * h / oh, y1 = Math.Max(y0 + 1, (y + 1) * h / oh);
            for (int x = 0; x < ow; x++)
            {
                // 출력 열 x가 덮는 원본 열 범위 [x0, x1).
                int x0 = x * w / ow, x1 = Math.Max(x0 + 1, (x + 1) * w / ow);
                float r = 0, g = 0, b = 0; int n = 0;
                // 범위 안 픽셀 RGB를 모두 더해 평균을 낸다.
                for (int sy = y0; sy < y1; sy++)
                    for (int sx = x0; sx < x1; sx++)
                    {
                        int i = (sy * w + sx) * 3;
                        r += src[i]; g += src[i + 1]; b += src[i + 2]; n++;
                    }
                int o = (y * ow + x) * 3;
                dst[o] = r / n; dst[o + 1] = g / n; dst[o + 2] = b / n;
            }
        }
        return dst;
    }

    /// <summary>5탭 이항(가우시안 근사) 커널 [1 4 6 4 1]/16. 합이 1이라 밝기가 보존된다.</summary>
    private static readonly float[] K = { 1 / 16f, 4 / 16f, 6 / 16f, 4 / 16f, 1 / 16f };

    /// <summary>5탭 가우시안. 가로는 경도라 감싸고(360° 이음매 없음), 세로는 극에서 가장자리 고정.</summary>
    private static void Gauss(float[] src, float[] dst, int w, int h, bool horizontal)
    {
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float r = 0, g = 0, b = 0;
                for (int k = -2; k <= 2; k++)
                {
                    // 가로 패스는 x를 w로 감싸고(경도 연속), 세로 패스는 y를 0..h-1로 클램프(극에서 반사 없이 가장자리 유지).
                    int sx = x, sy = y;
                    if (horizontal) sx = ((x + k) % w + w) % w; else sy = Math.Clamp(y + k, 0, h - 1);
                    int i = (sy * w + sx) * 3; float kw = K[k + 2];
                    r += src[i] * kw; g += src[i + 1] * kw; b += src[i + 2] * kw;
                }
                int o = (y * w + x) * 3;
                dst[o] = r; dst[o + 1] = g; dst[o + 2] = b;
            }
    }
}
