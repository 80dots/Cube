using Cube.Core.Scene;
using Godot;

namespace Cube.App.Viewport;

/// <summary>
/// MaterialDef → Godot ShaderMaterial 캐시. 셰이더는 (타입, 알파 모드, 양면) 조합마다 코드로 만든다(알파 모드는 렌더 패스가 달라 유니폼으로 바꿀 수 없음).
/// 모든 텍스처 가능한 파라미터는 `{key}_tex` + `use_{key}_tex` 유니폼을 갖고, KHR_texture_transform(오프셋/스케일/회전)을 모든 텍스처에 적용한다.
/// 뷰포트 미리보기: 베이스 컬러·불투명도·노멀·AO·메탈릭·러프니스·이미시브(세기)·클리어코트·이방성·스펙큘러·시인(림)까지. 투과/볼륨/IOR/이리데선스/분산은 내보내기 전용.
/// 양면이 아니면 뒷면은 검정(뒤집힌 면을 바로 보이게).
/// </summary>
public static class MaterialCache
{
    private static readonly Dictionary<(int id, bool textured), (ShaderMaterial mat, string key)> _cache = new();
    private static readonly Dictionary<string, Shader> _shaders = new();
    private static Texture2D? _defaultMatcap;
    private static readonly Dictionary<string, Texture2D?> _images = new();

    private static string ShaderKey(MaterialDef d) => $"{d.Type}_{(int)d.GetF("alphaMode")}_{(d.GetF("doubleSided") > 0.5f ? 1 : 0)}";

    private static Shader ShaderFor(MaterialDef d)
    {
        string key = ShaderKey(d);
        if (!_shaders.TryGetValue(key, out var sh))
        {
            sh = new Shader { Code = BuildShader(d.Type, (int)d.GetF("alphaMode"), d.GetF("doubleSided") > 0.5f) };
            _shaders[key] = sh;
        }
        return sh;
    }

    /// <summary>유니폼 선언 + 텍스처 샘플 헬퍼.</summary>
    private static string TexUniform(string key, bool color, bool normal = false)
        => $"uniform sampler2D {key}_tex : {(normal ? "hint_normal" : color ? "source_color" : "hint_default_white")}, filter_linear_mipmap_anisotropic, repeat_enable;\nuniform bool use_{key}_tex = false;\n";

    internal static string BuildShader(MaterialType type, int alphaMode, bool doubleSided)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("shader_type spatial;\n");
        string modes = "cull_disabled";
        modes += type switch
        {
            MaterialType.Unlit or MaterialType.Matcap => ", unshaded",
            MaterialType.Lambert => ", diffuse_lambert, specular_disabled",
            MaterialType.BlinnPhong => ", diffuse_lambert, specular_schlick_ggx",
            _ => ", diffuse_burley, specular_schlick_ggx",
        };
        if (alphaMode == 2) modes += ", blend_mix, depth_draw_opaque";
        sb.Append($"render_mode {modes};\n");
        sb.Append("uniform vec4 albedo : source_color = vec4(0.5, 0.5, 0.5, 1.0);\nuniform float alpha = 1.0;\nuniform float alpha_cutoff = 0.5;\n");
        sb.Append("uniform vec2 uv_offset = vec2(0.0);\nuniform vec2 uv_scale = vec2(1.0);\nuniform float uv_rotation = 0.0;\n");
        sb.Append("uniform sampler2D albedo_tex : source_color, filter_linear_mipmap_anisotropic, repeat_enable;\nuniform bool use_texture = false;\nuniform bool use_vertex_color = false;\n");
        sb.Append(TexUniform("alpha", false));
        bool lit = type is MaterialType.Lambert or MaterialType.BlinnPhong or MaterialType.Pbr;
        if (lit)
        {
            sb.Append("uniform float normal_scale = 1.0;\n").Append(TexUniform("normal", false, normal: true));
            sb.Append("uniform vec4 emissive : source_color = vec4(0.0, 0.0, 0.0, 1.0);\nuniform float emissive_strength = 1.0;\n").Append(TexUniform("emissive", true));
        }
        if (type == MaterialType.BlinnPhong)
        {
            sb.Append("uniform vec4 specular_color : source_color = vec4(0.5, 0.5, 0.5, 1.0);\nuniform float shininess = 32.0;\n");
            sb.Append(TexUniform("specular", true)).Append(TexUniform("shininess", false));
        }
        if (type == MaterialType.Pbr)
        {
            sb.Append("uniform float metallic = 0.0;\nuniform float roughness = 0.5;\nuniform float occlusion = 1.0;\n");
            sb.Append(TexUniform("metallic", false)).Append(TexUniform("roughness", false)).Append(TexUniform("occlusion", false));
            sb.Append("uniform float clearcoat = 0.0;\nuniform float clearcoat_roughness = 0.0;\n").Append(TexUniform("clearcoat", false)).Append(TexUniform("clearcoatRoughness", false));
            sb.Append("uniform float specular_factor = 1.0;\nuniform vec4 specular_color_factor : source_color = vec4(1.0);\n").Append(TexUniform("specularFactor", false)).Append(TexUniform("specularColorFactor", true));
            sb.Append("uniform vec4 sheen_color : source_color = vec4(0.0, 0.0, 0.0, 1.0);\nuniform float sheen_roughness = 0.0;\n").Append(TexUniform("sheenColor", true)).Append(TexUniform("sheenRoughness", false));
            sb.Append("uniform float anisotropy = 0.0;\nuniform float anisotropy_rotation = 0.0;\n").Append(TexUniform("anisotropy", false));
        }
        if (type == MaterialType.Matcap) sb.Append("uniform sampler2D matcap : source_color, filter_linear;\n");
        sb.Append(@"
vec2 tuv(vec2 uv) {
	vec2 s = uv * uv_scale;
	float c = cos(uv_rotation), n = sin(uv_rotation);
	return vec2(c * s.x - n * s.y, n * s.x + c * s.y) + uv_offset; // KHR_texture_transform: T·R·S
}
void fragment() {
	vec2 uv = tuv(UV);
	vec3 base = albedo.rgb;
	float a = alpha;
	if (use_texture) { vec4 t = texture(albedo_tex, uv); base *= t.rgb; a *= t.a; }
	if (use_alpha_tex) a *= texture(alpha_tex, uv).r;
	if (use_vertex_color) base = COLOR.rgb;
");
        if (!doubleSided) sb.Append("\tif (!FRONT_FACING) { base = vec3(0.0); }\n");
        switch (type)
        {
            case MaterialType.Unlit:
                sb.Append("\tALBEDO = base;\n"); break;
            case MaterialType.Matcap:
                sb.Append("\tvec3 nn = normalize(NORMAL); vec2 muv = nn.xy * 0.5 + 0.5; muv.y = 1.0 - muv.y;\n\tALBEDO = texture(matcap, muv).rgb * base;\n"); break;
            default:
                sb.Append("\tALBEDO = base;\n");
                sb.Append("\tif (use_normal_tex) { NORMAL_MAP = texture(normal_tex, uv).rgb; NORMAL_MAP_DEPTH = normal_scale; }\n");
                sb.Append("\tvec3 em = emissive.rgb; if (use_emissive_tex) em *= texture(emissive_tex, uv).rgb;\n\tEMISSION = em * emissive_strength;\n");
                if (type == MaterialType.Lambert) sb.Append("\tROUGHNESS = 1.0; METALLIC = 0.0;\n");
                if (type == MaterialType.BlinnPhong)
                    sb.Append("\tfloat sh = shininess; if (use_shininess_tex) sh = mix(1.0, 128.0, texture(shininess_tex, uv).r);\n\tvec3 sc = specular_color.rgb; if (use_specular_tex) sc *= texture(specular_tex, uv).rgb;\n\tROUGHNESS = clamp(sqrt(2.0 / (sh + 2.0)), 0.05, 1.0); METALLIC = 0.0; SPECULAR = clamp(dot(sc, vec3(0.333)), 0.0, 1.0);\n");
                if (type == MaterialType.Pbr)
                    sb.Append(@"	float m = metallic; if (use_metallic_tex) m *= texture(metallic_tex, uv).r;
	float r = roughness; if (use_roughness_tex) r *= texture(roughness_tex, uv).r;
	METALLIC = m; ROUGHNESS = r;
	float ao = 1.0; if (use_occlusion_tex) ao = texture(occlusion_tex, uv).r;
	AO = mix(1.0, ao, occlusion); AO_LIGHT_AFFECT = 1.0;
	float cc = clearcoat; if (use_clearcoat_tex) cc *= texture(clearcoat_tex, uv).r;
	float ccr = clearcoat_roughness; if (use_clearcoatRoughness_tex) ccr *= texture(clearcoatRoughness_tex, uv).r;
	if (cc > 0.0) { CLEARCOAT = cc; CLEARCOAT_ROUGHNESS = ccr; }
	float sf = specular_factor; if (use_specularFactor_tex) sf *= texture(specularFactor_tex, uv).r;
	vec3 scf = specular_color_factor.rgb; if (use_specularColorFactor_tex) scf *= texture(specularColorFactor_tex, uv).rgb;
	SPECULAR = clamp(0.5 * sf * dot(scf, vec3(0.333)), 0.0, 1.0);
	vec3 shc = sheen_color.rgb; if (use_sheenColor_tex) shc *= texture(sheenColor_tex, uv).rgb;
	float shr = sheen_roughness; if (use_sheenRoughness_tex) shr *= texture(sheenRoughness_tex, uv).r;
	float shi = max(shc.r, max(shc.g, shc.b));
	if (shi > 0.0) { RIM = shi; RIM_TINT = 1.0 - shr * 0.5; }
	float an = anisotropy; if (use_anisotropy_tex) an *= texture(anisotropy_tex, uv).r;
	if (an > 0.0) { ANISOTROPY = an; ANISOTROPY_FLOW = vec2(cos(anisotropy_rotation), sin(anisotropy_rotation)); }
");
                if (!doubleSided) sb.Append("\tif (!FRONT_FACING) { ROUGHNESS = 1.0; METALLIC = 0.0; EMISSION = vec3(0.0); }\n");
                break;
        }
        if (alphaMode == 1) sb.Append("\tALPHA = a; ALPHA_SCISSOR_THRESHOLD = alpha_cutoff;\n");
        else if (alphaMode == 2) sb.Append("\tALPHA = a;\n");
        sb.Append("}\n");
        return sb.ToString();
    }

    /// <summary>
    /// 머티리얼의 뷰포트용 ShaderMaterial. textured=false(Smooth Shade All, Maya 5)는 같은 셰이더에 모든 텍스처를 끈 변형을 따로 캐시한다
    /// (색·노멀 강도·메탈릭 등 값은 그대로, 이미지 맵만 끔).
    /// </summary>
    public static ShaderMaterial Get(MaterialDef def, bool textured = true)
    {
        string key = ShaderKey(def);
        var ck = (def.Id, textured);
        if (!_cache.TryGetValue(ck, out var entry) || entry.key != key)
        {
            entry = (new ShaderMaterial { Shader = ShaderFor(def) }, key);
            _cache[ck] = entry;
        }
        _textured = textured;
        Apply(entry.mat, def);
        _textured = true;
        return entry.mat;
    }

    /// <summary>Apply 중 텍스처 사용 여부(Get이 설정).</summary>
    private static bool _textured = true;

    public static void Invalidate(int id) { _cache.Remove((id, true)); _cache.Remove((id, false)); }

    private static Color C(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);

    private static void Tex(ShaderMaterial m, MaterialDef d, string key, string uniform)
    {
        var t = _textured ? LoadTexture(d.Tex(key)) : null;
        m.SetShaderParameter("use_" + uniform + "_tex", t != null);
        if (t != null) m.SetShaderParameter(uniform + "_tex", t);
    }

    private static void Apply(ShaderMaterial m, MaterialDef d)
    {
        m.SetShaderParameter("albedo", C(d.Color));
        var tex = _textured ? LoadTexture(d.TexturePath) : null;
        m.SetShaderParameter("use_texture", tex != null);
        if (tex != null) m.SetShaderParameter("albedo_tex", tex);
        m.SetShaderParameter("alpha", d.GetF("alpha"));
        m.SetShaderParameter("alpha_cutoff", d.GetF("alphaCutoff"));
        Tex(m, d, "alpha", "alpha");
        m.SetShaderParameter("uv_offset", new Vector2(d.GetF("uvOffsetU"), d.GetF("uvOffsetV")));
        m.SetShaderParameter("uv_scale", new Vector2(d.GetF("uvScaleU"), d.GetF("uvScaleV")));
        m.SetShaderParameter("uv_rotation", Mathf.DegToRad(d.GetF("uvRotation")));
        if (d.Type is MaterialType.Lambert or MaterialType.BlinnPhong or MaterialType.Pbr)
        {
            m.SetShaderParameter("normal_scale", d.GetF("normal")); Tex(m, d, "normal", "normal");
            m.SetShaderParameter("emissive", C(d.Get("emissive"))); m.SetShaderParameter("emissive_strength", d.GetF("emissiveStrength")); Tex(m, d, "emissive", "emissive");
        }
        switch (d.Type)
        {
            case MaterialType.BlinnPhong:
                m.SetShaderParameter("specular_color", C(d.Specular)); m.SetShaderParameter("shininess", d.Shininess);
                Tex(m, d, "specular", "specular"); Tex(m, d, "shininess", "shininess");
                break;
            case MaterialType.Pbr:
                m.SetShaderParameter("metallic", d.Metallic); m.SetShaderParameter("roughness", d.Roughness); m.SetShaderParameter("occlusion", d.GetF("occlusion"));
                Tex(m, d, "metallic", "metallic"); Tex(m, d, "roughness", "roughness"); Tex(m, d, "occlusion", "occlusion");
                m.SetShaderParameter("clearcoat", d.GetF("clearcoat")); m.SetShaderParameter("clearcoat_roughness", d.GetF("clearcoatRoughness"));
                Tex(m, d, "clearcoat", "clearcoat"); Tex(m, d, "clearcoatRoughness", "clearcoatRoughness");
                m.SetShaderParameter("specular_factor", d.GetF("specularFactor")); m.SetShaderParameter("specular_color_factor", C(d.Get("specularColorFactor")));
                Tex(m, d, "specularFactor", "specularFactor"); Tex(m, d, "specularColorFactor", "specularColorFactor");
                m.SetShaderParameter("sheen_color", C(d.Get("sheenColor"))); m.SetShaderParameter("sheen_roughness", d.GetF("sheenRoughness"));
                Tex(m, d, "sheenColor", "sheenColor"); Tex(m, d, "sheenRoughness", "sheenRoughness");
                m.SetShaderParameter("anisotropy", d.GetF("anisotropy")); m.SetShaderParameter("anisotropy_rotation", Mathf.DegToRad(d.GetF("anisotropyRotation")));
                Tex(m, d, "anisotropy", "anisotropy");
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
                        float diff = nrm.Dot(lightDir) * 0.5f + 0.5f;
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
