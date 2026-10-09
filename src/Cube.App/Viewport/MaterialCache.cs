using Cube.Core.Scene;
using Godot;

namespace Cube.App.Viewport;

/// <summary>
/// MaterialDef → Godot ShaderMaterial 캐시. 셰이더는 (타입, 알파 모드, 양면) 조합마다 코드로 만든다(알파 모드는 렌더 패스가 달라 유니폼으로 바꿀 수 없음).
/// 모든 텍스처 가능한 파라미터는 `{key}_tex` + `use_{key}_tex` 유니폼을 갖고, KHR_texture_transform(오프셋/스케일/회전)을 모든 텍스처에 적용한다.
/// 뷰포트 미리보기: 베이스 컬러·불투명도·노멀·AO·메탈릭·러프니스·이미시브(세기)·클리어코트·이방성·스펙큘러·시인(림)까지. 투과/볼륨/IOR/이리데선스/분산은 내보내기 전용.
/// 양면이 아니면 뒷면은 검정(뒤집힌 면을 바로 보이게).
/// </summary>
/// <remarks>
/// 캐시 3단: ① 셰이더 코드(<c>_shaders</c>, 키 = 타입_알파모드_양면) ② 머티리얼별 ShaderMaterial(<c>_cache</c>, 키 = (머티리얼 ID, 텍스처 사용))
/// ③ 이미지 경로별 텍스처(<c>_images</c>). <see cref="Get"/>은 매번 파라미터를 다시 써 넣으므로 MaterialDef 값이 바뀌어도 같은 ShaderMaterial을 재사용한다.
/// 셰이더 키가 바뀌면(타입/알파 모드/양면 변경) 새 ShaderMaterial을 만든다. 색 값은 <c>source_color</c> 유니폼이라 sRGB 그대로 넘긴다.
/// </remarks>
public static class MaterialCache
{
    /// <summary>(머티리얼 ID, 텍스처 사용 여부) → (ShaderMaterial, 만들 때의 셰이더 키). 키가 다르면 다시 만든다.</summary>
    private static readonly Dictionary<(int id, bool textured), (ShaderMaterial mat, string key)> _cache = new();
    /// <summary>셰이더 키 → 컴파일된 Shader. 같은 조합의 머티리얼들이 셰이더를 공유한다.</summary>
    private static readonly Dictionary<string, Shader> _shaders = new();
    /// <summary>지연 생성되는 내장 기본 matcap 텍스처.</summary>
    private static Texture2D? _defaultMatcap;
    /// <summary>이미지 파일 경로 → 텍스처(null = 로드 실패). 실패도 캐시해 매 프레임 재시도하지 않는다.</summary>
    private static readonly Dictionary<string, Texture2D?> _images = new();

    /// <summary>셰이더를 구분하는 키: "{타입}_{알파 모드 0/1/2}_{양면 0/1}".</summary>
    private static string ShaderKey(MaterialDef d) => $"{d.Type}_{(int)d.GetF("alphaMode")}_{(d.GetF("doubleSided") > 0.5f ? 1 : 0)}";

    /// <summary>머티리얼에 맞는 셰이더를 캐시에서 찾고, 없으면 <see cref="BuildShader"/>로 코드를 만들어 저장한다.</summary>
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
    /// <param name="key">파라미터 키(유니폼 이름 접두사).</param>
    /// <param name="color">색 텍스처면 true(source_color = sRGB 디코드), 값 텍스처면 false(hint_default_white).</param>
    /// <param name="normal">노멀 맵이면 true(hint_normal).</param>
    /// <returns>"uniform sampler2D {key}_tex ...; uniform bool use_{key}_tex = false;" 두 줄.</returns>
    private static string TexUniform(string key, bool color, bool normal = false)
        => $"uniform sampler2D {key}_tex : {(normal ? "hint_normal" : color ? "source_color" : "hint_default_white")}, filter_linear_mipmap_anisotropic, repeat_enable;\nuniform bool use_{key}_tex = false;\n";

    /// <summary>
    /// 머티리얼 타입·알파 모드·양면 여부로 Godot spatial 셰이더 코드를 생성한다.
    /// </summary>
    /// <remarks>
    /// 구성: render_mode(항상 cull_disabled; 타입별 diffuse/specular 모델, Unlit/Matcap = unshaded, 알파 모드 2 = blend_mix)
    /// → 공통 유니폼(albedo/alpha/alpha_cutoff/UV 변환/albedo_tex/use_vertex_color) → 타입별 유니폼 → <c>tuv()</c>(KHR_texture_transform)
    /// → fragment(베이스 색·알파 계산, 양면이 아니면 뒷면 검정, 타입별 출력, 알파 모드별 ALPHA 설정).
    /// 알파 모드: 0 = Opaque, 1 = Mask(ALPHA_SCISSOR_THRESHOLD), 2 = Blend. 텍스처 값은 계수와 곱한다(glTF 규칙).
    /// </remarks>
    /// <param name="type">머티리얼 타입.</param>
    /// <param name="alphaMode">0 Opaque / 1 Mask / 2 Blend.</param>
    /// <param name="doubleSided">false면 뒷면을 검정·무반사로 그려 뒤집힌 면이 바로 보이게 한다.</param>
    internal static string BuildShader(MaterialType type, int alphaMode, bool doubleSided)
    {
        var sb = new System.Text.StringBuilder();
        // 헤더와 render_mode: 타입별 라이팅 모델(Lambert = 스펙큘러 끔, BlinnPhong/PBR = GGX), Blend면 알파 블렌드 + 불투명 깊이 쓰기
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
        // 모든 타입 공통 유니폼: 베이스 색·알파·컷오프, UV 변환, 컬러 텍스처, 가중치 표시용 정점 색 스위치, 알파 맵
        sb.Append("uniform vec4 albedo : source_color = vec4(0.5, 0.5, 0.5, 1.0);\nuniform float alpha = 1.0;\nuniform float alpha_cutoff = 0.5;\n");
        sb.Append("uniform vec2 uv_offset = vec2(0.0);\nuniform vec2 uv_scale = vec2(1.0);\nuniform float uv_rotation = 0.0;\n");
        sb.Append("uniform sampler2D albedo_tex : source_color, filter_linear_mipmap_anisotropic, repeat_enable;\nuniform bool use_texture = false;\nuniform bool use_vertex_color = false;\n");
        sb.Append(TexUniform("alpha", false));
        // 조명을 받는 타입(Lambert/BlinnPhong/PBR)만 노멀 맵과 이미시브를 가진다
        bool lit = type is MaterialType.Lambert or MaterialType.BlinnPhong or MaterialType.Pbr;
        if (lit)
        {
            sb.Append("uniform float normal_scale = 1.0;\n").Append(TexUniform("normal", false, normal: true));
            sb.Append("uniform vec4 emissive : source_color = vec4(0.0, 0.0, 0.0, 1.0);\nuniform float emissive_strength = 1.0;\n").Append(TexUniform("emissive", true));
        }
        // Blinn-Phong: 스펙큘러 색·광택(shininess)과 맵
        if (type == MaterialType.BlinnPhong)
        {
            sb.Append("uniform vec4 specular_color : source_color = vec4(0.5, 0.5, 0.5, 1.0);\nuniform float shininess = 32.0;\n");
            sb.Append(TexUniform("specular", true)).Append(TexUniform("shininess", false));
        }
        // PBR: 메탈릭/러프니스/AO + glTF 확장(clearcoat, specular, sheen, anisotropy) 계수와 맵
        if (type == MaterialType.Pbr)
        {
            sb.Append("uniform float metallic = 0.0;\nuniform float roughness = 0.5;\nuniform float occlusion = 1.0;\n");
            sb.Append(TexUniform("metallic", false)).Append(TexUniform("roughness", false)).Append(TexUniform("occlusion", false));
            sb.Append("uniform float clearcoat = 0.0;\nuniform float clearcoat_roughness = 0.0;\n").Append(TexUniform("clearcoat", false)).Append(TexUniform("clearcoatRoughness", false));
            sb.Append("uniform float specular_factor = 1.0;\nuniform vec4 specular_color_factor : source_color = vec4(1.0);\n").Append(TexUniform("specularFactor", false)).Append(TexUniform("specularColorFactor", true));
            sb.Append("uniform vec4 sheen_color : source_color = vec4(0.0, 0.0, 0.0, 1.0);\nuniform float sheen_roughness = 0.0;\n").Append(TexUniform("sheenColor", true)).Append(TexUniform("sheenRoughness", false));
            sb.Append("uniform float anisotropy = 0.0;\nuniform float anisotropy_rotation = 0.0;\n").Append(TexUniform("anisotropy", false));
        }
        // Matcap: 카메라 공간 노멀로 샘플할 matcap 이미지
        if (type == MaterialType.Matcap) sb.Append("uniform sampler2D matcap : source_color, filter_linear;\n");
        // UV 변환 함수와 fragment 앞부분(베이스 색·알파에 컬러/알파 맵을 곱하고, 가중치 표시 중이면 정점 색으로 대체)
        sb.Append(@"
vec2 tuv(vec2 uv) {
	vec2 s = uv * uv_scale;
	float c = cos(uv_rotation), n = sin(uv_rotation);
	return vec2(c * s.x - n * s.y, n * s.x + c * s.y) + uv_offset; // KHR_texture_transform: T·R·S
}
void fragment() {
	// 음수 스케일(거울) 인스턴스: Godot의 FRONT_FACING·양면 노멀 뒤집기가 반대로 나오므로 되돌린다(surface.gdshader와 같음)
	bool mirrored = determinant(mat3(MODEL_MATRIX)) < 0.0;
	if (mirrored) NORMAL = -NORMAL;
	vec2 uv = tuv(UV);
	vec3 base = albedo.rgb;
	float a = alpha;
	if (use_texture) { vec4 t = texture(albedo_tex, uv); base *= t.rgb; a *= t.a; }
	if (use_alpha_tex) a *= texture(alpha_tex, uv).r;
	if (use_vertex_color) base = COLOR.rgb;
");
        // 단면 머티리얼은 뒷면 베이스 색을 검정으로
        if (!doubleSided) sb.Append("\tif (FRONT_FACING == mirrored) { base = vec3(0.0); }\n");
        // 타입별 출력: Unlit = 색 그대로, Matcap = 뷰 노멀 xy로 matcap 샘플, 나머지 = 조명 파라미터 설정
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
                // Blinn-Phong → PBR 근사: 러프니스 = sqrt(2/(shininess+2)), SPECULAR = 스펙큘러 색 밝기
                if (type == MaterialType.BlinnPhong)
                    sb.Append("\tfloat sh = shininess; if (use_shininess_tex) sh = mix(1.0, 128.0, texture(shininess_tex, uv).r);\n\tvec3 sc = specular_color.rgb; if (use_specular_tex) sc *= texture(specular_tex, uv).rgb;\n\tROUGHNESS = clamp(sqrt(2.0 / (sh + 2.0)), 0.05, 1.0); METALLIC = 0.0; SPECULAR = clamp(dot(sc, vec3(0.333)), 0.0, 1.0);\n");
                // PBR: 맵 × 계수로 METALLIC/ROUGHNESS/AO/CLEARCOAT/SPECULAR를 넣고, sheen은 RIM으로, 이방성은 ANISOTROPY(_FLOW)로 근사
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
                if (!doubleSided) sb.Append("\tif (FRONT_FACING == mirrored) { ROUGHNESS = 1.0; METALLIC = 0.0; EMISSION = vec3(0.0); }\n");
                break;
        }
        // 알파 모드 출력: Mask = 컷오프 스시저, Blend = 알파 그대로(Opaque면 ALPHA를 쓰지 않아 불투명 패스 유지)
        if (alphaMode == 1) sb.Append("\tALPHA = a; ALPHA_SCISSOR_THRESHOLD = alpha_cutoff;\n");
        else if (alphaMode == 2) sb.Append("\tALPHA = a;\n");
        sb.Append("}\n");
        return sb.ToString();
    }

    /// <summary>
    /// 머티리얼의 뷰포트용 ShaderMaterial. textured=false(Smooth Shade All, Maya 5)는 같은 셰이더에 모든 텍스처를 끈 변형을 따로 캐시한다
    /// (색·노멀 강도·메탈릭 등 값은 그대로, 이미지 맵만 끔).
    /// </summary>
    /// <param name="def">문서 머티리얼.</param>
    /// <param name="textured">false면 이미지 맵을 모두 끈 변형(Smooth Shade All 모드).</param>
    /// <returns>파라미터가 현재 값으로 갱신된 ShaderMaterial(캐시 공유 인스턴스).</returns>
    public static ShaderMaterial Get(MaterialDef def, bool textured = true)
    {
        // 캐시 항목이 없거나 셰이더 키가 바뀌었으면 새 ShaderMaterial을 만든다
        string key = ShaderKey(def);
        var ck = (def.Id, textured);
        if (!_cache.TryGetValue(ck, out var entry) || entry.key != key)
        {
            entry = (new ShaderMaterial { Shader = ShaderFor(def) }, key);
            _cache[ck] = entry;
        }
        // 정적 플래그로 Apply/Tex에 텍스처 사용 여부를 전달한 뒤 기본값(true)으로 되돌린다
        _textured = textured;
        Apply(entry.mat, def);
        _textured = true;
        return entry.mat;
    }

    /// <summary>Apply 중 텍스처 사용 여부(Get이 설정).</summary>
    private static bool _textured = true;

    /// <summary>머티리얼 ID의 캐시된 ShaderMaterial 두 변형(텍스처 사용/미사용)을 버린다(삭제·타입 변경 등).</summary>
    public static void Invalidate(int id) { _cache.Remove((id, true)); _cache.Remove((id, false)); }

    /// <summary>코어 RGB 벡터(0..1, sRGB)를 Godot Color로.</summary>
    private static Color C(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);

    /// <summary>파라미터 key의 텍스처 맵을 셰이더 유니폼 {uniform}_tex / use_{uniform}_tex에 설정한다(텍스처를 끈 변형이면 항상 사용 안 함).</summary>
    private static void Tex(ShaderMaterial m, MaterialDef d, string key, string uniform)
    {
        var t = _textured ? LoadTexture(d.Tex(key)) : null;
        m.SetShaderParameter("use_" + uniform + "_tex", t != null);
        if (t != null) m.SetShaderParameter(uniform + "_tex", t);
    }

    /// <summary>MaterialDef의 값·맵을 셰이더 유니폼에 모두 써 넣는다(타입에 없는 유니폼은 건너뜀).</summary>
    private static void Apply(ShaderMaterial m, MaterialDef d)
    {
        // 공통: 베이스 색과 컬러 텍스처, 알파·컷오프·알파 맵
        m.SetShaderParameter("albedo", C(d.Color));
        var tex = _textured ? LoadTexture(d.TexturePath) : null;
        m.SetShaderParameter("use_texture", tex != null);
        if (tex != null) m.SetShaderParameter("albedo_tex", tex);
        m.SetShaderParameter("alpha", d.GetF("alpha"));
        m.SetShaderParameter("alpha_cutoff", d.GetF("alphaCutoff"));
        Tex(m, d, "alpha", "alpha");
        // KHR_texture_transform: 오프셋·스케일·회전(도 → 라디안)
        m.SetShaderParameter("uv_offset", new Vector2(d.GetF("uvOffsetU"), d.GetF("uvOffsetV")));
        m.SetShaderParameter("uv_scale", new Vector2(d.GetF("uvScaleU"), d.GetF("uvScaleV")));
        m.SetShaderParameter("uv_rotation", Mathf.DegToRad(d.GetF("uvRotation")));
        // 조명 타입 공통: 노멀 강도/맵, 이미시브 색·세기·맵
        if (d.Type is MaterialType.Lambert or MaterialType.BlinnPhong or MaterialType.Pbr)
        {
            m.SetShaderParameter("normal_scale", d.GetF("normal")); Tex(m, d, "normal", "normal");
            m.SetShaderParameter("emissive", C(d.Get("emissive"))); m.SetShaderParameter("emissive_strength", d.GetF("emissiveStrength")); Tex(m, d, "emissive", "emissive");
        }
        // 타입별 파라미터
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

    /// <summary>matcap 이미지를 읽고, 경로가 없거나 실패하면 내장 기본 matcap을 쓴다.</summary>
    public static Texture2D LoadMatcap(string? path) => LoadTexture(path) ?? DefaultMatcap;

    /// <summary>이미지 파일 → 텍스처(경로별 캐시). 없거나 실패하면 null.</summary>
    /// <remarks>Godot <c>Image.Load</c>로 OS 경로를 직접 읽어(임포터 없이) 밉맵을 만든 뒤 ImageTexture로 만든다.</remarks>
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
            // 256×256 RGB 이미지에 구를 직접 그린다: 픽셀 → 구 노멀(nx, ny, √(1−r²)), 바깥은 어두운 회색
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
                        // 반 램버트 확산² + 블린 하이라이트(지수 48) + 가장자리 림(푸른 기운)으로 밝기 결정
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
