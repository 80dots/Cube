using System.Numerics;

namespace Cube.Core.Scene;

/// <summary>머티리얼 셰이딩 모델. Lambert = 확산만(기본 lambert1), BlinnPhong = 확산 + 스펙큘러 하이라이트, Pbr = 메탈릭/러프니스(glTF 핵심 + KHR 확장), Unlit = 조명 무시(내보내기 시 unshaded), Matcap = 매트캡 이미지 기반 표시(내보내기 시 unshaded).</summary>
public enum MaterialType { Lambert, BlinnPhong, Pbr, Unlit, Matcap }

/// <summary>머티리얼 파라미터 값 종류.</summary>
/// <remarks>Float = 실수(X), Color = sRGB 0..1 색(XYZ), Bool = 0/1(X), Enum = Choices 인덱스(X).</remarks>
public enum MatParamKind { Float, Color, Bool, Enum }

/// <summary>
/// 머티리얼 파라미터 정의 하나. Texturable이면 값 대신(또는 값과 곱해) 이미지 텍스처를 쓸 수 있다.
/// Extension은 glTF 내보내기에서 이 값이 들어가는 확장(KHR_materials_*; null = 핵심 사양).
/// TextureChannel은 glTF에서 이 텍스처를 읽는 채널(r/g/b/a/rgb/rgba; 내보내기가 같은 확장의 텍스처들을 한 이미지로 묶을 때 쓴다).
/// </summary>
/// <remarks>
/// Key = Values/Textures 사전 키이자 .cube 저장 키, Label = UI 라벨, Default/Min/Max = 기본값과 편집 범위,
/// Types = 이 파라미터가 보이는 머티리얼 타입, Group = 편집기 그룹(확장 그룹은 접혀 있음), Choices = Enum 선택지, Tip = 툴팁.
/// </remarks>
public sealed record MatParamInfo(
    string Key, string Label, MatParamKind Kind, Vector3 Default, float Min, float Max,
    MaterialType[] Types, string Group, bool Texturable = true, string? Extension = null,
    string TextureChannel = "rgb", string[]? Choices = null, string? Tip = null);

/// <summary>모든 머티리얼 파라미터 표(편집기·셰이더·저장·내보내기가 공유).</summary>
/// <remarks>새 파라미터는 All 표에 한 줄 추가하면 편집 UI(MaterialPropsEditor)·셰이더 생성(MaterialCache)·.cube 저장·glTF 내보내기가 모두 따라간다.</remarks>
public static class MaterialParams
{
    /// <summary>모든 타입(공통 파라미터용).</summary>
    private static readonly MaterialType[] All_ = { MaterialType.Lambert, MaterialType.BlinnPhong, MaterialType.Pbr, MaterialType.Unlit, MaterialType.Matcap };
    /// <summary>PBR 전용.</summary>
    private static readonly MaterialType[] Pbr = { MaterialType.Pbr };
    /// <summary>Blinn-Phong 전용.</summary>
    private static readonly MaterialType[] Blinn = { MaterialType.BlinnPhong };
    /// <summary>조명을 받는 타입(Lambert/BlinnPhong/Pbr).</summary>
    private static readonly MaterialType[] Lit = { MaterialType.Lambert, MaterialType.BlinnPhong, MaterialType.Pbr };
    /// <summary>스칼라 기본값을 Vector3(X)로 만드는 도우미.</summary>
    private static Vector3 F(float v) => new(v, 0, 0);

    /// <summary>편집기 그룹 이름(UI 표시 겸 그룹 키). 괄호 안은 관련 glTF 확장.</summary>
    public const string GBase = "Base", GSurface = "Surface", GEmission = "Emission", GClearcoat = "Clearcoat (KHR_materials_clearcoat)",
        GTransmission = "Transmission / Volume (KHR_materials_transmission, volume, ior)", GSpecular = "Specular (KHR_materials_specular)",
        GSheen = "Sheen (KHR_materials_sheen)", GIridescence = "Iridescence (KHR_materials_iridescence)", GAnisotropy = "Anisotropy (KHR_materials_anisotropy)",
        GAdvanced = "Alpha / Advanced", GUv = "Texture Transform (KHR_texture_transform)";

    /// <summary>전체 파라미터 표(편집기 표시 순서). 텍스처 가능한 값은 텍스처와 곱해진다(glTF 규칙).</summary>
    public static readonly MatParamInfo[] All =
    {
        // 기본(모든 타입 공통)
        new("color", "Base Color", MatParamKind.Color, new(0.5f, 0.5f, 0.5f), 0, 1, All_, GBase, Tip: "Base / albedo color (texture multiplies the color)"),
        new("alpha", "Opacity", MatParamKind.Float, F(1), 0, 1, All_, GBase, TextureChannel: "a", Tip: "glTF baseColorFactor alpha; the texture goes to the base color alpha channel"),
        // Blinn-Phong
        new("specular", "Specular Color", MatParamKind.Color, new(0.5f, 0.5f, 0.5f), 0, 1, Blinn, GSurface),
        new("shininess", "Shininess", MatParamKind.Float, F(32), 1, 128, Blinn, GSurface, TextureChannel: "r", Tip: "Texture value 0..1 maps to 1..128"),
        // PBR 핵심
        new("metallic", "Metallic", MatParamKind.Float, F(0), 0, 1, Pbr, GSurface, TextureChannel: "b"),
        new("roughness", "Roughness", MatParamKind.Float, F(0.5f), 0, 1, Pbr, GSurface, TextureChannel: "g"),
        new("normal", "Normal Map Scale", MatParamKind.Float, F(1), 0, 10, Lit, GSurface, Tip: "Tangent-space normal map (OpenGL +Y)"),
        new("occlusion", "Occlusion Strength", MatParamKind.Float, F(1), 0, 1, Pbr, GSurface, TextureChannel: "r"),
        new("emissive", "Emissive Color", MatParamKind.Color, Vector3.Zero, 0, 1, Lit, GEmission),
        new("emissiveStrength", "Emissive Strength", MatParamKind.Float, F(1), 0, 1000, Lit, GEmission, Texturable: false, Extension: "KHR_materials_emissive_strength"),
        // Clearcoat
        new("clearcoat", "Clearcoat", MatParamKind.Float, F(0), 0, 1, Pbr, GClearcoat, Extension: "KHR_materials_clearcoat", TextureChannel: "r"),
        new("clearcoatRoughness", "Clearcoat Roughness", MatParamKind.Float, F(0), 0, 1, Pbr, GClearcoat, Extension: "KHR_materials_clearcoat", TextureChannel: "g"),
        new("clearcoatNormal", "Clearcoat Normal Scale", MatParamKind.Float, F(1), 0, 10, Pbr, GClearcoat, Extension: "KHR_materials_clearcoat"),
        // Transmission / Volume / IOR
        new("transmission", "Transmission", MatParamKind.Float, F(0), 0, 1, Pbr, GTransmission, Extension: "KHR_materials_transmission", TextureChannel: "r"),
        new("thickness", "Thickness", MatParamKind.Float, F(0), 0, 100, Pbr, GTransmission, Extension: "KHR_materials_volume", TextureChannel: "g"),
        new("attenuationDistance", "Attenuation Distance", MatParamKind.Float, F(0), 0, 100000, Pbr, GTransmission, Texturable: false, Extension: "KHR_materials_volume", Tip: "0 = infinite"),
        new("attenuationColor", "Attenuation Color", MatParamKind.Color, Vector3.One, 0, 1, Pbr, GTransmission, Texturable: false, Extension: "KHR_materials_volume"),
        new("ior", "IOR", MatParamKind.Float, F(1.5f), 1, 5, Pbr, GTransmission, Texturable: false, Extension: "KHR_materials_ior"),
        new("dispersion", "Dispersion", MatParamKind.Float, F(0), 0, 10, Pbr, GTransmission, Texturable: false, Extension: "KHR_materials_dispersion"),
        // Specular(glTF)
        new("specularFactor", "Specular", MatParamKind.Float, F(1), 0, 1, Pbr, GSpecular, Extension: "KHR_materials_specular", TextureChannel: "a"),
        new("specularColorFactor", "Specular Color", MatParamKind.Color, Vector3.One, 0, 1, Pbr, GSpecular, Extension: "KHR_materials_specular"),
        // Sheen
        new("sheenColor", "Sheen Color", MatParamKind.Color, Vector3.Zero, 0, 1, Pbr, GSheen, Extension: "KHR_materials_sheen"),
        new("sheenRoughness", "Sheen Roughness", MatParamKind.Float, F(0), 0, 1, Pbr, GSheen, Extension: "KHR_materials_sheen", TextureChannel: "a"),
        // Iridescence
        new("iridescence", "Iridescence", MatParamKind.Float, F(0), 0, 1, Pbr, GIridescence, Extension: "KHR_materials_iridescence", TextureChannel: "r"),
        new("iridescenceIor", "Iridescence IOR", MatParamKind.Float, F(1.3f), 1, 5, Pbr, GIridescence, Texturable: false, Extension: "KHR_materials_iridescence"),
        new("iridescenceThicknessMin", "Thickness Min (nm)", MatParamKind.Float, F(100), 0, 2000, Pbr, GIridescence, Texturable: false, Extension: "KHR_materials_iridescence"),
        new("iridescenceThicknessMax", "Thickness Max (nm)", MatParamKind.Float, F(400), 0, 2000, Pbr, GIridescence, Extension: "KHR_materials_iridescence", TextureChannel: "g", Tip: "Texture blends between min and max"),
        // Anisotropy
        new("anisotropy", "Anisotropy Strength", MatParamKind.Float, F(0), 0, 1, Pbr, GAnisotropy, Extension: "KHR_materials_anisotropy", TextureChannel: "b"),
        new("anisotropyRotation", "Anisotropy Rotation (deg)", MatParamKind.Float, F(0), -360, 360, Pbr, GAnisotropy, Texturable: false, Extension: "KHR_materials_anisotropy"),
        // Alpha / 기타
        new("alphaMode", "Alpha Mode", MatParamKind.Enum, F(0), 0, 2, All_, GAdvanced, Texturable: false, Choices: new[] { "Opaque", "Mask", "Blend" }),
        new("alphaCutoff", "Alpha Cutoff", MatParamKind.Float, F(0.5f), 0, 1, All_, GAdvanced, Texturable: false),
        new("doubleSided", "Double Sided", MatParamKind.Bool, F(0), 0, 1, All_, GAdvanced, Texturable: false),
        // 텍스처 변환(모든 텍스처에 적용)
        new("uvOffsetU", "UV Offset U", MatParamKind.Float, F(0), -100, 100, All_, GUv, Texturable: false, Extension: "KHR_texture_transform"),
        new("uvOffsetV", "UV Offset V", MatParamKind.Float, F(0), -100, 100, All_, GUv, Texturable: false, Extension: "KHR_texture_transform"),
        new("uvScaleU", "UV Scale U", MatParamKind.Float, F(1), -100, 100, All_, GUv, Texturable: false, Extension: "KHR_texture_transform"),
        new("uvScaleV", "UV Scale V", MatParamKind.Float, F(1), -100, 100, All_, GUv, Texturable: false, Extension: "KHR_texture_transform"),
        new("uvRotation", "UV Rotation (deg)", MatParamKind.Float, F(0), -360, 360, All_, GUv, Texturable: false, Extension: "KHR_texture_transform"),
    };

    /// <summary>키 → 정의 색인.</summary>
    private static readonly Dictionary<string, MatParamInfo> _byKey = All.ToDictionary(p => p.Key);
    /// <summary>키로 파라미터 정의를 찾는다(없으면 null).</summary>
    public static MatParamInfo? Get(string key) => _byKey.TryGetValue(key, out var p) ? p : null;
    /// <summary>해당 머티리얼 타입에서 보이는 파라미터들(표 순서).</summary>
    public static IEnumerable<MatParamInfo> For(MaterialType t) => All.Where(p => p.Types.Contains(t));
}

/// <summary>
/// 문서 머티리얼. 오브젝트(SceneNode.MaterialId)에 할당한다. 색은 sRGB 0..1.
/// 값은 <see cref="MaterialParams"/> 표의 키로 Values에(없으면 기본값), 텍스처는 Textures[키] = 이미지 경로로 둔다.
/// 자주 쓰는 값(Color/Specular/Shininess/Metallic/Roughness/TexturePath)은 속성으로도 접근한다.
/// </summary>
/// <remarks>Values에 없는 키는 표의 Default로 읽힌다(희소 저장). 뷰포트 표시는 MaterialCache, 편집은 SetMaterialCommand(Clone/CopyFrom)로 한다.</remarks>
public sealed class MaterialDef
{
    /// <summary>문서 내 머티리얼 ID(1부터; 0 = 기본 lambert1, 할당 시 Document.NextMaterialId).</summary>
    public int Id;
    /// <summary>표시·내보내기 이름.</summary>
    public string Name = "material";
    /// <summary>셰이딩 모델(보이는 파라미터와 셰이더가 달라진다).</summary>
    public MaterialType Type = MaterialType.Lambert;
    /// <summary>파라미터 값(키 → 값; Float/Bool/Enum은 X).</summary>
    public readonly Dictionary<string, Vector3> Values = new();
    /// <summary>파라미터 텍스처(키 → 이미지 경로).</summary>
    public readonly Dictionary<string, string> Textures = new();
    /// <summary>Matcap 이미지 파일 경로(없으면 내장 기본 matcap).</summary>
    public string? MatcapPath;

    /// <summary>키의 값(없으면 표 기본값, 표에도 없으면 0).</summary>
    public Vector3 Get(string key) => Values.TryGetValue(key, out var v) ? v : MaterialParams.Get(key)?.Default ?? Vector3.Zero;
    /// <summary>스칼라 값(X).</summary>
    public float GetF(string key) => Get(key).X;
    /// <summary>벡터/색 값을 설정한다.</summary>
    public void Set(string key, Vector3 v) => Values[key] = v;
    /// <summary>스칼라 값을 설정한다(X에 저장).</summary>
    public void Set(string key, float v) => Values[key] = new Vector3(v, 0, 0);
    /// <summary>키의 텍스처 경로(없거나 빈 문자열이면 null).</summary>
    public string? Tex(string key) => Textures.TryGetValue(key, out var p) && !string.IsNullOrEmpty(p) ? p : null;
    /// <summary>텍스처 경로를 설정한다(null/빈 문자열이면 제거).</summary>
    public void SetTex(string key, string? path) { if (string.IsNullOrEmpty(path)) Textures.Remove(key); else Textures[key] = path; }

    /// <summary>베이스 컬러(sRGB 0..1).</summary>
    public Vector3 Color { get => Get("color"); set => Set("color", value); }
    /// <summary>BlinnPhong 스펙큘러 색.</summary>
    public Vector3 Specular { get => Get("specular"); set => Set("specular", value); }
    /// <summary>BlinnPhong 광택(1..128).</summary>
    public float Shininess { get => GetF("shininess"); set => Set("shininess", value); }
    /// <summary>PBR 메탈릭(0..1).</summary>
    public float Metallic { get => GetF("metallic"); set => Set("metallic", value); }
    /// <summary>PBR 러프니스(0..1).</summary>
    public float Roughness { get => GetF("roughness"); set => Set("roughness", value); }
    /// <summary>베이스 컬러 텍스처 경로(Textures["color"]).</summary>
    public string? TexturePath { get => Tex("color"); set => SetTex("color", value); }

    /// <summary>값·텍스처 사전까지 복사한 깊은 복사본(ID 포함).</summary>
    public MaterialDef Clone()
    {
        var c = new MaterialDef { Id = Id, Name = Name, Type = Type, MatcapPath = MatcapPath };
        foreach (var kv in Values) c.Values[kv.Key] = kv.Value;
        foreach (var kv in Textures) c.Textures[kv.Key] = kv.Value;
        return c;
    }

    /// <summary>다른 머티리얼의 내용(이름·타입·Matcap·값·텍스처)을 복사한다. ID와 객체 동일성은 유지된다.</summary>
    public void CopyFrom(MaterialDef o)
    {
        Name = o.Name; Type = o.Type; MatcapPath = o.MatcapPath;
        Values.Clear(); foreach (var kv in o.Values) Values[kv.Key] = kv.Value;
        Textures.Clear(); foreach (var kv in o.Textures) Textures[kv.Key] = kv.Value;
    }

    /// <summary>이름·타입·Matcap과 표의 모든 파라미터(유효값 기준, 기본값 포함)·텍스처가 같은지 비교한다.</summary>
    public bool ValuesEqual(MaterialDef o)
    {
        if (Name != o.Name || Type != o.Type || MatcapPath != o.MatcapPath) return false;
        foreach (var p in MaterialParams.All) if (Get(p.Key) != o.Get(p.Key) || Tex(p.Key) != o.Tex(p.Key)) return false;
        return true;
    }

    /// <summary>이 머티리얼이 쓰는 glTF 확장(기본값과 다른 값/텍스처가 있는 것).</summary>
    public IEnumerable<string> UsedExtensions()
    {
        // 현재 타입에서 보이는 확장 파라미터 중 기본값과 다르거나 텍스처가 있는 것의 확장 이름을 모은다.
        var set = new HashSet<string>();
        foreach (var p in MaterialParams.For(Type))
        {
            if (p.Extension == null) continue;
            if (Get(p.Key) != p.Default || Tex(p.Key) != null) set.Add(p.Extension);
        }
        return set;
    }
}
