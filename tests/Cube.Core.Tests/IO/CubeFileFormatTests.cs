using System.Numerics;
using Cube.Core.Commands;
using Cube.Core.IO;
using Cube.Core.Mesh;
using Cube.Core.Scene;

namespace Cube.Core.Tests.IO;

/// <summary>
/// 네이티브 문서 형식 <c>.cube</c>(JSON)의 저장/불러오기를 검증한다: 계층·트랜스폼(피벗 포함)·메시 위상·하드 엣지·코너 UV·머티리얼 보존,
/// 잘못된 형식/버전 거부, 실제 파일 저장/로드.
/// </summary>
public class CubeFileFormatTests
{
    /// <summary>
    /// Extrude된 큐브(소프트 엣지 둘, PBR 머티리얼 할당), 피벗 있는 트랜스폼의 구, 그 아래 숨김 자식 노드로 이루어진 문서를
    /// 직렬화 → 역직렬화한 뒤 노드 수·계층·이름·가시성·Local/Pivot·머티리얼 속성·메시 요소 수·하드 엣지 수·UV 집합이 같은지 확인한다.
    /// 불러온 문서는 깨끗(dirty 아님)하고 Undo 기록이 없어야 한다.
    /// </summary>
    [Fact]
    public void SaveLoad_RoundTrip_PreservesHierarchyMeshAndAttributes()
    {
        // 준비: 원본 문서 구성(큐브 + Extrude + 소프트 엣지, 구 + 트랜스폼, 머티리얼, 숨김 자식).
        var doc = new Document();
        var cube = CreatePrimitiveCommand.Cube(doc); doc.Undo.Push(cube);
        var mesh = cube.Node.Mesh!;
        int top = -1; for (int f = 0; f < mesh.FaceCount; f++) if (mesh.Faces[f].Normal.Y > 0.9f) top = f;
        doc.Undo.Push(new ExtrudeFacesCommand(cube.Node.Id, new[] { top }));
        MeshOps.SetEdgesHard(mesh, new[] { 0, 1 }, false); // 소프트 엣지 둘
        var sphere = CreatePrimitiveCommand.Sphere(doc); doc.Undo.Push(sphere);
        sphere.Node.Local = new Transform3(new Vector3(1, 2, 3), new Vector3(10, 20, 30), new Vector3(2, 2, 2), new Vector3(0.5f, -0.25f, 1));
        var mat = new MaterialDef { Name = "wood", Type = MaterialType.Pbr, Color = new Vector3(0.8f, 0.6f, 0.4f), Roughness = 0.7f, TexturePath = "C:/tex/wood.png" };
        doc.Undo.Push(new AddMaterialCommand(mat));
        doc.Undo.Push(new AssignMaterialCommand(new[] { cube.Node.Id }, mat.Id));
        var child = new SceneNode { Name = "child", Local = new Transform3(new Vector3(0, 1, 0), Vector3.Zero, Vector3.One) };
        doc.AddNode(child, sphere.Node);
        child.Visible = false;

        // 실행: JSON으로 직렬화한 뒤 새 문서에 역직렬화.
        string json = CubeFileFormat.Serialize(doc);
        var doc2 = new Document();
        CubeFileFormat.Deserialize(doc2, json);

        // 검증 1: 씬 계층·트랜스폼·머티리얼.
        Assert.Equal(3, doc2.Nodes.Count);
        Assert.Equal(2, doc2.Root.Children.Count);
        var cube2 = doc2.Root.Children.First(n => n.Name == "pCube1");
        var sphere2 = doc2.Root.Children.First(n => n.Name == "pSphere1");
        Assert.Single(sphere2.Children);
        Assert.Equal("child", sphere2.Children[0].Name);
        Assert.False(sphere2.Children[0].Visible);
        Assert.Equal(sphere.Node.Local, sphere2.Local);
        Assert.Equal(new Vector3(0.5f, -0.25f, 1), sphere2.Local.Pivot);
        var mat2 = doc2.FindMaterial(cube2.MaterialId);
        Assert.NotNull(mat2);
        Assert.Equal("wood", mat2!.Name);
        Assert.Equal("C:/tex/wood.png", mat2.TexturePath);
        Assert.Equal(MaterialType.Pbr, mat2.Type);

        // 검증 2: 메시 위상과 엣지/코너 속성.
        var m2 = cube2.Mesh!;
        Assert.Empty(MeshValidator.Check(m2));
        Assert.Equal(mesh.AliveVertexCount, m2.AliveVertexCount);
        Assert.Equal(mesh.AliveFaceCount, m2.AliveFaceCount);
        Assert.Equal(mesh.AliveEdgeCount, m2.AliveEdgeCount);
        int hardSrc = 0, hardDst = 0;
        for (int e = 0; e < mesh.EdgeCount; e++) if (mesh.Edges[e].Alive && mesh.Edges[e].Hard) hardSrc++;
        for (int e = 0; e < m2.EdgeCount; e++) if (m2.Edges[e].Alive && m2.Edges[e].Hard) hardDst++;
        Assert.Equal(hardSrc, hardDst);
        // 코너 UV 보존: 첫 면의 UV 집합 비교
        var uvSrc = new HashSet<Vector2>(); var uvDst = new HashSet<Vector2>();
        foreach (var h in mesh.Hes) if (h.Alive) uvSrc.Add(h.Uv0);
        foreach (var h in m2.Hes) if (h.Alive) uvDst.Add(h.Uv0);
        Assert.Equal(uvSrc, uvDst);
        Assert.False(doc2.IsDirty);
        Assert.False(doc2.Undo.CanUndo);
    }

    /// <summary>format 필드가 "cube"가 아니거나 지원하지 않는 version이면 <c>InvalidDataException</c>을 던져야 한다.</summary>
    [Fact]
    public void Deserialize_RejectsWrongFormat()
    {
        var doc = new Document();
        Assert.Throws<InvalidDataException>(() => CubeFileFormat.Deserialize(doc, "{\"format\":\"other\",\"version\":1,\"nodes\":[]}"));
        Assert.Throws<InvalidDataException>(() => CubeFileFormat.Deserialize(doc, "{\"format\":\"cube\",\"version\":99,\"nodes\":[]}"));
    }

    /// <summary>
    /// 임시 경로에 토러스 문서를 실제 파일로 저장하면 FilePath가 설정되고 dirty가 해제되며,
    /// 다시 불러온 문서의 노드 수·면 수가 같은지 확인한다. 테스트 후 임시 파일은 지운다.
    /// </summary>
    [Fact]
    public void SaveLoad_File()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cube_test_{Guid.NewGuid():N}.cube");
        try
        {
            var doc = new Document();
            doc.Undo.Push(CreatePrimitiveCommand.Torus(doc));
            CubeFileFormat.Save(doc, path);
            Assert.Equal(path, doc.FilePath);
            Assert.False(doc.IsDirty);
            var doc2 = new Document();
            CubeFileFormat.Load(doc2, path);
            Assert.Single(doc2.Nodes);
            Assert.Equal(doc.MeshNodes().First().Mesh!.AliveFaceCount, doc2.MeshNodes().First().Mesh!.AliveFaceCount);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
