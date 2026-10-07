using System.Text;
using Cube.Core.IO;

namespace Cube.Core.Tests.IO;

public class RgbeImageTests
{
    [Fact]
    public void Encode_Decode_FlatRoundTrip_WithinRgbePrecision()
    {
        const int w = 4, h = 3;
        var rgb = new float[w * h * 3];
        var rnd = new Random(7);
        for (int i = 0; i < w * h; i++)
        {
            rgb[i * 3] = (float)(rnd.NextDouble() * 20);        // 0..20
            rgb[i * 3 + 1] = (float)(rnd.NextDouble() * 0.01);  // 아주 작은 값
            rgb[i * 3 + 2] = (float)(rnd.NextDouble() * 2000);  // 큰 값
        }
        rgb[0] = rgb[1] = rgb[2] = 0;                           // 검정 픽셀 → e=0
        rgb[3] = 1f; rgb[4] = 0.5f; rgb[5] = 0.25f;             // 정확히 표현되는 값

        byte[] data = RgbeImage.Encode(w, h, rgb);
        Assert.StartsWith("#?RADIANCE\n", Encoding.ASCII.GetString(data, 0, 11));
        var img = RgbeImage.Decode(data);
        Assert.Equal(w, img.Width);
        Assert.Equal(h, img.Height);
        Assert.Equal(w * h * 3, img.Rgb.Length);

        Assert.Equal(0f, img.Rgb[0]); Assert.Equal(0f, img.Rgb[1]); Assert.Equal(0f, img.Rgb[2]);
        Assert.Equal(1f, img.Rgb[3]); Assert.Equal(0.5f, img.Rgb[4]); Assert.Equal(0.25f, img.Rgb[5]);
        for (int i = 0; i < w * h; i++)
        {
            float max = MathF.Max(rgb[i * 3], MathF.Max(rgb[i * 3 + 1], rgb[i * 3 + 2]));
            float tol = max / 128f + 1e-7f; // 가수 8비트 내림 오차
            for (int c = 0; c < 3; c++)
                Assert.True(MathF.Abs(img.Rgb[i * 3 + c] - rgb[i * 3 + c]) <= tol, $"pixel {i} ch {c}: {img.Rgb[i * 3 + c]} vs {rgb[i * 3 + c]}");
        }
    }

    [Fact]
    public void Decode_NewStyleRle_Scanline()
    {
        const int w = 8;
        var ms = new MemoryStream();
        var header = Encoding.ASCII.GetBytes("#?RGBE\nFORMAT=32-bit_rle_rgbe\nEXPOSURE=1.0\n\n-Y 1 +X 8\n");
        ms.Write(header);
        ms.Write(new byte[] { 2, 2, 0, 8 });                       // 새 방식 마커 + 너비
        ms.Write(new byte[] { 128 + 8, 10 });                      // R: 8개 모두 10
        ms.Write(new byte[] { 8, 0, 1, 2, 3, 4, 5, 6, 7 });        // G: 리터럴 8개
        ms.Write(new byte[] { 128 + 4, 20, 128 + 4, 30 });         // B: 4개 20, 4개 30
        ms.Write(new byte[] { 128 + 8, 128 });                     // E: 2^(128-136) = 1/256
        var img = RgbeImage.Decode(ms.ToArray());
        Assert.Equal(8, img.Width);
        Assert.Equal(1, img.Height);
        for (int x = 0; x < w; x++)
        {
            Assert.Equal(10f / 256f, img.Rgb[x * 3]);
            Assert.Equal(x / 256f, img.Rgb[x * 3 + 1]);
            Assert.Equal((x < 4 ? 20f : 30f) / 256f, img.Rgb[x * 3 + 2]);
        }
    }

    [Fact]
    public void Decode_PlusY_FlipsRows_AndFlatScanlines()
    {
        // 2x2, +Y: 파일의 첫 행이 이미지 아래 행
        var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n+Y 2 +X 2\n"));
        ms.Write(new byte[] { 128, 0, 0, 129, 0, 128, 0, 129 });   // 파일 행 0: (1,0,0), (0,1,0)
        ms.Write(new byte[] { 0, 0, 128, 129, 128, 128, 128, 129 }); // 파일 행 1: (0,0,1), (1,1,1)
        var img = RgbeImage.Decode(ms.ToArray());
        Assert.Equal(2, img.Width); Assert.Equal(2, img.Height);
        // 맨 위 행 = 파일 행 1
        Assert.Equal(new float[] { 0, 0, 1, 1, 1, 1 }, img.Rgb.Take(6).ToArray());
        Assert.Equal(new float[] { 1, 0, 0, 0, 1, 0 }, img.Rgb.Skip(6).ToArray());
    }

    [Fact]
    public void Decode_OldStyleRle_RepeatsPreviousPixel()
    {
        var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("#?RADIANCE\n\n-Y 1 +X 4\n"));
        ms.Write(new byte[] { 64, 32, 16, 130 });   // (64,32,16)·2^(130-136) = (1, 0.5, 0.25)
        ms.Write(new byte[] { 1, 1, 1, 3 });        // 이전 픽셀 3번 반복
        var img = RgbeImage.Decode(ms.ToArray());
        for (int x = 0; x < 4; x++)
        {
            Assert.Equal(1f, img.Rgb[x * 3]);
            Assert.Equal(0.5f, img.Rgb[x * 3 + 1]);
            Assert.Equal(0.25f, img.Rgb[x * 3 + 2]);
        }
    }

    [Fact]
    public void Decode_RejectsBadSignatureAndFormat()
    {
        Assert.Throws<InvalidDataException>(() => RgbeImage.Decode(Encoding.ASCII.GetBytes("PNG\n\n-Y 1 +X 1\n\0\0\0\0")));
        Assert.Throws<InvalidDataException>(() => RgbeImage.Decode(Encoding.ASCII.GetBytes("#?RADIANCE\nFORMAT=32-bit_rle_xyze\n\n-Y 1 +X 1\n\0\0\0\0")));
    }
}
