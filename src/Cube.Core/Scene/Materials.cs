using System.Numerics;

namespace Cube.Core.Scene;

public enum MaterialType { Lambert, BlinnPhong, Pbr, Unlit, Matcap }

/// <summary>머티리얼 파라미터 값 종류.</summary>
public enum MatParamKind { Float, Color, Bool, Enum }

/// <summary>
/// 머티리얼 파라미터 정의 하나. Texturable이면 값 대신(또는 값과 곱해) 이미지 텍스처를 쓸 수 있다.
/// Extension은 glTF 내보내기에서 이 값이 들어가는 확장(KHR_materials_*; null = 핵심 사양).
/// TextureChannel은 glTF에서 이 텍스처를 읽는 채널(r/g/b/a/rgb/rgba; 내보내기가 같은 확장의 텍스처들을 한 이미지로 묶을 때 쓴다).
/// </summary>
public sealed record MatParamInfo(
    string Key, string Label, MatParamKind Kind, Vector3 Default, float Min, float Max,
    MaterialType[] Types, string Group, bool Texturable = true, string? Extension = null,
    string TextureChannel = "rgb", string[]? Choices = null, string? Tip = null);

/// <summary>모든 머티리얼 파라미터 표(편집기·셰이더·저장·내보내기가 공유).</summary>
public static class MaterialParams
{
    private static readonly MaterialType[] All_ = { MaterialType.Lambert, MaterialType.BlinnPhong, MaterialType.Pbr, MaterialType.Unlit, MaterialType.Matcap };
    private static readonly MaterialType[] Pbr = { MaterialType.Pbr };
    private static readonly MaterialType[] Blinn = { MaterialType.BlinnPhong };
    private static readonly MaterialType[] Lit = { MaterialType.Lambert, MaterialType.BlinnPhong, MaterialType.Pbr };
    private static Vector3 F(float v) => new(v, 0, 0);

    public const string GBase = "Base", GSurface = "Surface", GEmission = "Emission", GClearcoat = "Clearcoat (KHR_materials_clearcoat)",
        GTransmission = "Transmission / Volume (KHR_materials_transmission, volume, ior)", GSpecular = "Specular (KHR_materials_specular)",
        GSheen = "Sheen (KHR_materials_sheen)", GIridescence = "Iridescence (KHR_materials_iridescence)", GAnisotropy = "Anisotropy (KHR_materials_anisotropy)",
        GAdvanced = "Alpha / Advanced", GUv = "Texture Transform (KHR_texture_transform)";

    public static readonly MatParamInfo[] All =
    {
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

    private static readonly Dictionary<string, MatParamInfo> _byKey = All.ToDictionary(p => p.Key);
    public static MatParamInfo? Get(string key) => _byKey.TryGetValue(key, out var p) ? p : null;
    public static IEnumerable<MatParamInfo> For(MaterialType t) => All.Where(p => p.Types.Contains(t));
}

/// <summary>
/// 문서 머티리얼. 오브젝트(SceneNode.MaterialId)에 할당한다. 색은 sRGB 0..1.
/// 값은 <see cref="MaterialParams"/> 표의 키로 Values에(없으면 기본값), 텍스처는 Textures[키] = 이미지 경로로 둔다.
/// 자주 쓰는 값(Color/Specular/Shininess/Metallic/Roughness/TexturePath)은 속성으로도 접근한다.
/// </summary>
public sealed class MaterialDef
{
    public int Id;
    public string Name = "material";
    public MaterialType Type = MaterialType.Lambert;
    /// <summary>파라미터 값(키 → 값; Float/Bool/Enum은 X).</summary>
    public readonly Dictionary<string, Vector3> Values = new();
    /// <summary>파라미터 텍스처(키 → 이미지 경로).</summary>
    public readonly Dictionary<string, string> Textures = new();
    /// <summary>Matcap 이미지 파일 경로(없으면 내장 기본 matcap).</summary>
    public string? MatcapPath;

    public Vector3 Get(string key) => Values.TryGetValue(key, out var v) ? v : MaterialParams.Get(key)?.Default ?? Vector3.Zero;
    public float GetF(string key) => Get(key).X;
    public void Set(string key, Vector3 v) => Values[key] = v;
    public void Set(string key, float v) => Values[key] = new Vector3(v, 0, 0);
    public string? Tex(string key) => Textures.TryGetValue(key, out var p) && !string.IsNullOrEmpty(p) ? p : null;
    public void SetTex(string key, string? path) { if (string.IsNullOrEmpty(path)) Textures.Remove(key); else Textures[key] = path; }

    public Vector3 Color { get => Get("color"); set => Set("color", value); }
    /// <summary>BlinnPhong 스펙큘러 색.</summary>
    public Vector3 Specular { get => Get("specular"); set => Set("specular", value); }
    /// <summary>BlinnPhong 광택(1..128).</summary>
    public float Shininess { get => GetF("shininess"); set => Set("shininess", value); }
    public float Metallic { get => GetF("metallic"); set => Set("metallic", value); }
    public float Roughness { get => GetF("roughness"); set => Set("roughness", value); }
    /// <summary>베이스 컬러 텍스처 경로(Textures["color"]).</summary>
    public string? TexturePath { get => Tex("color"); set => SetTex("color", value); }

    public MaterialDef Clone()
    {
        var c = new MaterialDef { Id = Id, Name = Name, Type = Type, MatcapPath = MatcapPath };
        foreach (var kv in Values) c.Values[kv.Key] = kv.Value;
        foreach (var kv in Textures) c.Textures[kv.Key] = kv.Value;
        return c;
    }

    public void CopyFrom(MaterialDef o)
    {
        Name = o.Name; Type = o.Type; MatcapPath = o.MatcapPath;
        Values.Clear(); foreach (var kv in o.Values) Values[kv.Key] = kv.Value;
        Textures.Clear(); foreach (var kv in o.Textures) Textures[kv.Key] = kv.Value;
    }

    public bool ValuesEqual(MaterialDef o)
    {
        if (Name != o.Name || Type != o.Type || MatcapPath != o.MatcapPath) return false;
        foreach (var p in MaterialParams.All) if (Get(p.Key) != o.Get(p.Key) || Tex(p.Key) != o.Tex(p.Key)) return false;
        return true;
    }

    /// <summary>이 머티리얼이 쓰는 glTF 확장(기본값과 다른 값/텍스처가 있는 것).</summary>
    public IEnumerable<string> UsedExtensions()
    {
        var set = new HashSet<string>();
        foreach (var p in MaterialParams.For(Type))
        {
            if (p.Extension == null) continue;
            if (Get(p.Key) != p.Default || Tex(p.Key) != null) set.Add(p.Extension);
        }
        return set;
    }
}
