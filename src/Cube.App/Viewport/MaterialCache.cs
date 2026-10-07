using Cube.Core.Scene;
using Godot;

namespace Cube.App.Viewport;

/// <summary>MaterialDef → Godot ShaderMaterial 캐시. 속성이 바뀌면 유니폼만 갱신한다.</summary>
public static class MaterialCache
{
    private static readonly Dictionary<int, (ShaderMaterial mat, MaterialType type)> _cache = new();
    private static readonly Dictionary<MaterialType, Shader> _shaders = new();
    private static Texture2D? _defaultMatcap;
    private static readonly Dictionary<string, Texture2D?> _images = new();

    private static Shader ShaderFor(MaterialType t)
    {
        if (!_shaders.TryGetValue(t, out var sh))
        {
            string path = t switch
            {
                MaterialType.BlinnPhong => "res://assets/shaders/mat_blinn.gdshader",
                MaterialType.Pbr => "res://assets/shaders/mat_pbr.gdshader",
                MaterialType.Unlit => "res://assets/shaders/mat_unlit.gdshader",
                MaterialType.Matcap => "res://assets/shaders/mat_matcap.gdshader",
                _ => "res://assets/shaders/surface.gdshader",
            };
            sh = GD.Load<Shader>(path); _shaders[t] = sh;
        }
        return sh;
    }

    public static ShaderMaterial Get(MaterialDef def)
    {
        if (!_cache.TryGetValue(def.Id, out var entry) || entry.type != def.Type)
        {
            entry = (new ShaderMaterial { Shader = ShaderFor(def.Type) }, def.Type);
            _cache[def.Id] = entry;
        }
        Apply(entry.mat, def);
        return entry.mat;
    }

    public static void Invalidate(int id) => _cache.Remove(id);

    private static void Apply(ShaderMaterial m, MaterialDef d)
    {
        m.SetShaderParameter("albedo", new Color(d.Color.X, d.Color.Y, d.Color.Z));
        var tex = LoadTexture(d.TexturePath);
        m.SetShaderParameter("use_texture", tex != null);
        if (tex != null) m.SetShaderParameter("albedo_tex", tex);
        switch (d.Type)
        {
            case MaterialType.BlinnPhong:
                m.SetShaderParameter("specular_color", new Color(d.Specular.X, d.Specular.Y, d.Specular.Z));
                m.SetShaderParameter("shininess", d.Shininess);
                break;
            case MaterialType.Pbr:
                m.SetShaderParameter("metallic", d.Metallic);
                m.SetShaderParameter("roughness", d.Roughness);
                break;
            case MaterialType.Matcap:
                m.SetShaderParameter("matcap", LoadMatcap(d.MatcapPath));
                break;
        }
    }

    public static Texture2D LoadMatcap(string? path) => LoadTexture(path) ?? DefaultMatcap;

    /// <summary>이미지 파일 → 텍스처(경로별 캐시). 없거나 실패하면 null.</summary>
    public static Texture2D? LoadTexture(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (!_images.TryGetValue(path, out var tex))
        {
            tex = null;
            try
            {
                var img = new Image();
                if (img.Load(path) == Error.Ok) { img.GenerateMipmaps(); tex = ImageTexture.CreateFromImage(img); }
                else GD.PushWarning($"[Material] image load failed: {path}");
            }
            catch (Exception ex) { GD.PushWarning($"[Material] image load failed: {ex.Message}"); }
            _images[path] = tex;
        }
        return tex;
    }

    /// <summary>파일이 바뀌었을 때 다시 읽도록 캐시를 비운다.</summary>
    public static void ForgetTexture(string path) => _images.Remove(path);

    /// <summary>내장 기본 matcap: 위-왼쪽 조명의 회색 구에 하이라이트.</summary>
    public static Texture2D DefaultMatcap
    {
        get
        {
            if (_defaultMatcap != null) return _defaultMatcap;
            const int n = 256;
            var img = Image.CreateEmpty(n, n, false, Image.Format.Rgb8);
            var lightDir = new Vector3(-0.5f, 0.6f, 0.65f).Normalized();
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float nx = (x + 0.5f) / n * 2 - 1, ny = 1 - (y + 0.5f) / n * 2;
                    float r2 = nx * nx + ny * ny;
                    Color c;
                    if (r2 > 1f) c = new Color(0.1f, 0.1f, 0.1f);
                    else
                    {
                        var nrm = new Vector3(nx, ny, Mathf.Sqrt(1 - r2));
                        float diff = nrm.Dot(lightDir) * 0.5f + 0.5f; // half-lambert: 뒤쪽도 너무 어둡지 않게
                        var h = (lightDir + new Vector3(0, 0, 1)).Normalized();
                        float spec = Mathf.Pow(Mathf.Max(nrm.Dot(h), 0f), 48f);
                        float v = 0.2f + 0.75f * diff * diff + 0.5f * spec;
                        float rim = Mathf.Pow(1 - nrm.Z, 3f) * 0.2f;
                        c = new Color(v + rim * 0.6f, v + rim * 0.7f, v + rim);
                    }
                    img.SetPixel(x, y, c);
                }
            _defaultMatcap = ImageTexture.CreateFromImage(img);
            return _defaultMatcap;
        }
    }
}
