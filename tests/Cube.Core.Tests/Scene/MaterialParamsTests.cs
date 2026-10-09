using System.Numerics;
using Cube.Core.IO;
using Cube.Core.Scene;

namespace Cube.Core.Tests.Scene;

/// <summary>머티리얼 파라미터 표·텍스처 슬롯·glTF 확장 판정·.cube 왕복.</summary>
public class MaterialParamsTests
{
    /// <summary>
    /// 새 PBR 머티리얼의 기본값(색 0.5 회색, roughness 0.5, ior 1.5, alpha 1)과
    /// 예전 속성 접근자(TexturePath ↔ "color" 텍스처 슬롯)가 파라미터 표와 연결되어 있는지, 기본 상태에서 glTF 확장이 필요 없는지 확인한다.
    /// </summary>
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

    /// <summary>
    /// Float/Color 값 파라미터는 glTF에 텍스처 슬롯이 없는 스칼라(<c>noSlot</c> 목록)를 제외하고 모두 텍스처 가능해야 한다.
    /// 예전부터 있던 기본 파라미터도 텍스처 가능 여부가 유지되는지 확인한다.
    /// </summary>
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

    /// <summary>
    /// 기본값이 아닌 값이나 텍스처가 지정된 파라미터만 해당 glTF 확장(KHR_materials_*, KHR_texture_transform)을 요구해야 한다.
    /// 타입을 Lambert로 바꾸면 PBR 전용 확장은 빠지고 UV 변환 확장만 남는지 확인한다.
    /// </summary>
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

    /// <summary>Clone/ValuesEqual/CopyFrom이 값 사전뿐 아니라 텍스처 사전까지 복사·비교하는지 확인한다.</summary>
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

    /// <summary>
    /// 확장 파라미터 값(transmission, ior, attenuationColor, alphaMode 등)과 확장 텍스처 슬롯이
    /// .cube 직렬화(<c>values</c>/<c>textures</c>)를 거쳐 그대로 복원되는지 확인한다.
    /// </summary>
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

    /// <summary>
    /// v0.0.35 이하 파일(색 + texture 필드)을 불러오면 텍스처 경로는 유지하고 색을 흰색으로 보정해야 한다.
    /// 현재 규칙은 텍스처 × 색(곱셈)이므로, 보정하지 않으면 예전 파일이 어둡게 보이게 된다.
    /// </summary>
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
    /// <summary>새 Lambert 이름은 내장 기본 머티리얼 이름(lambert1)과 겹치지 않아야 한다(Maya처럼 lambert2부터).</summary>
    [Fact]
    public void UniqueMaterialName_Skips_DefaultLambert1()
    {
        var doc = new Document();
        Assert.Equal("lambert2", doc.UniqueMaterialName("lambert"));
        Assert.Equal("pbr1", doc.UniqueMaterialName("pbr"));
        doc.Materials.Add(new MaterialDef { Name = "lambert2" });
        Assert.Equal("lambert3", doc.UniqueMaterialName("lambert"));
    }
}
