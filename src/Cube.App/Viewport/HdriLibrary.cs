using System.Runtime.InteropServices;
using Cube.Core.IO;
using Godot;

namespace Cube.App.Viewport;

/// <summary>
/// IBL용 HDRI 텍스처. 내장 10개(assets/hdri/*.hdrbin = Poly Haven CC0 Radiance .hdr, 임포터를 피하려고 확장자만 바꿈)는
/// <see cref="RgbeImage"/>로 직접 디코드해 Rgbf 이미지로 만들고, 사용자 파일(.hdr은 같은 디코더, 그 외는 Image.Load)도 받는다.
/// </summary>
/// <remarks>
/// 모든 결과는 키("builtin:id" / "file:경로")로 캐시하며 실패(null)도 캐시한다. 사용자 파일이 바뀌면 <see cref="Forget"/>으로 지운다.
/// 디코드한 HDR 값은 선형 float(Rgbf)이므로 색 공간 변환 없이 PanoramaSkyMaterial에 그대로 넣는다.
/// </remarks>
public static class HdriLibrary
{
    /// <summary>RenderSettings.Hdri가 이 값이면 내장 대신 사용자 파일(RenderSettings.HdriPath)을 쓴다.</summary>
    public const string Custom = "custom";

    /// <summary>내장 HDRI 목록(파일 ID, UI 표시 이름). 첫 항목이 기본값이다.</summary>
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

    /// <summary>캐시 키 → 텍스처(null = 로드 실패).</summary>
    private static readonly Dictionary<string, Texture2D?> Cache = new();
    /// <summary>이미 경고한 없는 파일 경로(패널 4개 × 설정 변경마다 같은 경고가 반복되지 않도록).</summary>
    private static readonly HashSet<string> Warned = new();

    /// <summary>렌더 설정에 맞는 HDRI를 읽는다: Custom이면 경로 파일(비어 있으면 null), 아니면 내장 ID.</summary>
    public static Texture2D? Load(RenderSettings r) => r.Hdri == Custom ? (string.IsNullOrEmpty(r.HdriPath) ? null : LoadFile(r.HdriPath)) : LoadBuiltIn(r.Hdri);

    /// <summary>
    /// 내장 HDRI를 읽는다. 알 수 없는 ID는 첫 내장 항목으로 대체하고, res://assets/hdri/{id}.hdrbin 바이트를 RGBE 디코더로 풀어 텍스처를 만든다.
    /// </summary>
    /// <returns>텍스처, 파일이 없거나 디코드에 실패하면 null(경고 출력).</returns>
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

    /// <summary>
    /// 사용자 HDRI 파일을 읽는다. .hdr/.hdrbin/.rgbe는 RGBE 디코더로, 그 밖의 형식(.exr/.png 등)은 Godot <c>Image.LoadFromFile</c>로 읽는다.
    /// </summary>
    /// <param name="path">OS 파일 경로.</param>
    /// <returns>텍스처, 파일이 없거나 실패하면 null.</returns>
    public static Texture2D? LoadFile(string path)
    {
        string key = "file:" + path;
        if (Cache.TryGetValue(key, out var cached)) return cached;
        Texture2D? tex = null;
        try
        {
            // 없는 파일은 경고만 하고 캐시하지 않는다(나중에 파일이 생기면 다시 읽도록; IBL은 꺼진 것처럼 앰비언트 색으로 표시됨)
            if (!System.IO.File.Exists(path)) { if (Warned.Add(path)) GD.PushWarning($"[HDRI] file not found: {path}"); return null; }
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

    /// <summary>디코드된 RGBE 이미지(선형 float RGB 배열)를 바이트로 재해석해 Rgbf ImageTexture로 만든다(밉맵 없음).</summary>
    private static Texture2D FromRgbe(RgbeImage img)
    {
        var bytes = MemoryMarshal.AsBytes<float>(img.Rgb).ToArray();
        var image = Image.CreateFromData(img.Width, img.Height, false, Image.Format.Rgbf, bytes);
        return ImageTexture.CreateFromImage(image);
    }
}
