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
public sealed class ImportedMaterials
{
    private readonly Document _doc;
    private readonly string _dir;
    private readonly Dictionary<Material, int> _ids = new();
    private readonly Dictionary<(Texture2D, int), string?> _files = new();
    private int _texCount;
    /// <summary>문서에 새로 넣어야 하는 머티리얼(호출자가 AddMaterialCommand로 넣는다).</summary>
    public readonly List<MaterialDef> Created = new();

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
        if (gm == null) return 0;
        if (_ids.TryGetValue(gm, out int id)) return id;
        var def = Convert(gm);
        var same = _doc.Materials.FirstOrDefault(m => m.ValuesEqual(def)) ?? Created.FirstOrDefault(m => m.ValuesEqual(def));
        if (same != null) id = same.Id;
        else { def.Id = _doc.NextMaterialId(); Created.Add(def); id = def.Id; }
        _ids[gm] = id;
        return id;
    }

    public string NameOf(int id) => (Created.FirstOrDefault(m => m.Id == id) ?? _doc.FindMaterial(id))?.Name ?? "lambert1";

    private MaterialDef Convert(Material gm)
    {
        string name = string.IsNullOrWhiteSpace(gm.ResourceName) ? "material" : gm.ResourceName;
        var d = new MaterialDef { Name = name, Type = MaterialType.Pbr };
        if (gm is not BaseMaterial3D bm) return d;
        if (bm.ShadingMode == BaseMaterial3D.ShadingModeEnum.Unshaded) d.Type = MaterialType.Unlit;
        var ac = bm.AlbedoColor;
        d.Set("color", new NVec3(ac.R, ac.G, ac.B));
        d.Set("alpha", ac.A);
        d.SetTex("color", Save(bm.AlbedoTexture, -1));
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
