using Cube.Core.IO;
using Godot;

namespace Cube.App.Viewport;

/// <summary>
/// Render Settings → Background Blur: IBL 하늘을 배경으로 그릴 때만 흐린 파노라마를 쓰는 하늘 셰이더와 흐린 텍스처 캐시.
/// 조명(앰비언트·반사)을 만드는 큐브맵 패스(AT_CUBEMAP_PASS)는 원본을 그대로 써서 조명은 바뀌지 않는다.
/// </summary>
public static class HdriBlur
{
    private static Shader? _shader;
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
    public static Texture2D? Blurred(Texture2D src, int level)
    {
        level = Math.Clamp(level, 0, PanoramaBlur.MaxLevel);
        if (level == 0) return null;
        var key = (src.GetInstanceId(), level);
        if (Cache.TryGetValue(key, out var cached)) return cached;
        Texture2D? tex = null;
        try
        {
            var img = src.GetImage();
            if (img != null && !img.IsEmpty())
            {
                img = (Image)img.Duplicate();
                if (img.IsCompressed()) img.Decompress();
                if (img.HasMipmaps()) img.ClearMipmaps();
                img.Convert(Image.Format.Rgbf);
                int w = img.GetWidth(), h = img.GetHeight();
                var bytes = img.GetData();
                var f = new float[w * h * 3];
                Buffer.BlockCopy(bytes, 0, f, 0, f.Length * 4);
                var outF = PanoramaBlur.Blur(f, w, h, level, out int ow, out int oh);
                var ob = new byte[outF.Length * 4];
                Buffer.BlockCopy(outF, 0, ob, 0, ob.Length);
                tex = ImageTexture.CreateFromImage(Image.CreateFromData(ow, oh, false, Image.Format.Rgbf, ob));
            }
        }
        catch (Exception ex) { GD.PushWarning($"[HDRI] blur failed: {ex.Message}"); }
        Cache[key] = tex;
        return tex;
    }
}
