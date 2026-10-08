using Cube.Core.IO;

namespace Cube.Core.Tests.IO;

/// <summary>
/// IBL용 등장방형(equirectangular) 파노라마 HDR 블러(<c>PanoramaBlur</c>)를 검증한다.
/// 레벨이 올라갈수록 해상도가 줄고 더 부드러워지되 평균 밝기는 보존되어야 하며, 가로 방향은 경도 0/360°에서 이어져야 한다.
/// </summary>
public class PanoramaBlurTests
{
    /// <summary>
    /// 4픽셀 단위 체커 무늬 RGB float 배열을 만든다(밝은 칸 4, 어두운 칸 0 → 평균 2).
    /// 분산이 큰 입력이라 블러 강도를 분산 감소로 측정하기 좋다.
    /// </summary>
    private static float[] Checker(int w, int h)
    {
        var a = new float[w * h * 3];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) { float v = ((x / 4 + y / 4) & 1) == 0 ? 4f : 0f; int i = (y * w + x) * 3; a[i] = a[i + 1] = a[i + 2] = v; }
        return a;
    }

    /// <summary>
    /// 레벨 0은 입력을 그대로 복사해야 하고, 1~MaxLevel로 갈수록 출력 폭이 줄어들되(최소 8x4) 평균 밝기는 약 2로 유지되며
    /// 분산은 단조 감소해야 한다. 마지막 레벨은 거의 평탄(분산 &lt; 0.05)해야 거친 반사/확산 조명에 쓸 수 있다.
    /// </summary>
    [Fact]
    public void Level0_IsCopy_HigherLevelsShrinkAndSmooth()
    {
        const int w = 256, h = 128;
        var src = Checker(w, h);
        var same = PanoramaBlur.Blur(src, w, h, 0, out int w0, out int h0);
        Assert.Equal((w, h), (w0, h0));
        Assert.Equal(src, same);
        // 이전 레벨의 분산/폭을 기억해 레벨마다 "더 작고 더 부드러운지"를 비교한다.
        float prevVar = float.MaxValue; int prevW = w;
        for (int lv = 1; lv <= PanoramaBlur.MaxLevel; lv++)
        {
            var b = PanoramaBlur.Blur(src, w, h, lv, out int ow, out int oh);
            Assert.True(ow <= prevW && ow >= 8 && oh >= 4);
            float mean = b.Average();
            Assert.InRange(mean, 1.9f, 2.1f); // 평균 밝기(에너지) 보존
            float var = b.Select(v => (v - mean) * (v - mean)).Average();
            Assert.True(var <= prevVar + 1e-4f, $"level {lv} variance {var} > {prevVar}");
            prevVar = var; prevW = ow;
        }
        Assert.True(prevVar < 0.05f); // 9단계는 거의 평탄
    }

    /// <summary>
    /// 가장 오른쪽 열만 밝은 이미지를 블러했을 때 가장 왼쪽 픽셀에도 빛이 번지는지 확인한다.
    /// 파노라마는 좌우 끝이 같은 경도로 이어지므로 가로 블러가 감싸기(wrap)를 하지 않으면 환경맵에 이음매가 생긴다.
    /// </summary>
    [Fact]
    public void HorizontalBlurWrapsAround()
    {
        // 가로 끝(경도 0/360°)에만 밝은 열 → 감싸기 블러면 반대쪽 끝에도 번진다
        const int w = 64, h = 32;
        var src = new float[w * h * 3];
        for (int y = 0; y < h; y++) { int i = (y * w + (w - 1)) * 3; src[i] = src[i + 1] = src[i + 2] = 100f; }
        var b = PanoramaBlur.Blur(src, w, h, 2, out int ow, out _);
        Assert.True(b[0] > 0.5f); // (0,0) 픽셀이 반대쪽 끝의 빛을 받음
        Assert.True(b[(ow / 2) * 3] < b[0]);
    }
}
