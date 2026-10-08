using Cube.Core.IO;
using Godot;

namespace Cube.App.Viewport;

/// <summary>
/// Render Settings → Background Blur: IBL 하늘을 배경으로 그릴 때만 흐린 파노라마를 쓰는 하늘 셰이더와 흐린 텍스처 캐시.
/// 조명(앰비언트·반사)을 만드는 큐브맵 패스(AT_CUBEMAP_PASS)는 원본을 그대로 써서 조명은 바뀌지 않는다.
/// </summary>
/// <remarks>
/// 흐림 연산 자체는 코어 <c>PanoramaBlur.Blur</c>(Rgbf float 배열, 등장방형 파노라마)가 하고, 여기서는 Godot 이미지 ↔ float 배열 변환과
/// (텍스처 인스턴스 ID, 단계) 키 캐시만 담당한다. 셰이더는 앱 전체에서 하나를 공유한다.
/// </remarks>
public static class HdriBlur
{
    /// <summary>지연 생성되는 하늘 셰이더(한 번 만들어 재사용).</summary>
    private static Shader? _shader;
    /// <summary>(원본 텍스처 인스턴스 ID, 흐림 단계) → 흐린 텍스처. 실패한 결과(null)도 저장해 다시 시도하지 않는다.</summary>
    private static readonly Dictionary<(ulong, int), Texture2D?> Cache = new();

    /// <summary>원본(source)과 흐린 이미지(blurred)를 받아 배경 패스만 흐린 것을 쓰는 하늘 셰이더. PanoramaSkyMaterial과 같은 SKY_COORDS 매핑.</summary>
    public static Shader SkyShader => _shader ??= new Shader
    {
        Code = """
            shader_type sky;
            uniform sampler2D source : filter_linear, source_color, repeat_enable, hint_default_black;
            uniform sampler2D blurred : filter_linear, source_color, repeat_enable, hint_default_black;
            uniform bool use_blur = false;
            void sky() {
            	if (use_blur && !AT_CUBEMAP_PASS) COLOR = texture(blurred, SKY_COORDS).rgb;
            	else COLOR = texture(source, SKY_COORDS).rgb;
            }
            """,
    };

    /// <summary>level(1~9)로 흐린 텍스처. 원본·단계별로 캐시한다. 0이면 null.</summary>
    /// <param name="src">원본 HDRI 파노라마 텍스처.</param>
    /// <param name="level">흐림 단계(0 = 없음, 최대 <c>PanoramaBlur.MaxLevel</c>로 잘림).</param>
    /// <returns>흐린 Rgbf 텍스처, level이 0이거나 실패하면 null.</returns>
    public static Texture2D? Blurred(Texture2D src, int level)
    {
        // 단계를 범위로 자르고 캐시를 먼저 확인한다
        level = Math.Clamp(level, 0, PanoramaBlur.MaxLevel);
        if (level == 0) return null;
        var key = (src.GetInstanceId(), level);
        if (Cache.TryGetValue(key, out var cached)) return cached;
        Texture2D? tex = null;
        try
        {
            // GPU 텍스처에서 이미지를 받아 복제(원본 보호) → 압축·밉맵 해제 → Rgbf(float3)로 통일
            var img = src.GetImage();
            if (img != null && !img.IsEmpty())
            {
                img = (Image)img.Duplicate();
                if (img.IsCompressed()) img.Decompress();
                if (img.HasMipmaps()) img.ClearMipmaps();
                img.Convert(Image.Format.Rgbf);
                // 이미지 바이트를 float 배열로 옮겨 코어 블러를 돌린다(출력 해상도는 단계에 따라 줄어들 수 있음)
                int w = img.GetWidth(), h = img.GetHeight();
                var bytes = img.GetData();
                var f = new float[w * h * 3];
                Buffer.BlockCopy(bytes, 0, f, 0, f.Length * 4);
                var outF = PanoramaBlur.Blur(f, w, h, level, out int ow, out int oh);
                // 결과 float을 다시 바이트로 바꿔 Rgbf 이미지/텍스처를 만든다
                var ob = new byte[outF.Length * 4];
                Buffer.BlockCopy(outF, 0, ob, 0, ob.Length);
                tex = ImageTexture.CreateFromImage(Image.CreateFromData(ow, oh, false, Image.Format.Rgbf, ob));
            }
        }
        catch (Exception ex) { GD.PushWarning($"[HDRI] blur failed: {ex.Message}"); }
        // 성공·실패와 무관하게 결과를 캐시
        Cache[key] = tex;
        return tex;
    }
}
