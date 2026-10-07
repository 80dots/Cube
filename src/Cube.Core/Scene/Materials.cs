using System.Numerics;

namespace Cube.Core.Scene;

public enum MaterialType { Lambert, BlinnPhong, Pbr, Unlit, Matcap }

/// <summary>문서 머티리얼. 오브젝트(SceneNode.MaterialId)에 할당한다. 색은 sRGB 0..1.</summary>
public sealed class MaterialDef
{
    public int Id;
    public string Name = "material";
    public MaterialType Type = MaterialType.Lambert;
    public Vector3 Color = new(0.5f, 0.5f, 0.5f);
    /// <summary>BlinnPhong 스펙큘러 색.</summary>
    public Vector3 Specular = new(0.5f, 0.5f, 0.5f);
    /// <summary>BlinnPhong 광택(1..128).</summary>
    public float Shininess = 32f;
    public float Metallic = 0f;
    public float Roughness = 0.5f;
    /// <summary>Matcap 이미지 파일 경로(없으면 내장 기본 matcap).</summary>
    public string? MatcapPath;
    /// <summary>알베도(컬러) 텍스처 파일 경로. 메시 UV로 입힌다(색과 곱함).</summary>
    public string? TexturePath;

    public MaterialDef Clone() => new() { Id = Id, Name = Name, Type = Type, Color = Color, Specular = Specular, Shininess = Shininess, Metallic = Metallic, Roughness = Roughness, MatcapPath = MatcapPath, TexturePath = TexturePath };

    public void CopyFrom(MaterialDef o) { Name = o.Name; Type = o.Type; Color = o.Color; Specular = o.Specular; Shininess = o.Shininess; Metallic = o.Metallic; Roughness = o.Roughness; MatcapPath = o.MatcapPath; TexturePath = o.TexturePath; }

    public bool ValuesEqual(MaterialDef o) => Name == o.Name && Type == o.Type && Color == o.Color && Specular == o.Specular && Shininess == o.Shininess && Metallic == o.Metallic && Roughness == o.Roughness && MatcapPath == o.MatcapPath && TexturePath == o.TexturePath;
}
