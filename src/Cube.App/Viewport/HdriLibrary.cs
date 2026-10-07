using System.Runtime.InteropServices;
using Cube.Core.IO;
using Godot;

namespace Cube.App.Viewport;

/// <summary>
/// IBL용 HDRI 텍스처. 내장 10개(assets/hdri/*.hdrbin = Poly Haven CC0 Radiance .hdr, 임포터를 피하려고 확장자만 바꿈)는
/// <see cref="RgbeImage"/>로 직접 디코드해 Rgbf 이미지로 만들고, 사용자 파일(.hdr은 같은 디코더, 그 외는 Image.Load)도 받는다.
/// </summary>
public static class HdriLibrary
{
    public const string Custom = "custom";

    public static readonly (string id, string label)[] BuiltIn =
    {
        ("studio_small_03", "Studio Small 03 (neutral)"),
        ("studio_small_09", "Studio Small 09 (soft)"),
        ("brown_photostudio_02", "Brown Photo Studio 02"),
        ("photo_studio_01", "Photo Studio 01"),
        ("kloppenheim_06", "Kloppenheim 06 (overcast)"),
        ("venice_sunset", "Venice Sunset"),
        ("spruit_sunrise", "Spruit Sunrise"),
        ("lebombo", "Lebombo (interior)"),
        ("qwantani_dusk_2", "Qwantani Dusk"),
        ("autumn_field_puresky", "Autumn Field (pure sky)"),
    };

    private static readonly Dictionary<string, Texture2D?> Cache = new();

    public static Texture2D? Load(RenderSettings r) => r.Hdri == Custom ? (string.IsNullOrEmpty(r.HdriPath) ? null : LoadFile(r.HdriPath)) : LoadBuiltIn(r.Hdri);

    public static Texture2D? LoadBuiltIn(string id)
    {
        if (!BuiltIn.Any(b => b.id == id)) id = BuiltIn[0].id;
        string key = "builtin:" + id;
        if (Cache.TryGetValue(key, out var cached)) return cached;
        Texture2D? tex = null;
        try
        {
            var bytes = Godot.FileAccess.GetFileAsBytes($"res://assets/hdri/{id}.hdrbin");
            if (bytes.Length > 0) tex = FromRgbe(RgbeImage.Decode(bytes));
            else GD.PushWarning($"[HDRI] missing res://assets/hdri/{id}.hdrbin");
        }
        catch (Exception ex) { GD.PushWarning($"[HDRI] {id}: {ex.Message}"); }
        Cache[key] = tex;
        return tex;
    }

    public static Texture2D? LoadFile(string path)
    {
        string key = "file:" + path;
        if (Cache.TryGetValue(key, out var cached)) return cached;
        Texture2D? tex = null;
        try
        {
            if (System.IO.File.Exists(path))
            {
                string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
                if (ext is ".hdr" or ".hdrbin" or ".rgbe") tex = FromRgbe(RgbeImage.Decode(System.IO.File.ReadAllBytes(path)));
                else
                {
                    var img = Image.LoadFromFile(path);
                    if (img != null && !img.IsEmpty()) tex = ImageTexture.CreateFromImage(img);
                }
            }
        }
        catch (Exception ex) { GD.PushWarning($"[HDRI] {path}: {ex.Message}"); }
        Cache[key] = tex;
        return tex;
    }

    /// <summary>파일이 바뀌었을 때 다시 읽도록 캐시를 비운다.</summary>
    public static void Forget(string path) => Cache.Remove("file:" + path);

    private static Texture2D FromRgbe(RgbeImage img)
    {
        var bytes = MemoryMarshal.AsBytes<float>(img.Rgb).ToArray();
        var image = Image.CreateFromData(img.Width, img.Height, false, Image.Format.Rgbf, bytes);
        return ImageTexture.CreateFromImage(image);
    }
}
