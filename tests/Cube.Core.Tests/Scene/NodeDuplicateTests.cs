using System.Numerics;
using Cube.Core.Commands;
using Cube.Core.Mesh;
using Cube.Core.Scene;

namespace Cube.Core.Tests.Scene;

/// <summary><see cref="NodeDuplicate.CloneTree"/>(Edit → Duplicate)가 계층·셰이프·머티리얼·피벗을 보존하고 이름이 겹치지 않는지 검증한다.</summary>
public class NodeDuplicateTests
{
    /// <summary>
    /// 그룹(피벗·머티리얼) 아래 메시·라이트·조인트를 두고 복제하면 모든 자식과 셰이프 종류, 머티리얼, 피벗이 유지되고
    /// 메시는 독립 사본이며 모든 이름이 문서 안에서 고유하다. Undo하면 복제 트리가 모두 사라진다.
    /// </summary>
    [Fact]
    public void CloneTree_KeepsHierarchyShapesMaterialPivot_UniqueNames()
    {
        var doc = new Document();
        var group = new SceneNode { Name = "grp1", MaterialId = 0 };
        group.Local.Pivot = new Vector3(1, 2, 3);
        var mesh = new SceneNode { Name = "pCube1", Shape = new MeshShape(MeshBuilder.Cube()) { SmoothPreview = 2 }, MaterialId = 7 };
        var light = new SceneNode { Name = "light1", Shape = new LightShape { Intensity = 3f } };
        var joint = new SceneNode { Name = "joint1", Shape = new JointShape { Radius = 0.2f } };
        group.AttachChild(mesh); group.AttachChild(light); group.AttachChild(joint);
        doc.Undo.Push(new AddNodeCommand("Create", group));
        int before = doc.Nodes.Count;

        var names = new HashSet<string>();
        var copy = NodeDuplicate.CloneTree(doc, group, names);
        doc.Undo.Push(new AddNodeCommand("Duplicate", copy));

        Assert.Equal(before + 4, doc.Nodes.Count);
        Assert.Equal(new Vector3(1, 2, 3), copy.Local.Pivot);
        Assert.Equal(3, copy.Children.Count);
        var cm = copy.Children[0];
        Assert.NotNull(cm.MeshShape);
        Assert.NotSame(mesh.Mesh, cm.Mesh);
        Assert.Equal(7, cm.MaterialId);
        Assert.Equal(2, cm.MeshShape!.SmoothPreview);
        Assert.Equal(3f, copy.Children[1].Light!.Intensity);
        Assert.Equal(0.2f, copy.Children[2].Joint!.Radius);
        var allNames = doc.Nodes.Values.Where(n => !n.IsRoot).Select(n => n.Name).ToList();
        Assert.Equal(allNames.Count, allNames.Distinct().Count());

        doc.Undo.Undo();
        Assert.Equal(before, doc.Nodes.Count);
    }
}
