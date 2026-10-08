using Cube.Core.Scene;
using Godot;
using NVec3 = System.Numerics.Vector3;

namespace Cube.App.IO;

/// <summary>
/// 가져온 Godot 머티리얼(glTF/FBX → StandardMaterial3D)을 문서 <see cref="MaterialDef"/>로 바꾼다.
/// 텍스처는 user://imported/&lt;파일 이름&gt;/에 PNG로 저장한다(임베드 이미지는 경로가 없음). 코어 머티리얼은 메탈릭·러프니스·AO 등을
/// 흑백 이미지의 R 채널로 읽으므로 glTF의 묶인 채널(ORM)은 채널별 흑백 PNG로 나눈다.
/// 같은 이름·같은 값의 머티리얼이 문서에 이미 있으면 다시 만들지 않는다(브리지 재가져오기에서 머티리얼이 쌓이지 않게).
/// </summary>
/// <remarks>
/// 한 번의 가져오기(<see cref="GodotSceneImporterBase"/>의 ImportCtx)마다 하나 만든다. 같은 Godot 머티리얼 객체는
/// <c>_ids</c> 캐시로 한 번만 변환하고, 같은 (텍스처, 채널) 조합은 <c>_files</c> 캐시로 한 번만 저장한다.
/// </remarks>
public sealed class ImportedMaterials
{
    /// <summary>대상 문서(기존 머티리얼 비교·새 머티리얼 ID 발급).</summary>
    private readonly Document _doc;
    /// <summary>텍스처 PNG 저장 폴더의 절대 경로(user://imported/&lt;원본 파일 이름&gt;).</summary>
    private readonly string _dir;
    /// <summary>Godot 머티리얼 객체 → 배정된 문서 머티리얼 ID 캐시.</summary>
    private readonly Dictionary<Material, int> _ids = new();
    /// <summary>(텍스처, 채널) → 저장한 PNG 경로(실패면 null) 캐시. 채널 -1 = 원본 그대로.</summary>
    private readonly Dictionary<(Texture2D, int), string?> _files = new();
    /// <summary>저장한 텍스처 수. 파일 이름 앞 두 자리 번호(이름 충돌 방지)로 쓴다.</summary>
    private int _texCount;
    /// <summary>문서에 새로 넣어야 하는 머티리얼(호출자가 AddMaterialCommand로 넣는다).</summary>
    public readonly List<MaterialDef> Created = new();

    /// <summary>대상 문서와 가져오는 원본 파일 경로로 만든다. 파일 이름에서 쓸 수 없는 문자를 _로 바꿔 저장 폴더 이름으로 쓴다.</summary>
    public ImportedMaterials(Document doc, string sourcePath)
    {
        _doc = doc;
        string stem = System.IO.Path.GetFileNameWithoutExtension(sourcePath);
        foreach (var c in System.IO.Path.GetInvalidFileNameChars()) stem = stem.Replace(c, '_');
        _dir = ProjectSettings.GlobalizePath($"user://imported/{stem}");
    }

    /// <summary>Godot 머티리얼 → 문서 머티리얼 ID(0 = 기본 lambert1).</summary>
    public int IdFor(Material? gm)
    {
        // 머티리얼이 없는 서피스는 기본 lambert1
        if (gm == null) return 0;
        if (_ids.TryGetValue(gm, out int id)) return id;
        // 변환 후 값이 같은 머티리얼이 문서나 이번 가져오기에 이미 있으면 그 ID를 재사용, 없으면 새 ID로 생성 목록에 추가
        var def = Convert(gm);
        var same = _doc.Materials.FirstOrDefault(m => m.ValuesEqual(def)) ?? Created.FirstOrDefault(m => m.ValuesEqual(def));
        if (same != null) id = same.Id;
        else { def.Id = _doc.NextMaterialId(); Created.Add(def); id = def.Id; }
        _ids[gm] = id;
        return id;
    }

    /// <summary>머티리얼 ID의 이름(이번에 만든 것 우선, 없으면 문서, 그래도 없으면 "lambert1"). 머티리얼별 자식 노드 이름에 쓴다.</summary>
    public string NameOf(int id) => (Created.FirstOrDefault(m => m.Id == id) ?? _doc.FindMaterial(id))?.Name ?? "lambert1";

    /// <summary>
    /// Godot 머티리얼을 PBR <see cref="MaterialDef"/>로 변환한다(Unshaded면 Unlit).
    /// 색·알파·메탈릭·러프니스·노멀·AO·이미시브·클리어코트·투명 모드·양면·UV1 스케일/오프셋을 옮기고,
    /// 텍스처는 <see cref="Save"/>로 PNG로 저장해 경로를 단다. BaseMaterial3D가 아니면(셰이더 머티리얼 등) 이름만 가진 기본값.
    /// </summary>
    private MaterialDef Convert(Material gm)
    {
        string name = string.IsNullOrWhiteSpace(gm.ResourceName) ? "material" : gm.ResourceName;
        var d = new MaterialDef { Name = name, Type = MaterialType.Pbr };
        if (gm is not BaseMaterial3D bm) return d;
        if (bm.ShadingMode == BaseMaterial3D.ShadingModeEnum.Unshaded) d.Type = MaterialType.Unlit;
        // 베이스 컬러와 알파(Godot 색은 sRGB 값 그대로 저장)
        var ac = bm.AlbedoColor;
        d.Set("color", new NVec3(ac.R, ac.G, ac.B));
        d.Set("alpha", ac.A);
        d.SetTex("color", Save(bm.AlbedoTexture, -1));
        // 메탈릭/러프니스: glTF ORM처럼 채널이 묶인 텍스처는 지정 채널만 흑백 PNG로 분리
        d.Set("metallic", bm.Metallic);
        d.Set("roughness", bm.Roughness);
        d.SetTex("metallic", Save(bm.MetallicTexture, Channel(bm.MetallicTextureChannel)));
        d.SetTex("roughness", Save(bm.RoughnessTexture, Channel(bm.RoughnessTextureChannel)));
        if (bm.NormalEnabled && bm.NormalTexture != null)
        {
            d.Set("normal", bm.NormalScale);
            d.SetTex("normal", Save(bm.NormalTexture, -1));
        }
        if (bm.AOEnabled && bm.AOTexture != null) d.SetTex("occlusion", Save(bm.AOTexture, Channel(bm.AOTextureChannel)));
        if (bm.EmissionEnabled)
        {
            var e = bm.Emission;
            d.Set("emissive", new NVec3(e.R, e.G, e.B));
            if (Math.Abs(bm.EmissionEnergyMultiplier - 1f) > 1e-4f) d.Set("emissiveStrength", bm.EmissionEnergyMultiplier);
            d.SetTex("emissive", Save(bm.EmissionTexture, -1));
        }
        if (bm.ClearcoatEnabled)
        {
            d.Set("clearcoat", bm.Clearcoat);
            d.Set("clearcoatRoughness", bm.ClearcoatRoughness);
            // Godot 클리어코트 텍스처: R = 세기, G = 러프니스(glTF와 같음)
            d.SetTex("clearcoat", Save(bm.ClearcoatTexture, 0));
            d.SetTex("clearcoatRoughness", Save(bm.ClearcoatTexture, 1));
        }
        // 투명 모드: Alpha/DepthPrePass → Blend(2), Scissor/Hash → Mask(1)
        switch (bm.Transparency)
        {
            case BaseMaterial3D.TransparencyEnum.Alpha:
            case BaseMaterial3D.TransparencyEnum.AlphaDepthPrePass:
                d.Set("alphaMode", 2f); break;
            case BaseMaterial3D.TransparencyEnum.AlphaScissor:
            case BaseMaterial3D.TransparencyEnum.AlphaHash:
                d.Set("alphaMode", 1f); d.Set("alphaCutoff", bm.AlphaScissorThreshold); break;
        }
        if (bm.CullMode == BaseMaterial3D.CullModeEnum.Disabled) d.Set("doubleSided", 1f);
        // UV1 스케일·오프셋이 기본값이 아니면 KHR_texture_transform 대응 파라미터로 옮긴다
        var sc = bm.Uv1Scale; var of = bm.Uv1Offset;
        if (Math.Abs(sc.X - 1f) > 1e-5f || Math.Abs(sc.Y - 1f) > 1e-5f) { d.Set("uvScaleU", sc.X); d.Set("uvScaleV", sc.Y); }
        if (Math.Abs(of.X) > 1e-5f || Math.Abs(of.Y) > 1e-5f) { d.Set("uvOffsetU", of.X); d.Set("uvOffsetV", of.Y); }
        return d;
    }

    /// <summary>Godot 텍스처 채널 → 0..3(R/G/B/A), 회색조는 R.</summary>
    private static int Channel(BaseMaterial3D.TextureChannel c) => c switch
    {
        BaseMaterial3D.TextureChannel.Green => 1,
        BaseMaterial3D.TextureChannel.Blue => 2,
        BaseMaterial3D.TextureChannel.Alpha => 3,
        _ => 0,
    };

    /// <summary>텍스처를 PNG로 저장하고 절대 경로를 돌려준다. channel ≥ 0이면 그 채널만 흑백 이미지로.</summary>
    /// <remarks>
    /// 단계: 이미지 복제 → 압축 해제·밉맵 제거 → (channel ≥ 0이면 RGBA8로 바꿔 해당 채널만 L8 이미지로 추출, 이름에 _r/_g/_b/_a) →
    /// "번호_이름.png"로 저장. 실패하면 오류를 출력하고 null(역시 캐시).
    /// </remarks>
    private string? Save(Texture2D? tex, int channel)
    {
        if (tex == null) return null;
        if (_files.TryGetValue((tex, channel), out var cached)) return cached;
        string? path = null;
        try
        {
            var img = tex.GetImage();
            if (img != null && !img.IsEmpty())
            {
                img = (Image)img.Duplicate();
                if (img.IsCompressed()) img.Decompress();
                if (img.HasMipmaps()) img.ClearMipmaps();
                System.IO.Directory.CreateDirectory(_dir);
                string baseName = string.IsNullOrWhiteSpace(tex.ResourceName) ? "texture" : tex.ResourceName;
                foreach (var c in System.IO.Path.GetInvalidFileNameChars()) baseName = baseName.Replace(c, '_');
                if (channel >= 0)
                {
                    img.Convert(Image.Format.Rgba8);
                    var src = img.GetData();
                    var dst = new byte[img.GetWidth() * img.GetHeight()];
                    for (int i = 0; i < dst.Length; i++) dst[i] = src[i * 4 + channel];
                    img = Image.CreateFromData(img.GetWidth(), img.GetHeight(), false, Image.Format.L8, dst);
                    baseName += "_" + "rgba"[channel];
                }
                path = System.IO.Path.Combine(_dir, $"{_texCount++:D2}_{baseName}.png");
                if (img.SavePng(path) != Error.Ok) path = null;
            }
        }
        catch (Exception ex) { GD.PrintErr($"[Import] texture save failed: {ex.Message}"); path = null; }
        _files[(tex, channel)] = path;
        return path;
    }
}
