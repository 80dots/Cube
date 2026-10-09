using System.Numerics;
using Cube.Core.Commands;
using Cube.Core.IO;
using Cube.Core.IO.Fbx;
using Cube.Core.Scene;

namespace Cube.Core.Tests.IO;

/// <summary>
/// 계층이 있는 내보내기 검증(감사): OBJ는 넘겨받은 노드의 자손 메시까지(그룹 아래 메시 포함) 쓰고,
/// FBX는 부모와 자식을 함께 넘겨도 자식 모델을 한 번만 쓴다.
/// </summary>
public class ExportHierarchyTests
{
    /// <summary>그룹(빈 노드) → 부모 큐브 → 자식 구 문서.</summary>
    private static (Document doc, SceneNode group, SceneNode parent, SceneNode child) Build()
    {
        var doc = new Document();
        var group = new SceneNode { Name = "grp", Local = new Transform3(new Vector3(0, 0, 5), Vector3.Zero, Vector3.One) };
        doc.AddNode(group);
        var cube = CreatePrimitiveCommand.Cube(doc); doc.Undo.Push(cube);
        doc.Reparent(cube.Node, group);
        var sphere = CreatePrimitiveCommand.Sphere(doc); doc.Undo.Push(sphere);
        doc.Reparent(sphere.Node, cube.Node);
        sphere.Node.Local = new Transform3(new Vector3(0, 2, 0), Vector3.Zero, Vector3.One);
        return (doc, group, cube.Node, sphere.Node);
    }

    /// <summary>빈 그룹만 넘겨도 아래 메시 둘이 모두 OBJ에 들어가고, 부모·자식을 함께 넘겨도 한 번씩만 들어가야 한다.</summary>
    [Fact]
    public void Obj_ExportsDescendantMeshesOnce()
    {
        var (doc, group, parent, child) = Build();
        var path = Path.Combine(Path.GetTempPath(), $"cube_hier_{Guid.NewGuid():N}.obj");
        try
        {
            var r = new ObjExporter().Export(doc, new[] { group }, path, ExportPreset.Generic);
            Assert.True(r.Ok, r.Message);
            var text = File.ReadAllText(path);
            Assert.Equal(2, text.Split('\n').Count(l => l.StartsWith("o ")));
            r = new ObjExporter().Export(doc, new[] { parent, child }, path, ExportPreset.Generic);
            Assert.True(r.Ok, r.Message);
            Assert.Equal(2, File.ReadAllText(path).Split('\n').Count(l => l.StartsWith("o ")));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    /// <summary>부모와 자식을 함께 넘기면 Model은 둘(큐브·구)이어야 한다(전에는 자식이 최상위로 한 번 더 나가 셋).</summary>
    [Fact]
    public void Fbx_ParentAndChildSelected_ChildWrittenOnce()
    {
        var (doc, _, parent, child) = Build();
        var top = new FbxSceneBuilder(doc).Build(new[] { parent, child });
        var (_, nodes) = FbxBinaryReader.Read(FbxBinaryWriter.Write(top));
        var models = nodes.First(n => n.Name == "Objects").All("Model").ToList();
        Assert.Equal(2, models.Count);
    }
}
