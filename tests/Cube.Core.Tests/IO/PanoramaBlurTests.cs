using Cube.Core.IO;

namespace Cube.Core.Tests.IO;

public class PanoramaBlurTests
{
    private static float[] Checker(int w, int h)
    {
        var a = new float[w * h * 3];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) { float v = ((x / 4 + y / 4) & 1) == 0 ? 4f : 0f; int i = (y * w + x) * 3; a[i] = a[i + 1] = a[i + 2] = v; }
        return a;
    }

    [Fact]
    public void Level0_IsCopy_HigherLevelsShrinkAndSmooth()
    {
        const int w = 256, h = 128;
        var src = Checker(w, h);
        var same = PanoramaBlur.Blur(src, w, h, 0, out int w0, out int h0);
        Assert.Equal((w, h), (w0, h0));
        Assert.Equal(src, same);
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
