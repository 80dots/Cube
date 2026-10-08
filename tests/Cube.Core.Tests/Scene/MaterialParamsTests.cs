using System.Numerics;
using Cube.Core.IO;
using Cube.Core.Scene;

namespace Cube.Core.Tests.Scene;

/// <summary>머티리얼 파라미터 표·텍스처 슬롯·glTF 확장 판정·.cube 왕복.</summary>
public class MaterialParamsTests
{
    [Fact]
    public void Defaults_And_LegacyAccessors()
    {
        var m = new MaterialDef { Type = MaterialType.Pbr };
        Assert.Equal(new Vector3(0.5f), m.Color);
        Assert.Equal(0.5f, m.Roughness);
        Assert.Equal(1.5f, m.GetF("ior"));
        Assert.Equal(1f, m.GetF("alpha"));
        m.TexturePath = "a.png";
        Assert.Equal("a.png", m.Tex("color"));
        m.TexturePath = null;
        Assert.Null(m.Tex("color"));
        Assert.Empty(m.UsedExtensions());
    }

    [Fact]
    public void EveryValueParamIsTexturableExceptGltfScalars()
    {
        var noSlot = new HashSet<string> { "emissiveStrength", "attenuationDistance", "attenuationColor", "ior", "dispersion", "iridescenceIor", "iridescenceThicknessMin", "anisotropyRotation", "alphaCutoff",
            "uvOffsetU", "uvOffsetV", "uvScaleU", "uvScaleV", "uvRotation" };
        foreach (var p in MaterialParams.All.Where(p => p.Kind is MatParamKind.Float or MatParamKind.Color))
            Assert.Equal(!noSlot.Contains(p.Key), p.Texturable);
        // 기존 파라미터(Color/Specular/Shininess/Metallic/Roughness)는 모두 텍스처 가능
        foreach (var k in new[] { "color", "specular", "shininess", "metallic", "roughness" }) Assert.True(MaterialParams.Get(k)!.Texturable);
    }

    [Fact]
    public void UsedExtensions_FollowNonDefaultValuesAndTextures()
    {
        var m = new MaterialDef { Type = MaterialType.Pbr };
        m.Set("clearcoat", 1f);
        m.SetTex("sheenColor", "sheen.png");
        m.Set("ior", 1.33f);
        m.Set("uvScaleU", 2f);
        var ext = m.UsedExtensions().ToHashSet();
        Assert.Equal(new HashSet<string> { "KHR_materials_clearcoat", "KHR_materials_sheen", "KHR_materials_ior", "KHR_texture_transform" }, ext);
        // 다른 타입에서는 PBR 확장 값이 쓰이지 않는다
        m.Type = MaterialType.Lambert;
        Assert.Equal(new HashSet<string> { "KHR_texture_transform" }, m.UsedExtensions().ToHashSet());
    }

    [Fact]
    public void CloneCopyEquality_IncludeValuesAndTextures()
    {
        var a = new MaterialDef { Id = 3, Name = "x", Type = MaterialType.Pbr };
        a.Set("transmission", 0.7f); a.SetTex("normal", "n.png");
        var b = a.Clone();
        Assert.True(a.ValuesEqual(b));
        b.SetTex("normal", "m.png");
        Assert.False(a.ValuesEqual(b));
        a.CopyFrom(b);
        Assert.True(a.ValuesEqual(b));
    }

    [Fact]
    public void CubeFile_RoundTripsAllParameters()
    {
        var doc = new Document();
        var mat = new MaterialDef { Name = "glass", Type = MaterialType.Pbr };
        mat.Set("transmission", 0.9f); mat.Set("ior", 1.45f); mat.Set("attenuationColor", new Vector3(0.2f, 0.4f, 0.6f));
        mat.Set("alphaMode", 2f); mat.Set("doubleSided", 1f);
        mat.SetTex("roughness", "C:/t/rough.png"); mat.SetTex("clearcoatNormal", "C:/t/ccn.png");
        mat.Id = 1; doc.AddMaterialWithId(mat);
        var json = CubeFileFormat.Serialize(doc);
        var doc2 = new Document();
        CubeFileFormat.Deserialize(doc2, json);
        var m2 = doc2.FindMaterial(1)!;
        Assert.Equal(0.9f, m2.GetF("transmission"));
        Assert.Equal(1.45f, m2.GetF("ior"));
        Assert.Equal(new Vector3(0.2f, 0.4f, 0.6f), m2.Get("attenuationColor"));
        Assert.Equal(2f, m2.GetF("alphaMode"));
        Assert.Equal("C:/t/rough.png", m2.Tex("roughness"));
        Assert.Equal("C:/t/ccn.png", m2.Tex("clearcoatNormal"));
    }

    [Fact]
    public void LegacyFile_ColorTextureKeepsLook()
    {
        // v0.0.35 이하: 컬러 텍스처가 색을 대신했다 → 불러오면 색을 흰색으로(곱셈 규칙에서도 같은 모습)
        const string json = "{\"version\":1,\"materials\":[{\"id\":1,\"name\":\"old\",\"type\":\"lambert\",\"color\":[0.5,0.5,0.5],\"texture\":\"C:/t/wood.png\"}],\"nodes\":[]}";
        var doc = new Document();
        CubeFileFormat.Deserialize(doc, json);
        var m = doc.FindMaterial(1)!;
        Assert.Equal(Vector3.One, m.Color);
        Assert.Equal("C:/t/wood.png", m.TexturePath);
    }
}
